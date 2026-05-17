using LmpCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server;
using Server.Log;
using Server.Web.Formatting;
using Server.Web.Structures;
using System;
using System.Linq;

namespace ServerTest
{
    [TestClass]
    public class ForkInformationTest
    {
        [TestMethod]
        public void Constructor_PopulatesFromForkBuildInfoAndLmpVersioning()
        {
            var info = new ForkInformation();

            Assert.AreEqual(ForkBuildInfo.ForkName, info.ForkName);
            Assert.AreEqual(LmpVersioning.CurrentVersion.ToString(), info.ProtocolVersion);
            CollectionAssert.AreEqual(ForkBuildInfo.ActiveFixes, info.ActiveFixes);
        }

        [TestMethod]
        public void ActiveFixes_IncludesStage2Capstone()
        {
            // BUG-005/006 is the protocol-bump capstone — losing it from the
            // boot banner or dashboard would mean the server lied about what
            // it's running. Guard against an accidental edit to ForkBuildInfo.
            var info = new ForkInformation();
            Assert.IsTrue(info.ActiveFixes.Contains("BUG-005/006"),
                "ForkInformation.ActiveFixes must surface the protocol-bump capstone (BUG-005/006).");
        }

        [TestMethod]
        public void ProtocolVersion_IsNonEmpty()
        {
            // LmpVersioning.CurrentVersion is sourced from AssemblyVersion;
            // an empty string would mean the assembly metadata is broken.
            var info = new ForkInformation();
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.ProtocolVersion));
        }
    }

    [TestClass]
    public class LogSnapshotTest
    {
        [TestInitialize]
        public void Setup() => LogRingBuffer.Clear();

        [TestCleanup]
        public void Teardown() => LogRingBuffer.Clear();

        private static LogEntry MakeEntry(int i) =>
            new LogEntry(new DateTime(2026, 5, 16).AddSeconds(i), "Info", string.Empty, $"msg-{i}", $"raw-{i}");

        [TestMethod]
        public void Empty_HasZeroCountAndAdvertisesCapacity()
        {
            var snapshot = new LogSnapshot();

            Assert.AreEqual(0, snapshot.Count);
            Assert.AreEqual(0, snapshot.Entries.Length);
            Assert.AreEqual(LogRingBuffer.Capacity, snapshot.Capacity);
        }

        [TestMethod]
        public void ReflectsRingBufferContentsOldestFirst()
        {
            for (var i = 0; i < 5; i++)
                LogRingBuffer.Add(MakeEntry(i));

            var snapshot = new LogSnapshot();

            Assert.AreEqual(5, snapshot.Count);
            Assert.AreEqual(5, snapshot.Entries.Length);
            for (var i = 0; i < 5; i++)
                Assert.AreEqual($"msg-{i}", snapshot.Entries[i].Message);
        }

        [TestMethod]
        public void CapAt_RingBufferCapacity()
        {
            // Push past capacity and verify the snapshot mirrors the ring
            // buffer's drop-oldest behavior rather than ballooning.
            var n = LogRingBuffer.Capacity + 50;
            for (var i = 0; i < n; i++)
                LogRingBuffer.Add(MakeEntry(i));

            var snapshot = new LogSnapshot();

            Assert.AreEqual(LogRingBuffer.Capacity, snapshot.Count);
            Assert.AreEqual($"msg-{n - 1}", snapshot.Entries[snapshot.Entries.Length - 1].Message);
        }
    }

    [TestClass]
    public class LogTextFormatterTest
    {
        [TestInitialize]
        public void Setup() => LogRingBuffer.Clear();

        [TestCleanup]
        public void Teardown() => LogRingBuffer.Clear();

        [TestMethod]
        public void Format_EmptyBuffer_StillIncludesContextHeader()
        {
            var output = LogTextFormatter.Format();

            // Header lines should always be present so operators can identify
            // the running build even when the ring buffer happens to be empty.
            StringAssert.Contains(output, ForkBuildInfo.ForkName);
            StringAssert.Contains(output, LmpVersioning.CurrentVersion.ToString());
            foreach (var fix in ForkBuildInfo.ActiveFixes)
                StringAssert.Contains(output, fix);
            StringAssert.Contains(output, "0/" + LogRingBuffer.Capacity);
            StringAssert.Contains(output, "log ring buffer is empty");
        }

        [TestMethod]
        public void Format_WithEntries_PrefersFormattedLineForReadability()
        {
            // The Formatted string is exactly what operators see in the console
            // — preserve it verbatim rather than reconstructing from parts.
            var ts = new DateTime(2026, 5, 16, 12, 34, 56, DateTimeKind.Utc);
            LogRingBuffer.Add(new LogEntry(ts, "Info", string.Empty, "first hello", "[12:34:56][Info]: first hello"));
            LogRingBuffer.Add(new LogEntry(ts.AddSeconds(1), "Normal", "fix:BUG-005/006", "vessel 0x1234 auth=0",
                "[12:34:57][LMP]: [fix:BUG-005/006]: vessel 0x1234 auth=0"));

            var output = LogTextFormatter.Format();

            StringAssert.Contains(output, "[12:34:56][Info]: first hello");
            StringAssert.Contains(output, "[12:34:57][LMP]: [fix:BUG-005/006]: vessel 0x1234 auth=0");
            StringAssert.Contains(output, "2/" + LogRingBuffer.Capacity);
        }

        [TestMethod]
        public void Format_EntryWithoutFormatted_FallsBackToReconstruction()
        {
            // Defensive path: if a captured entry somehow has no Formatted
            // string (e.g., AfterPrint wasn't the source), build a readable
            // line from the structured fields so the dump never goes blank.
            var ts = new DateTime(2026, 5, 16, 9, 0, 0, DateTimeKind.Utc);
            LogRingBuffer.Add(new LogEntry(ts, "Warning", "Lock", "cross-subspace acquire denied", string.Empty));

            var output = LogTextFormatter.Format();

            StringAssert.Contains(output, "[09:00:00][Warning]: [Lock]: cross-subspace acquire denied");
        }

        [TestMethod]
        public void Format_OutputIsOldestFirst()
        {
            // Operators tailing the dashboard expect chronological order.
            for (var i = 0; i < 4; i++)
            {
                var ts = new DateTime(2026, 5, 16).AddMinutes(i);
                LogRingBuffer.Add(new LogEntry(ts, "Info", string.Empty, $"seq-{i}", $"[00:0{i}:00][Info]: seq-{i}"));
            }

            var output = LogTextFormatter.Format();

            var i0 = output.IndexOf("seq-0", StringComparison.Ordinal);
            var i3 = output.IndexOf("seq-3", StringComparison.Ordinal);
            Assert.IsTrue(i0 > 0 && i3 > 0 && i0 < i3, "seq-0 must appear before seq-3 in the output");
        }
    }
}
