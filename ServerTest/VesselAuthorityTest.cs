using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.System;
using Server.System.Vessel.Classes;
using System;
using System.IO;
using System.Linq;

namespace ServerTest
{
    [TestClass]
    public class VesselAuthorityTest
    {
        private static readonly string XmlExamplePath = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        [TestInitialize]
        public void Setup()
        {
            //BUG-005/006 tests poke at static WarpContext/VesselStoreSystem state. Each test resets both.
            WarpContext.Subspaces.Clear();
            VesselStoreSystem.CurrentVessels.Clear();
        }

        [TestCleanup]
        public void Teardown()
        {
            WarpContext.Subspaces.Clear();
            VesselStoreSystem.CurrentVessels.Clear();
        }

        // -------- Vessel.AuthoritativeSubspaceId persistence round-trip --------

        [TestMethod]
        public void Vessel_AuthoritativeSubspaceId_DefaultsToZero_ForExampleFiles()
        {
            var samplePath = Directory.GetFiles(XmlExamplePath).First();
            var vessel = new Vessel(File.ReadAllText(samplePath));

            //Sample files predate the AuthoritativeSubspaceId field; they should load with 0.
            Assert.AreEqual(0, vessel.AuthoritativeSubspaceId);
        }

        [TestMethod]
        public void Vessel_AuthoritativeSubspaceId_RoundTripsThroughToStringAndParse()
        {
            var samplePath = Directory.GetFiles(XmlExamplePath).First();
            var vessel = new Vessel(File.ReadAllText(samplePath));

            vessel.AuthoritativeSubspaceId = 42;
            var reparsed = new Vessel(vessel.ToString());

            Assert.AreEqual(42, reparsed.AuthoritativeSubspaceId);
        }

        [TestMethod]
        public void Vessel_AuthoritativeSubspaceId_OverwritesPreviousValue()
        {
            var samplePath = Directory.GetFiles(XmlExamplePath).First();
            var vessel = new Vessel(File.ReadAllText(samplePath));

            vessel.AuthoritativeSubspaceId = 42;
            vessel.AuthoritativeSubspaceId = 99;
            var reparsed = new Vessel(vessel.ToString());

            Assert.AreEqual(99, reparsed.AuthoritativeSubspaceId);
        }

        [TestMethod]
        public void Vessel_AuthoritativeSubspaceId_ZeroIsValidSentinel()
        {
            var samplePath = Directory.GetFiles(XmlExamplePath).First();
            var vessel = new Vessel(File.ReadAllText(samplePath));

            vessel.AuthoritativeSubspaceId = 7;
            vessel.AuthoritativeSubspaceId = 0;
            var reparsed = new Vessel(vessel.ToString());

            Assert.AreEqual(0, reparsed.AuthoritativeSubspaceId);
        }

        // -------- WarpSystem.IsStrictlyPast --------

        [TestMethod]
        public void IsStrictlyPast_EqualSubspaces_ReturnsFalse()
        {
            SeedSubspace(5, time: 100d);
            Assert.IsFalse(WarpSystem.IsStrictlyPast(5, 5));
        }

        [TestMethod]
        public void IsStrictlyPast_EarlierTime_ReturnsTrue()
        {
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);

            Assert.IsTrue(WarpSystem.IsStrictlyPast(1, 2));
            Assert.IsFalse(WarpSystem.IsStrictlyPast(2, 1));
        }

        [TestMethod]
        public void IsStrictlyPast_CandidateWarpingSentinel_ReturnsFalse()
        {
            SeedSubspace(2, time: 100d);
            //-1 (warping) is inert — not treated as past so the lock/proto path doesn't reject.
            Assert.IsFalse(WarpSystem.IsStrictlyPast(-1, 2));
        }

        [TestMethod]
        public void IsStrictlyPast_ReferenceNoAuthSentinel_ReturnsFalse()
        {
            SeedSubspace(1, time: 10d);
            //0 (no auth) is inert — vessel without authority cannot be a reference.
            Assert.IsFalse(WarpSystem.IsStrictlyPast(1, 0));
        }

        [TestMethod]
        public void IsStrictlyPast_UnknownSubspaceIds_ReturnsFalse()
        {
            SeedSubspace(1, time: 10d);
            Assert.IsFalse(WarpSystem.IsStrictlyPast(1, 999));
            Assert.IsFalse(WarpSystem.IsStrictlyPast(999, 1));
        }

        // -------- WarpSystem.RemoveSubspace vessel-authority guard --------

        [TestMethod]
        public void RemoveSubspace_VesselStillAuthoritative_RefusesRemoval()
        {
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);

            var vessel = LoadSampleVessel();
            vessel.AuthoritativeSubspaceId = 1;
            VesselStoreSystem.CurrentVessels.TryAdd(Guid.NewGuid(), vessel);

            //Subspace 1 has a vessel authoritative there — must refuse removal even though no
            //client occupies it. Subspace 2 is the latest so it's protected anyway.
            Assert.IsFalse(WarpSystem.RemoveSubspace(1));
            Assert.IsTrue(WarpContext.Subspaces.ContainsKey(1));
        }

        [TestMethod]
        public void RemoveSubspace_NoVesselAuthoritative_AllowsRemoval()
        {
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);
            SeedSubspace(3, time: 200d);

            //Subspace 2 is in the middle; no clients, no vessels. Should be removable.
            Assert.IsTrue(WarpSystem.RemoveSubspace(2));
            Assert.IsFalse(WarpContext.Subspaces.ContainsKey(2));
        }

        // -------- Helpers --------

        private static void SeedSubspace(int id, double time)
        {
            WarpContext.Subspaces.TryAdd(id, new Subspace(id, time, "test"));
        }

        private static Vessel LoadSampleVessel()
        {
            return new Vessel(File.ReadAllText(Directory.GetFiles(XmlExamplePath).First()));
        }
    }
}
