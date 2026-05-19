using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MockClientTest
{
    /// <summary>
    /// [Phase 4 Slice B-2] End-to-end coverage for
    /// <see cref="AgencyWolfDepotRouter"/>'s wire path. Brings up the
    /// in-process server + two real Lidgren clients and asserts the
    /// per-agency router's decisions wire-end-to-end:
    /// <list type="number">
    ///   <item>Same-agency depot mutation: echoes to the owner via
    ///        <see cref="AgencyWolfDepotStateMsgData"/>; peer receives nothing
    ///        (privacy rule, spec §10 Q1 — depots are body+biome-keyed but
    ///        partition per-agency).</item>
    ///   <item>Per-agency persistence: a routed depot entry lands in
    ///        <see cref="AgencyState.WolfDepots"/>.</item>
    ///   <item>Connect-time catch-up: the handshake-side
    ///        <see cref="AgencySystemSender.SendWolfDepotCatchupTo"/> ships
    ///        the owner's persisted dict (including the empty-dict case).</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class WolfDepotRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_DepotMutation_RoutesToOwner_NoPeerLeak()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgency(alice, "h-wolfd-a1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgency(bob, "h-wolfd-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency, "Alice/Bob must have distinct agencies.");

                // Alice sends a depot mutation.
                var msg = BuildDepotMsg("Duna", "Lowlands", isEstablished: true);
                alice.SendMessage<AgencyCliMsg>(msg);

                // Alice receives the owner-only echo.
                var echo = alice.WaitForReply<AgencyWolfDepotStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyWolfDepotStateMsgData echo.");
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual("Duna", echo.Entries[0].Body);
                Assert.AreEqual("Lowlands", echo.Entries[0].Biome);
                Assert.IsTrue(echo.Entries[0].IsEstablished);
                Assert.AreEqual(aliceAgency, echo.AgencyId,
                    "Echo's AgencyId must be the routed owner's agency (server-derived, not wire-supplied).");

                // Bob receives nothing depot-shaped (privacy rule).
                var stray = bob.WaitForReply<AgencyWolfDepotStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Peer received AgencyWolfDepotStateMsgData under gate=on — privacy rule violated.");

                // Alice's per-agency state holds the entry.
                var aliceState = AgencySystem.Agencies[aliceAgency];
                Assert.AreEqual(1, aliceState.WolfDepots.Count);
                Assert.IsTrue(aliceState.WolfDepots.ContainsKey("Duna|Lowlands"));
                Assert.IsTrue(aliceState.WolfDepots["Duna|Lowlands"].IsEstablished);
            }
        }

        [TestMethod]
        public void GateOn_Reconnect_FullDepotStateCatchUpReceived()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            // Pre-seed Alice's agency with 2 depots so the catchup is non-empty.
            // First connect mints the agency; we then directly populate
            // WolfDepots on the server-side state to simulate persisted prior-
            // session WOLF activity. (A purely-wire pre-seed would also work
            // but takes 2 handshake cycles — directly mutating AgencyState is
            // cheaper for this test's scope.)
            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgency(alice, "h-wolfd-rc");

                var state = AgencySystem.Agencies[aliceAgency];
                state.WolfDepots["Duna|Lowlands"] = new AgencyWolfDepotEntry
                {
                    Body = "Duna", Biome = "Lowlands", IsEstablished = true, IsSurveyed = true,
                };
                state.WolfDepots["Mun|Highlands"] = new AgencyWolfDepotEntry
                {
                    Body = "Mun", Biome = "Highlands", IsEstablished = false, IsSurveyed = false,
                };

                alice.Dispose();

                // Reconnect — should receive the 2-depot catchup.
                using (var alice2 = new MockNetClient())
                {
                    Assert.IsTrue(alice2.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                    var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                    handshake.PlayerName = "h-wolfd-rc";
                    handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                    handshake.KspVersion = "1.12.5";
                    alice2.SendMessage<HandshakeCliMsg>(handshake);

                    var reply = alice2.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                    Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                    // Drain the standard agency reconnect messages (Handshake +
                    // State + various catchups). The WolfDepot catchup fires after
                    // orbital.
                    alice2.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                    alice2.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));

                    var depotCatchup = alice2.WaitForReply<AgencyWolfDepotStateMsgData>(TimeSpan.FromSeconds(5));
                    Assert.IsNotNull(depotCatchup, "Did not receive AgencyWolfDepotStateMsgData catchup on reconnect.");
                    Assert.AreEqual(2, depotCatchup.EntryCount, "Catchup must include both pre-seeded depots.");
                    var bodies = new HashSet<string>(depotCatchup.Entries.Take(depotCatchup.EntryCount).Select(e => $"{e.Body}|{e.Biome}"));
                    Assert.IsTrue(bodies.Contains("Duna|Lowlands"));
                    Assert.IsTrue(bodies.Contains("Mun|Highlands"));
                }
            }
        }

        [TestMethod]
        public void GateOff_DepotMutation_NoRouting_DualModeSilence()
        {
            // Under gate=off, the server-side router's early-return drops the
            // inbound; no echo flows back; no agency state mutates.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-wolfd-off";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);

                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // No agency catchup under gate=off (the agency surface is silent).
                var stray = alice.WaitForReply<AgencyWolfDepotStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "AgencyWolfDepotStateMsgData arrived under gate=off — dual-mode silence violated.");

                // Send a depot mutation as a buggy-client simulation.
                var msg = BuildDepotMsg("Duna", "Lowlands", isEstablished: true);
                alice.SendMessage<AgencyCliMsg>(msg);

                // No echo flows back.
                var stray2 = alice.WaitForReply<AgencyWolfDepotStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray2,
                    "Server emitted AgencyWolfDepotStateMsgData echo under gate=off — dual-mode silence violated.");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfDepotStateMsgData BuildDepotMsg(string body, string biome, bool isEstablished)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyWolfDepotStateMsgData>();
            // AgencyId left at Guid.Empty — server ignores wire-supplied value.
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfDepotEntry
                {
                    Body = body,
                    Biome = biome,
                    IsEstablished = isEstablished,
                    IsSurveyed = false,
                },
            };
            return msg;
        }

        /// <summary>
        /// Performs the handshake + drains the mandatory agency catchup
        /// messages (AgencyHandshake + AgencyState + AgencyKolony-catchup +
        /// AgencyWolfDepot-catchup). Returns the agency id assigned by the
        /// server.
        /// </summary>
        private static Guid HandshakeAndDrainAgency(MockNetClient client, string playerName)
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

            // Drain the kolony catchup (always sent under gate=on, even when empty).
            var kolony = client.WaitForReply<AgencyKolonyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(kolony, $"Did not receive AgencyKolonyStateMsgData catch-up for {playerName}.");

            // Drain the wolf-depot catchup (always sent under gate=on, even when empty).
            var depotCatchup = client.WaitForReply<AgencyWolfDepotStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(depotCatchup, $"Did not receive AgencyWolfDepotStateMsgData catch-up for {playerName}.");

            return state.AgencyId;
        }
    }
}
