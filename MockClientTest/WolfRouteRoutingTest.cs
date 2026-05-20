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

namespace MockClientTest
{
    /// <summary>
    /// Phase 4 Slice C — end-to-end coverage for <see cref="AgencyWolfRouteRouter"/>'s
    /// wire path. Brings up the in-process server + a real Lidgren client and
    /// asserts the per-agency router's decisions wire-end-to-end:
    /// <list type="number">
    ///   <item>Same-agency route mutation: echoes to the owner via
    ///        <see cref="AgencyWolfRouteStateMsgData"/>; peer receives nothing
    ///        (privacy rule, spec §10 Q1). Routes are body/biome-keyed (no
    ///        vessel-proxy auth — two agencies could legitimately have routes
    ///        between the same endpoints).</item>
    ///   <item>Connect-time catch-up: <see cref="AgencySystemSender.SendWolfRouteCatchupTo"/>
    ///        ships the owner's persisted <see cref="AgencyState.WolfRoutes"/> dict
    ///        (including the empty-dict case so the future client mirror can
    ///        distinguish "fresh agency" from "server skipped catchup").</item>
    ///   <item>Gate-off dual-mode silence: under <c>PerAgencyCareer=false</c>, no
    ///        <see cref="AgencyWolfRouteStateMsgData"/> ever flows.</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class WolfRouteRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_SameAgencyRouteMutation_RoutesToOwner_NoPeerLeak()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-wolfr4-a1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-wolfr4-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency, "Alice/Bob must have distinct agencies.");

                // Alice sends a route mutation. Routes are body+biome composite-
                // keyed; no vessel-proxy auth means the router upserts under the
                // sender's authoritative agency regardless of route key.
                var msg = BuildRouteMsg("Duna", "Lowlands", "Mun", "Highlands", payload: 1500);
                alice.SendMessage<AgencyCliMsg>(msg);

                // Alice receives the owner-only echo.
                var echo = alice.WaitForReply<AgencyWolfRouteStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyWolfRouteStateMsgData echo.");
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual(1500, echo.Entries[0].Payload);
                Assert.AreEqual("Duna", echo.Entries[0].OriginBody);
                Assert.AreEqual("Mun", echo.Entries[0].DestinationBody);
                Assert.AreEqual(aliceAgency, echo.AgencyId,
                    "Echo's AgencyId must be the routed owner's agency (server-derived, not wire-supplied).");

                // Bob receives nothing route-shaped (privacy rule).
                var stray = bob.WaitForReply<AgencyWolfRouteStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Peer received AgencyWolfRouteStateMsgData under gate=on — privacy rule violated.");

                // Alice's per-agency state holds the entry under the composite key.
                var aliceState = AgencySystem.Agencies[aliceAgency];
                Assert.AreEqual(1, aliceState.WolfRoutes.Count);
                Assert.IsTrue(aliceState.WolfRoutes.ContainsKey("Duna|Lowlands|Mun|Highlands"));
            }
        }

        [TestMethod]
        public void GateOn_HandshakeCatchup_ShipsEmptyDictOnFirstConnect()
        {
            // Catch-up is unconditional under gate=on — even an empty dict ships
            // so a future client mirror can distinguish "no per-agency routes yet"
            // from "server didn't send catch-up". See SendWolfRouteCatchupTo XML.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-wolfr4-c1";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);

                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // Drain mandatory agency handshake / state. The MockNetClient inbox
                // preserves out-of-order messages, so we only need to grab the
                // type we care about (AgencyWolfRouteStateMsgData).
                var hs = alice.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(hs);
                var state = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(state);

                var routes = alice.WaitForReply<AgencyWolfRouteStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(routes,
                    "Connect-time route catch-up did not arrive — pre-Slice-C client mirror would not know the empty state.");
                Assert.AreEqual(0, routes.EntryCount,
                    "Empty-dict catch-up must carry EntryCount=0.");
                Assert.AreEqual(state.AgencyId, routes.AgencyId,
                    "Catch-up AgencyId must match the owner's assigned agency.");
            }
        }

        [TestMethod]
        public void GateOff_NoRouteEcho_DualModeSilence()
        {
            // Under gate=off the entire AgencyWolfRouteState wire is silent:
            // postfix doesn't emit (client-side gate), router would early-return
            // false (server-side gate), no echo, no catchup. Verify by sending
            // an AgencyWolfRouteStateMsgData explicitly (simulating a buggy/
            // spoofed peer) and asserting no echo flows back.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test precondition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                // Handshake under gate=off — agency wire is silent (no
                // AgencyHandshake or AgencyState arrives).
                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-wolfr4-go";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);
                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // No catchup route state arrives under gate=off.
                var noCatchup = alice.WaitForReply<AgencyWolfRouteStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(noCatchup,
                    "Catchup AgencyWolfRouteStateMsgData arrived under gate=off — dual-mode silence violated.");

                // Send a route mutation as a buggy-client simulation.
                var msg = BuildRouteMsg("Duna", "Lowlands", "Mun", "Highlands", payload: 500);
                alice.SendMessage<AgencyCliMsg>(msg);

                // No echo flows back — server router's gate=off early-return drops the inbound.
                var stray = alice.WaitForReply<AgencyWolfRouteStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray,
                    "Server emitted AgencyWolfRouteStateMsgData echo under gate=off — dual-mode silence violated.");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfRouteStateMsgData BuildRouteMsg(
            string originBody, string originBiome,
            string destBody, string destBiome,
            int payload)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyWolfRouteStateMsgData>();
            // AgencyId left at Guid.Empty — server ignores wire-supplied value.
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfRouteEntry
                {
                    OriginBody = originBody,
                    OriginBiome = originBiome,
                    DestinationBody = destBody,
                    DestinationBiome = destBiome,
                    Payload = payload,
                },
            };
            return msg;
        }

        /// <summary>
        /// Performs the handshake + drains the mandatory agency catchup
        /// messages (AgencyHandshake + AgencyState + the catchup family up
        /// through Slice C's WolfRouteState). Returns the agency id assigned
        /// by the server. Routes are queued AFTER WolfDepots per
        /// HandshakeSystem ordering — the inbox is type-buffered so order
        /// doesn't matter, but we drain both to verify both arrived.
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

            // Drain the route catchup. The depot catchup is also queued
            // (HandshakeSystem ships it first), but the type-buffered inbox
            // means we don't have to drain it in order; the route message
            // will be available regardless.
            var routes = client.WaitForReply<AgencyWolfRouteStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(routes, $"Did not receive AgencyWolfRouteStateMsgData catch-up for {playerName}.");

            return state.AgencyId;
        }
    }
}
