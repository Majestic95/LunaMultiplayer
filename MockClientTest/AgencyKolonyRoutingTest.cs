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
    /// Phase 3 Slice B — end-to-end coverage for <see cref="AgencyKolonyRouter"/>'s
    /// wire path. Brings up the in-process server + two real Lidgren clients,
    /// plants vessels into <see cref="VesselStoreSystem.CurrentVessels"/>, and
    /// asserts the per-agency router's decisions wire-end-to-end:
    /// <list type="number">
    ///   <item>Same-agency kolony mutation: echoes to the owner via
    ///        <see cref="AgencyKolonyStateMsgData"/>; peer receives nothing
    ///        (privacy rule, spec §10 Q1).</item>
    ///   <item>Cross-agency kolony mutation: server-side router drops the entry;
    ///        owner sees no echo carrying the dropped entry.</item>
    ///   <item>Unassigned-sentinel vessel (<see cref="Guid.Empty"/>): per spec
    ///        §10 Q3, any agency may interact; the entry is accepted.</item>
    ///   <item>Connect-time catch-up: the handshake-side
    ///        <see cref="AgencySystemSender.SendKolonyCatchupTo"/> ships the
    ///        owner's persisted <see cref="AgencyState.KolonyEntries"/> dict
    ///        (including the empty-dict case).</item>
    ///   <item>Per-agency persistence: a routed kolony entry round-trips through
    ///        <c>SaveAgency</c> + parse on disk via
    ///        <c>Universe/Agencies/{id}.txt</c>.</item>
    ///   <item>Gate-off dual-mode silence: under <c>PerAgencyCareer=false</c>,
    ///        no <see cref="AgencyKolonyStateMsgData"/> ever flows
    ///        (postfix would be no-op anyway; server-side also gates).</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class AgencyKolonyRoutingTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_SameAgencyKolonyMutation_RoutesToOwner_NoPeerLeak()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2-a1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency, "Alice/Bob must have distinct agencies.");

                // Plant a vessel stamped with Alice's agency.
                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Alice sends a kolony mutation for HER vessel.
                var msg = BuildKolonyMsg(vesselId, bodyIndex: 5, geology: 42.5);
                alice.SendMessage<AgencyCliMsg>(msg);

                // Alice receives the owner-only echo.
                var echo = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyKolonyStateMsgData echo.");
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual(42.5, echo.Entries[0].GeologyResearch);
                Assert.AreEqual(aliceAgency, echo.AgencyId,
                    "Echo's AgencyId must be the routed owner's agency (server-derived, not wire-supplied).");

                // Bob receives nothing kolony-shaped (privacy rule).
                var stray = bob.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Peer received AgencyKolonyStateMsgData under gate=on — privacy rule violated.");

                // Alice's per-agency state holds the entry under the composite key.
                var aliceState = AgencySystem.Agencies[aliceAgency];
                Assert.AreEqual(1, aliceState.KolonyEntries.Count);
                Assert.IsTrue(aliceState.KolonyEntries.ContainsKey($"{vesselId:N}|5"));
            }
        }

        [TestMethod]
        public void GateOn_CrossAgencyKolonyMutation_Dropped_NoEcho_NoPersist()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2-a2");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-mksr2-b2");

                // Plant a vessel stamped with ALICE's agency.
                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                // Bob sends a kolony mutation claiming Alice's vessel.
                var msg = BuildKolonyMsg(vesselId, bodyIndex: 5, geology: 999.0);
                bob.SendMessage<AgencyCliMsg>(msg);

                // No echo arrives at Bob (router dropped the entry, no upsert).
                var stray = bob.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Bob received an echo for a cross-agency-claimed entry — rejection failed.");

                // Neither agency's state holds the cross-claimed entry.
                Assert.AreEqual(0, AgencySystem.Agencies[aliceAgency].KolonyEntries.Count);
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgency].KolonyEntries.Count);
            }
        }

        [TestMethod]
        public void GateOn_UnassignedSentinelVessel_KolonyEntryAccepted()
        {
            // Spec §10 Q3: an Unassigned-sentinel vessel (OwningAgencyId == Guid.Empty)
            // may be interacted with by any agency until admin transferagency stamps it.
            // The router's cross-agency check must bypass for the sentinel.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2-sn");

                // Plant a vessel with the Unassigned sentinel (pre-0.31 vessel shape).
                var vesselId = Guid.NewGuid();
                var unassignedVessel = new Vessel(SampleVesselText.Value);
                unassignedVessel.OwningAgencyId = Guid.Empty;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, unassignedVessel));

                var msg = BuildKolonyMsg(vesselId, bodyIndex: 3, geology: 10.0);
                alice.SendMessage<AgencyCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo,
                    "Owner did not receive echo for Unassigned-sentinel vessel — spec §10 Q3 bypass missing.");
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual(10.0, echo.Entries[0].GeologyResearch);

                Assert.AreEqual(1, AgencySystem.Agencies[aliceAgency].KolonyEntries.Count);
            }
        }

        [TestMethod]
        public void GateOn_HandshakeCatchup_ShipsEmptyDictOnFirstConnect()
        {
            // Catch-up is unconditional under gate=on — even an empty dict ships so
            // a returning client mirror can distinguish "no per-agency kolony yet"
            // from "server didn't send catch-up". See SendKolonyCatchupTo XML.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-mksr2-c1";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);

                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // Drain mandatory agency catchup chain: AgencyHandshake → AgencyState
                // → KolonyState (empty). Other catchup messages (Contracts) may arrive
                // alongside but the kolony state MUST be present.
                var hs = alice.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(hs);
                var state = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(state);

                var kolony = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(kolony,
                    "Connect-time kolony catch-up did not arrive — pre-Slice-B client mirror would not know the empty state.");
                Assert.AreEqual(0, kolony.EntryCount,
                    "Empty-dict catch-up must carry EntryCount=0.");
                Assert.AreEqual(state.AgencyId, kolony.AgencyId,
                    "Catch-up AgencyId must match the owner's assigned agency.");
            }
        }

        [TestMethod]
        public void GateOn_KolonyEntryPersistsAcrossSaveLoad_DiskFileHasEntry()
        {
            // The router's persistence path: a routed entry must survive a
            // SaveAgency round-trip through Universe/Agencies/{id}.txt — without
            // this, server restart drops every in-flight per-agency kolony record.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-mksr2-ps");

                var vesselId = Guid.NewGuid();
                var aliceVessel = new Vessel(SampleVesselText.Value);
                aliceVessel.OwningAgencyId = aliceAgency;
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, aliceVessel));

                var msg = BuildKolonyMsg(vesselId, bodyIndex: 7, geology: 17.5);
                alice.SendMessage<AgencyCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo);

                // Read disk DIRECTLY (registry-served LoadAgency would mask a
                // persistence failure). Same pattern as the 5.17d contract
                // persistence test.
                var diskPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies",
                    aliceAgency.ToString("N") + ".txt");
                Assert.IsTrue(File.Exists(diskPath),
                    $"SaveAgency did not produce {diskPath}.");

                var persisted = AgencyState.Parse(File.ReadAllText(diskPath));
                Assert.AreEqual(1, persisted.KolonyEntries.Count,
                    "Per-agency kolony entry was not persisted to disk.");
                var expectedKey = $"{vesselId:N}|7";
                Assert.IsTrue(persisted.KolonyEntries.ContainsKey(expectedKey),
                    $"Disk-persisted dict missing key {expectedKey}.");
                Assert.AreEqual(17.5, persisted.KolonyEntries[expectedKey].GeologyResearch);
            }
        }

        [TestMethod]
        public void GateOff_NoKolonyEcho_DualModeSilence()
        {
            // Under gate=off the entire AgencyKolonyState wire is silent:
            // postfix doesn't emit (client-side gate), router would early-return
            // false (server-side gate), no echo, no catchup. Verify by sending
            // an AgencyKolonyStateMsgData explicitly (simulating a buggy/spoofed
            // peer) and asserting no echo flows back.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test precondition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                // Handshake under gate=off — agency wire is silent (no
                // AgencyHandshake or AgencyState arrives).
                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-mksr2-go";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);
                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // No catchup kolony state arrives under gate=off.
                var noCatchup = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(noCatchup,
                    "Catchup AgencyKolonyStateMsgData arrived under gate=off — dual-mode silence violated.");

                // Send a kolony mutation as a buggy-client simulation.
                var vesselId = Guid.NewGuid();
                var vessel = new Vessel(SampleVesselText.Value);
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));
                var msg = BuildKolonyMsg(vesselId, bodyIndex: 1, geology: 5.0);
                alice.SendMessage<AgencyCliMsg>(msg);

                // No echo flows back — server router's gate=off early-return drops the inbound.
                var stray = alice.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Server emitted AgencyKolonyStateMsgData echo under gate=off — dual-mode silence violated.");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyKolonyStateMsgData BuildKolonyMsg(Guid vesselId, int bodyIndex, double geology)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyKolonyStateMsgData>();
            // AgencyId left at Guid.Empty — server ignores wire-supplied value.
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyKolonyEntry
                {
                    VesselId = vesselId.ToString("N"),
                    BodyIndex = bodyIndex,
                    GeologyResearch = geology,
                },
            };
            return msg;
        }

        /// <summary>
        /// Performs the handshake + drains the four mandatory agency catchup
        /// messages (AgencyHandshake + AgencyState + AgencyContract-catchup +
        /// AgencyKolony-catchup). Returns the agency id assigned by the server.
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

            // Drain the kolony catchup (always sent under gate=on). The contract
            // catchup is NOT sent when zero contracts persist — typical for a
            // fresh test agency — so we don't drain it here. The MockNetClient
            // inbox preserves out-of-order messages, so any contract catchup
            // that does arrive stays buffered for explicit consumption.
            var kolony = client.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(kolony, $"Did not receive AgencyKolonyStateMsgData catch-up for {playerName}.");

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
