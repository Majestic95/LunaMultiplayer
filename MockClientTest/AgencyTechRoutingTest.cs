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
using System.Globalization;
using System.IO;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17e-4 — end-to-end coverage for <see cref="AgencyTechRouter"/>'s
    /// wire path. The router intercepts <see cref="ShareProgressTechnologyMsgData"/>
    /// under gate=on, persists the unlock to the sender's
    /// <see cref="AgencyState.TechNodes"/>, AND scopes BUG-025 dedup to the
    /// per-agency tree (Alice and Bob can independently unlock the same tech —
    /// only Alice's own re-purchase gets refunded). Closes two leaks from the
    /// session-15 audit:
    /// <list type="number">
    ///   <item>Cross-agency tech-tree leak — Alice's tech unlock no longer relays
    ///        to Bob's KSP via <see cref="ShareProgressTechnologyMsgData"/>.</item>
    ///   <item>Global BUG-025 dedup blocking independent same-tech purchases —
    ///        each agency runs its own tree.</item>
    /// </list>
    /// Unit-level early-return + persistence round-trip coverage lives in
    /// <c>ServerTest/AgencyTechRouterTest.cs</c>; this suite covers the
    /// integration with <see cref="ShareTechnologySystem"/> + wire delivery.
    /// </summary>
    [TestClass]
    public class AgencyTechRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_FirstTechUnlock_StoresPerAgency_NoPeerRelay()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e4-alice");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e4-bob");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e4-alice"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e4-bob"];

                alice.SendMessage<ShareProgressCliMsg>(BuildTechMsg("basicRocketry", cost: 5f));

                // Bob (peer) MUST NOT receive a relayed ShareProgressTechnologyMsgData —
                // the cross-agency tech-tree leak is the primary fix.
                var strayRelay = bob.WaitForReply<ShareProgressTechnologyMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayRelay,
                    "Peer received the relayed tech unlock under gate=on — cross-agency leak.");

                // Alice MUST NOT receive a rejection (this was a fresh unlock for her tree).
                var strayRejection = alice.WaitForReply<ShareProgressTechnologyRejectedMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayRejection,
                    "Owner received a BUG-025 rejection for a first-time unlock — per-agency dedup is broken.");

                // Per-agency persistence: Alice has it, Bob doesn't.
                Assert.IsTrue(AgencySystem.Agencies[aliceAgencyId].TechNodes.ContainsKey("basicRocketry"),
                    "Alice's AgencyState.TechNodes does not contain the unlocked tech.");
                Assert.IsFalse(AgencySystem.Agencies[bobAgencyId].TechNodes.ContainsKey("basicRocketry"),
                    "Bob's AgencyState.TechNodes contains a tech he never unlocked — leak.");

                // Disk persistence assertion (5.17e-3 round-1 precedent).
                AssertPersistedAgencyContainsTech(aliceAgencyId, "basicRocketry");
                AssertPersistedAgencyDoesNotContainTech(bobAgencyId, "basicRocketry");
            }
        }

        [TestMethod]
        public void GateOn_AliceAndBobIndependentlyUnlockSameTech_BothSucceed_NoRejections()
        {
            // The key per-agency BUG-025 fix: under shared-agency, the second purchaser
            // would get a rejection because the global tech-list already contained the
            // node. Under per-agency, each agency runs its own tree — both unlocks
            // succeed without interference.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e4-indep-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e4-indep-b");

                alice.SendMessage<ShareProgressCliMsg>(BuildTechMsg("survivability", cost: 15f));
                bob.SendMessage<ShareProgressCliMsg>(BuildTechMsg("survivability", cost: 15f));

                // Neither client receives a rejection — both purchases succeed in
                // their own agency tree.
                var aliceReject = alice.WaitForReply<ShareProgressTechnologyRejectedMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(aliceReject,
                    "Alice received a BUG-025 rejection for HER first-time unlock — global dedup leaked.");
                var bobReject = bob.WaitForReply<ShareProgressTechnologyRejectedMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(bobReject,
                    "Bob received a BUG-025 rejection — global dedup is still gating independent agencies.");

                // Both agencies have the tech in their respective trees.
                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e4-indep-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e4-indep-b"];
                Assert.IsTrue(AgencySystem.Agencies[aliceAgencyId].TechNodes.ContainsKey("survivability"));
                Assert.IsTrue(AgencySystem.Agencies[bobAgencyId].TechNodes.ContainsKey("survivability"));
            }
        }

        [TestMethod]
        public void GateOn_AliceUnlocksSameTechTwice_SecondGetsRejection_NoDoubleStore()
        {
            // Per-agency BUG-025: Alice's own re-purchase of a tech she already
            // owns DOES get refunded. The scope of dedup moved from "global tech list"
            // to "this agency's tech list" — same protection, just per-agency-scoped.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e4-dup");

                alice.SendMessage<ShareProgressCliMsg>(BuildTechMsg("advRocketry", cost: 25f));
                // First should NOT produce a rejection.
                var firstReject = alice.WaitForReply<ShareProgressTechnologyRejectedMsgData>(TimeSpan.FromMilliseconds(400));
                Assert.IsNull(firstReject, "First unlock unexpectedly got rejected.");

                // Second purchase of the SAME tech in the SAME agency: BUG-025 fires.
                alice.SendMessage<ShareProgressCliMsg>(BuildTechMsg("advRocketry", cost: 25f));
                var secondReject = alice.WaitForReply<ShareProgressTechnologyRejectedMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(secondReject,
                    "Alice's duplicate purchase did NOT get a BUG-025 rejection — per-agency dedup is broken.");
                Assert.AreEqual("advRocketry", secondReject.TechId);
                Assert.AreEqual(25f, secondReject.RefundScience, 0.001f,
                    "Rejection refund cost did not match the science the payload claimed.");

                // Per-agency state still has exactly ONE copy of the tech (dedup
                // worked — the duplicate didn't double-write).
                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e4-dup"];
                Assert.AreEqual(1, CountTechNodes(AgencySystem.Agencies[aliceAgencyId], "advRocketry"));
            }
        }

        [TestMethod]
        public void GateOnButSandboxMode_TechUnlock_StillRelaysToPeer_SharedAgencyPathUnchanged()
        {
            // [Round-1 upgrade-lens review] Covers (PerAgencyCareer=true + GameMode!=Career)
            // misconfiguration. AgencySystem.PerAgencyEnabled is false → router falls
            // through → legacy BUG-025 + shared-scenario relay path runs unchanged.
            // Closes the two-axis test hole same as 5.17e-3's
            // GateOnButSandboxMode_FundsMutation. Pins that future PerAgencyEnabled
            // refactors (e.g. accidentally adding `|| GameMode == Sandbox`) don't
            // silently activate per-agency routing in non-Career modes.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e4-sbx-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e4-sbx-b");

                alice.SendMessage<ShareProgressCliMsg>(BuildTechMsg("aerodynamics", cost: 8f));

                // Bob receives the legacy relay (proves router fell through).
                var relayed = bob.WaitForReply<ShareProgressTechnologyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive ShareProgressTechnologyMsgData under (gate=on+Sandbox) — router should have fallen through to legacy shared path.");
                Assert.AreEqual("aerodynamics", relayed.TechNode.Id);

                Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("h-017e4-sbx-a"),
                    "AgencySystem must not register player under (gate=on+Sandbox).");
            }
        }

        [TestMethod]
        public void GateOff_TechUnlock_StillRelaysToPeer_LegacyBugFix25PathUnchanged()
        {
            // Dual-mode silence: with gate off, the router opts out and the legacy
            // BUG-025 shared-scenario path runs unchanged. Alice's unlock relays to
            // Bob's KSP (pre-5.17e-4 behaviour).
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test pre-condition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e4-off-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e4-off-b");

                alice.SendMessage<ShareProgressCliMsg>(BuildTechMsg("electrics", cost: 18f));

                var relayed = bob.WaitForReply<ShareProgressTechnologyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive ShareProgressTechnologyMsgData under gate=off — dual-mode silence violated.");
                Assert.AreEqual("electrics", relayed.TechNode.Id);

                // No agency state writes under gate=off.
                Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("h-017e4-off-a"),
                    "AgencySystem must not register the player under gate=off.");
            }
        }

        // ----- helpers -----------------------------------------------------

        private static ShareProgressTechnologyMsgData BuildTechMsg(string techId, float cost)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressTechnologyMsgData>();
            msg.TechNode.Id = techId;
            var payload = $"id = {techId}\ncost = {cost.ToString(CultureInfo.InvariantCulture)}\nstate = Available";
            msg.TechNode.Data = Encoding.UTF8.GetBytes(payload);
            msg.TechNode.NumBytes = msg.TechNode.Data.Length;
            return msg;
        }

        private static int CountTechNodes(AgencyState agency, string techId)
        {
            // Dictionary keyed by TechId, so 0 or 1.
            return agency.TechNodes.ContainsKey(techId) ? 1 : 0;
        }

        private static void AssertPersistedAgencyContainsTech(Guid agencyId, string techId)
        {
            var state = LoadPersistedAgency(agencyId);
            Assert.IsTrue(state.TechNodes.ContainsKey(techId),
                $"Agency {agencyId:N}: persisted file does not contain tech {techId} — SaveAgency did not persist the router's mutation.");
        }

        private static void AssertPersistedAgencyDoesNotContainTech(Guid agencyId, string techId)
        {
            var path = Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
            if (!File.Exists(path))
                return; // No file → no leak by definition.
            var state = AgencyState.Parse(File.ReadAllText(path));
            Assert.IsFalse(state.TechNodes.ContainsKey(techId),
                $"Agency {agencyId:N}: persisted file contains tech {techId} — cross-agency leak made it to disk.");
        }

        private static AgencyState LoadPersistedAgency(Guid agencyId)
        {
            var path = Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
            Assert.IsTrue(File.Exists(path),
                $"Expected per-agency file at {path}; SaveAgency in the router did not run.");
            return AgencyState.Parse(File.ReadAllText(path));
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
