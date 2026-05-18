using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.ShareProgress;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17e-6 — end-to-end coverage for <see cref="AgencyProgressRouter"/>
    /// (Strategy / Achievement / FacilityUpgrade). Same shape as 5.17e-5.
    /// </summary>
    [TestClass]
    public class AgencyProgressRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_Strategy_RoutesPerAgency_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e6-strat-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e6-strat-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e6-strat-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e6-strat-b"];

                alice.SendMessage<ShareProgressCliMsg>(BuildStratMsg("AggressiveNegotiations"));

                var stray = bob.WaitForReply<ShareProgressStrategyMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray, "Peer received Strategy under gate=on — cross-agency leak.");

                Assert.IsTrue(AgencySystem.Agencies[aliceAgencyId].Strategies.ContainsKey("AggressiveNegotiations"));
                Assert.IsFalse(AgencySystem.Agencies[bobAgencyId].Strategies.ContainsKey("AggressiveNegotiations"));
            }
        }

        [TestMethod]
        public void GateOn_Achievement_RoutesPerAgency_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e6-ach-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e6-ach-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e6-ach-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e6-ach-b"];

                alice.SendMessage<ShareProgressCliMsg>(BuildAchMsg("Kerbin/RocketLaunch"));

                var stray = bob.WaitForReply<ShareProgressAchievementsMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray, "Peer received Achievement under gate=on — cross-agency leak.");

                Assert.IsTrue(AgencySystem.Agencies[aliceAgencyId].Achievements.ContainsKey("Kerbin/RocketLaunch"));
                Assert.IsFalse(AgencySystem.Agencies[bobAgencyId].Achievements.ContainsKey("Kerbin/RocketLaunch"));
            }
        }

        [TestMethod]
        public void GateOn_FacilityUpgrade_RoutesPerAgency_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e6-fac-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e6-fac-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e6-fac-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e6-fac-b"];

                alice.SendMessage<ShareProgressCliMsg>(BuildFacMsg("SpaceCenter/LaunchPad", 0.5f));

                var stray = bob.WaitForReply<ShareProgressFacilityUpgradeMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray, "Peer received FacilityUpgrade under gate=on — cross-agency leak.");

                Assert.AreEqual(0.5f, AgencySystem.Agencies[aliceAgencyId].FacilityLevels["SpaceCenter/LaunchPad"]);
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgencyId].FacilityLevels.Count);
            }
        }

        [TestMethod]
        public void GateOff_Strategy_StillRelaysToPeer()
        {
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer);
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e6-off-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e6-off-b");
                alice.SendMessage<ShareProgressCliMsg>(BuildStratMsg("offStrategy"));
                var relayed = bob.WaitForReply<ShareProgressStrategyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed, "Peer did not receive Strategy under gate=off — dual-mode silence violated.");
            }
        }

        [TestMethod]
        public void GateOnButSandboxMode_FacilityUpgrade_StillRelaysToPeer()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e6-sbx-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e6-sbx-b");
                alice.SendMessage<ShareProgressCliMsg>(BuildFacMsg("SpaceCenter/LaunchPad", 0.5f));
                var relayed = bob.WaitForReply<ShareProgressFacilityUpgradeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed, "Peer did not receive FacilityUpgrade under gate=on+Sandbox — router should have fallen through.");
            }
        }

        // ----- helpers -----------------------------------------------------

        private static ShareProgressStrategyMsgData BuildStratMsg(string name)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressStrategyMsgData>();
            msg.Strategy.Name = name;
            msg.Strategy.Data = Encoding.UTF8.GetBytes($"name = {name}\nfactor = 0.5");
            msg.Strategy.NumBytes = msg.Strategy.Data.Length;
            return msg;
        }

        private static ShareProgressAchievementsMsgData BuildAchMsg(string id)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressAchievementsMsgData>();
            msg.Id = id;
            msg.Data = Encoding.UTF8.GetBytes("completed = True");
            msg.NumBytes = msg.Data.Length;
            return msg;
        }

        private static ShareProgressFacilityUpgradeMsgData BuildFacMsg(string id, float normLevel)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressFacilityUpgradeMsgData>();
            msg.FacilityId = id;
            msg.NormLevel = normLevel;
            msg.Level = 1;
            return msg;
        }

        private static void HandshakeAndDrainAgencyMessages(MockNetClient client, string playerName)
        {
            HandshakeWithoutAgency(client, playerName);
            Assert.IsNotNull(client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5)));
            Assert.IsNotNull(client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5)));
        }

        private static void HandshakeAndNoAgencyExpected(MockNetClient client, string playerName)
        {
            HandshakeWithoutAgency(client, playerName);
            Assert.IsNull(client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromMilliseconds(200)));
        }

        private static void HandshakeWithoutAgency(MockNetClient client, string playerName)
        {
            var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
            handshake.PlayerName = playerName;
            handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
            handshake.KspVersion = "1.12.5";
            client.SendMessage<HandshakeCliMsg>(handshake);
            var reply = client.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(reply);
            Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);
        }
    }
}
