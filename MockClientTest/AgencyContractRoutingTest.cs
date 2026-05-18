using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.ShareProgress;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17d — end-to-end harness coverage for <see cref="AgencyContractRouter"/>'s
    /// wire path. Verifies the Q6 hybrid contract routing decisions from spec §2:
    /// <list type="number">
    ///   <item>Under gate=on, an Active contract sent by Alice's mock client arrives at
    ///        Alice as a <see cref="AgencyContractMsgData"/> AND is NOT relayed to Bob
    ///        as the legacy <see cref="ShareProgressContractsMsgData"/> (privacy rule,
    ///        spec §10 Q1).</item>
    ///   <item>Under gate=on, an Offered contract sent by Alice is NOT echoed back as
    ///        <see cref="AgencyContractMsgData"/> (Offered contracts stay in the shared
    ///        pool — Q6 commitment a) and NOT relayed to Bob (the projector / scenario
    ///        path delivers them on demand, not as a push).</item>
    ///   <item>Per-agency contracts persist into <see cref="AgencyState.Contracts"/>
    ///        and round-trip to disk via the existing <see cref="AgencySystem.SaveAgency"/>
    ///        path.</item>
    ///   <item>Under gate=off, the existing shared-agency behaviour is unchanged —
    ///        Alice's <see cref="ShareProgressContractsMsgData"/> is relayed to Bob
    ///        (dual-mode silence).</item>
    /// </list>
    /// Unit-level classification + upsert logic is covered in
    /// <c>ServerTest/AgencyContractRouterTest.cs</c>; this suite covers the
    /// integration with <see cref="ShareContractsSystem"/> + wire delivery.
    /// </summary>
    [TestClass]
    public class AgencyContractRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_ActiveContract_RoutesToOwnerOnly_NotToPeers()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017d-alice");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017d-bob");

                var contractGuid = Guid.NewGuid();
                alice.SendMessage<ShareProgressCliMsg>(BuildContractsMsg(contractGuid, "Active"));

                // Alice (owner) receives an AgencyContractMsgData echo.
                var echo = alice.WaitForReply<AgencyContractMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyContractMsgData echo.");
                Assert.AreEqual(1, echo.ContractCount);
                Assert.AreEqual(contractGuid, echo.Contracts[0].ContractGuid);

                // Bob (peer) does NOT receive the legacy ShareProgressContractsMsgData
                // relay nor an AgencyContractMsgData for a contract he doesn't own.
                // Privacy rule: under gate=on, no peer ever learns of another agency's
                // post-Accept contract state.
                var strayShare = bob.WaitForReply<ShareProgressContractsMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayShare,
                    "Peer received a relayed ShareProgressContractsMsgData under gate=on — privacy rule violated.");
                var strayAgency = bob.WaitForReply<AgencyContractMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayAgency,
                    "Peer received an AgencyContractMsgData for a contract they don't own.");

                // Per-agency persistence: the contract is recorded in Alice's AgencyState.
                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017d-alice"];
                var aliceState = AgencySystem.Agencies[aliceAgencyId];
                Assert.AreEqual(1, aliceState.Contracts.Count,
                    "Active contract was not stored in Alice's per-agency Contracts list.");
                Assert.AreEqual(contractGuid, aliceState.Contracts[0].ContractGuid);
                Assert.AreEqual("Active", aliceState.Contracts[0].State);
            }
        }

        [TestMethod]
        public void GateOn_OfferedContract_StaysInSharedScenario_NoOwnerEcho_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017d-offer-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017d-offer-b");

                var contractGuid = Guid.NewGuid();
                alice.SendMessage<ShareProgressCliMsg>(BuildContractsMsg(contractGuid, "Offered"));

                // Q6 commitment (a) — no Offered persistence per-agency. Owner does
                // NOT receive an AgencyContractMsgData for the Offered entry.
                var strayEcho = alice.WaitForReply<AgencyContractMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayEcho,
                    "Owner received an AgencyContractMsgData echo for an Offered contract — Q6 commitment (a) violated.");

                // Privacy rule under gate=on: no peer relay. (In a future stage the
                // projector will deliver Offered contracts via SendScenarioModules; for
                // now they just live in the shared scenario, picked up on scene change.)
                var strayShare = bob.WaitForReply<ShareProgressContractsMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayShare,
                    "Peer received a relayed ShareProgressContractsMsgData under gate=on for an Offered contract.");

                // Alice's per-agency Contracts list stays empty — Offered never persists per-agency.
                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017d-offer-a"];
                Assert.AreEqual(0, AgencySystem.Agencies[aliceAgencyId].Contracts.Count,
                    "Offered contract leaked into per-agency storage — Q6 commitment (a) violated.");
            }
        }

        [TestMethod]
        public void GateOff_ContractReceived_StillRelaysToPeer_SharedAgencyPathUnchanged()
        {
            // Dual-mode silence: with PerAgencyCareer=false the router opts out and the
            // existing shared-agency relay+scenario-write path runs unchanged. A peer
            // learns of the inbound contract via ShareProgressContractsMsgData relay.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test pre-condition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeWithoutAgency(alice, "h-017d-off-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeWithoutAgency(bob, "h-017d-off-b");

                var contractGuid = Guid.NewGuid();
                alice.SendMessage<ShareProgressCliMsg>(BuildContractsMsg(contractGuid, "Active"));

                // Bob (peer) receives the relayed contract via the unchanged shared path.
                var relayed = bob.WaitForReply<ShareProgressContractsMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Under gate=off, peer must receive ShareProgressContractsMsgData relay (existing behaviour).");
                Assert.AreEqual(1, relayed.ContractCount);
                Assert.AreEqual(contractGuid, relayed.Contracts[0].ContractGuid);

                // Alice does NOT receive an AgencyContractMsgData echo (gate is off).
                var strayAgency = alice.WaitForReply<AgencyContractMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayAgency,
                    "AgencyContractMsgData emitted while PerAgencyCareer=false — dual-mode silence violated.");
            }
        }

        [TestMethod]
        public void GateOn_PerAgencyContract_PersistsAcrossSaveLoad()
        {
            // The router's persistence path: contracts stored in AgencyState.Contracts
            // must survive a SaveAgency round-trip through Universe/Agencies/{id}.txt.
            // Without this, server restart drops every in-flight per-agency contract.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017d-persist");

                var contractGuid = Guid.NewGuid();
                alice.SendMessage<ShareProgressCliMsg>(BuildContractsMsg(contractGuid, "Completed"));

                // Wait for the owner echo so we know the upsert + SaveAgency have run.
                var echo = alice.WaitForReply<AgencyContractMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo);

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017d-persist"];
                var diskPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies",
                    aliceAgencyId.ToString("N") + ".txt");
                Assert.IsTrue(File.Exists(diskPath),
                    $"SaveAgency did not produce {diskPath}.");

                // Read disk DIRECTLY rather than via AgencySystem.LoadAgency (which is
                // registry-first and would serve the in-memory object even if persistence
                // failed). Same pattern Stage 5.16a established for persistence assertions.
                var persisted = AgencyState.Parse(File.ReadAllText(diskPath));
                Assert.AreEqual(1, persisted.Contracts.Count,
                    "Per-agency contract was not persisted to disk through Serialize/Parse.");
                Assert.AreEqual(contractGuid, persisted.Contracts[0].ContractGuid);
                Assert.AreEqual("Completed", persisted.Contracts[0].State);
            }
        }

        [TestMethod]
        public void GateOn_AcceptingContract_RemovesGuidFromSharedOfferedPool()
        {
            // [Stage 5.17d upgrade-lens review — MUST FIX] When Alice accepts an Offered
            // contract under gate=on, the matching guid must be removed from the shared
            // ContractSystem scenario's CONTRACTS node. Without this, Bob's next
            // SendScenarioModules would still ship the entry as Offered and he could
            // "accept" the same contract independently — duplicate per-agency claims.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            var seededGuid = Guid.NewGuid();
            // Plant an Offered contract in the shared scenario to simulate CC's pool.
            var contractsNode = new LunaConfigNode.CfgNode.ConfigNode("") { Name = "ContractSystem" };
            var contractsRoot = new LunaConfigNode.CfgNode.ConfigNode("") { Name = "CONTRACTS" };
            contractsNode.AddNode(contractsRoot);
            var offeredEntry = new LunaConfigNode.CfgNode.ConfigNode("") { Name = "CONTRACT" };
            offeredEntry.CreateValue(new LunaConfigNode.CfgNode.CfgNodeValue<string, string>("guid", seededGuid.ToString("N")));
            offeredEntry.CreateValue(new LunaConfigNode.CfgNode.CfgNodeValue<string, string>("state", "Offered"));
            offeredEntry.CreateValue(new LunaConfigNode.CfgNode.CfgNodeValue<string, string>("type", "TestContract"));
            contractsRoot.AddNode(offeredEntry);
            ScenarioStoreSystem.CurrentScenarios["ContractSystem"] = contractsNode;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017d-accept");

                // Alice "accepts" the contract — the wire ShareProgressContractsMsgData
                // carries the new state=Active.
                alice.SendMessage<ShareProgressCliMsg>(BuildContractsMsg(seededGuid, "Active"));

                // Wait for the echo so we know the router has run.
                var echo = alice.WaitForReply<AgencyContractMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner echo did not arrive after Accept.");

                // Drain the asynchronous remove from the shared pool (the router's Task.Run
                // path for ScenarioDataUpdater.RemoveContractFromSharedOfferedPool runs
                // independently of the echo send). Poll up to 2s with 50ms steps.
                var deadline = DateTime.UtcNow.AddSeconds(2);
                var stillPresent = true;
                while (DateTime.UtcNow < deadline)
                {
                    stillPresent = ScenarioStillContainsGuid(seededGuid);
                    if (!stillPresent) break;
                    System.Threading.Thread.Sleep(50);
                }

                Assert.IsFalse(stillPresent,
                    "Shared ContractSystem scenario's CONTRACTS node still carries the guid after Accept — peer agencies would re-Accept the same Offered slot.");
            }
        }

        [TestMethod]
        public void GateOn_OwnerReconnect_ReceivesContractCatchupForPersistedContracts()
        {
            // [Stage 5.17d consumer-lens review — MUST FIX] A returning player whose
            // AgencyState.Contracts persisted across a server restart must receive their
            // entire Active+Finished pool via AgencyContractMsgData at handshake. Without
            // this, the Stage 5.18a client mirror lands empty on every reconnect and the
            // player can't see their in-flight contracts until they mutate something.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            const string playerName = "h-017d-recon";

            // First connection: seed two contracts into Alice's per-agency state via
            // the production router path.
            var firstGuid = Guid.NewGuid();
            var secondGuid = Guid.NewGuid();
            using (var first = new MockNetClient())
            {
                Assert.IsTrue(first.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(first, playerName);

                var batch = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressContractsMsgData>();
                batch.Contracts = new[]
                {
                    BuildContractInfo(firstGuid, "Active"),
                    BuildContractInfo(secondGuid, "Completed"),
                };
                batch.ContractCount = 2;
                first.SendMessage<ShareProgressCliMsg>(batch);

                var echo = first.WaitForReply<AgencyContractMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "First-session echo did not arrive.");
                Assert.AreEqual(2, echo.ContractCount);
            }

            // Second connection: the player reconnects. The handshake must include a
            // catch-up AgencyContractMsgData carrying the persisted entries. (The agency
            // registry is in-process for the harness — we're simulating a reconnect, not
            // a true server restart — but the catch-up path runs from HandshakeSystem on
            // the same auth code path either way.)
            using (var second = new MockNetClient())
            {
                Assert.IsTrue(second.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(second, playerName);

                var catchup = second.WaitForReply<AgencyContractMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(catchup,
                    "Reconnecting owner did NOT receive AgencyContractMsgData catch-up — Stage 5.18a mirror would land empty.");
                Assert.AreEqual(2, catchup.ContractCount,
                    "Catch-up batch should carry every persisted contract (Active + Completed).");
                var guids = catchup.Contracts.Take(catchup.ContractCount).Select(c => c.ContractGuid).ToArray();
                CollectionAssert.Contains(guids, firstGuid);
                CollectionAssert.Contains(guids, secondGuid);
            }
        }

        private static bool ScenarioStillContainsGuid(Guid contractGuid)
        {
            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ContractSystem", out var scenario))
                return false;
            lock (Server.System.Scenario.ScenarioDataUpdater.GetSemaphore("ContractSystem"))
            {
                var contractsNode = scenario.GetNode("CONTRACTS")?.Value;
                if (contractsNode == null) return false;
                foreach (var entry in contractsNode.GetNodes("CONTRACT"))
                {
                    var raw = entry.Value.GetValue("guid")?.Value;
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (Guid.TryParse(raw, out var parsed) && parsed == contractGuid)
                        return true;
                }
                return false;
            }
        }

        [TestMethod]
        public void GateOn_MixedBatch_OfferedSharedActivePerAgency_ClassifiedCorrectly()
        {
            // A realistic contract batch can include both Offered (CC's pre-loader
            // refreshing the pool) and post-Accept entries. The router must classify
            // each entry independently — one Offered alongside one Active in the same
            // ShareProgressContractsMsgData should NOT route either as if they were
            // identical. This is the test that PlagueNZ's full-isolation didn't have
            // (and is the closest unit-style harness equivalent of the CC soak in 5.18e).
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017d-mixed");

                var activeGuid = Guid.NewGuid();
                var offeredGuid = Guid.NewGuid();
                var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressContractsMsgData>();
                msg.Contracts = new[]
                {
                    BuildContractInfo(activeGuid, "Active"),
                    BuildContractInfo(offeredGuid, "Offered"),
                };
                msg.ContractCount = msg.Contracts.Length;
                alice.SendMessage<ShareProgressCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyContractMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Active half of the mixed batch did not echo to owner.");
                Assert.AreEqual(1, echo.ContractCount,
                    "Owner echo should contain ONLY the per-agency entries — Offered must be filtered out.");
                Assert.AreEqual(activeGuid, echo.Contracts[0].ContractGuid);

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017d-mixed"];
                var aliceState = AgencySystem.Agencies[aliceAgencyId];
                Assert.AreEqual(1, aliceState.Contracts.Count,
                    "Per-agency Contracts list should contain exactly the Active entry, not the Offered one.");
                Assert.AreEqual(activeGuid, aliceState.Contracts[0].ContractGuid);
                Assert.IsFalse(aliceState.Contracts.Any(c => c.ContractGuid == offeredGuid),
                    "Offered entry leaked into per-agency storage — Q6 commitment (a) violated.");
            }
        }

        private static ShareProgressContractsMsgData BuildContractsMsg(Guid contractGuid, string state)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressContractsMsgData>();
            msg.Contracts = new[] { BuildContractInfo(contractGuid, state) };
            msg.ContractCount = 1;
            return msg;
        }

        private static ContractInfo BuildContractInfo(Guid contractGuid, string state)
        {
            var configNodeBody = "guid = " + contractGuid.ToString("N") + "\nstate = " + state + "\nprestige = 0\nseed = 1234";
            var bytes = Encoding.UTF8.GetBytes(configNodeBody);
            return new ContractInfo
            {
                ContractGuid = contractGuid,
                Data = bytes,
                NumBytes = bytes.Length,
            };
        }

        private static void HandshakeAndDrainAgencyMessages(MockNetClient client, string playerName)
        {
            HandshakeWithoutAgency(client, playerName);
            // Drain the auto-registration AgencyHandshake + AgencyState so the
            // subsequent assertions about contract messages aren't racing the
            // handshake-time agency wire.
            var hs = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(hs, $"Did not receive AgencyHandshake for {playerName}.");
            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyState for {playerName}.");
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
