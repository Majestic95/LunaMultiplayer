using LmpCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server;
using Server.Log;
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
}
