// TakeoverAudioOverrides.cs - LGUMonitorTakeover
//
// Session cache for the loose-file audio overrides players drop into
// BepInEx/config/Y4NGZCompany/MonitorTakeovers. The registry does the
// sandboxing and the SHA-256 host/client verification; this only turns the
// verified paths into AudioClips and hands them to the quota takeover.
//
// Deliberately quota-only: the Mask Man sequence keeps using the bundled clips.

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    internal static class TakeoverAudioOverrides
    {
        // Keyed by resolved path + verified hash, so a replaced file on disk
        // never serves the previous session's decoded clip.
        private static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        private static readonly HashSet<string> Loading = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.Ordinal);

        private static List<TakeoverAudioOverrideFile> _mumble = new List<TakeoverAudioOverrideFile>();
        private static TakeoverAudioOverrideFile _alarm;
        private static string _signature = string.Empty;

        // Idempotent. Called both when the takeover is requested and again after
        // the orbit delay, so a client whose payload arrived late still gets a
        // head start on decoding before the alarm and mumble loop need clips.
        internal static void Prepare(MonoBehaviour host, string[] builtInDialogue)
        {
            if (host == null) return;
            QuotaTakeoverPayload payload = QuotaProgressionRegistry.GetCurrentPayload(builtInDialogue);
            string signature = string.Join("|", new[]
            {
                payload.AlarmAudioFile, payload.AlarmAudioHash, payload.MumbleAudioFiles, payload.MumbleAudioHashes
            });
            if (!string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                // Resolution logs the one fallback warning, so only re-resolve
                // when the host actually published different overrides.
                _signature = signature;
                _alarm = QuotaProgressionRegistry.ResolveTakeoverAlarmAudio(payload);
                _mumble = QuotaProgressionRegistry.ResolveTakeoverMumbleAudio(payload);
            }

            Request(host, _alarm);
            for (int index = 0; index < _mumble.Count; index++) Request(host, _mumble[index]);
        }

        // Null when no alarm override is configured, verified or decoded yet —
        // callers fall back to the bundled alarm rather than stalling.
        internal static AudioClip GetAlarmClip() => Resolve(_alarm);

        // Empty when no mumble override is usable yet; callers fall back to the
        // bundled pool for the whole takeover.
        internal static AudioClip[] GetMumbleClips()
        {
            if (_mumble.Count == 0) return Array.Empty<AudioClip>();
            var clips = new List<AudioClip>(_mumble.Count);
            for (int index = 0; index < _mumble.Count; index++)
            {
                AudioClip clip = Resolve(_mumble[index]);
                if (clip != null) clips.Add(clip);
            }
            return clips.ToArray();
        }

        // internal since #715: the soundtrack override is a fourth consumer of this same
        // decoded-clip cache, and duplicating the loader for it would mean two caches that
        // can disagree about the same file on disk.
        internal static AudioClip Resolve(TakeoverAudioOverrideFile file)
        {
            if (!file.IsValid) return null;
            return Cache.TryGetValue(file.CacheKey, out AudioClip clip) ? clip : null;
        }

        internal static void Request(MonoBehaviour host, TakeoverAudioOverrideFile file)
        {
            if (!file.IsValid) return;
            string key = file.CacheKey;
            if (Cache.ContainsKey(key) || Failed.Contains(key) || Loading.Contains(key)) return;
            Loading.Add(key);
            host.StartCoroutine(LoadClip(file));
        }

        private static IEnumerator LoadClip(TakeoverAudioOverrideFile file)
        {
            string key = file.CacheKey;
            string name = Path.GetFileName(file.Path);
            AudioType audioType = AudioTypeFor(file.Path);
            if (audioType == AudioType.UNKNOWN)
            {
                Loading.Remove(key);
                Failed.Add(key);
                TakeoverBootstrap.Log?.LogWarning(
                    $"[TakeoverAudioOverrides] '{name}' has no supported audio format; using the built-in clip.");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(file.Path).AbsoluteUri, audioType))
            {
                yield return request.SendWebRequest();
                Loading.Remove(key);
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Failed.Add(key);
                    TakeoverBootstrap.Log?.LogWarning(
                        $"[TakeoverAudioOverrides] Failed loading '{name}': {request.error}; using the built-in clip.");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    Failed.Add(key);
                    TakeoverBootstrap.Log?.LogWarning(
                        $"[TakeoverAudioOverrides] '{name}' decoded to no clip; using the built-in clip.");
                    yield break;
                }

                clip.name = "Y4NGZ_TakeoverOverride_" + Path.GetFileNameWithoutExtension(file.Path);
                Cache[key] = clip;
                TakeoverBootstrap.Log?.LogInfo(
                    $"[TakeoverAudioOverrides] Loaded '{name}' ({clip.length:F2}s) for the quota takeover.");
            }
        }

        private static AudioType AudioTypeFor(string path)
        {
            string extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".mp3": return AudioType.MPEG;
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                default: return AudioType.UNKNOWN;
            }
        }
    }
}
#endif
