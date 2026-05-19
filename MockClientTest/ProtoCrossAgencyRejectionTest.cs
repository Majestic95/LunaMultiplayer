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
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// v4 VesselProto cross-agency write guard — end-to-end wire coverage. Sibling
    /// to <see cref="CrossAgencyVesselRelayTest"/>, which covers the 11 relayed
    /// message types + Remove + Couple. This suite pins the proto-path-specific
    /// behaviour: a cross-agency <see cref="VesselProtoMsgData"/> from a real
    /// peer is silently dropped (no broadcast to watchers, no overwrite of the
    /// server's authoritative vessel store), while same-agency protos and
    /// Unassigned-sentinel-vessel protos pass through normally.
    ///
    /// The exploit being closed: <see cref="Server.Message.VesselMsgReader.HandleVesselProto"/>
    /// in pre-v4 builds had NO cross-agency guard. A modified client could craft a
    /// <see cref="VesselProtoMsgData"/> for another agency's vessel-id with arbitrary
    /// payload (modified crew list, parts, resources, position) and the server would
    /// persist those bytes via <see cref="Server.System.Vessel.VesselDataUpdater.RawConfigNodeInsertOrUpdate"/>
    /// + relay them to every connected client. The 5.16b stamp-preservation only
    /// preserves <see cref="Vessel.OwningAgencyId"/>; the rest of the proto bytes
    /// overwrite the authoritative store. This was the load-bearing kerbal-seizure
    /// vector behind Phase 4 §1.b's documented attack.
    ///
    /// See <c>docs/research/v4-vessel-proto-cross-agency-write-guard.md</c> for the
    /// full threat model + the documented race-craft-pre-create limitation.
    /// </summary>
    [TestClass]
    public class ProtoCrossAgencyRejectionTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void BobCrossAgency_Proto_DroppedAtServer_AliceVesselUnchanged()
        {
            // The exploit closed by this v4 fix. Alice owns vessel V_A (her agency
            // is stamped on lmpOwningAgency). Bob (different agency) crafts a proto
            // for V_A with modified payload bytes. Server-side HandleVesselProto's
            // new RejectIfCrossAgencyWrite call drops the message synchronously;
            // RawConfigNodeInsertOrUpdate never runs; RelayMessage never runs.
            // Verify: (a) watcher doesn't receive a VesselProtoMsgData for V_A from
            // Bob's send, (b) Alice's vessel object reference in CurrentVessels is
            // unchanged (Bob's bytes did not replace the in-memory authoritative
            // vessel record).
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-pt-alice");
                SetClientSubspace("h-pt-alice", 1);

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndGetAgencyId(bob, "h-pt-bob");
                Assert.AreNotEqual(aliceAgency, bobAgency,
                    "Test setup: Alice and Bob must have distinct agencies for the cross-agency rejection to be meaningful.");
                SetClientSubspace("h-pt-bob", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-pt-wtch");
                SetClientSubspace("h-pt-wtch", 1);

                // Seed Alice's vessel in the store (simulating a previously-broadcast vessel).
                var vesselId = Guid.NewGuid();
                var aliceVesselText = SampleVesselText.Value;
                var aliceVessel = new Vessel(aliceVesselText);
                aliceVessel.OwningAgencyId = aliceAgency;
                aliceVessel.AuthoritativeSubspaceId = 1;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Capture the original vessel reference for the post-attack equality check.
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var originalVesselRef));

                // Bob crafts and sends a proto for Alice's vessel. The payload is
                // intentionally the same sample bytes (we're testing the guard, not
                // the payload-parsing side; what matters is whether the server
                // accepts the write at all).
                var bobBytes = Encoding.UTF8.GetBytes(aliceVesselText);
                var protoMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                protoMsg.VesselId = vesselId;
                protoMsg.Data = bobBytes;
                protoMsg.NumBytes = bobBytes.Length;
                bob.SendMessage<VesselCliMsg>(protoMsg);

                // (a) Watcher MUST NOT receive a relay of Bob's proto for V_A.
                var dropped = WaitForProtoForVessel(watcher, vesselId, TimeSpan.FromMilliseconds(1500));
                Assert.IsNull(dropped,
                    "Server relayed Bob's cross-agency VesselProtoMsgData — proto-write guard failed.");

                // (b) Alice's authoritative vessel in CurrentVessels is unchanged.
                // RawConfigNodeInsertOrUpdate's AddOrUpdate replaces the dict value
                // with a NEW Vessel instance on every accepted proto. If Bob's proto
                // had been accepted, the reference would no longer equal the
                // originally-seeded one.
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var currentVesselRef));
                Assert.AreSame(originalVesselRef, currentVesselRef,
                    "Alice's vessel object reference in CurrentVessels changed — Bob's proto was persisted.");
            }
        }

        [TestMethod]
        public void AliceSameAgency_Proto_RelaysToWatcher()
        {
            // Positive control: Alice owns V_A; Alice broadcasts a proto for V_A.
            // Server's new guard sees same-agency, falls through, the proto is
            // persisted + relayed normally. Pins that the v4 fix hasn't broken
            // the legitimate-owner happy path.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var alice = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-pt-al-ok");
                SetClientSubspace("h-pt-al-ok", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-pt-wt-ok");
                SetClientSubspace("h-pt-wt-ok", 1);

                var vesselId = Guid.NewGuid();
                var vesselText = SampleVesselText.Value;
                var vessel = new Vessel(vesselText);
                vessel.OwningAgencyId = aliceAgency;
                vessel.AuthoritativeSubspaceId = 1;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));

                var bytes = Encoding.UTF8.GetBytes(vesselText);
                var protoMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                protoMsg.VesselId = vesselId;
                protoMsg.Data = bytes;
                protoMsg.NumBytes = bytes.Length;
                alice.SendMessage<VesselCliMsg>(protoMsg);

                var relayed = WaitForProtoForVessel(watcher, vesselId, TimeSpan.FromSeconds(3));
                Assert.IsNotNull(relayed,
                    "Watcher did not receive Alice's same-agency proto relay — guard over-rejected.");
                Assert.AreEqual(vesselId, relayed.VesselId);
            }
        }

        [TestMethod]
        public void UnassignedSentinelVessel_AnyAgencyProtoRelays()
        {
            // Spec §10 Q3: pre-0.31 vessel (no lmpOwningAgency → OwningAgencyId == Empty)
            // is the Unassigned sentinel. Any agency may proto for it (transferagency
            // is the operator-side path to assign ownership). Bob's proto on an
            // Unassigned vessel must reach the watcher.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            SeedSubspace(1, time: 100d);

            using (var bob = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(bob, "h-pt-bb-un");
                SetClientSubspace("h-pt-bb-un", 1);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndGetAgencyId(watcher, "h-pt-wt-un");
                SetClientSubspace("h-pt-wt-un", 1);

                var vesselId = Guid.NewGuid();
                var unassignedVesselText = SampleVesselText.Value;
                var unassignedVessel = new Vessel(unassignedVesselText);
                // OwningAgencyId left at Guid.Empty (sample fixture has no lmpOwningAgency field).
                Assert.AreEqual(Guid.Empty, unassignedVessel.OwningAgencyId);
                unassignedVessel.AuthoritativeSubspaceId = 1;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, unassignedVessel));

                var bytes = Encoding.UTF8.GetBytes(unassignedVesselText);
                var protoMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                protoMsg.VesselId = vesselId;
                protoMsg.Data = bytes;
                protoMsg.NumBytes = bytes.Length;
                bob.SendMessage<VesselCliMsg>(protoMsg);

                var relayed = WaitForProtoForVessel(watcher, vesselId, TimeSpan.FromSeconds(3));
                Assert.IsNotNull(relayed,
                    "Bob's proto on Unassigned-sentinel vessel must relay (spec §10 Q3).");
            }
        }

        /// <summary>
        /// Drains the watcher's inbox of <see cref="VesselProtoMsgData"/> entries,
        /// returning the first one whose <c>VesselId</c> matches. Other broadcasts
        /// are discarded.
        /// </summary>
        private static VesselProtoMsgData WaitForProtoForVessel(MockNetClient client, Guid vesselId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var msg = client.WaitForReply<VesselProtoMsgData>(remaining);
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
