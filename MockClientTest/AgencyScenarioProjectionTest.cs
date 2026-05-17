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
using Server.System.Agency;
using System;
using System.Globalization;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17c — end-to-end coverage for <see cref="AgencyScenarioProjector"/>'s
    /// wire path. Two clients with distinct agency states request the canonical
    /// ScenarioModule set; each receives Funding/Science/Reputation values overwritten
    /// with their own agency's scalars. A gate-off test asserts the shared scenarios
    /// pass through unchanged (dual-mode silence, spec §11).
    ///
    /// Field-level projection logic is unit-tested in
    /// <c>ServerTest/AgencyScenarioProjectorTest.cs</c>; this suite covers the
    /// SendScenarioModules wire-handler integration.
    /// </summary>
    [TestClass]
    public class AgencyScenarioProjectionTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void TwoPlayers_DistinctAgencies_ReceiveOwnProjectedCareerScalars()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            // Plant the three career scenarios server-side. Use ConfigNode parsing so the
            // canonical store matches what production LoadExistingScenarios would produce.
            PlantScenario("Funding", "name = Funding\nfunds = 0");
            PlantScenario("ResearchAndDevelopment", "name = ResearchAndDevelopment\nsci = 0");
            PlantScenario("Reputation", "name = Reputation\nrep = 0");

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndGetAgencyId(alice, "h-017c-alice");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndGetAgencyId(bob, "h-017c-bob");
                Assert.AreNotEqual(aliceAgency, bobAgency,
                    "Test setup: Alice and Bob must have distinct agencies.");

                // Set distinct, easy-to-distinguish career scalars per agency.
                AgencySystem.Agencies[aliceAgency].Funds = 100000;
                AgencySystem.Agencies[aliceAgency].Science = 500;
                AgencySystem.Agencies[aliceAgency].Reputation = 75;

                AgencySystem.Agencies[bobAgency].Funds = 22222;
                AgencySystem.Agencies[bobAgency].Science = 33;
                AgencySystem.Agencies[bobAgency].Reputation = -10;

                // Each client requests the scenario module set.
                alice.SendMessage<ScenarioCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<ScenarioRequestMsgData>());
                var aliceReply = alice.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(aliceReply, "Alice did not receive ScenarioDataMsgData.");

                bob.SendMessage<ScenarioCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<ScenarioRequestMsgData>());
                var bobReply = bob.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobReply, "Bob did not receive ScenarioDataMsgData.");

                // Alice sees her own values.
                Assert.AreEqual("100000", FindRootValue(aliceReply, "Funding", "funds"),
                    "Alice's Funding scenario did not carry her agency's funds.");
                Assert.AreEqual("500", FindRootValue(aliceReply, "ResearchAndDevelopment", "sci"),
                    "Alice's ResearchAndDevelopment scenario did not carry her agency's sci.");
                Assert.AreEqual("75", FindRootValue(aliceReply, "Reputation", "rep"),
                    "Alice's Reputation scenario did not carry her agency's rep.");

                // Bob sees his own values. Confirms cross-agency isolation —
                // Alice's values must NOT appear in Bob's response.
                Assert.AreEqual("22222", FindRootValue(bobReply, "Funding", "funds"),
                    "Bob's Funding scenario did not carry his agency's funds.");
                Assert.AreEqual("33", FindRootValue(bobReply, "ResearchAndDevelopment", "sci"),
                    "Bob's ResearchAndDevelopment scenario did not carry his agency's sci.");
                Assert.AreEqual("-10", FindRootValue(bobReply, "Reputation", "rep"),
                    "Bob's Reputation scenario did not carry his agency's rep.");

                // [Round-1 consumer-lens] Defensive cross-leak assertions per spec §10 Q1
                // (PrivateAgencyResources=true). Walk the full decoded payload — not just
                // the targeted key — and assert that the OTHER agency's scalar values do
                // not appear anywhere. Without these, a bug where the projector appended
                // a stale value or duplicated the blob would pass the per-key assertions
                // above but still leak data across agencies.
                var aliceText = DecodeAllScenarios(aliceReply);
                Assert.IsFalse(aliceText.Contains("22222"),
                    "Bob's funds (22222) leaked into Alice's scenario payload — privacy rule violated.");
                Assert.IsFalse(aliceText.Contains(" 33") && !aliceText.Contains("333"),
                    "Bob's sci (33) leaked into Alice's scenario payload.");
                var bobText = DecodeAllScenarios(bobReply);
                Assert.IsFalse(bobText.Contains("100000"),
                    "Alice's funds (100000) leaked into Bob's scenario payload — privacy rule violated.");
                Assert.IsFalse(bobText.Contains(" 500\n") || bobText.Contains(" 500\r"),
                    "Alice's sci (500) leaked into Bob's scenario payload.");
            }
        }

        /// <summary>Concatenates every scenario blob in a reply into one string. Used for
        /// defensive cross-leak assertions — a leak would show up somewhere in the
        /// combined payload even if it bypassed the targeted key.</summary>
        private static string DecodeAllScenarios(ScenarioDataMsgData reply)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < reply.ScenarioCount; i++)
            {
                var info = reply.ScenariosData[i];
                sb.AppendLine(info.Module);
                sb.AppendLine(Encoding.UTF8.GetString(info.Data, 0, info.NumBytes));
            }
            return sb.ToString();
        }

        [TestMethod]
        public void GateOff_ScenariosPassThroughUnchanged()
        {
            // Dual-mode silence (spec §11): with PerAgencyCareer=false, the projector
            // never runs and clients see the canonical shared-agency scalar values.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test pre-condition: reset must leave PerAgencyCareer=false.");

            PlantScenario("Funding", "name = Funding\nfunds = 88888");

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeWithoutAgency(client, "h-017c-off");

                client.SendMessage<ScenarioCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<ScenarioRequestMsgData>());
                var reply = client.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply);

                Assert.AreEqual("88888", FindRootValue(reply, "Funding", "funds"),
                    "Under gate=off the canonical shared-agency Funds value must reach the client unchanged.");
            }
        }

        [TestMethod]
        public void Sandbox_ProjectionSkipped_EvenUnderGateOn()
        {
            // Sandbox mode has no career scalars to project. Projector's Sandbox-bypass
            // ensures clients see the canonical (shared) scenario text. This pins the
            // dual-mode behaviour for the Sandbox vs Career game-mode axis independently
            // of the per-agency gate.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            PlantScenario("Funding", "name = Funding\nfunds = 77777");

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var clientAgency = HandshakeAndGetAgencyId(client, "h-017c-sandb");
                AgencySystem.Agencies[clientAgency].Funds = 11111;

                client.SendMessage<ScenarioCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<ScenarioRequestMsgData>());
                var reply = client.WaitForReply<ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply);

                Assert.AreEqual("77777", FindRootValue(reply, "Funding", "funds"),
                    "Sandbox bypass: canonical Funding value must reach the client unchanged.");
            }
        }

        private static void PlantScenario(string module, string text)
        {
            ScenarioStoreSystem.CurrentScenarios[module] = new ConfigNode(text) { Name = module };
        }

        /// <summary>Locate a scenario in the reply by module name, decode its bytes, and
        /// return the value of the requested root-level key. Asserts the scenario exists.</summary>
        private static string FindRootValue(ScenarioDataMsgData reply, string module, string key)
        {
            for (var i = 0; i < reply.ScenarioCount; i++)
            {
                var info = reply.ScenariosData[i];
                if (info.Module != module) continue;
                var text = Encoding.UTF8.GetString(info.Data, 0, info.NumBytes);
                foreach (var line in text.Split('\n'))
                {
                    // Root-level lines: no leading whitespace, "key = value" shape.
                    if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) continue;
                    var trimmed = line.TrimEnd('\r', '\n', ' ', '\t');
                    var eq = trimmed.IndexOf('=');
                    if (eq < 0) continue;
                    var k = trimmed.Substring(0, eq).TrimEnd();
                    if (k != key) continue;
                    return trimmed.Substring(eq + 1).TrimStart();
                }
                Assert.Fail($"Scenario '{module}' has no root key '{key}'. Body:\n{text}");
            }
            Assert.Fail($"ScenarioDataMsgData reply did not include module '{module}'.");
            return null; // unreachable
        }

        private static Guid HandshakeAndGetAgencyId(MockNetClient client, string playerName)
        {
            Assert.IsTrue(GameplaySettings.SettingsStore.PerAgencyCareer,
                "HandshakeAndGetAgencyId requires PerAgencyCareer=true.");
            HandshakeWithoutAgency(client, playerName);
            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyStateMsgData for {playerName}.");
            return state.AgencyId;
        }

        private static void HandshakeWithoutAgency(MockNetClient client, string playerName)
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
        }
    }
}
