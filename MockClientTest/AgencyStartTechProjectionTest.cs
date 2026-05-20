using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Scenario;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// End-to-end coverage for the 2026-05-20 <c>EnsureStartTechSeeded</c> fix
    /// (server-side details in <c>ServerTest/AgencyStartTechSeedingTest.cs</c>).
    /// Pin the full flow: shared scenario contains the canonical <c>start</c> Tech
    /// node, a fresh player connects under PerAgencyCareer=true, and the projected
    /// <c>ResearchAndDevelopment</c> scenario delivered in response to
    /// <c>ScenarioRequestMsgData</c> still carries the start Tech with its starter
    /// parts. Without the seed the projector strips the start node and the player
    /// receives an empty tech tree (the live-soak symptom this fix addresses).
    /// </summary>
    [TestClass]
    public class AgencyStartTechProjectionTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void FreshPlayer_PerAgencyCareer_ProjectedRDIncludesStartTech()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            // Plant a shared ResearchAndDevelopment scenario containing only the
            // canonical start Tech node with three starter parts. The server's
            // RegisterAgency → EnsureStartTechSeeded → AgencyTechRouter splice
            // chain should land all three parts inside the projected Tech block.
            var rd = new ConfigNode("") { Name = "ResearchAndDevelopment" };
            rd.CreateValue(new CfgNodeValue<string, string>("sci", "0"));
            var start = new ConfigNode("") { Name = "Tech" };
            start.CreateValue(new CfgNodeValue<string, string>("id", "start"));
            start.CreateValue(new CfgNodeValue<string, string>("state", "Available"));
            start.CreateValue(new CfgNodeValue<string, string>("cost", "0"));
            start.CreateValue(new CfgNodeValue<string, string>("part", "mk1pod"));
            start.CreateValue(new CfgNodeValue<string, string>("part", "parachuteSingle"));
            start.CreateValue(new CfgNodeValue<string, string>("part", "basicFin"));
            rd.AddNode(start);
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = rd;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyState(alice, "h-start-alice");

                alice.SendMessage<ScenarioCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<ScenarioRequestMsgData>());
                var reply = alice.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply, "Alice did not receive ScenarioDataMsgData.");

                var rdBlob = FindScenarioBlob(reply, "ResearchAndDevelopment");
                Assert.IsNotNull(rdBlob, "ResearchAndDevelopment missing from ScenarioDataMsgData.");

                // The projected R&D must contain a Tech node carrying id=start AND
                // all three starter parts. Without the seed the projector's
                // strip-then-resplice path emits R&D with no Tech children at all.
                StringAssert.Contains(rdBlob, "id = start",
                    "Projected R&D must include 'id = start' — EnsureStartTechSeeded failed to land the start node.\n" + rdBlob);
                StringAssert.Contains(rdBlob, "part = mk1pod",
                    "Start node missing mk1pod after projection — seed bytes or splice path drifted.\n" + rdBlob);
                StringAssert.Contains(rdBlob, "part = parachuteSingle",
                    "Start node missing parachuteSingle after projection.\n" + rdBlob);
                StringAssert.Contains(rdBlob, "part = basicFin",
                    "Start node missing basicFin after projection.\n" + rdBlob);
            }
        }

        private static string FindScenarioBlob(ScenarioDataMsgData reply, string module)
        {
            for (var i = 0; i < reply.ScenarioCount; i++)
            {
                var info = reply.ScenariosData[i];
                if (info.Module == module)
                    return Encoding.UTF8.GetString(info.Data, 0, info.NumBytes);
            }
            return null;
        }

        private static void HandshakeAndDrainAgencyState(MockNetClient client, string playerName)
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

            // Drain the AgencyStateMsgData the server pushes on auth so it doesn't
            // sit in the inbox confusing the later WaitForReply<ScenarioDataMsgData>.
            var agencyState = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(agencyState, $"Did not receive AgencyStateMsgData for {playerName}.");
        }
    }
}
