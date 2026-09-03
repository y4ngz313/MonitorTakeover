// TakeoverSoundtrack.cs - LGUMonitorTakeover
//
// #715: resolves the host's published soundtrack decision into something the
// quota takeover can actually play. Modelled on TakeoverAudioOverrides: the
// registry owns the sandboxing and the SHA-256 host/client verification, this
// only turns a verified path or a cached link into a playable plan.
//
// Deliberately quota-only, like every other loose-file override: the Mask Man
// sequence never reads a payload and therefore never reads a plan.

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>How the resolved soundtrack is going to be produced.</summary>
    internal enum TakeoverSoundtrackKind
    {
        /// <summary>Nothing plays; the built-in bed is untouched.</summary>
        None,
        /// <summary>A decoded local mp3/wav/ogg played through a plain AudioSource.</summary>
        LocalClip,
        /// <summary>A cached YouTube mp4 whose audio track is driven by a hidden VideoPlayer.</summary>
        YoutubeCached,
        /// <summary>
        /// The soundtrack link and the takeover's media link are the same video, so the picture
        /// player's own audio track is bound to the soundtrack source instead of decoding the
        /// same file twice.
        /// </summary>
        ReuseVideoTrack
    }

    internal readonly struct TakeoverSoundtrackPlan
    {
        internal readonly TakeoverSoundtrackMode Mode;
        internal readonly TakeoverSoundtrackKind Kind;
        internal readonly string ResolvedPath;
        internal readonly float Volume;

        internal TakeoverSoundtrackPlan(TakeoverSoundtrackMode mode, TakeoverSoundtrackKind kind, string resolvedPath, float volume)
        {
            Mode = mode;
            Kind = kind;
            ResolvedPath = resolvedPath ?? string.Empty;
            Volume = volume;
        }

        /// <summary>
        /// True only when Replace was asked for AND something is actually going to play. A
        /// Replace whose source failed to resolve must not silence the bed — that is the
        /// "never a silent takeover" rule.
        /// </summary>
        internal bool Silences => Mode == TakeoverSoundtrackMode.Replace && Kind != TakeoverSoundtrackKind.None;

        internal bool Active => Kind != TakeoverSoundtrackKind.None;
    }

    internal static class TakeoverSoundtrack
    {
        private static string _signature = string.Empty;
        private static TakeoverAudioOverrideFile _file;

        /// <summary>
        /// Idempotent. Called from BeginTakeover beside <see cref="TakeoverAudioOverrides.Prepare"/>
        /// so the orbit delay is spent decoding rather than waiting.
        /// </summary>
        internal static void Prepare(MonoBehaviour host)
        {
            if (host == null) return;
            TakeoverSoundtrackPlanWire wire = QuotaProgressionRegistry.GetSoundtrackPlan();
            if (wire.IsOff)
            {
                _signature = string.Empty;
                _file = default;
                return;
            }

            if (YoutubeVideoResolver.IsYoutubeUrl(wire.Source))
            {
                // A link is decoded by a VideoPlayer at playback time, not here; all this can
                // usefully do is make sure the download has started.
                _signature = string.Empty;
                _file = default;
                YoutubeVideoResolver.Prefetch(wire.Source);
                return;
            }

            string signature = wire.Source + "|" + wire.Hash;
            if (!string.Equals(signature, _signature, System.StringComparison.Ordinal))
            {
                _signature = signature;
                _file = QuotaProgressionRegistry.TryResolveAudioFile(wire.Source, wire.Hash, out string path)
                    ? new TakeoverAudioOverrideFile(path, wire.Hash)
                    : default;
            }

            TakeoverAudioOverrides.Request(host, _file);
        }

        /// <summary>Null until the local file has finished decoding, or when none is configured.</summary>
        internal static AudioClip GetClip() => TakeoverAudioOverrides.Resolve(_file);

        /// <summary>
        /// Resolved once, at the head of TakeoverSequence and BEFORE SetupVideo, because
        /// SetupVideoForUrl needs to know whether the picture player's audio track is the
        /// soundtrack, is muted by it, or is unaffected.
        /// </summary>
        internal static TakeoverSoundtrackPlan ResolvePlan(string configuredMediaFile)
        {
            TakeoverSoundtrackPlanWire wire = QuotaProgressionRegistry.GetSoundtrackPlan();
            if (wire.IsOff) return default;

            if (YoutubeVideoResolver.IsYoutubeUrl(wire.Source))
            {
                if (YoutubeVideoResolver.TryNormalizeYoutubeUrl(wire.Source, out string soundtrackUrl)
                    && YoutubeVideoResolver.TryNormalizeYoutubeUrl(configuredMediaFile, out string mediaUrl)
                    && string.Equals(soundtrackUrl, mediaUrl, System.StringComparison.Ordinal))
                {
                    // Same video on both settings: one decoder, and the picture player's own
                    // track becomes the soundtrack rather than a second copy of it.
                    return new TakeoverSoundtrackPlan(
                        wire.Mode, TakeoverSoundtrackKind.ReuseVideoTrack, string.Empty, wire.Volume);
                }

                if (YoutubeVideoResolver.TryGetCachedVideo(wire.Source, out string cached))
                    return new TakeoverSoundtrackPlan(wire.Mode, TakeoverSoundtrackKind.YoutubeCached, cached, wire.Volume);

                YoutubeVideoResolver.Prefetch(wire.Source);
                TakeoverBootstrap.Log?.LogWarning(
                    "[TakeoverSoundtrack] The configured soundtrack link is not cached yet; this takeover uses the built-in audio.");
                return default;
            }

            // Mirrors PlayAlarm's half-loaded rule: a clip that has not finished decoding falls
            // back for this takeover rather than delaying the sequence, and is cached for the next.
            AudioClip clip = GetClip();
            if (clip != null)
                return new TakeoverSoundtrackPlan(
                    wire.Mode, TakeoverSoundtrackKind.LocalClip, _file.Path, wire.Volume);

            TakeoverBootstrap.Log?.LogWarning(
                $"[TakeoverSoundtrack] Soundtrack '{wire.Source}' is missing, does not match the host copy, or has not decoded yet; this takeover uses the built-in audio.");
            return default;
        }
    }
}
#endif
