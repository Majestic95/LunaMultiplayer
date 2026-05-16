using LmpCommon.Locks;
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
    public class LockSystemTest
    {
        private static readonly string XmlExamplePath = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        private Guid _vessel1 = Guid.NewGuid();
        private Guid _vessel2 = Guid.NewGuid();
        private string _player1 = "Player1";
        private string _player2 = "Player2";

        [TestInitialize]
        public void Setup()
        {
            var allLocks = LockSystem.LockQuery.GetAllLocks().ToList();
            foreach (var l in allLocks)
            {
                LockSystem.ReleaseLock(l);
            }
            //BUG-005/006: subspace/vessel state shared with other tests; reset between runs.
            WarpContext.Subspaces.Clear();
            VesselStoreSystem.CurrentVessels.Clear();
        }

        [TestCleanup]
        public void Teardown()
        {
            WarpContext.Subspaces.Clear();
            VesselStoreSystem.CurrentVessels.Clear();
        }

        [TestMethod]
        public void TestAcquireAndReleaseLock()
        {
            var lockDef = new LockDefinition(LockType.Control, _player1, _vessel1);

            // Acquire
            bool repeated;
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, false, out repeated));
            Assert.IsFalse(repeated);
            Assert.IsTrue(LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, _vessel1, null, _player1));

            // Release
            Assert.IsTrue(LockSystem.ReleaseLock(lockDef));
            Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.Control, _vessel1, null));
        }

        [TestMethod]
        public void TestCannotAcquireAlreadyOwnedLock()
        {
            var lockDef1 = new LockDefinition(LockType.Control, _player1, _vessel1);
            var lockDef2 = new LockDefinition(LockType.Control, _player2, _vessel1);

            bool repeated;
            Assert.IsTrue(LockSystem.AcquireLock(lockDef1, false, out repeated));
            
            // Player2 tries to take it
            Assert.IsFalse(LockSystem.AcquireLock(lockDef2, false, out repeated));
            Assert.IsTrue(LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, _vessel1, null, _player1));
        }

        [TestMethod]
        public void TestForceAcquireLock()
        {
            var lockDef1 = new LockDefinition(LockType.Control, _player1, _vessel1);
            var lockDef2 = new LockDefinition(LockType.Control, _player2, _vessel1);

            bool repeated;
            LockSystem.AcquireLock(lockDef1, false, out repeated);
            
            // Player2 forces it
            Assert.IsTrue(LockSystem.AcquireLock(lockDef2, true, out repeated));
            Assert.IsTrue(LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, _vessel1, null, _player2));
        }

        [TestMethod]
        public void TestOnlyOneControlLockPerPlayer()
        {
            var lockDef1 = new LockDefinition(LockType.Control, _player1, _vessel1);
            var lockDef2 = new LockDefinition(LockType.Control, _player1, _vessel2);

            bool repeated;
            LockSystem.AcquireLock(lockDef1, false, out repeated);
            Assert.IsTrue(LockSystem.LockQuery.LockExists(LockType.Control, _vessel1, null));

            // Acquire second control lock, first should be released
            LockSystem.AcquireLock(lockDef2, false, out repeated);

            Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.Control, _vessel1, null));
            Assert.IsTrue(LockSystem.LockQuery.LockExists(LockType.Control, _vessel2, null));
        }

        // -------- BUG-005/006 cross-subspace acquire rejection --------

        [TestMethod]
        public void TestAcquireFromPastSubspace_OnVesselTiedLock_IsRejected()
        {
            //Subspace 1 is in the past relative to 2. Vessel is authoritative in 2. A player in 1
            //must not be able to acquire UnloadedUpdate on the vessel.
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);

            var vessel = LoadSampleVessel();
            vessel.AuthoritativeSubspaceId = 2;
            VesselStoreSystem.CurrentVessels.TryAdd(_vessel1, vessel);

            var lockDef = new LockDefinition(LockType.UnloadedUpdate, _player1, _vessel1);
            Assert.IsFalse(LockSystem.AcquireLock(lockDef, force: false, out _, requesterSubspace: 1));
            Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.UnloadedUpdate, _vessel1, null));
        }

        [TestMethod]
        public void TestAcquireFromSameSubspace_OnVesselTiedLock_Succeeds()
        {
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);

            var vessel = LoadSampleVessel();
            vessel.AuthoritativeSubspaceId = 2;
            VesselStoreSystem.CurrentVessels.TryAdd(_vessel1, vessel);

            var lockDef = new LockDefinition(LockType.UnloadedUpdate, _player1, _vessel1);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _, requesterSubspace: 2));
            Assert.IsTrue(LockSystem.LockQuery.LockExists(LockType.UnloadedUpdate, _vessel1, null));
        }

        [TestMethod]
        public void TestAcquireFromFutureSubspace_OnVesselTiedLock_Succeeds()
        {
            //A player in subspace 3 (future) can acquire a lock on a vessel authoritative in 1
            //(past) — the rejection only fires when the candidate is strictly PAST the vessel's auth.
            SeedSubspace(1, time: 10d);
            SeedSubspace(3, time: 300d);

            var vessel = LoadSampleVessel();
            vessel.AuthoritativeSubspaceId = 1;
            VesselStoreSystem.CurrentVessels.TryAdd(_vessel1, vessel);

            var lockDef = new LockDefinition(LockType.UnloadedUpdate, _player1, _vessel1);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _, requesterSubspace: 3));
        }

        [TestMethod]
        public void TestAcquireFromPastSubspace_OnSpectatorLock_NotRejected()
        {
            //Spectator/Asteroid/Contract/Kerbal locks have no vessel-subspace dimension; subspace
            //rejection must not apply to them. Spectator carries no VesselId.
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);

            var vessel = LoadSampleVessel();
            vessel.AuthoritativeSubspaceId = 2;
            VesselStoreSystem.CurrentVessels.TryAdd(_vessel1, vessel);

            var lockDef = new LockDefinition(LockType.Spectator, _player1);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _, requesterSubspace: 1));
        }

        [TestMethod]
        public void TestAcquire_VesselWithNoAuthYet_NotRejected()
        {
            //Vessel exists but has no recorded authority (AuthoritativeSubspaceId == 0). The first
            //ACQUIRE from any subspace must be allowed; rejection only fires once authority has
            //been established by a proto-update.
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);

            var vessel = LoadSampleVessel();
            //AuthoritativeSubspaceId left at its 0 default.
            VesselStoreSystem.CurrentVessels.TryAdd(_vessel1, vessel);

            var lockDef = new LockDefinition(LockType.UnloadedUpdate, _player1, _vessel1);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _, requesterSubspace: 1));
        }

        [TestMethod]
        public void TestAcquire_LegacyCaller_NotAffectedBySubspaceCheck()
        {
            //Callers that don't pass requesterSubspace (or pass 0) must hit the legacy path —
            //no subspace check at all. Preserves backward compatibility for any future helper
            //wanting to bypass the check.
            SeedSubspace(1, time: 10d);
            SeedSubspace(2, time: 100d);

            var vessel = LoadSampleVessel();
            vessel.AuthoritativeSubspaceId = 2;
            VesselStoreSystem.CurrentVessels.TryAdd(_vessel1, vessel);

            var lockDef = new LockDefinition(LockType.UnloadedUpdate, _player1, _vessel1);
            //Legacy 3-arg call (defaults requesterSubspace=0); no rejection should fire even though
            //the vessel's auth is set.
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _));
        }

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
