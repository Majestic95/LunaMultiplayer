using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
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
    /// Phase 3 Slice C — end-to-end coverage for <see cref="AgencyPlanetaryRouter"/>'s
    /// wire path. Same harness shape as Slice B's
    /// <see cref="AgencyKolonyRoutingTest"/>; differences trace to the
    /// body-and-resource partition key (vs vessel-and-body) and the
    /// non-vessel-migration policy (planetary entries don't migrate on
    /// transferagency per pre-spec §4.e).
    ///
    /// <para>Four integration cases — smaller surface than Slice B's six
    /// because the body-keyed partition has fewer edge cases (no malformed-
    /// VesselId-string-parse path, no SameVesselDifferentBodyIndex distinct-
    /// entries case — that one is covered by the ServerTest Upsert tests).</para>
    /// </summary>
    [TestClass]
    public class AgencyPlanetaryRoutingTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_SameAgencyPlanetaryMutation_RoutesToOwner_NoPeerLeak()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2c-a1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2c-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency, "Alice/Bob must have distinct agencies.");

                // Plant a vessel stamped with Alice's agency.
                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Alice sends a planetary mutation for her warehouse on Duna.
                var msg = BuildPlanetaryMsg(vesselId, bodyIndex: 5, resourceName: "Hydrates", quantity: 42.5);
                alice.SendMessage<AgencyCliMsg>(msg);

                // Alice receives the owner-only echo.
                var echo = alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyPlanetaryStateMsgData echo.");
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual(42.5, echo.Entries[0].StoredQuantity);
                Assert.AreEqual("Hydrates", echo.Entries[0].ResourceName);
                Assert.AreEqual(vesselId, echo.Entries[0].OwningVesselId);
                Assert.AreEqual(aliceAgency, echo.AgencyId,
                    "Echo's AgencyId must be the routed owner's agency (server-derived, not wire-supplied).");

                // Bob receives nothing planetary-shaped (privacy rule).
                var stray = bob.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Peer received AgencyPlanetaryStateMsgData under gate=on — privacy rule violated.");

                // Alice's per-agency state holds the entry under the body-resource composite key.
                var aliceState = AgencySystem.Agencies[aliceAgency];
                Assert.AreEqual(1, aliceState.PlanetaryEntries.Count);
                Assert.IsTrue(aliceState.PlanetaryEntries.ContainsKey("5|Hydrates"));
            }
        }

        [TestMethod]
        public void GateOn_CrossAgencyPlanetaryMutation_Dropped_NoEcho_NoPersist()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2c-a2");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2c-b2");

                // Plant a vessel stamped with ALICE's agency.
                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Bob sends a planetary mutation claiming Alice's vessel.
                var msg = BuildPlanetaryMsg(vesselId, bodyIndex: 5, resourceName: "Karbonite", quantity: 999.0);
                bob.SendMessage<AgencyCliMsg>(msg);

                // No echo arrives at Bob (router dropped the entry, no upsert).
                var stray = bob.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Bob received an echo for a cross-agency-claimed entry — rejection failed.");

                // Neither agency's state holds the cross-claimed entry.
                Assert.AreEqual(0, AgencySystem.Agencies[aliceAgency].PlanetaryEntries.Count);
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgency].PlanetaryEntries.Count);
            }
        }

        [TestMethod]
        public void GateOn_HandshakeCatchup_ShipsEmptyDictOnFirstConnect()
        {
            // Catch-up is unconditional under gate=on — even an empty dict ships
            // so a returning client mirror can distinguish "no per-agency
            // planetary balances yet" from "server didn't send catch-up". The
            // Slice C catchup fires AFTER the Slice B kolony catchup per
            // HandshakeSystem ordering, so we drain both.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-mksr2c-c1";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);

                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // AgencyHandshake → AgencyState → KolonyState (Slice B) →
                // PlanetaryState (Slice C). The MockNetClient inbox preserves
                // out-of-order arrivals so we can drain each by type.
                var hs = alice.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(hs);
                var state = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(state);
                var kolony = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(kolony, "Kolony catch-up (Slice B prerequisite) must arrive before planetary.");

                var planetary = alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(planetary,
                    "Connect-time planetary catch-up did not arrive — pre-Slice-C client mirror would not know the empty state.");
                Assert.AreEqual(0, planetary.EntryCount,
                    "Empty-dict catch-up must carry EntryCount=0.");
                Assert.AreEqual(state.AgencyId, planetary.AgencyId,
                    "Catch-up AgencyId must match the owner's assigned agency.");
            }
        }

        [TestMethod]
        public void GateOn_CrossAgencySpoof_WireSuppliedAgencyIdIgnored()
        {
            // [Phase 3 Slice C / consumer-lens SHOULD FIX SF#7] The router
            // class XML claims "server IGNORES the wire-supplied AgencyId"
            // and AgencyPlanetarySender leaves it Guid.Empty by convention.
            // This case end-to-end pins the trust posture: Alice sends with
            // msg.AgencyId set to Bob's agency; the entry must land in
            // ALICE's state (server-derived from PlayerName), Alice's echo
            // must carry Alice's agency, Bob's state must stay empty.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2c-sp1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2c-sp2");
                Assert.AreNotEqual(aliceAgency, bobAgency);

                // Plant a vessel stamped with ALICE's agency so the cross-
                // agency check passes for Alice's identity (the spoof attack
                // we want to pin is the WIRE-side AgencyId override, not a
                // vessel-stamp claim).
                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Alice sends a planetary mutation but spoofs AgencyId = bob's.
                var msg = BuildPlanetaryMsg(vesselId, bodyIndex: 5, resourceName: "Hydrates", quantity: 1.5);
                msg.AgencyId = bobAgency; // The spoof attempt.
                alice.SendMessage<AgencyCliMsg>(msg);

                // Alice's echo carries Alice's agency (server-derived from
                // PlayerName), NOT the wire-supplied spoof.
                var echo = alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Alice didn't receive owner-only echo (spoof should not block routing).");
                Assert.AreEqual(aliceAgency, echo.AgencyId,
                    "Echo's AgencyId must be the server-derived owner's agency (aliceAgency), NOT the wire-supplied spoof (bobAgency).");

                // Entry lands in Alice's state, not Bob's.
                Assert.AreEqual(1, AgencySystem.Agencies[aliceAgency].PlanetaryEntries.Count,
                    "Entry must route to Alice's state (server-derived from sender), not Bob's.");
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgency].PlanetaryEntries.Count,
                    "Bob's state must stay empty — wire-supplied AgencyId is IGNORED.");

                // Bob receives nothing — privacy preserved despite spoof.
                var stray = bob.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Bob received an echo for Alice's spoofed mutation — privacy violated.");
            }
        }

        [TestMethod]
        public void GateOff_NoPlanetaryEcho_DualModeSilence()
        {
            // Under gate=off the entire AgencyPlanetaryState wire is silent:
            // postfix doesn't emit (client-side gate), router would early-return
            // false (server-side gate), no echo, no catchup. Same shape as
            // Slice B's kolony dual-mode-silence test.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test precondition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                // Handshake under gate=off — agency wire is silent.
                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-mksr2c-go";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);
                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // No catchup planetary state arrives under gate=off.
                var noCatchup = alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(noCatchup,
                    "Catchup AgencyPlanetaryStateMsgData arrived under gate=off — dual-mode silence violated.");

                // Send a planetary mutation as a buggy-client simulation.
                var vesselId = Guid.NewGuid();
                var vessel = new Vessel(SampleVesselText.Value);
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));
                var msg = BuildPlanetaryMsg(vesselId, bodyIndex: 1, resourceName: "Hydrates", quantity: 5.0);
                alice.SendMessage<AgencyCliMsg>(msg);

                // No echo flows back — server router's gate=off early-return drops the inbound.
                var stray = alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Server emitted AgencyPlanetaryStateMsgData echo under gate=off — dual-mode silence violated.");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyPlanetaryStateMsgData BuildPlanetaryMsg(Guid vesselId, int bodyIndex, string resourceName, double quantity)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyPlanetaryStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyPlanetaryEntry
                {
                    OwningVesselId = vesselId,
                    BodyIndex = bodyIndex,
                    ResourceName = resourceName,
                    StoredQuantity = quantity,
                },
            };
            return msg;
        }

        /// <summary>
        /// Performs the handshake + drains the mandatory agency catchup chain
        /// (AgencyHandshake + AgencyState + AgencyKolony-catchup +
        /// AgencyPlanetary-catchup). Returns the agency id.
        /// </summary>
        private static Guid HandshakeAndDrainAgencyMessages(MockNetClient client, string playerName)
        {
            var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
            handshake.PlayerName = playerName;
            handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
            handshake.KspVersion = "1.12.5";
            client.SendMessage<HandshakeCliMsg>(handshake);

            var reply = client.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(reply, $"Handshake reply missing for {playerName}.");
            Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response,
                $"Handshake rejected for {playerName}: " + reply.Reason);

            var hs = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(hs, $"Did not receive AgencyHandshakeMsgData for {playerName}.");
            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyStateMsgData for {playerName}.");

            // Drain Slice B kolony catchup (always sent under gate=on; landing
            // BEFORE planetary per HandshakeSystem ordering).
            var kolony = client.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(kolony, $"Did not receive AgencyKolonyStateMsgData catch-up for {playerName}.");

            // Drain the Slice C planetary catchup.
            var planetary = client.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(planetary, $"Did not receive AgencyPlanetaryStateMsgData catch-up for {playerName}.");

            return state.AgencyId;
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
