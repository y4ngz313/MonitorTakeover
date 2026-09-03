// TakeoverMediaPlanResolver.cs — #662. The peer-side half of the per-monitor
// media plan: turning the host's published pool into playable local paths on
// THIS machine.
//
// It lives beside TakeoverMediaPlan.cs rather than inside it because that file
// is compiled standalone by tests/PerMonitorMediaAssignmentRegression and must
// stay free of UnityEngine and of the config registry. The arithmetic is
// provable in isolation; file resolution is not.

using System;
using System.Collections.Generic;
using System.Linq;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// One takeover's per-monitor media decision, resolved for this peer.
    ///
    /// <para><see cref="Sources"/> holds local paths this machine can actually play. It is not
    /// the host's pool: a peer missing one file drops that entry and shows the rest, because a
    /// takeover whose timeline depends on every peer owning every file would desync the moment
    /// one of them did not. Content therefore diverges between peers by design; the layout
    /// (which surface takes which slot) does not, because it is derived from the seed.</para>
    /// </summary>
    internal readonly struct TakeoverMediaPlan
    {
        internal readonly int Seed;
        internal readonly int MaxPlayers;
        internal readonly IReadOnlyList<string> Sources;

        private TakeoverMediaPlan(int seed, int maxPlayers, IReadOnlyList<string> sources)
        {
            Seed = seed;
            MaxPlayers = maxPlayers;
            Sources = sources ?? Array.Empty<string>();
        }

        /// <summary>
        /// True only when this peer has at least two distinct sources to show. One source is the
        /// ordinary takeover and must run down the untouched single-player path, so that the
        /// feature being on is never, by itself, a behaviour change.
        /// </summary>
        internal bool Active => Sources != null && Sources.Count > 1;

        /// <summary>Number of sources beyond the primary, i.e. how many extra players to open.</summary>
        internal int ExtraSourceCount => Active ? Sources.Count - 1 : 0;

        /// <summary>
        /// Resolves the host's published plan against this machine. Returns an inactive plan
        /// whenever the toggle is off, the host published nothing, or fewer than two entries
        /// survive resolution — every one of which means "run the single-source takeover".
        /// </summary>
        internal static TakeoverMediaPlan Resolve()
        {
            TakeoverMediaPlanWire wire = QuotaProgressionRegistry.GetMediaPlan();
            if (!wire.HasMultipleSources) return default;

            var sources = new List<string>();
            var dropped = new List<string>();
            for (int index = 0; index < wire.Pool.Length; index++)
            {
                string entry = wire.Pool[index];
                string hash = wire.Hashes != null && index < wire.Hashes.Length
                    ? wire.Hashes[index]
                    : string.Empty;
                if (QuotaProgressionRegistry.TryResolveMediaFile(entry, hash, out string path)
                    && !sources.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    sources.Add(path);
                    continue;
                }
                dropped.Add(entry);
            }

            // One aggregate line, not one per entry: a peer missing a whole modpack's worth of
            // pool files would otherwise turn the log into the failure.
            if (dropped.Count > 0)
            {
                TakeoverBootstrap.Log.LogWarning(
                    $"[TakeoverManager] Per-monitor media: {dropped.Count} of {wire.Pool.Length} pool "
                    + "entries are missing on this player, do not match the host copy, or are not cached "
                    + $"yet ({string.Join(", ", dropped.ToArray())}); this player shows the {sources.Count} "
                    + "source(s) it has.");
            }

            if (sources.Count < 2) return default;
            return new TakeoverMediaPlan(wire.Seed, wire.MaxPlayers, sources);
        }
    }
}
