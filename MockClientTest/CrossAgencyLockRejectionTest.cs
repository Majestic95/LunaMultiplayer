using LmpCommon.Enums;
using LmpCommon.Locks;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Lock;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel.Classes;
using System;
using System.IO;
using System.Linq;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17a — end-to-end coverage for cross-agency lock rejection. The server-side
    /// check in <see cref="LockSystem.AcquireLock"/> is unit-tested in
    /// <c>ServerTest/LockSystemAgencyTest.cs</c> across the eight branch variations; this
    /// suite pins the wire-level behaviour: a cross-agency LockAcquireMsgData from a real
    /// peer is refused (no broadcast to other clients, LockStore unchanged), while a
    /// same-agency acquire and an Unassigned-sentinel vessel both succeed and broadcast.
    /// </summary>
    [TestClass]
    public class CrossAgencyLockRejectionTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void BobCrossAgency_LockAcquire_IsRejected_WatcherSeesNoBobAcquireBroadcast()
        {
            // Alice owns the vessel (her agency is stamped on lmpOwningAgency). Bob (different
            // agency) sends a Control-lock request via the wire. Server-side
            // LockSystem.AcquireLock returns false; LockSystemSender.SendLockAcquireMessage
            // therefore does NOT broadcast LockAcquireMsgData with Bob as owner. The watcher
            // (a third connected client) waits 800ms and asserts no such broadcast arrived.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            // Bring up Alice + Bob via real handshake so each has a real agency in
            // AgencySystem.AgencyByPlayerName. Then plant a vessel owned by Alice's agency
            // directly into the store (avoids needing Alice to send a proto in this test).
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-017a-alice");
                SetClientSubspace("h-017a-alice", 1);

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndGetAgencyId(bob, "h-017a-bob");
                Assert.AreNotEqual(aliceAgency, bobAgency,
                    "Test setup: Alice and Bob must have distinct agencies for cross-agency rejection to be meaningful.");
                SetClientSubspace("h-017a-bob", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-017a-wtch");

                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Bob fires the cross-agency request.
                var request = ServerContext.ClientMessageFactory.CreateNewMessageData<LockAcquireMsgData>();
                request.Lock = new LockDefinition(LockType.Control, "h-017a-bob", vesselId);
                request.Force = false;
                bob.SendMessage<LockCliMsg>(request);

                // The watcher MUST NOT receive a LockAcquireMsgData where the lock's
                // PlayerName == "h-017a-bob" (that would mean the server broadcast a
                // successful acquire). Inbox preserves out-of-order messages so we drain
                // until we either find a Bob-owned acquire or hit the timeout.
                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
                LockAcquireMsgData bobAcquireBroadcast = null;
                while (DateTime.UtcNow < deadline)
                {
                    var msg = watcher.WaitForReply<LockAcquireMsgData>(TimeSpan.FromMilliseconds(200));
                    if (msg == null) break;
                    if (msg.Lock.PlayerName == "h-017a-bob")
                    {
                        bobAcquireBroadcast = msg;
                        break;
                    }
                    // Different PlayerName (e.g. an Alice-owned stored-lock relay): keep draining.
                }
                Assert.IsNull(bobAcquireBroadcast,
                    "Server broadcast a Bob-owned Control lock — cross-agency rejection failed.");

                Assert.IsFalse(LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, vesselId, null, "h-017a-bob"),
                    "Bob's rejected lock made it into LockStore.");
            }
        }

        [TestMethod]
        public void AliceSameAgency_LockAcquire_Succeeds_AndBroadcastsToWatcher()
        {
            // Positive control: Alice owns the vessel; Alice acquires Control. Server
            // broadcasts the successful LockAcquireMsgData to all clients (including the
            // watcher), and LockStore reflects the new lock.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var alice = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-017a-al-ok");
                SetClientSubspace("h-017a-al-ok", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-017a-wt-ok");

                var vesselId = Guid.NewGuid();
                var vessel = new Vessel(SampleVesselText.Value);
                vessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));

                var request = ServerContext.ClientMessageFactory.CreateNewMessageData<LockAcquireMsgData>();
                request.Lock = new LockDefinition(LockType.Control, "h-017a-al-ok", vesselId);
                request.Force = false;
                alice.SendMessage<LockCliMsg>(request);

                var broadcast = WaitForLockAcquireForPlayer(watcher, "h-017a-al-ok", TimeSpan.FromSeconds(3));
                Assert.IsNotNull(broadcast, "Watcher did not receive Alice's successful acquire broadcast.");
                Assert.AreEqual(LockType.Control, broadcast.Lock.Type);
                Assert.AreEqual(vesselId, broadcast.Lock.VesselId);

                Assert.IsTrue(LockSystem.LockQuery.LockBelongsToPlayer(LockType.Control, vesselId, null, "h-017a-al-ok"),
                    "Alice's lock was not stored after a successful acquire.");
            }
        }

        [TestMethod]
        public void UnassignedSentinelVessel_AnyAgencyMayAcquire()
        {
            // Spec §10 Q3: pre-0.31 vessel (no lmpOwningAgency → OwningAgencyId == Empty)
            // is the Unassigned sentinel. Any agency may interact. Bob (with his own
            // agency) acquires Control on an Unassigned vessel — should succeed.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(bob, "h-017a-bb-un");
                SetClientSubspace("h-017a-bb-un", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-017a-wt-un");

                var vesselId = Guid.NewGuid();
                var unassignedVessel = new Vessel(SampleVesselText.Value);
                // OwningAgencyId left at Guid.Empty (sample fixture has no lmpOwningAgency field).
                Assert.AreEqual(Guid.Empty, unassignedVessel.OwningAgencyId);
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, unassignedVessel));

                var request = ServerContext.ClientMessageFactory.CreateNewMessageData<LockAcquireMsgData>();
                request.Lock = new LockDefinition(LockType.Control, "h-017a-bb-un", vesselId);
                request.Force = false;
                bob.SendMessage<LockCliMsg>(request);

                var broadcast = WaitForLockAcquireForPlayer(watcher, "h-017a-bb-un", TimeSpan.FromSeconds(3));
                Assert.IsNotNull(broadcast,
                    "Bob's acquire on Unassigned-sentinel vessel must succeed and broadcast (spec §10 Q3).");
            }
        }

        /// <summary>
        /// Drains the watcher's inbox of <see cref="LockAcquireMsgData"/> entries, returning
        /// the first one whose <c>Lock.PlayerName</c> matches the expected player. Other
        /// broadcasts (e.g. earlier acquires) are discarded.
        /// </summary>
        private static LockAcquireMsgData WaitForLockAcquireForPlayer(MockNetClient client, string expectedPlayer, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var msg = client.WaitForReply<LockAcquireMsgData>(remaining);
                if (msg == null) return null;
                if (msg.Lock.PlayerName == expectedPlayer) return msg;
            }
            return null;
        }

        private static Guid HandshakeAndGetAgencyId(MockNetClient client, string playerName)
        {
            Assert.IsTrue(GameplaySettings.SettingsStore.PerAgencyCareer,
                "HandshakeAndGetAgencyId requires PerAgencyCareer=true.");

            var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
            handshake.PlayerName = playerName;
            handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
            handshake.KspVersion = "1.12.5";
            client.SendMessage<HandshakeCliMsg>(handshake);

            var reply = client.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(reply, $"Handshake reply missing for {playerName}.");
            Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response,
                $"Handshake rejected for {playerName}: " + reply.Reason);

            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyStateMsgData for {playerName}.");
            return state.AgencyId;
        }

        private static void SetClientSubspace(string playerName, int subspace)
        {
            var registered = ServerContext.Clients.Values.SingleOrDefault(c => c.PlayerName == playerName);
            Assert.IsNotNull(registered, $"Server did not register {playerName}.");
            registered.Subspace = subspace;
        }

        private static void SeedSubspace(int id, double time)
        {
            WarpContext.Subspaces.TryAdd(id, new Subspace(id, time, "test"));
        }

        private static string LoadSampleVesselText()
        {
            var probe = new DirectoryInfo(AppContext.BaseDirectory);
            while (probe != null && !Directory.Exists(Path.Combine(probe.FullName, "ServerTest", "XmlExampleFiles", "Others")))
                probe = probe.Parent;

            Assert.IsNotNull(probe, "Could not locate ServerTest/XmlExampleFiles/Others.");
            var fixtureDir = Path.Combine(probe.FullName, "ServerTest", "XmlExampleFiles", "Others");
            var samplePath = Directory.GetFiles(fixtureDir).OrderBy(p => p, StringComparer.Ordinal).First();
            return File.ReadAllText(samplePath);
        }
    }
}
