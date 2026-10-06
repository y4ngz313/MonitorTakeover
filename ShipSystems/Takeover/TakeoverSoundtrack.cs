// TakeoverSoundtrack.cs - LGUMonitorTakeover
//
// #715: resolves the host's published soundtrack decision into something the
// quota takeover can try to play. Modelled on TakeoverAudioOverrides: TakeoverAudioPolicy
// owns the sandboxing and the SHA-256 host/client verification, and a local file is decoded
// by TakeoverAudioOverrides' one cache; this only turns a verified, decoded clip or a cached
// link into a plan.
//
// #861: a plan is not proof of playback. TakeoverManager confirms that the planned source
// actually started (a local clip playing, a VideoPlayer prepared with an enabled audio track)
// and drops a failed Replace back to the built-in bed.
//
// Quota takeovers only, like every other loose-file override: a takeover without a
// payload never reads a plan.

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
        /// <summary>The captured clip for <see cref="TakeoverSoundtrackKind.LocalClip"/>.</summary>
        internal readonly AudioClip Clip;

        internal TakeoverSoundtrackPlan(
            TakeoverSoundtrackMode mode, TakeoverSoundtrackKind kind, string resolvedPath, float volume, AudioClip clip)
        {
            Mode = mode;
            Kind = kind;
            ResolvedPath = resolvedPath ?? string.Empty;
            Volume = volume;
            Clip = clip;
        }

        /// <summary>
        /// True while Replace was asked for AND something is going to play or is still being
        /// confirmed. The manager drops the plan to <c>default</c> the moment playback fails, so
        /// a Replace whose source does not play never keeps the bed silent — the "never a
        /// silent takeover" rule. A volume of 0 is still a playing soundtrack.
        /// </summary>
        internal bool Silences => Mode == TakeoverSoundtrackMode.Replace && Kind != TakeoverSoundtrackKind.None;

        internal bool Active => Kind != TakeoverSoundtrackKind.None;
    }

    internal static class TakeoverSoundtrack
    {
        /// <summary>
        /// #861: runs when the host publishes the plan and when a client receives it. A local
        /// file is verified against the host's hash here and starts decoding at once; a link is
        /// decoded by a VideoPlayer at playback time, so all this can do for one is make sure
        /// the download has started.
        /// </summary>
        internal static void Prepare(MonoBehaviour owner, TakeoverSoundtrackPlanWire wire)
        {
            if (owner == null) return;
            if (wire.IsOff || YoutubeVideoResolver.IsYoutubeUrl(wire.Source))
            {
                if (!wire.IsOff) YoutubeVideoResolver.Prefetch(wire.Source);
                TakeoverAudioOverrides.SelectSoundtrack(owner, wire, default);
                return;
            }

            TakeoverAudioFileCheck check = TakeoverAudioPolicy.VerifyPublishedFile(
                QuotaProgressionRegistry.MediaDirectory, "Soundtrack", wire.Source, wire.Hash);
            if (!check.Accepted)
                TakeoverBootstrap.Log?.LogWarning("[TakeoverSoundtrack] " + check.Describe("no soundtrack plays on this player"));
            TakeoverAudioOverrides.SelectSoundtrack(owner, wire, TakeoverAudioOverrideFile.From(check));
        }

        /// <summary>
        /// Resolved once, at the head of TakeoverSequence and BEFORE SetupVideo, because
        /// SetupVideoForUrl needs to know whether the picture player's audio track is the
        /// soundtrack, is muted by it, or is unaffected. <paramref name="localClip"/> is the
        /// takeover's captured local soundtrack clip, null when none was ready; the capture has
        /// already said why.
        /// </summary>
        internal static TakeoverSoundtrackPlan ResolvePlan(
            TakeoverSoundtrackPlanWire wire, string configuredMediaFile, AudioClip localClip)
        {
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
                        wire.Mode, TakeoverSoundtrackKind.ReuseVideoTrack, string.Empty, wire.Volume, null);
                }

                if (YoutubeVideoResolver.TryGetCachedVideo(wire.Source, out string cached))
                    return new TakeoverSoundtrackPlan(wire.Mode, TakeoverSoundtrackKind.YoutubeCached, cached, wire.Volume, null);

                YoutubeVideoResolver.Prefetch(wire.Source);
                TakeoverBootstrap.Log?.LogWarning(
                    "[TakeoverSoundtrack] The configured soundtrack link is not cached yet; this takeover plays no soundtrack.");
                return default;
            }

            return localClip != null
                ? new TakeoverSoundtrackPlan(wire.Mode, TakeoverSoundtrackKind.LocalClip, string.Empty, wire.Volume, localClip)
                : default;
        }
    }
}
#endif
