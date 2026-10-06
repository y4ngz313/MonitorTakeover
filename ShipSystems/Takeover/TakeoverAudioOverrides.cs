// TakeoverAudioOverrides.cs - LGUMonitorTakeover
//
// Session cache for the loose-file audio overrides players drop into
// BepInEx/config/Y4NGZCompany/MonitorTakeovers: AlarmFile, VoiceFiles and a local Soundtrack.
// TakeoverAudioPolicy decides which published files this peer accepts (sandbox, size and the
// host's SHA-256); this turns the accepted files into AudioClips and hands one decided set to
// each quota takeover.
//
// #861 lifecycle, in one place:
//   * A preparation runs when this peer learns a selection: the host when it builds the
//     payload or soundtrack plan, a client when the payload or the soundtrack sidecar arrives.
//     Each one re-verifies the files, evicts clips nothing selects any more, and starts the
//     loads it still needs. A load that failed is tried again only by a later preparation.
//   * Every load owns its UnityWebRequest through its entry, not through a `using` in a
//     coroutine, and runs on a TakeoverAudioLoader on the owner's GameObject. Unity stops a
//     coroutine without resuming it when that object is deactivated or destroyed, so the
//     loader's OnDisable aborts and disposes its requests at that boundary; cancel, eviction
//     and reset abort and dispose explicitly too. Nothing waits for the next request to notice.
//   * A takeover decides its audio once, with Capture, at the shared deadline. A clip that
//     finishes after that waits for the next takeover; a captured clip is pinned and survives
//     eviction until ReleaseCapture.
//
// Quota takeovers only: nothing else in this assembly plays these clips.

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>Where one accepted takeover audio file is on its way to a playable clip.</summary>
    internal enum TakeoverAudioLoadState
    {
        /// <summary>Not requested, or its decoded clip was destroyed and must load again.</summary>
        None,
        Loading,
        Ready,
        /// <summary>The request or the decoder failed. A later preparation may try again.</summary>
        Failed,
        /// <summary>
        /// Aborted by a reset, an eviction, or its loader being disabled, deactivated or
        /// destroyed. The next preparation or takeover starts it again.
        /// </summary>
        Cancelled,
    }

    /// <summary>
    /// The audio one takeover uses, decided once. Null or empty means the built-in layer plays
    /// (alarm, voices) or no soundtrack file plays.
    /// </summary>
    internal readonly struct TakeoverAudioSnapshot
    {
        internal readonly int Generation;
        internal readonly AudioClip Alarm;
        internal readonly AudioClip[] Voices;
        internal readonly AudioClip Soundtrack;

        internal TakeoverAudioSnapshot(int generation, AudioClip alarm, AudioClip[] voices, AudioClip soundtrack)
        {
            Generation = generation;
            Alarm = alarm;
            Voices = voices ?? Array.Empty<AudioClip>();
            Soundtrack = soundtrack;
        }

        internal static readonly TakeoverAudioSnapshot Empty =
            new TakeoverAudioSnapshot(0, null, Array.Empty<AudioClip>(), null);
    }

    internal static class TakeoverAudioOverrides
    {
        private sealed class Entry
        {
            internal readonly TakeoverAudioOverrideFile File;
            internal TakeoverAudioLoadState State;
            internal AudioClip Clip;
            internal UnityWebRequest Request;
            internal TakeoverAudioLoader Owner;
            internal Coroutine Routine;
            internal int Attempt;
            internal float StartedAt;
            internal string Error = string.Empty;

            internal Entry(TakeoverAudioOverrideFile file)
            {
                File = file;
            }
        }

        // Keyed by resolved path + verified hash, so a replaced file on disk never serves the
        // clip decoded from its previous bytes.
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly HashSet<string> SelectedKeys = new HashSet<string>(StringComparer.Ordinal);
        // Keys of the clips the running takeover captured. Never evicted or destroyed.
        private static readonly HashSet<string> Pinned = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<Entry> Scratch = new List<Entry>();

        private static TakeoverAudioOverrideFile _alarm;
        private static readonly List<TakeoverAudioOverrideFile> _voices = new List<TakeoverAudioOverrideFile>();
        private static TakeoverAudioOverrideFile _soundtrack;
        // What the current selection was prepared from, so BeginTakeover can tell "already
        // prepared" from "never prepared". Cleared by Reset; never used to skip a preparation
        // that a new payload or sidecar asks for.
        private static string _preparedPayloadKey;
        private static string _preparedSoundtrackKey;
        private static int _generation;
        private static int _attempts;

        /// <summary>Incremented by every preparation and every reset.</summary>
        internal static int Generation => _generation;

        internal static TakeoverAudioOverrideFile AlarmFile => _alarm;
        internal static IReadOnlyList<TakeoverAudioOverrideFile> VoiceFiles => _voices;
        internal static TakeoverAudioOverrideFile SoundtrackFile => _soundtrack;

        /// <summary>
        /// The payload's AlarmFile and VoiceFiles, as this peer accepts them. The receive
        /// boundary for a client, and the host's own preparation after it publishes.
        /// </summary>
        internal static void PreparePayload(MonoBehaviour owner, QuotaTakeoverPayload payload)
        {
            if (owner == null) return;
            _generation++;
            _preparedPayloadKey = PayloadKey(payload);
            string root = QuotaProgressionRegistry.MediaDirectory;

            TakeoverAudioFileCheck alarm = TakeoverAudioPolicy.VerifyPublishedFile(
                root, "AlarmFile", payload.AlarmAudioFile, payload.AlarmAudioHash);
            _alarm = TakeoverAudioOverrideFile.From(alarm);
            if (!alarm.Accepted && alarm.Status != TakeoverAudioFileStatus.Blank)
                Warn(alarm.Describe("this player hears the built-in siren"));

            List<TakeoverAudioFileCheck> voices = TakeoverAudioPolicy.VerifyPublishedVoices(
                root, payload.MumbleAudioFiles, payload.MumbleAudioHashes);
            _voices.Clear();
            for (int index = 0; index < voices.Count; index++)
                if (voices[index].Accepted) _voices.Add(TakeoverAudioOverrideFile.From(voices[index]));
            for (int index = 0; index < voices.Count; index++)
            {
                if (voices[index].Accepted) continue;
                Warn(voices[index].Describe(_voices.Count > 0
                    ? $"it is skipped on this player and the other {_voices.Count} voice file(s) still play"
                    : "it is skipped on this player, and with no usable voice file this player hears the built-in voices"));
            }

            if (_alarm.IsValid || voices.Count > 0)
            {
                TakeoverBootstrap.Log?.LogInfo(
                    $"[TakeoverAudioOverrides] Preparation {_generation}: AlarmFile {(_alarm.IsValid ? "'" + _alarm.Name + "'" : "built-in")}, "
                    + $"VoiceFiles {_voices.Count} of {voices.Count} accepted.");
            }

            RebuildSelection();
            Request(owner, _alarm, retryFailed: true);
            for (int index = 0; index < _voices.Count; index++)
                Request(owner, _voices[index], retryFailed: true);
        }

        /// <summary>
        /// The soundtrack sidecar's local file (default when the plan is Off or a link), as this
        /// peer accepts it. Called by <see cref="TakeoverSoundtrack.Prepare"/>.
        /// </summary>
        internal static void SelectSoundtrack(MonoBehaviour owner, TakeoverSoundtrackPlanWire wire, TakeoverAudioOverrideFile file)
        {
            if (owner == null) return;
            _generation++;
            _preparedSoundtrackKey = SoundtrackKey(wire);
            _soundtrack = file;
            RebuildSelection();
            Request(owner, _soundtrack, retryFailed: true);
        }

        internal static bool IsPreparedFor(QuotaTakeoverPayload payload) =>
            _preparedPayloadKey != null && string.Equals(_preparedPayloadKey, PayloadKey(payload), StringComparison.Ordinal);

        internal static bool IsPreparedFor(TakeoverSoundtrackPlanWire wire) =>
            _preparedSoundtrackKey != null && string.Equals(_preparedSoundtrackKey, SoundtrackKey(wire), StringComparison.Ordinal);

        /// <summary>
        /// Restarts what the current selection still needs without re-verifying anything: a
        /// cancelled load (its loader went away, or a reset) or a decoded clip that was
        /// destroyed. A failed load is left for the next preparation.
        /// </summary>
        internal static void EnsureRequested(MonoBehaviour owner)
        {
            if (owner == null) return;
            Request(owner, _alarm, retryFailed: false);
            for (int index = 0; index < _voices.Count; index++)
                Request(owner, _voices[index], retryFailed: false);
            Request(owner, _soundtrack, retryFailed: false);
        }

        internal static TakeoverAudioLoadState GetState(TakeoverAudioOverrideFile file)
        {
            if (!file.IsValid || !Entries.TryGetValue(file.CacheKey, out Entry entry)) return TakeoverAudioLoadState.None;
            if (entry.State == TakeoverAudioLoadState.Ready && entry.Clip == null) return TakeoverAudioLoadState.None;
            return entry.State;
        }

        /// <summary>The decoded clip, or null while it is not ready or after it was destroyed.</summary>
        internal static AudioClip Resolve(TakeoverAudioOverrideFile file)
        {
            if (!file.IsValid || !Entries.TryGetValue(file.CacheKey, out Entry entry)) return null;
            return entry.State == TakeoverAudioLoadState.Ready && entry.Clip != null ? entry.Clip : null;
        }

        /// <summary>
        /// The takeover's one audio decision. Whatever is ready is used and pinned; everything
        /// else falls back for this takeover with one line saying why, and a load still running
        /// keeps running for the next takeover. A partial voice pool plays only its ready files.
        /// </summary>
        internal static TakeoverAudioSnapshot Capture()
        {
            Pinned.Clear();
            AudioClip alarm = Take(_alarm, "the built-in siren plays this takeover");

            AudioClip[] voices = Array.Empty<AudioClip>();
            if (_voices.Count > 0)
            {
                var ready = new List<AudioClip>(_voices.Count);
                for (int index = 0; index < _voices.Count; index++)
                {
                    AudioClip clip = Take(_voices[index], null);
                    if (clip != null) ready.Add(clip);
                }
                for (int index = 0; index < _voices.Count; index++)
                {
                    if (Resolve(_voices[index]) != null) continue;
                    Warn(DescribeUnready(_voices[index]) + (ready.Count > 0
                        ? $"; this takeover plays the {ready.Count} ready voice file(s)."
                        : "; with no voice file ready this takeover plays the built-in voices."));
                }
                voices = ready.ToArray();
            }

            AudioClip soundtrack = Take(_soundtrack, "no soundtrack plays this takeover");
            return new TakeoverAudioSnapshot(_generation, alarm, voices, soundtrack);
        }

        /// <summary>The takeover is over; its clips may be evicted by the next preparation.</summary>
        internal static void ReleaseCapture()
        {
            Pinned.Clear();
        }

        /// <summary>
        /// Session boundary: aborts and disposes every load, destroys every decoded clip no
        /// running takeover holds, and forgets the selection. The next preparation starts clean.
        /// </summary>
        internal static void Reset(string reason)
        {
            int cancelled = 0;
            int released = 0;
            Scratch.Clear();
            foreach (KeyValuePair<string, Entry> pair in Entries) Scratch.Add(pair.Value);
            for (int index = 0; index < Scratch.Count; index++)
            {
                Entry entry = Scratch[index];
                if (entry.State == TakeoverAudioLoadState.Loading)
                {
                    Cancel(entry, reason);
                    cancelled++;
                }
                string key = entry.File.CacheKey;
                if (Pinned.Contains(key)) continue;
                if (entry.Clip != null)
                {
                    UnityEngine.Object.Destroy(entry.Clip);
                    released++;
                }
                entry.Clip = null;
                Entries.Remove(key);
            }
            Scratch.Clear();

            _alarm = default;
            _voices.Clear();
            _soundtrack = default;
            SelectedKeys.Clear();
            _preparedPayloadKey = null;
            _preparedSoundtrackKey = null;
            _generation++;

            if (cancelled > 0 || released > 0)
            {
                TakeoverBootstrap.Log?.LogInfo(
                    $"[TakeoverAudioOverrides] Reset ({reason}): cancelled {cancelled} load(s), released {released} clip(s).");
            }
        }

        /// <summary>
        /// Called by <paramref name="loader"/>'s OnDisable, which Unity runs when the loader is
        /// disabled, its GameObject is deactivated, or either is destroyed. Its coroutines are
        /// stopped and their requests aborted and disposed here, before anything can request
        /// them again; their entries become Cancelled for the next preparation or takeover.
        /// </summary>
        internal static void CancelLoadsOwnedBy(TakeoverAudioLoader loader)
        {
            int cancelled = 0;
            foreach (Entry entry in Entries.Values)
            {
                if (entry.State != TakeoverAudioLoadState.Loading || !ReferenceEquals(entry.Owner, loader)) continue;
                Cancel(entry, "the object running it was disabled, deactivated or destroyed");
                cancelled++;
            }
            if (cancelled > 0)
            {
                TakeoverBootstrap.Log?.LogInfo(
                    $"[TakeoverAudioOverrides] Cancelled {cancelled} load(s) running on '{loader.name}', which was disabled, "
                    + "deactivated or destroyed; the next preparation or takeover starts them again.");
            }
        }

        // ── Loading ────────────────────────────────────────────────────────────

        private static void Request(MonoBehaviour owner, TakeoverAudioOverrideFile file, bool retryFailed)
        {
            if (owner == null || !file.IsValid) return;
            string key = file.CacheKey;
            if (!Entries.TryGetValue(key, out Entry entry))
            {
                entry = new Entry(file);
                Entries.Add(key, entry);
            }

            switch (entry.State)
            {
                case TakeoverAudioLoadState.Ready:
                    if (entry.Clip != null) return;
                    TakeoverBootstrap.Log?.LogInfo(
                        $"[TakeoverAudioOverrides] {file.Label}'s decoded clip was destroyed; loading it again.");
                    entry.Clip = null;
                    break;
                case TakeoverAudioLoadState.Loading:
                    // The loader's OnDisable cancels its loads, so an active loader inside the
                    // request's own timeout is still working. Anything else is a load nothing
                    // will ever consume; release it and start again.
                    if (entry.Owner != null && entry.Owner.isActiveAndEnabled && entry.Request != null
                        && Time.realtimeSinceStartup - entry.StartedAt <= TakeoverAudioPolicy.LoadTimeoutSeconds + 5f)
                        return;
                    Cancel(entry, entry.Owner == null ? "its loader was destroyed" : "its loader stopped");
                    break;
                case TakeoverAudioLoadState.Failed:
                    if (!retryFailed) return;
                    break;
            }

            Start(owner, entry);
        }

        private static void Start(MonoBehaviour owner, Entry entry)
        {
            entry.Attempt = ++_attempts;
            entry.State = TakeoverAudioLoadState.Loading;
            entry.Error = string.Empty;
            entry.Clip = null;
            entry.Owner = null;
            entry.Routine = null;
            entry.StartedAt = Time.realtimeSinceStartup;

            // The loads run on a TakeoverAudioLoader beside the owner, added only once a load
            // actually starts on it.
            TakeoverAudioLoader loader = owner.isActiveAndEnabled ? TakeoverAudioLoader.For(owner) : null;
            if (loader == null || !loader.isActiveAndEnabled)
            {
                Cancel(entry, "the loader is inactive");
                return;
            }
            entry.Owner = loader;

            AudioType audioType = AudioTypeFor(entry.File.Path);
            if (audioType == AudioType.UNKNOWN)
            {
                Fail(entry, "it is not an MP3, WAV or OGG file");
                return;
            }

            try
            {
                entry.Request = UnityWebRequestMultimedia.GetAudioClip(new Uri(entry.File.Path).AbsoluteUri, audioType);
                entry.Request.timeout = TakeoverAudioPolicy.LoadTimeoutSeconds;
                Coroutine routine = loader.StartCoroutine(Load(entry, entry.Attempt));
                // A load that already finished inside StartCoroutine owns nothing any more.
                if (entry.State == TakeoverAudioLoadState.Loading) entry.Routine = routine;
            }
            catch (Exception ex)
            {
                if (entry.State == TakeoverAudioLoadState.Loading)
                {
                    ReleaseRequest(entry, abort: true);
                    Fail(entry, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static IEnumerator Load(Entry entry, int attempt)
        {
            UnityWebRequestAsyncOperation operation = null;
            string startError = null;
            try
            {
                operation = entry.Request.SendWebRequest();
            }
            catch (Exception ex)
            {
                startError = ex.GetType().Name + ": " + ex.Message;
            }

            if (startError != null)
            {
                if (IsCurrent(entry, attempt))
                {
                    ReleaseRequest(entry, abort: true);
                    Fail(entry, startError);
                }
                yield break;
            }

            yield return operation;

            // Cancelled, evicted, reset or restarted meanwhile: whoever did that already
            // aborted and disposed this request and owns the entry now.
            if (!IsCurrent(entry, attempt)) yield break;
            Complete(entry);
        }

        private static void Complete(Entry entry)
        {
            UnityWebRequest request = entry.Request;
            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail(entry, request.error);
                    return;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null || clip.loadState == AudioDataLoadState.Failed || clip.samples <= 0)
                {
                    if (clip != null) UnityEngine.Object.Destroy(clip);
                    Fail(entry, "Unity could not decode it (corrupt, empty, or not really an MP3, WAV or OGG file)");
                    return;
                }

                clip.name = "Y4NGZ_TakeoverOverride_" + System.IO.Path.GetFileNameWithoutExtension(entry.File.Path);
                entry.Clip = clip;
                entry.State = TakeoverAudioLoadState.Ready;
                entry.Error = string.Empty;
                TakeoverBootstrap.Log?.LogInfo(
                    $"[TakeoverAudioOverrides] Loaded {entry.File.Label} ({clip.length:F2}s) for the quota takeover "
                    + $"in {Time.realtimeSinceStartup - entry.StartedAt:F2}s.");
            }
            catch (Exception ex)
            {
                Fail(entry, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                ReleaseRequest(entry, abort: false);
            }
        }

        private static bool IsCurrent(Entry entry, int attempt) =>
            entry.State == TakeoverAudioLoadState.Loading
            && entry.Attempt == attempt
            && Entries.TryGetValue(entry.File.CacheKey, out Entry current)
            && ReferenceEquals(current, entry);

        private static void Fail(Entry entry, string error)
        {
            entry.State = TakeoverAudioLoadState.Failed;
            entry.Error = string.IsNullOrEmpty(error) ? "unknown error" : error;
            Warn($"{entry.File.Label} failed to load: {entry.Error}; {FallbackFor(entry.File.Setting)} "
                + "until a later takeover loads it.");
        }

        private static void Cancel(Entry entry, string reason)
        {
            if (entry.State != TakeoverAudioLoadState.Loading) return;
            if (entry.Owner != null && entry.Routine != null)
            {
                try { entry.Owner.StopCoroutine(entry.Routine); } catch { }
            }
            ReleaseRequest(entry, abort: true);
            entry.State = TakeoverAudioLoadState.Cancelled;
            entry.Error = reason ?? string.Empty;
        }

        private static void ReleaseRequest(Entry entry, bool abort)
        {
            UnityWebRequest request = entry.Request;
            entry.Request = null;
            entry.Routine = null;
            entry.Owner = null;
            if (request == null) return;
            if (abort)
            {
                try { request.Abort(); } catch { }
            }
            try { request.Dispose(); } catch { }
        }

        // ── Selection bookkeeping ─────────────────────────────────────────────

        private static void RebuildSelection()
        {
            SelectedKeys.Clear();
            if (_alarm.IsValid) SelectedKeys.Add(_alarm.CacheKey);
            for (int index = 0; index < _voices.Count; index++) SelectedKeys.Add(_voices[index].CacheKey);
            if (_soundtrack.IsValid) SelectedKeys.Add(_soundtrack.CacheKey);

            // Nothing selected and nothing the running takeover holds keeps its clip: the cache
            // never outgrows one selection plus one captured takeover.
            Scratch.Clear();
            foreach (KeyValuePair<string, Entry> pair in Entries)
                if (!SelectedKeys.Contains(pair.Key) && !Pinned.Contains(pair.Key)) Scratch.Add(pair.Value);
            for (int index = 0; index < Scratch.Count; index++)
            {
                Entry entry = Scratch[index];
                Cancel(entry, "no longer selected");
                if (entry.Clip != null) UnityEngine.Object.Destroy(entry.Clip);
                entry.Clip = null;
                Entries.Remove(entry.File.CacheKey);
            }
            Scratch.Clear();
        }

        private static AudioClip Take(TakeoverAudioOverrideFile file, string fallback)
        {
            AudioClip clip = Resolve(file);
            if (clip != null)
            {
                Pinned.Add(file.CacheKey);
                return clip;
            }
            if (file.IsValid && fallback != null) Warn(DescribeUnready(file) + "; " + fallback + ".");
            return null;
        }

        private static string DescribeUnready(TakeoverAudioOverrideFile file)
        {
            if (!Entries.TryGetValue(file.CacheKey, out Entry entry))
                return $"{file.Label} was never loaded";
            switch (entry.State)
            {
                case TakeoverAudioLoadState.Loading:
                    return $"{file.Label} was still loading {Time.realtimeSinceStartup - entry.StartedAt:F1}s after it started "
                        + "when the takeover's audio preparation window closed (it keeps loading for the next takeover)";
                case TakeoverAudioLoadState.Failed:
                    return $"{file.Label} failed to load ({entry.Error})";
                case TakeoverAudioLoadState.Cancelled:
                    return $"{file.Label}'s load was interrupted ({entry.Error})";
                default:
                    return $"{file.Label} is not loaded";
            }
        }

        private static string FallbackFor(string setting)
        {
            switch (setting)
            {
                case "AlarmFile": return "takeovers play the built-in siren";
                case "VoiceFiles": return "takeovers play the other voice files, or the built-in voices when none is ready,";
                case "Soundtrack": return "takeovers play no soundtrack";
                default: return "takeovers play without it";
            }
        }

        private static string PayloadKey(QuotaTakeoverPayload payload) =>
            payload.AlarmAudioFile + "|" + payload.AlarmAudioHash + "|" + payload.MumbleAudioFiles + "|" + payload.MumbleAudioHashes;

        private static string SoundtrackKey(TakeoverSoundtrackPlanWire wire) =>
            ((int)wire.Mode).ToString() + "|" + wire.Source + "|" + wire.Hash;

        private static void Warn(string message)
        {
            TakeoverBootstrap.Log?.LogWarning("[TakeoverAudioOverrides] " + message);
        }

        private static AudioType AudioTypeFor(string path)
        {
            string extension = (System.IO.Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".mp3": return AudioType.MPEG;
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                default: return AudioType.UNKNOWN;
            }
        }
    }

    /// <summary>
    /// #861: runs the takeover audio loads started through one GameObject and ends them with
    /// it. OnDisable runs when this component is disabled, when its GameObject is deactivated,
    /// and before either is destroyed; Unity would stop the coroutines at the last two without
    /// resuming them, so OnDisable stops them itself and aborts and disposes their requests.
    /// No load outlives an active, enabled loader. Living on its own component also keeps the
    /// loads clear of the owner's own StopAllCoroutines.
    /// </summary>
    internal sealed class TakeoverAudioLoader : MonoBehaviour
    {
        /// <summary>The loader on <paramref name="host"/>'s GameObject, added on first use.</summary>
        internal static TakeoverAudioLoader For(MonoBehaviour host)
        {
            TakeoverAudioLoader loader = host.GetComponent<TakeoverAudioLoader>();
            return loader != null ? loader : host.gameObject.AddComponent<TakeoverAudioLoader>();
        }

        private void OnDisable()
        {
            TakeoverAudioOverrides.CancelLoadsOwnedBy(this);
        }
    }
}
#endif
