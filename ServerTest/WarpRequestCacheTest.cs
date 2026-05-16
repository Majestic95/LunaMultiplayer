using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerTest
{
    [TestClass]
    public class WarpRequestCacheTest
    {
        private TimeSpan _originalTtl;

        [TestInitialize]
        public void Setup()
        {
            _originalTtl = WarpRequestCache.EntryTtl;
            WarpRequestCache.Clear();
        }

        [TestCleanup]
        public void Teardown()
        {
            WarpRequestCache.EntryTtl = _originalTtl;
            WarpRequestCache.Clear();
        }

        [TestMethod]
        public void Add_ThenTryGet_ReturnsCachedSubspaceAndTimeDiff()
        {
            WarpRequestCache.Add("alice", seq: 42u, subspaceId: 7, serverTimeDifference: -123.4d);

            var hit = WarpRequestCache.TryGet("alice", 42u, out var subspaceId, out var serverTimeDiff);

            Assert.IsTrue(hit);
            Assert.AreEqual(7, subspaceId);
            Assert.AreEqual(-123.4d, serverTimeDiff);
        }

        [TestMethod]
        public void TryGet_UnknownPlayerSeqPair_ReturnsFalse()
        {
            WarpRequestCache.Add("alice", seq: 42u, subspaceId: 7, serverTimeDifference: 0d);

            var hit = WarpRequestCache.TryGet("alice", 99u, out var subspaceId, out _);

            Assert.IsFalse(hit);
            Assert.AreEqual(0, subspaceId);
        }

        [TestMethod]
        public void TryGet_DifferentPlayerSameSeq_ReturnsFalse()
        {
            WarpRequestCache.Add("alice", seq: 42u, subspaceId: 7, serverTimeDifference: 0d);

            var hit = WarpRequestCache.TryGet("bob", 42u, out _, out _);

            Assert.IsFalse(hit);
        }

        [TestMethod]
        public void TryGet_SequenceZero_AlwaysReturnsFalse()
        {
            // seq=0 is the sentinel "pre-fix client / do not dedupe". Even if something
            // managed to insert it (Add ignores seq=0), TryGet must never report a hit.
            var hit = WarpRequestCache.TryGet("alice", 0u, out _, out _);
            Assert.IsFalse(hit);
        }

        [TestMethod]
        public void Add_SequenceZero_IsIgnored()
        {
            WarpRequestCache.Add("alice", seq: 0u, subspaceId: 7, serverTimeDifference: 0d);

            Assert.AreEqual(0, WarpRequestCache.Count);
        }

        [TestMethod]
        public void Add_NullOrEmptyPlayer_IsIgnored()
        {
            WarpRequestCache.Add(null, seq: 1u, subspaceId: 7, serverTimeDifference: 0d);
            WarpRequestCache.Add(string.Empty, seq: 2u, subspaceId: 7, serverTimeDifference: 0d);

            Assert.AreEqual(0, WarpRequestCache.Count);
        }

        [TestMethod]
        public void Add_OverwritesPreviousEntry()
        {
            WarpRequestCache.Add("alice", seq: 42u, subspaceId: 7, serverTimeDifference: 1d);
            WarpRequestCache.Add("alice", seq: 42u, subspaceId: 11, serverTimeDifference: 2d);

            Assert.IsTrue(WarpRequestCache.TryGet("alice", 42u, out var subspaceId, out var serverTimeDiff));
            Assert.AreEqual(11, subspaceId);
            Assert.AreEqual(2d, serverTimeDiff);
        }

        [TestMethod]
        public void TryGet_AfterTtlElapses_ReturnsFalseAndEvicts()
        {
            WarpRequestCache.EntryTtl = TimeSpan.FromMilliseconds(50);
            WarpRequestCache.Add("alice", seq: 42u, subspaceId: 7, serverTimeDifference: 0d);

            Assert.AreEqual(1, WarpRequestCache.Count);

            Thread.Sleep(120);

            var hit = WarpRequestCache.TryGet("alice", 42u, out _, out _);

            Assert.IsFalse(hit);
            Assert.AreEqual(0, WarpRequestCache.Count, "Expired entry should be evicted on TryGet");
        }

        [TestMethod]
        public void Clear_EmptiesCache()
        {
            for (var i = 1u; i <= 10u; i++)
                WarpRequestCache.Add("alice", i, (int)i, 0d);

            Assert.AreEqual(10, WarpRequestCache.Count);

            WarpRequestCache.Clear();

            Assert.AreEqual(0, WarpRequestCache.Count);
        }

        [TestMethod]
        public void ConcurrentAdds_SameKey_LeaveExactlyOneEntry()
        {
            // The cache's correctness under contention is what makes it safe for the
            // receiver lock to be the single mint serialization point. If two threads
            // race on Add for the same (player, seq), exactly one final value wins and
            // the count stays at 1.
            const int threads = 16;
            const int perThread = 1000;

            Parallel.For(0, threads, t =>
            {
                for (var i = 0; i < perThread; i++)
                    WarpRequestCache.Add("alice", seq: 42u, subspaceId: t, serverTimeDifference: t * 0.1d);
            });

            Assert.AreEqual(1, WarpRequestCache.Count);
            Assert.IsTrue(WarpRequestCache.TryGet("alice", 42u, out var subspaceId, out _));
            Assert.IsTrue(subspaceId >= 0 && subspaceId < threads,
                $"Cached subspaceId {subspaceId} must have come from one of the {threads} concurrent writers");
        }

        [TestMethod]
        public void ConcurrentAdds_DifferentKeys_AllPersist()
        {
            const int threads = 8;
            const int perThread = 200;

            Parallel.For(0, threads, t =>
            {
                for (var i = 0; i < perThread; i++)
                    WarpRequestCache.Add($"player-{t}", seq: (uint)(i + 1), subspaceId: i, serverTimeDifference: 0d);
            });

            Assert.AreEqual(threads * perThread, WarpRequestCache.Count);
        }
    }
}
