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
    /// Phase 3 Slice D-2 — end-to-end coverage for <see cref="AgencyOrbitalRouter"/>'s
    /// wire path + the connect-time catchup wired into <c>HandshakeSystem</c>
    /// by Slice D-1. Same harness shape as Slice C's
    /// <see cref="AgencyPlanetaryRoutingTest"/>; differences trace to (a) the
    /// Guid-keyed TransferGuid partition (vs vessel-and-body / body-and-resource
    /// composite strings in B/C), (b) the asymmetric privacy rule keyed on
    /// DESTINATION vessel (Origin is informational) per
    /// <see cref="AgencyOrbitalRouter"/> XML, and (c) the
    /// <see cref="AgencyOrbitalTransferEntry.PayloadBytes"/> tail which Slice
    /// D-1 defensively copies on store.
    ///
    /// <para><b>Why the client-side Harmony surface isn't tested here.</b>
    /// MockClientTest has no KSP runtime — <c>OrbitalLogisticsTransferRequest</c>
    /// instances + <c>ScenarioOrbitalLogistics</c> coroutines aren't available
    /// in the test environment. The four Harmony patches
    /// (<c>OrbitalLogisticsTransferRequest_DeliverPrefix</c> +
    /// the three state-machine postfixes) are pinned by
    /// <c>LmpClientTest/OrbitalDeliveryGateDecisionTest</c> at the pure-helper
    /// level. Two-client smoke (§7.d) is the integration boundary that
    /// covers the in-game prefix + postfix wiring. Here we cover the
    /// SERVER-SIDE wire path: routing + projection + catchup + cross-agency
    /// rejection + dual-mode silence.</para>
    ///
    /// <para><b>Six integration cases</b> matching pre-spec §7.c list (the
    /// "&gt;1024 transfers stress" sub-case in the pre-spec is folded into
    /// a simpler 3-transfer catchup test because the MockClientTest harness
    /// doesn't reproduce KSP's per-frame cadence and Slice D-1's chunking
    /// is already pinned by <c>AgencyOrbitalRouterTest</c> at the unit
    /// level).</para>
    /// </summary>
    [TestClass]
    public class AgencyOrbitalRoutingTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_SameAgencyOrbitalMutation_RoutesToOwner_NoPeerLeak()
        {
            // The state-machine postfix on the OWNING peer (Alice) emits a
            // single entry per transition. Server routes by DESTINATION
            // vessel's OwningAgencyId → Alice's agency. Bob (different agency)
            // never receives a per-agency echo (privacy rule).
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2d-a1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2d-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency, "Alice/Bob must have distinct agencies.");

                // Plant a destination vessel stamped with Alice's agency.
                var destVesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(destVesselId, aliceVessel));

                var transferGuid = Guid.NewGuid();
                var msg = BuildOrbitalMsg(transferGuid, originId: Guid.NewGuid(), destinationId: destVesselId,
                    status: AgencyOrbitalTransferEntry.StatusLaunched, startTime: 1000d, duration: 300d);
                alice.SendMessage<AgencyCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyOrbitalStateMsgData echo.");
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual(transferGuid, echo.Entries[0].TransferGuid);
                Assert.AreEqual(destVesselId, echo.Entries[0].DestinationVesselId);
                Assert.AreEqual(AgencyOrbitalTransferEntry.StatusLaunched, echo.Entries[0].Status);
                Assert.AreEqual(aliceAgency, echo.AgencyId,
                    "Echo's AgencyId must be the routed owner's agency (server-derived, not wire-supplied).");

                // Bob receives nothing orbital-shaped — privacy rule.
                var stray = bob.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Peer received AgencyOrbitalStateMsgData under gate=on — privacy rule violated.");

                // Alice's per-agency state holds the entry under the
                // TransferGuid partition key.
                var aliceState = AgencySystem.Agencies[aliceAgency];
                Assert.AreEqual(1, aliceState.OrbitalTransfers.Count);
                Assert.IsTrue(aliceState.OrbitalTransfers.ContainsKey(transferGuid));
                Assert.AreEqual(AgencyOrbitalTransferEntry.StatusLaunched, aliceState.OrbitalTransfers[transferGuid].Status);
            }
        }

        [TestMethod]
        public void GateOn_CrossAgencyOrbitalMutation_Dropped_NoEcho_NoPersist()
        {
            // The asymmetric privacy rule (router XML): cross-agency check
            // is on the DESTINATION vessel's owner. Bob (different agency
            // from the destination's owner) sends a mutation; router drops
            // the entry; no state mutation, no echo.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2d-a2");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2d-b2");

                // Destination vessel stamped with ALICE's agency.
                var destVesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(destVesselId, aliceVessel));

                // Bob claims a mutation against Alice's destination.
                var msg = BuildOrbitalMsg(Guid.NewGuid(), originId: Guid.NewGuid(), destinationId: destVesselId,
                    status: AgencyOrbitalTransferEntry.StatusLaunched, startTime: 2000d, duration: 600d);
                bob.SendMessage<AgencyCliMsg>(msg);

                // No echo arrives at Bob — fully-rejected batch under the
                // no-echo contract (Slice D-1 router XML).
                var stray = bob.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Bob received echo for a cross-agency-claimed entry — rejection failed.");

                // Neither agency's state holds the cross-claimed entry.
                Assert.AreEqual(0, AgencySystem.Agencies[aliceAgency].OrbitalTransfers.Count);
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgency].OrbitalTransfers.Count);
            }
        }

        [TestMethod]
        public void GateOn_HandshakeCatchup_ShipsEmptyDictOnFirstConnect()
        {
            // Slice D-1 wired SendOrbitalCatchupTo into HandshakeSystem
            // immediately after the Slice C planetary catchup. Empty dict
            // ships zero-entry (per AgencyOrbitalStateMsgData XML lines
            // 165-179 — distinct from the postfix-side sender which
            // early-returns on empty). The mirror needs the empty state to
            // distinguish "no per-agency transfers yet" from "unsynced".
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-mksr2d-c1";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);

                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // Drain preceding catchup messages in HandshakeSystem order:
                // AgencyHandshake → AgencyState → AgencyContract →
                // AgencyKolony (Slice B) → AgencyPlanetary (Slice C) →
                // AgencyOrbital (Slice D-1). Type-based drain doesn't care
                // about cross-channel arrival order.
                Assert.IsNotNull(alice.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5)));
                var state = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(state);
                Assert.IsNotNull(alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5)),
                    "Kolony catch-up (Slice B prerequisite) must arrive before orbital.");
                Assert.IsNotNull(alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5)),
                    "Planetary catch-up (Slice C prerequisite) must arrive before orbital.");

                var orbital = alice.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(orbital,
                    "Connect-time orbital catch-up did not arrive — pre-Slice-D-1 client mirror would not know empty state.");
                Assert.AreEqual(0, orbital.EntryCount,
                    "Empty-dict catch-up must carry EntryCount=0.");
                Assert.AreEqual(state.AgencyId, orbital.AgencyId,
                    "Catch-up AgencyId must match the owner's assigned agency.");
            }
        }

        [TestMethod]
        public void GateOn_OrbitalCatchup_DeliversPersistedTransfers_OnReconnect()
        {
            // Persistence + catchup integration. Plant 3 transfers in
            // Alice's per-agency state, fresh-connect Alice, verify the
            // catchup batch carries all three with stable TransferGuid
            // keying.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            // Bootstrap Alice once to mint her agency, then disconnect.
            Guid aliceAgency;
            using (var bootstrap = new MockNetClient())
            {
                Assert.IsTrue(bootstrap.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                aliceAgency = HandshakeAndDrainAgencyMessages(bootstrap, "h-mksr2d-cu1");
            }

            // Seed three persisted transfers in Alice's agency state
            // directly. We bypass the router because the goal is to test
            // the catchup-on-reconnect path, not the routing path
            // (covered in case 1).
            var aliceState = AgencySystem.Agencies[aliceAgency];
            var transferGuids = new[]
            {
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
            };
            for (var i = 0; i < transferGuids.Length; i++)
            {
                aliceState.OrbitalTransfers[transferGuids[i]] = new AgencyOrbitalTransferEntry
                {
                    TransferGuid = transferGuids[i],
                    OriginVesselId = Guid.NewGuid(),
                    DestinationVesselId = Guid.NewGuid(),
                    Status = AgencyOrbitalTransferEntry.StatusLaunched,
                    StartTime = 5000d + i,
                    Duration = 600d,
                    PayloadBytes = new byte[] { 0x01, 0x02, 0x03 },
                    NumBytes = 3,
                };
            }

            // Fresh reconnect. HandshakeSystem fires SendOrbitalCatchupTo
            // unconditionally under gate=on; we expect a single batch with
            // EntryCount=3 (no chunking — 3 entries is well below
            // MaxEntryCount=1024).
            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-mksr2d-cu1";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);

                Assert.AreEqual(HandshakeReply.HandshookSuccessfully,
                    alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5)).Response);
                Assert.IsNotNull(alice.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5)));
                Assert.IsNotNull(alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5)));
                Assert.IsNotNull(alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5)));
                Assert.IsNotNull(alice.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5)));

                var orbital = alice.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(orbital, "Orbital catch-up missing on reconnect with persisted transfers.");
                Assert.AreEqual(3, orbital.EntryCount,
                    "Catchup must deliver all three persisted transfers.");

                // Round-trip TransferGuid set — order isn't part of the
                // contract (server iterates Values() snapshot) but the
                // SET must match.
                var receivedGuids = orbital.Entries.Take(orbital.EntryCount)
                    .Select(e => e.TransferGuid).ToHashSet();
                foreach (var g in transferGuids)
                    Assert.IsTrue(receivedGuids.Contains(g), $"Transfer {g:N} missing from catchup batch.");
            }
        }

        [TestMethod]
        public void GateOn_WireSuppliedAgencyIdIgnored_ServerDerivedFromPlayerName()
        {
            // Trust posture pin (matches Slice C's spoof test). Alice sends
            // an orbital state but spoofs AgencyId = bob's. Router ignores
            // wire AgencyId, derives from authenticated PlayerName. Entry
            // lands in Alice's state; Alice's echo carries Alice's agency;
            // Bob's state stays empty.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2d-sp1");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2d-sp2");
                Assert.AreNotEqual(aliceAgency, bobAgency);

                // Destination stamped to Alice (the cross-agency check on
                // destination passes for Alice's identity).
                var destVesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(destVesselId, aliceVessel));

                var transferGuid = Guid.NewGuid();
                var msg = BuildOrbitalMsg(transferGuid, originId: Guid.NewGuid(), destinationId: destVesselId,
                    status: AgencyOrbitalTransferEntry.StatusLaunched, startTime: 6000d, duration: 120d);
                msg.AgencyId = bobAgency; // The spoof attempt.
                alice.SendMessage<AgencyCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Alice didn't receive owner-only echo (spoof should not block routing).");
                Assert.AreEqual(aliceAgency, echo.AgencyId,
                    "Echo's AgencyId must be the server-derived owner's agency, NOT the wire-supplied spoof.");

                Assert.AreEqual(1, AgencySystem.Agencies[aliceAgency].OrbitalTransfers.Count,
                    "Entry must route to Alice's state (server-derived from sender), not Bob's.");
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgency].OrbitalTransfers.Count,
                    "Bob's state must stay empty — wire-supplied AgencyId is IGNORED.");

                var stray = bob.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Bob received echo for Alice's spoofed mutation — privacy violated.");
            }
        }

        [TestMethod]
        public void GateOff_NoOrbitalEcho_DualModeSilence()
        {
            // Under gate=off the entire per-agency orbital wire is silent:
            // postfix doesn't emit (client-side gate), router early-returns
            // false (server-side gate), no catchup ships. Same shape as
            // Slice B/C dual-mode silence tests.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test precondition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-mksr2d-go";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully,
                    alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5)).Response);

                // No catchup orbital state arrives under gate=off.
                var noCatchup = alice.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(noCatchup,
                    "Catchup AgencyOrbitalStateMsgData arrived under gate=off — dual-mode silence violated.");

                // Send an orbital mutation as buggy-client simulation.
                var destVesselId = Guid.NewGuid();
                var vessel = new Vessel(SampleVesselText.Value);
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(destVesselId, vessel));
                var msg = BuildOrbitalMsg(Guid.NewGuid(), originId: Guid.NewGuid(), destinationId: destVesselId,
                    status: AgencyOrbitalTransferEntry.StatusLaunched, startTime: 7000d, duration: 30d);
                alice.SendMessage<AgencyCliMsg>(msg);

                var stray = alice.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Server emitted AgencyOrbitalStateMsgData echo under gate=off — dual-mode silence violated.");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyOrbitalStateMsgData BuildOrbitalMsg(
            Guid transferGuid, Guid originId, Guid destinationId,
            int status, double startTime, double duration)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyOrbitalTransferEntry
                {
                    TransferGuid = transferGuid,
                    OriginVesselId = originId,
                    DestinationVesselId = destinationId,
                    Status = status,
                    StartTime = startTime,
                    Duration = duration,
                    PayloadBytes = new byte[] { 0xCA, 0xFE },
                    NumBytes = 2,
                },
            };
            return msg;
        }

        /// <summary>
        /// Handshake + drain the mandatory agency catchup chain
        /// (AgencyHandshake + AgencyState + AgencyKolony + AgencyPlanetary +
        /// AgencyOrbital). Returns the agency id.
        ///
        /// <para><b>Cross-file ordering dependency:</b> the catchup chain
        /// here pins the SAME ordering as <c>Server\System\HandshakeSystem.cs</c>'s
        /// post-auth catchup sequence (AgencyHandshake → AgencyState →
        /// AgencyKolony → AgencyPlanetary → AgencyOrbital). The
        /// <c>MockNetClient.WaitForReply&lt;T&gt;</c> inbox is by-type
        /// non-destructive (s13 fix), so a future HandshakeSystem reorder
        /// won't break THESE tests — but a missing-from-the-server-side
        /// message would block on the 5s timeout and fail every test in
        /// this file simultaneously. Future Slice E or 5.18-series authors
        /// adding new agency-channel messages between the handshake and the
        /// per-agency catchup should ALSO update this drain helper.</para>
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

            Assert.IsNotNull(client.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5)),
                $"Did not receive AgencyKolonyStateMsgData catch-up for {playerName}.");
            Assert.IsNotNull(client.WaitForReply<AgencyPlanetaryStateMsgData>(TimeSpan.FromSeconds(5)),
                $"Did not receive AgencyPlanetaryStateMsgData catch-up for {playerName}.");
            Assert.IsNotNull(client.WaitForReply<AgencyOrbitalStateMsgData>(TimeSpan.FromSeconds(5)),
                $"Did not receive AgencyOrbitalStateMsgData catch-up for {playerName}.");

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
