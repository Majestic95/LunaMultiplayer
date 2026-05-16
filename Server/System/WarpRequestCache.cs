using System;
using System.Collections.Concurrent;

namespace Server.System
{
    /// <summary>
    /// Dedupes NewSubspace requests on (PlayerCreator, RequestSeq). Without this, a stuck client
    /// retrying its subspace-creation request mints a fresh subspace every retry — see
    /// docs/research/02-analysis/bug-051-stuck-warp-limbo.md.
    ///
    /// Entries TTL out after 60s. Sentinel sequence 0 ("pre-fix client") is never cached and
    /// always falls through to the legacy always-mint path.
    /// </summary>
    public static class WarpRequestCache
    {
        public static TimeSpan EntryTtl = TimeSpan.FromSeconds(60);

        private static readonly ConcurrentDictionary<(string Player, uint Seq), CacheEntry> Entries =
            new ConcurrentDictionary<(string, uint), CacheEntry>();

        public static bool TryGet(string player, uint seq, out int subspaceId, out double serverTimeDifference)
        {
            subspaceId = 0;
            serverTimeDifference = 0d;

            if (seq == 0u || string.IsNullOrEmpty(player))
                return false;

            if (!Entries.TryGetValue((player, seq), out var entry))
                return false;

            if (DateTime.UtcNow >= entry.ExpiresAtUtc)
            {
                Entries.TryRemove((player, seq), out _);
                return false;
            }

            subspaceId = entry.SubspaceId;
            serverTimeDifference = entry.ServerTimeDifference;
            return true;
        }

        public static void Add(string player, uint seq, int subspaceId, double serverTimeDifference)
        {
            if (seq == 0u || string.IsNullOrEmpty(player))
                return;

            Entries[(player, seq)] = new CacheEntry(subspaceId, serverTimeDifference, DateTime.UtcNow + EntryTtl);
        }

        public static int Count => Entries.Count;

        public static void Clear() => Entries.Clear();

        private readonly struct CacheEntry
        {
            public readonly int SubspaceId;
            public readonly double ServerTimeDifference;
            public readonly DateTime ExpiresAtUtc;

            public CacheEntry(int subspaceId, double serverTimeDifference, DateTime expiresAtUtc)
            {
                SubspaceId = subspaceId;
                ServerTimeDifference = serverTimeDifference;
                ExpiresAtUtc = expiresAtUtc;
            }
        }
    }
}
