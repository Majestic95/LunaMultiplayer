using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Vessel.Classes;
using System;
using System.IO;
using System.Linq;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17a write-path counterpart — end-to-end coverage for the cross-agency
    /// vessel-relay guard at <see cref="Server.Message.VesselMsgReader.RejectIfCrossAgencyWrite"/>.
    /// The server-side helper is unit-tested in
    /// <c>ServerTest/VesselMsgReaderCrossAgencyTest.cs</c> across the seven branch
    /// variations; this suite pins the wire-level behaviour: a cross-agency
    /// <see cref="VesselPositionMsgData"/> from a real peer is silently dropped (no
    /// broadcast to other clients), while a same-agency send and an Unassigned-sentinel
    /// vessel both pass through the relay.
    ///
    /// Soak Finding 2 (session 19): Player A clicking "Fly" from the tracking station
    /// on Player B's vessel caused physics jitter on B's instance — A's client became
    /// the active simulator and broadcast position updates that the server-relay path
    /// forwarded to B unconditionally. The 5.17a lock-acquire guard closes the lock-
    /// taking hole but not the broadcast hole.
    /// </summary>
    [TestClass]
    public class CrossAgencyVesselRelayTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void BobCrossAgency_PositionUpdate_IsDropped_WatcherSeesNoRelay()
        {
            // Alice owns the vessel (her agency is stamped on lmpOwningAgency). Bob (different
            // agency) sends a VesselPositionMsgData via the wire — the soak Finding 2
            // tracking-station-Fly hazard. Server-side RejectIfCrossAgencyWrite returns true;
            // RelayMessage is never called. The watcher (a third connected client) drains
            // its inbox for 1500ms and asserts no VesselPositionMsgData for the vessel arrived.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-rly-alice");
                SetClientSubspace("h-rly-alice", 1);

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndGetAgencyId(bob, "h-rly-bob");
                Assert.AreNotEqual(aliceAgency, bobAgency,
                    "Test setup: Alice and Bob must have distinct agencies for cross-agency rejection to be meaningful.");
                SetClientSubspace("h-rly-bob", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-rly-wtch");
                SetClientSubspace("h-rly-wtch", 1);

                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                aliceVessel.AuthoritativeSubspaceId = 1; // Match subspace so RejectIfPastSubspace doesn't fire.
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Bob fires the cross-agency position broadcast.
                var positionMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselPositionMsgData>();
                positionMsg.VesselId = vesselId;
                positionMsg.SubspaceId = 1;
                positionMsg.BodyName = "Kerbin";
                bob.SendMessage<VesselCliMsg>(positionMsg);

                // Watcher MUST NOT receive a VesselPositionMsgData for this vessel. Drain
                // until we either find one (failure) or hit the timeout (success).
                var dropped = WaitForPositionForVessel(watcher, vesselId, TimeSpan.FromMilliseconds(1500));
                Assert.IsNull(dropped,
                    "Server relayed Bob's cross-agency VesselPositionMsgData — relay guard failed.");
            }
        }

        [TestMethod]
        public void AliceSameAgency_PositionUpdate_RelaysToWatcher()
        {
            // Positive control: Alice owns the vessel; Alice broadcasts a position. Server
            // relays to the watcher. Pins that the new guard hasn't broken the same-agency
            // happy path.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var alice = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-rly-al-ok");
                SetClientSubspace("h-rly-al-ok", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-rly-wt-ok");
                SetClientSubspace("h-rly-wt-ok", 1);

                var vesselId = Guid.NewGuid();
                var vessel = new Vessel(SampleVesselText.Value);
                vessel.OwningAgencyId = aliceAgency;
                vessel.AuthoritativeSubspaceId = 1;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));

                var positionMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselPositionMsgData>();
                positionMsg.VesselId = vesselId;
                positionMsg.SubspaceId = 1;
                positionMsg.BodyName = "Kerbin";
                alice.SendMessage<VesselCliMsg>(positionMsg);

                var relayed = WaitForPositionForVessel(watcher, vesselId, TimeSpan.FromSeconds(3));
                Assert.IsNotNull(relayed,
                    "Watcher did not receive Alice's same-agency relay broadcast — guard over-rejected.");
                Assert.AreEqual(vesselId, relayed.VesselId);
            }
        }

        [TestMethod]
        public void UnassignedSentinelVessel_AnyAgencyPositionRelays()
        {
            // Spec §10 Q3: pre-0.31 vessel (no lmpOwningAgency → OwningAgencyId == Empty)
            // is the Unassigned sentinel. Any agency may broadcast state for it. Bob's
            // position update on an Unassigned vessel must reach the watcher.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(bob, "h-rly-bb-un");
                SetClientSubspace("h-rly-bb-un", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-rly-wt-un");
                SetClientSubspace("h-rly-wt-un", 1);

                var vesselId = Guid.NewGuid();
                var unassignedVessel = new Vessel(SampleVesselText.Value);
                // OwningAgencyId left at Guid.Empty (sample fixture has no lmpOwningAgency field).
                Assert.AreEqual(Guid.Empty, unassignedVessel.OwningAgencyId);
                unassignedVessel.AuthoritativeSubspaceId = 1;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, unassignedVessel));

                var positionMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselPositionMsgData>();
                positionMsg.VesselId = vesselId;
                positionMsg.SubspaceId = 1;
                positionMsg.BodyName = "Kerbin";
                bob.SendMessage<VesselCliMsg>(positionMsg);

                var relayed = WaitForPositionForVessel(watcher, vesselId, TimeSpan.FromSeconds(3));
                Assert.IsNotNull(relayed,
                    "Bob's position on Unassigned-sentinel vessel must relay (spec §10 Q3).");
            }
        }

        [TestMethod]
        public void BobCrossAgency_VesselRemove_IsDropped_VesselStaysInStore()
        {
            // Consumer-lens [MUST FIX] from session 19 review: HandleVesselRemove's existing
            // ControlLockExists check only fires when SOME player holds Control; with no
            // Control lock active (Alice logged off, BUG-010 pinned the vessel without
            // re-acquire), a cross-agency Bob's VesselRemoveMsgData would delete Alice's
            // vessel and broadcast the removal. Pinned: server drops the remove and the
            // vessel remains in CurrentVessels; the watcher sees no VesselRemoveMsgData
            // broadcast for the vessel.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-rly-al-rm");
                SetClientSubspace("h-rly-al-rm", 1);

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndGetAgencyId(bob, "h-rly-bb-rm");
                Assert.AreNotEqual(aliceAgency, bobAgency);
                SetClientSubspace("h-rly-bb-rm", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-rly-wt-rm");
                SetClientSubspace("h-rly-wt-rm", 1);

                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                aliceVessel.AuthoritativeSubspaceId = 1;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Bob fires the cross-agency remove. No Control lock exists, so the legacy
                // ControlLockExists check would let it through — only the new guard catches it.
                var removeMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
                removeMsg.VesselId = vesselId;
                removeMsg.AddToKillList = true;
                bob.SendMessage<VesselCliMsg>(removeMsg);

                // Watcher MUST NOT receive a VesselRemoveMsgData for this vessel.
                var dropped = WaitForRemoveForVessel(watcher, vesselId, TimeSpan.FromMilliseconds(1500));
                Assert.IsNull(dropped,
                    "Server relayed Bob's cross-agency VesselRemoveMsgData — remove guard failed.");

                Assert.IsTrue(VesselStoreSystem.CurrentVessels.ContainsKey(vesselId),
                    "Alice's vessel must still be in the store after Bob's rejected remove.");
            }
        }

        [TestMethod]
        public void BobCrossAgency_VesselCouple_IsDropped_DominantUnchanged()
        {
            // Server-systems review [CONSIDER → fix] from session 19: HandleVesselCouple
            // rewrites the dominant's AuthoritativeSubspaceId and broadcasts a remove of
            // the weak vessel. Without the guard, cross-agency Bob could take over
            // Alice's dominant vessel by initiating a couple. Pinned: server drops the
            // couple message; the dominant's auth subspace is unchanged.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);
            SeedSubspace(2, time: 200d);

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-rly-al-cp");
                SetClientSubspace("h-rly-al-cp", 1);

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndGetAgencyId(bob, "h-rly-bb-cp");
                Assert.AreNotEqual(aliceAgency, bobAgency);
                SetClientSubspace("h-rly-bb-cp", 2);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-rly-wt-cp");
                SetClientSubspace("h-rly-wt-cp", 1);

                // Alice's dominant vessel; Bob owns a weak vessel that would couple into it.
                var dominantId = Guid.NewGuid();
                var dominant = new Vessel(SampleVesselText.Value);
                dominant.OwningAgencyId = aliceAgency;
                dominant.AuthoritativeSubspaceId = 1;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(dominantId, dominant));

                var weakId = Guid.NewGuid();
                var weak = new Vessel(SampleVesselText.Value);
                weak.OwningAgencyId = bobAgency;
                weak.AuthoritativeSubspaceId = 2;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(weakId, weak));

                var coupleMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselCoupleMsgData>();
                coupleMsg.VesselId = dominantId;
                coupleMsg.CoupledVesselId = weakId;
                bob.SendMessage<VesselCliMsg>(coupleMsg);

                // Watcher MUST NOT receive a VesselRemoveMsgData for the weak vessel
                // (would mean the couple succeeded and triggered SendToAllClients remove).
                var dropped = WaitForRemoveForVessel(watcher, weakId, TimeSpan.FromMilliseconds(1500));
                Assert.IsNull(dropped,
                    "Server processed Bob's cross-agency Couple — couple guard failed.");

                // Dominant auth subspace must still be Alice's (1), not Bob's (2).
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryGetValue(dominantId, out var dominantNow));
                Assert.AreEqual(1, dominantNow.AuthoritativeSubspaceId,
                    "Dominant's AuthoritativeSubspaceId was rewritten by a cross-agency Couple.");

                // Both vessels remain in the store.
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.ContainsKey(weakId),
                    "Weak vessel was removed by a cross-agency Couple.");
            }
        }

        /// <summary>
        /// Drains the watcher's inbox of <see cref="VesselRemoveMsgData"/> entries, returning
        /// the first one whose <c>VesselId</c> matches. Other broadcasts are discarded.
        /// </summary>
        private static VesselRemoveMsgData WaitForRemoveForVessel(MockNetClient client, Guid vesselId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var msg = client.WaitForReply<VesselRemoveMsgData>(remaining);
                if (msg == null) return null;
                if (msg.VesselId == vesselId) return msg;
            }
            return null;
        }

        /// <summary>
        /// Drains the watcher's inbox of <see cref="VesselPositionMsgData"/> entries, returning
        /// the first one whose <c>VesselId</c> matches. Other broadcasts are discarded.
        /// </summary>
        private static VesselPositionMsgData WaitForPositionForVessel(MockNetClient client, Guid vesselId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var msg = client.WaitForReply<VesselPositionMsgData>(remaining);
                if (msg == null) return null;
                if (msg.VesselId == vesselId) return msg;
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
