// TakeoverMediaPlan.cs — #662. The deterministic half of per-monitor takeover
// media: how the claimed surfaces are ordered, how the host's seed becomes a
// shuffle every peer reproduces bit-for-bit, and how surfaces are dealt to the
// media players under the concurrency cap.
//
// This file is deliberately FREE of UnityEngine types. The whole point of the
// arithmetic here is that host and clients agree, and that is only provable by
// executing it — which tests/PerMonitorMediaAssignmentRegression does, by
// compiling this exact source standalone. Anything needing a Texture, a
// VideoPlayer or UnityEngine.Random belongs in TakeoverManager, not here.

using System;
using System.Collections.Generic;

namespace Y4NGZCompany.ShipSystems.Takeover
{
    /// <summary>
    /// Identity of one assignable takeover surface. The hierarchy path plus the
    /// material slot is stable across peers and across a scene reload, whereas
    /// traversal order is not — two peers can enumerate the same wall in
    /// different orders, and that would hand them different videos.
    /// </summary>
    internal readonly struct TakeoverMediaSurfaceKey : IEquatable<TakeoverMediaSurfaceKey>
    {
        /// <summary>Hierarchy path of the renderer, or "overlay:&lt;path&gt;" for a canvas overlay.</summary>
        internal string Path { get; }

        /// <summary>Material slot on the renderer; -1 for an overlay, which has no slot.</summary>
        internal int MaterialIndex { get; }

        internal TakeoverMediaSurfaceKey(string path, int materialIndex)
        {
            Path = path ?? string.Empty;
            MaterialIndex = materialIndex;
        }

        public bool Equals(TakeoverMediaSurfaceKey other) =>
            MaterialIndex == other.MaterialIndex &&
            string.Equals(Path, other.Path, StringComparison.Ordinal);

        public override bool Equals(object obj) =>
            obj is TakeoverMediaSurfaceKey other && Equals(other);

        public override int GetHashCode() =>
            unchecked((Path == null ? 0 : StringComparer.Ordinal.GetHashCode(Path)) * 397
                ^ MaterialIndex);

        public override string ToString() => $"{Path}#{MaterialIndex}";
    }

    internal static class TakeoverMediaAssignment
    {
        /// <summary>
        /// Orders surfaces by ordinal path then material slot. Ordinal, never
        /// culture-aware: a peer with a Turkish locale must produce the same
        /// order as everyone else.
        /// </summary>
        internal static void SortDeterministically(List<TakeoverMediaSurfaceKey> keys)
        {
            if (keys == null || keys.Count < 2) return;
            keys.Sort(CompareKeys);
        }

        internal static int CompareKeys(TakeoverMediaSurfaceKey a, TakeoverMediaSurfaceKey b)
        {
            int path = string.CompareOrdinal(a.Path ?? string.Empty, b.Path ?? string.Empty);
            return path != 0 ? path : a.MaterialIndex.CompareTo(b.MaterialIndex);
        }

        /// <summary>
        /// A Fisher-Yates shuffle of 0..count-1 driven by an own xorshift32, so
        /// the result depends on nothing but the seed. UnityEngine.Random is
        /// explicitly not used: it is global state every other system on the
        /// peer is also drawing from, so it would differ per machine.
        /// </summary>
        internal static int[] BuildPermutation(int seed, int count)
        {
            if (count <= 0) return Array.Empty<int>();

            var result = new int[count];
            for (int i = 0; i < count; i++) result[i] = i;
            if (count == 1) return result;

            uint state = unchecked((uint)seed);
            // xorshift32 is degenerate at zero; any non-zero constant works.
            if (state == 0u) state = 0x9E3779B9u;

            for (int i = count - 1; i > 0; i--)
            {
                state = NextState(state);
                int j = (int)(state % (uint)(i + 1));
                int swap = result[i];
                result[i] = result[j];
                result[j] = swap;
            }
            return result;
        }

        private static uint NextState(uint state)
        {
            unchecked
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
            }
            return state;
        }

        /// <summary>
        /// Round-robin over the permutation, so the first
        /// <paramref name="playerCount"/> surfaces in sorted order use every
        /// player exactly once before any player repeats. Out-of-range or absent
        /// inputs fall back to player 0, which is the primary — the takeover
        /// never leaves a surface without a source.
        /// </summary>
        internal static int PlayerForSurface(int surfaceIndex, int playerCount, int[] permutation)
        {
            if (playerCount <= 1 || surfaceIndex < 0) return 0;
            int slot = surfaceIndex % playerCount;
            if (permutation == null || slot >= permutation.Length) return slot % playerCount;
            int player = permutation[slot];
            return player >= 0 && player < playerCount ? player : slot % playerCount;
        }

        /// <summary>
        /// How many media players a takeover actually opens: never more decoders
        /// than the configured cap, never more than there are distinct sources,
        /// never more than there are surfaces to show them on — and never fewer
        /// than the one primary player the takeover always has.
        /// </summary>
        internal static int ClampPlayerCount(int cap, int poolCount, int surfaceCount)
        {
            int value = cap;
            if (poolCount < value) value = poolCount;
            if (surfaceCount < value) value = surfaceCount;
            return value < 1 ? 1 : value;
        }
    }
}
