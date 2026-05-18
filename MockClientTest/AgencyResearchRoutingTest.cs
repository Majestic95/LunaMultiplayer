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
using System.IO;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17e-5 — end-to-end coverage for <see cref="AgencyResearchRouter"/>'s
    /// three TryRoute methods (ScienceSubject / PartPurchase / ExperimentalPart).
    /// Each gate-on test asserts: owner's per-agency collection is mutated, peer
    /// does NOT receive the legacy ShareProgress* relay, on-disk persistence is
    /// observable. Gate-off path verified for one surface (the dual-mode silence
    /// branch is structurally identical across all three, gated on the same
    /// <see cref="AgencySystem.PerAgencyEnabled"/> check).
    /// </summary>
    [TestClass]
    public class AgencyResearchRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_ScienceSubject_RoutesPerAgency_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e5-sub-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e5-sub-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-sub-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-sub-b"];

                alice.SendMessage<ShareProgressCliMsg>(BuildSubjectMsg("crewReport@KerbinSrf"));

                var stray = bob.WaitForReply<ShareProgressScienceSubjectMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray,
                    "Peer received the legacy ShareProgressScienceSubjectMsgData under gate=on — cross-agency leak.");

                Assert.IsTrue(AgencySystem.Agencies[aliceAgencyId].ScienceSubjects.ContainsKey("crewReport@KerbinSrf"),
                    "Alice's per-agency subjects missing the routed entry.");
                Assert.IsFalse(AgencySystem.Agencies[bobAgencyId].ScienceSubjects.ContainsKey("crewReport@KerbinSrf"),
                    "Bob's per-agency subjects gained an entry he never sent — leak.");

                AssertPersistedAgencyContainsSubject(aliceAgencyId, "crewReport@KerbinSrf");
            }
        }

        [TestMethod]
        public void GateOn_PartPurchase_RoutesPerAgency_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e5-part-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e5-part-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-part-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-part-b"];

                // Seed Alice's per-agency Tech tree with the parent tech so the
                // PartPurchase isn't dropped by the orphan-Tech guard (round-1
                // review MUST FIX — parts belong inside a Tech block in the KSP
                // scenario format, so purchases for an unknown Tech are dropped
                // matching the shared-scenario writer's behavior).
                var techText = "id = basicRocketry\ncost = 5\nstate = Available";
                AgencySystem.Agencies[aliceAgencyId].TechNodes["basicRocketry"] = new AgencyTechNodeEntry
                {
                    TechId = "basicRocketry",
                    Data = Encoding.UTF8.GetBytes(techText),
                    NumBytes = techText.Length,
                };

                alice.SendMessage<ShareProgressCliMsg>(BuildPartMsg("basicRocketry", "RTG10"));

                var stray = bob.WaitForReply<ShareProgressPartPurchaseMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray,
                    "Peer received the legacy ShareProgressPartPurchaseMsgData under gate=on — cross-agency leak.");

                var aliceAgency = AgencySystem.Agencies[aliceAgencyId];
                Assert.IsTrue(aliceAgency.PurchasedParts.ContainsKey("basicRocketry"));
                Assert.IsTrue(aliceAgency.PurchasedParts["basicRocketry"].Contains("RTG10"));

                Assert.AreEqual(0, AgencySystem.Agencies[bobAgencyId].PurchasedParts.Count,
                    "Bob's PurchasedParts gained an entry — leak.");
            }
        }

        [TestMethod]
        public void GateOn_ExperimentalPart_RoutesPerAgency_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e5-exp-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e5-exp-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-exp-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-exp-b"];

                alice.SendMessage<ShareProgressCliMsg>(BuildExpMsg("expPartA", 2));

                var stray = bob.WaitForReply<ShareProgressExperimentalPartMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray,
                    "Peer received the legacy ShareProgressExperimentalPartMsgData under gate=on — cross-agency leak.");

                Assert.AreEqual(2, AgencySystem.Agencies[aliceAgencyId].ExperimentalParts["expPartA"]);
                Assert.AreEqual(0, AgencySystem.Agencies[bobAgencyId].ExperimentalParts.Count);
            }
        }

        [TestMethod]
        public void GateOn_ExperimentalPart_CountZero_RemovesEntry()
        {
            // Verifies the count==0 → remove semantics match the shared writer.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e5-exp0");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-exp0"];

                alice.SendMessage<ShareProgressCliMsg>(BuildExpMsg("expPartB", 3));
                System.Threading.Thread.Sleep(200); // let the route apply
                Assert.AreEqual(3, AgencySystem.Agencies[aliceAgencyId].ExperimentalParts["expPartB"]);

                alice.SendMessage<ShareProgressCliMsg>(BuildExpMsg("expPartB", 0));
                System.Threading.Thread.Sleep(200);
                Assert.IsFalse(AgencySystem.Agencies[aliceAgencyId].ExperimentalParts.ContainsKey("expPartB"),
                    "ExperimentalPart count=0 must remove the entry per spec parity with shared writer.");
            }
        }

        [TestMethod]
        public void GateOn_PartPurchase_ProjectorMergesPartIntoSpliceTechBlock()
        {
            // [Round-1 review MUST FIX] Full router→projector→client loop for the
            // novel part-merge logic. Seeds Alice's per-agency Tech node so the
            // PartPurchase isn't dropped as an orphan; sends a PartPurchase; then
            // drives a ScenarioRequest and asserts the projected R&D scenario
            // contains a Tech block with the merged `part = X` value. This is the
            // ONLY e2e test that exercises the splice + merge path together — the
            // four "no peer relay" tests above only verify the writer-side.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e5-merge");
                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-merge"];

                // Seed: Alice's per-agency Tech tree contains "basicRocketry".
                // Wire-payload of a Tech node is bare key/value pairs (the form
                // ParseClientConfigNode expects + the form stored in AgencyState).
                var techText = "id = basicRocketry\ncost = 5\nstate = Available";
                AgencySystem.Agencies[aliceAgencyId].TechNodes["basicRocketry"] = new AgencyTechNodeEntry
                {
                    TechId = "basicRocketry",
                    Data = Encoding.UTF8.GetBytes(techText),
                    NumBytes = techText.Length,
                };

                // Plant the R&D scenario server-side so the projector has something
                // to splice into.
                Server.System.ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] =
                    new LunaConfigNode.CfgNode.ConfigNode("name = ResearchAndDevelopment\nsci = 0")
                    { Name = "ResearchAndDevelopment" };

                alice.SendMessage<ShareProgressCliMsg>(BuildPartMsg("basicRocketry", "RTG10"));
                System.Threading.Thread.Sleep(300); // let the router commit

                // Verify the persisted state has the part.
                Assert.IsTrue(AgencySystem.Agencies[aliceAgencyId].PurchasedParts.ContainsKey("basicRocketry"),
                    "Router did not store the part purchase.");

                // Drive a ScenarioRequest so SendScenarioModules runs + projector splices.
                alice.SendMessage<LmpCommon.Message.Client.ScenarioCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<
                        LmpCommon.Message.Data.Scenario.ScenarioRequestMsgData>());
                var reply = alice.WaitForReply<LmpCommon.Message.Data.Scenario.ScenarioDataMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply, "ScenarioRequest reply missing.");

                // Decode the R&D scenario bytes + assert the Tech-with-Part is there.
                // Scenario Data on the wire is the raw text bytes (no compression on this
                // path — matches the existing FindRootValue pattern in AgencyScenarioProjectionTest).
                string rndText = null;
                for (var i = 0; i < reply.ScenarioCount; i++)
                {
                    if (reply.ScenariosData[i].Module == "ResearchAndDevelopment")
                    {
                        var info = reply.ScenariosData[i];
                        rndText = Encoding.UTF8.GetString(info.Data, 0, info.NumBytes);
                        break;
                    }
                }

                Assert.IsNotNull(rndText, "R&D scenario module missing from ScenarioData reply.");
                StringAssert.Contains(rndText, "basicRocketry",
                    "Projected R&D scenario does not contain the spliced Tech block.");
                StringAssert.Contains(rndText, "RTG10",
                    "Per-agency part purchase did not merge into the spliced Tech block.");
            }
        }

        [TestMethod]
        public void GateOnButSandboxMode_ScienceSubject_StillRelaysToPeer()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e5-sbxA");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e5-sbxB");
                alice.SendMessage<ShareProgressCliMsg>(BuildSubjectMsg("sbxSubj"));
                var relayed = bob.WaitForReply<ShareProgressScienceSubjectMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive ScienceSubject under (gate=on+Sandbox) — router should have fallen through.");
            }
        }

        [TestMethod]
        public void GateOnButSandboxMode_PartPurchase_StillRelaysToPeer()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e5-pSbxA");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e5-pSbxB");
                alice.SendMessage<ShareProgressCliMsg>(BuildPartMsg("basicRocketry", "RTG10"));
                var relayed = bob.WaitForReply<ShareProgressPartPurchaseMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive PartPurchase under (gate=on+Sandbox) — router should have fallen through.");
            }
        }

        [TestMethod]
        public void GateOnButSandboxMode_ExperimentalPart_StillRelaysToPeer()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e5-eSbxA");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e5-eSbxB");
                alice.SendMessage<ShareProgressCliMsg>(BuildExpMsg("sbxExp", 1));
                var relayed = bob.WaitForReply<ShareProgressExperimentalPartMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive ExperimentalPart under (gate=on+Sandbox) — router should have fallen through.");
            }
        }

        [TestMethod]
        public void GateOn_PartPurchase_OrphanTechId_IsDroppedNotStored()
        {
            // [Round-1 review MUST FIX] Verify parity with shared-scenario writer:
            // a part purchase for a TechId NOT in the agency's TechNodes is a
            // no-op (silent drop with Warning log). Without this guard the router
            // would store an orphan PurchasedParts entry that the projector splices
            // nowhere — player silently loses the part.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e5-orph");
                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e5-orph"];

                // No TechNode for "uncolnockedTech" yet.
                alice.SendMessage<ShareProgressCliMsg>(BuildPartMsg("uncolnockedTech", "RTG10"));
                System.Threading.Thread.Sleep(300);

                Assert.IsFalse(AgencySystem.Agencies[aliceAgencyId].PurchasedParts.ContainsKey("uncolnockedTech"),
                    "Orphan part purchase was stored instead of being dropped.");
            }
        }

        [TestMethod]
        public void GateOff_ScienceSubject_StillRelaysToPeer_LegacyPathUnchanged()
        {
            // Dual-mode silence proven for one resource — the router opts out
            // identically across all three under PerAgencyEnabled=false.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer);

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e5-off-a");
                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e5-off-b");

                alice.SendMessage<ShareProgressCliMsg>(BuildSubjectMsg("relaySubject"));

                var relayed = bob.WaitForReply<ShareProgressScienceSubjectMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive ShareProgressScienceSubjectMsgData under gate=off — dual-mode silence violated.");
                Assert.AreEqual("relaySubject", relayed.ScienceSubject.Id);
            }
        }

        // ----- helpers -----------------------------------------------------

        private static ShareProgressScienceSubjectMsgData BuildSubjectMsg(string id)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressScienceSubjectMsgData>();
            msg.ScienceSubject.Id = id;
            var payload = $"id = {id}\ndataScale = 1.0";
            msg.ScienceSubject.Data = Encoding.UTF8.GetBytes(payload);
            msg.ScienceSubject.NumBytes = msg.ScienceSubject.Data.Length;
            return msg;
        }

        private static ShareProgressPartPurchaseMsgData BuildPartMsg(string techId, string partName)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressPartPurchaseMsgData>();
            msg.TechId = techId;
            msg.PartName = partName;
            return msg;
        }

        private static ShareProgressExperimentalPartMsgData BuildExpMsg(string partName, int count)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressExperimentalPartMsgData>();
            msg.PartName = partName;
            msg.Count = count;
            return msg;
        }

        private static void AssertPersistedAgencyContainsSubject(Guid agencyId, string subjectId)
        {
            var path = Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
            Assert.IsTrue(File.Exists(path), $"Expected per-agency file at {path}");
            var state = AgencyState.Parse(File.ReadAllText(path));
            Assert.IsTrue(state.ScienceSubjects.ContainsKey(subjectId),
                $"Agency {agencyId:N}: persisted file does not contain subject {subjectId}.");
        }

        private static void HandshakeAndDrainAgencyMessages(MockNetClient client, string playerName)
        {
            HandshakeWithoutAgency(client, playerName);
            var hs = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(hs, $"Did not receive AgencyHandshake for {playerName}.");
            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyState for {playerName}.");
        }

        private static void HandshakeAndNoAgencyExpected(MockNetClient client, string playerName)
        {
            HandshakeWithoutAgency(client, playerName);
            var stray = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromMilliseconds(200));
            Assert.IsNull(stray, $"Unexpected AgencyHandshake for {playerName} under gate=off.");
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
