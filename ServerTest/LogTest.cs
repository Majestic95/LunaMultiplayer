using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Log;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerTest
{
    [TestClass]
    public class LogEntryParseTest
    {
        [TestMethod]
        public void Parse_StandardLine_ExtractsLevelAndMessage()
        {
            var ts = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);
            var entry = LogEntry.Parse("[12:34:56][Info]: hello world", ts);

            Assert.AreEqual(ts, entry.TimestampUtc);
            Assert.AreEqual("Info", entry.Level);
            Assert.AreEqual(string.Empty, entry.Subsystem);
            Assert.AreEqual("hello world", entry.Message);
            Assert.AreEqual("[12:34:56][Info]: hello world", entry.Formatted);
        }

        [TestMethod]
        public void Parse_WithSubsystemPrefix_ExtractsSubsystem()
        {
            var entry = LogEntry.Parse("[09:00:00][Warning]: [BackupSystem]: archive complete", DateTime.UtcNow);

            Assert.AreEqual("Warning", entry.Level);
            Assert.AreEqual("BackupSystem", entry.Subsystem);
            Assert.AreEqual("archive complete", entry.Message);
        }

        [TestMethod]
        public void Parse_NormalLevelFromLunaLogNormal_GivesLmpType()
        {
            // BaseLogger.Normal() emits "[LMP]:" (not "[Normal]:") — this is the
            // existing convention and must not regress.
            var entry = LogEntry.Parse("[09:00:00][LMP]: server started", DateTime.UtcNow);

            Assert.AreEqual("LMP", entry.Level);
            Assert.AreEqual("server started", entry.Message);
        }

        [TestMethod]
        public void Parse_SubsystemPrefixWithoutColon_StillExtracts()
        {
            // Some legacy call sites format as "[Subsystem] message" without the colon.
            var entry = LogEntry.Parse("[09:00:00][Info]: [Vessel] update applied", DateTime.UtcNow);

            Assert.AreEqual("Info", entry.Level);
            Assert.AreEqual("Vessel", entry.Subsystem);
            Assert.AreEqual("update applied", entry.Message);
        }

        [TestMethod]
        public void Parse_NoSubsystem_LeavesSubsystemEmpty()
        {
            var entry = LogEntry.Parse("[09:00:00][Info]: just a message", DateTime.UtcNow);

            Assert.AreEqual(string.Empty, entry.Subsystem);
            Assert.AreEqual("just a message", entry.Message);
        }

        [TestMethod]
        public void Parse_EmptyLine_ReturnsEmptyEntry()
        {
            var entry = LogEntry.Parse(string.Empty, DateTime.UtcNow);

            Assert.AreEqual(string.Empty, entry.Level);
            Assert.AreEqual(string.Empty, entry.Subsystem);
            Assert.AreEqual(string.Empty, entry.Message);
        }

        [TestMethod]
        public void Parse_MalformedLine_DoesNotThrow()
        {
            // A garbage line should still produce a usable (mostly-empty) entry.
            var entry = LogEntry.Parse("garbage with no brackets at all", DateTime.UtcNow);

            Assert.AreEqual(string.Empty, entry.Level);
            Assert.AreEqual(string.Empty, entry.Subsystem);
            // The entire input becomes the message body since no [Type] tag was found.
            Assert.AreEqual("garbage with no brackets at all", entry.Message);
        }
    }

    [TestClass]
    public class LogRingBufferTest
    {
        [TestInitialize]
        public void Setup() => LogRingBuffer.Clear();

        [TestCleanup]
        public void Teardown() => LogRingBuffer.Clear();

        private static LogEntry MakeEntry(int i) =>
            new LogEntry(new DateTime(2026, 1, 1).AddSeconds(i), "Info", string.Empty, $"msg-{i}", $"raw-{i}");

        [TestMethod]
        public void Add_BelowCapacity_PreservesAllEntriesInOrder()
        {
            for (var i = 0; i < 5; i++)
                LogRingBuffer.Add(MakeEntry(i));

            var snapshot = LogRingBuffer.Snapshot();
            Assert.AreEqual(5, snapshot.Length);
            for (var i = 0; i < 5; i++)
                Assert.AreEqual($"msg-{i}", snapshot[i].Message);
        }

        [TestMethod]
        public void Add_AboveCapacity_DropsOldestKeepsNewest()
        {
            var n = LogRingBuffer.Capacity + 100;
            for (var i = 0; i < n; i++)
                LogRingBuffer.Add(MakeEntry(i));

            var snapshot = LogRingBuffer.Snapshot();
            Assert.AreEqual(LogRingBuffer.Capacity, snapshot.Length);
            // First retained entry is index 100, last is n-1.
            Assert.AreEqual("msg-100", snapshot[0].Message);
            Assert.AreEqual($"msg-{n - 1}", snapshot[snapshot.Length - 1].Message);
        }

        [TestMethod]
        public void Snapshot_EmptyBuffer_ReturnsEmptyArray()
        {
            var snapshot = LogRingBuffer.Snapshot();
            Assert.AreEqual(0, snapshot.Length);
        }

        [TestMethod]
        public void Clear_EmptiesBuffer()
        {
            for (var i = 0; i < 10; i++)
                LogRingBuffer.Add(MakeEntry(i));

            LogRingBuffer.Clear();

            Assert.AreEqual(0, LogRingBuffer.Count);
            Assert.AreEqual(0, LogRingBuffer.Snapshot().Length);
        }

        [TestMethod]
        public void Add_NullEntry_IsIgnoredAndDoesNotThrow()
        {
            LogRingBuffer.Add(null);
            Assert.AreEqual(0, LogRingBuffer.Count);
        }

        [TestMethod]
        public void Add_FromMultipleThreads_NoExceptionsAndCountIsBounded()
        {
            const int threads = 8;
            const int perThread = 5000;

            Parallel.For(0, threads, t =>
            {
                for (var i = 0; i < perThread; i++)
                    LogRingBuffer.Add(MakeEntry(t * perThread + i));
            });

            // We don't assert specific content order across threads — only that
            // the buffer survived concurrent writes and stayed at <= capacity.
            Assert.IsTrue(LogRingBuffer.Count <= LogRingBuffer.Capacity);
            Assert.AreEqual(LogRingBuffer.Capacity, LogRingBuffer.Snapshot().Length);
        }
    }
}
