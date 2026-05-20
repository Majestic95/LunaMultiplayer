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
    /// Phase 4 Slice D — end-to-end coverage for
    /// <see cref="AgencyWolfTerminalRouter"/>'s wire path. Same shape as
    /// <see cref="WolfHopperRoutingTest"/>.
    /// </summary>
    [TestClass]
    public class WolfTerminalRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_SameAgencyTerminalMutation_RoutesToOwner_NoPeerLeak()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-wolft4-a1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-wolft4-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency);

                // Terminal id is Guid.NewGuid().ToString("N") — no hyphens.
                var terminalId = Guid.NewGuid().ToString("N");
                Assert.IsFalse(terminalId.Contains("-"));

                var msg = BuildTerminalMsg(terminalId, "Duna", "Lowlands");
                alice.SendMessage<AgencyCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyWolfTerminalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo);
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual(terminalId, echo.Entries[0].Id,
                    "Echo's Id matches what we sent (N-form, no hyphens preserved).");
                Assert.AreEqual("Duna", echo.Entries[0].Body);
                Assert.AreEqual(aliceAgency, echo.AgencyId);

                var stray = bob.WaitForReply<AgencyWolfTerminalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Peer received terminal state — privacy rule violated.");

                Assert.AreEqual(1, AgencySystem.Agencies[aliceAgency].WolfTerminals.Count);
                Assert.IsTrue(AgencySystem.Agencies[aliceAgency].WolfTerminals.ContainsKey(terminalId));
            }
        }

        [TestMethod]
        public void GateOn_TerminalRemoval_DropsFromAgencyStateAndEchoes()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-wolft4-r1");

                var terminalId = Guid.NewGuid().ToString("N");
                var seed = BuildTerminalMsg(terminalId, "Duna", "Lowlands");
                alice.SendMessage<AgencyCliMsg>(seed);
                var seedEcho = alice.WaitForReply<AgencyWolfTerminalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, seedEcho.EntryCount);

                var removeMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyWolfTerminalStateMsgData>();
                removeMsg.EntryCount = 0;
                removeMsg.Entries = new AgencyWolfTerminalEntry[0];
                removeMsg.RemovedKeyCount = 1;
                removeMsg.RemovedKeys = new[] { terminalId };
                alice.SendMessage<AgencyCliMsg>(removeMsg);

                var removeEcho = alice.WaitForReply<AgencyWolfTerminalStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(removeEcho);
                Assert.AreEqual(0, removeEcho.EntryCount);
                Assert.AreEqual(1, removeEcho.RemovedKeyCount);
                Assert.AreEqual(terminalId, removeEcho.RemovedKeys[0]);

                Assert.AreEqual(0, AgencySystem.Agencies[aliceAgency].WolfTerminals.Count);
            }
        }

        [TestMethod]
        public void GateOff_NoTerminalEcho_DualModeSilence()
        {
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer);

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-wolft4-go";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);
                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                var noCatchup = alice.WaitForReply<AgencyWolfTerminalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(noCatchup, "Catchup terminal state arrived under gate=off.");

                var msg = BuildTerminalMsg(Guid.NewGuid().ToString("N"), "Duna", "Lowlands");
                alice.SendMessage<AgencyCliMsg>(msg);

                var stray = alice.WaitForReply<AgencyWolfTerminalStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Server emitted terminal echo under gate=off.");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfTerminalStateMsgData BuildTerminalMsg(string id, string body, string biome)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyWolfTerminalStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfTerminalEntry { Id = id, Body = body, Biome = biome },
            };
            return msg;
        }

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

            var terminals = client.WaitForReply<AgencyWolfTerminalStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(terminals, $"Did not receive AgencyWolfTerminalStateMsgData catchup for {playerName}.");

            return state.AgencyId;
        }
    }
}
