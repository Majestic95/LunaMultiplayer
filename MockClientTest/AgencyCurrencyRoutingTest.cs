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

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.17e-3 — end-to-end coverage for <see cref="AgencyCurrencyRouter"/>'s
    /// wire path. Verifies the per-agency Funds / Science / Reputation routing
    /// closes the leak the session-15 audit identified
    /// (docs/research/05b-ksp-career-surface-audit.md):
    /// <list type="number">
    ///   <item>Under gate=on, Alice's Share* mutation updates Alice's AgencyState
    ///        and echoes an owner-only <see cref="AgencyStateMsgData"/> snapshot
    ///        back to Alice — Bob receives neither the legacy
    ///        <c>ShareProgress*MsgData</c> relay nor a stray
    ///        <see cref="AgencyStateMsgData"/> (privacy rule, spec §10 Q1).</item>
    ///   <item>Bob's AgencyState scalar is unchanged after Alice's mutation —
    ///        cross-agency leak guard.</item>
    ///   <item>Under gate=off, the existing shared-agency relay+scenario-write
    ///        path is unchanged — Alice's <see cref="ShareProgressFundsMsgData"/>
    ///        reaches Bob (dual-mode silence).</item>
    /// </list>
    /// Unit-level router branch coverage (gate-off bypass, null inputs) lives in
    /// <c>ServerTest/AgencyCurrencyRouterTest.cs</c>; this suite covers the
    /// integration with <see cref="Server.System.ShareFundsSystem"/> /
    /// <see cref="Server.System.ShareScienceSystem"/> /
    /// <see cref="Server.System.ShareReputationSystem"/> + wire delivery.
    /// </summary>
    [TestClass]
    public class AgencyCurrencyRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_FundsMutation_RoutesToOwnerEchoOnly_BobReceivesNothing()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e3-funds-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e3-funds-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e3-funds-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e3-funds-b"];
                var bobFundsBefore = AgencySystem.Agencies[bobAgencyId].Funds;

                var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
                msg.Funds = 99_999d;
                msg.Reason = "test:vessel-recovery";
                alice.SendMessage<ShareProgressCliMsg>(msg);

                // Alice (owner) receives an AgencyStateMsgData echo with the new Funds.
                var echo = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyStateMsgData echo for Funds mutation.");
                Assert.AreEqual(aliceAgencyId, echo.AgencyId);
                Assert.AreEqual(99_999d, echo.Funds, 0.0001);

                // Bob (peer) MUST NOT receive the legacy ShareProgressFundsMsgData relay.
                var strayShare = bob.WaitForReply<ShareProgressFundsMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayShare,
                    "Peer received the relayed ShareProgressFundsMsgData under gate=on — cross-agency leak.");

                // Bob MUST NOT receive an AgencyStateMsgData for Alice's agency (privacy rule).
                var strayState = bob.WaitForReply<AgencyStateMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayState,
                    $"Peer received an AgencyStateMsgData (AgencyId={strayState?.AgencyId:N}) — privacy rule violated.");

                // Per-agency persistence: Alice's state is updated, Bob's is unchanged.
                Assert.AreEqual(99_999d, AgencySystem.Agencies[aliceAgencyId].Funds, 0.0001,
                    "Alice's AgencyState.Funds was not updated by the router.");
                Assert.AreEqual(bobFundsBefore, AgencySystem.Agencies[bobAgencyId].Funds, 0.0001,
                    "Bob's AgencyState.Funds drifted — cross-agency mutation occurred.");

                // Round-1 review: assert disk-side persistence too. SaveAgency in the router
                // is what makes the in-memory update durable; a future refactor that wraps
                // SaveAgency in a condition (e.g. only-on-state-version-change) would silently
                // regress without this assertion.
                AssertPersistedAgencyFundsEqual(aliceAgencyId, 99_999d);
            }
        }

        [TestMethod]
        public void GateOn_ScienceMutation_RoutesToOwnerEchoOnly_BobReceivesNothing()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e3-sci-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e3-sci-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e3-sci-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e3-sci-b"];
                var bobSciBefore = AgencySystem.Agencies[bobAgencyId].Science;

                var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressScienceMsgData>();
                msg.Science = 1234.5f;
                msg.Reason = "test:experiment-transmit";
                alice.SendMessage<ShareProgressCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyStateMsgData echo for Science mutation.");
                Assert.AreEqual(aliceAgencyId, echo.AgencyId);
                Assert.AreEqual(1234.5d, echo.Science, 0.001);

                var strayShare = bob.WaitForReply<ShareProgressScienceMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayShare,
                    "Peer received the relayed ShareProgressScienceMsgData under gate=on — cross-agency leak.");

                var strayState = bob.WaitForReply<AgencyStateMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayState,
                    $"Peer received an AgencyStateMsgData (AgencyId={strayState?.AgencyId:N}) — privacy rule violated.");

                Assert.AreEqual(1234.5d, AgencySystem.Agencies[aliceAgencyId].Science, 0.001,
                    "Alice's AgencyState.Science was not updated by the router.");
                Assert.AreEqual(bobSciBefore, AgencySystem.Agencies[bobAgencyId].Science, 0.0001,
                    "Bob's AgencyState.Science drifted — cross-agency mutation occurred.");

                AssertPersistedAgencyScienceEqual(aliceAgencyId, 1234.5d);
            }
        }

        [TestMethod]
        public void GateOn_ReputationMutation_RoutesToOwnerEchoOnly_BobReceivesNothing()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(alice, "h-017e3-rep-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(bob, "h-017e3-rep-b");

                var aliceAgencyId = AgencySystem.AgencyByPlayerName["h-017e3-rep-a"];
                var bobAgencyId = AgencySystem.AgencyByPlayerName["h-017e3-rep-b"];
                var bobRepBefore = AgencySystem.Agencies[bobAgencyId].Reputation;

                var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressReputationMsgData>();
                msg.Reputation = 42.25f;
                msg.Reason = "test:contract-complete";
                alice.SendMessage<ShareProgressCliMsg>(msg);

                var echo = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyStateMsgData echo for Reputation mutation.");
                Assert.AreEqual(aliceAgencyId, echo.AgencyId);
                Assert.AreEqual(42.25d, echo.Reputation, 0.001);

                var strayShare = bob.WaitForReply<ShareProgressReputationMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayShare,
                    "Peer received the relayed ShareProgressReputationMsgData under gate=on — cross-agency leak.");

                var strayState = bob.WaitForReply<AgencyStateMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayState,
                    $"Peer received an AgencyStateMsgData (AgencyId={strayState?.AgencyId:N}) — privacy rule violated.");

                Assert.AreEqual(42.25d, AgencySystem.Agencies[aliceAgencyId].Reputation, 0.001,
                    "Alice's AgencyState.Reputation was not updated by the router.");
                Assert.AreEqual(bobRepBefore, AgencySystem.Agencies[bobAgencyId].Reputation, 0.0001,
                    "Bob's AgencyState.Reputation drifted — cross-agency mutation occurred.");

                AssertPersistedAgencyReputationEqual(aliceAgencyId, 42.25d);
            }
        }

        [TestMethod]
        public void GateOnButSandboxMode_FundsMutation_StillRelaysToPeer_SharedAgencyPathUnchanged()
        {
            // [Round-1 upgrade-lens review] Covers the (PerAgencyCareer=true + GameMode!=Career)
            // misconfiguration case. AgencySystem.PerAgencyEnabled is false → router falls
            // through → shared-agency relay+write path runs unchanged. Same observable
            // behaviour as gate=off, but exercised through a different code-path inside
            // PerAgencyEnabled — closes the two-axis test hole (PerAgencyCareer ✗ GameMode).
            // Per Stage 5.17e-1 boot-warning design, an operator running this configuration
            // already sees the misconfig warning at boot; this test pins that the runtime
            // behaviour stays safe even if they ignore the warning.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e3-sbx-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e3-sbx-b");

                var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
                msg.Funds = 12_345d;
                msg.Reason = "test:sandbox-misconfig";
                alice.SendMessage<ShareProgressCliMsg>(msg);

                // Bob receives the legacy relayed message — proves the router did NOT
                // intercept and the shared path ran. Misconfig = no per-agency routing.
                var relayed = bob.WaitForReply<ShareProgressFundsMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive ShareProgressFundsMsgData under (gate=on+Sandbox) — router should have fallen through.");
                Assert.AreEqual(12_345d, relayed.Funds, 0.0001);

                // No agency wire fires under PerAgencyEnabled=false.
                var strayAgencyState = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayAgencyState,
                    "Server emitted AgencyStateMsgData under (gate=on+Sandbox) — dual-mode silence violated.");

                // No registry entries either (5.17e-1 contract: LoadExistingAgencies +
                // OnPlayerAuthenticated are no-ops under PerAgencyEnabled=false).
                Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey("h-017e3-sbx-a"),
                    "AgencySystem must not register the player under (gate=on+Sandbox).");
            }
        }

        [TestMethod]
        public void GateOff_FundsMutation_StillRelaysToPeer_SharedAgencyPathUnchanged()
        {
            // Dual-mode silence: with PerAgencyCareer=false the router opts out and the
            // existing shared-agency relay+scenario-write path runs unchanged. Bob
            // learns of Alice's funds change via ShareProgressFundsMsgData relay,
            // matching the pre-5.17e-3 shared-agency behaviour. Pinned for ONE of the
            // three resources here — the dual-mode no-op path is structurally
            // identical across Funds / Science / Reputation, gated on a single
            // PerAgencyEnabled check in the router (covered exhaustively at the
            // ServerTest unit level), so triplicating this e2e test would just
            // multiply mock-harness wall time without pinning new behaviour.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test pre-condition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(alice, "h-017e3-off-a");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndNoAgencyExpected(bob, "h-017e3-off-b");

                var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
                msg.Funds = 55_555d;
                msg.Reason = "test:gate-off-relay";
                alice.SendMessage<ShareProgressCliMsg>(msg);

                // Bob receives the legacy relayed message (pre-5.17e-3 behaviour).
                var relayed = bob.WaitForReply<ShareProgressFundsMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed,
                    "Peer did not receive ShareProgressFundsMsgData under gate=off — dual-mode silence violated.");
                Assert.AreEqual(55_555d, relayed.Funds, 0.0001);

                // No agency wire fires under gate=off.
                var strayAgencyState = alice.WaitForReply<AgencyStateMsgData>(TimeSpan.FromMilliseconds(200));
                Assert.IsNull(strayAgencyState,
                    "Server emitted AgencyStateMsgData with gate off — dual-mode silence violated.");
            }
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
            // Gate-off → no agency messages should arrive. Short timeout because we're
            // proving absence, not waiting on something.
            var stray = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromMilliseconds(200));
            Assert.IsNull(stray, $"Unexpected AgencyHandshake for {playerName} under gate=off.");
        }

        private static void AssertPersistedAgencyFundsEqual(Guid agencyId, double expected)
        {
            var state = LoadPersistedAgency(agencyId);
            Assert.AreEqual(expected, state.Funds, 0.0001,
                $"Agency {agencyId:N}: persisted Funds on disk did not match the router's mutation (in-memory was OK).");
        }

        private static void AssertPersistedAgencyScienceEqual(Guid agencyId, double expected)
        {
            var state = LoadPersistedAgency(agencyId);
            Assert.AreEqual(expected, state.Science, 0.001,
                $"Agency {agencyId:N}: persisted Science on disk did not match the router's mutation.");
        }

        private static void AssertPersistedAgencyReputationEqual(Guid agencyId, double expected)
        {
            var state = LoadPersistedAgency(agencyId);
            Assert.AreEqual(expected, state.Reputation, 0.001,
                $"Agency {agencyId:N}: persisted Reputation on disk did not match the router's mutation.");
        }

        /// <summary>Reads the agency file from disk and parses it via the production
        /// AgencyState.Parse path. Asserts the file exists; the parse exception (if any)
        /// surfaces as the actual test failure with parser context for triage.</summary>
        private static AgencyState LoadPersistedAgency(Guid agencyId)
        {
            var path = Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
            Assert.IsTrue(File.Exists(path),
                $"Expected per-agency file at {path}; SaveAgency in the router did not run, or wrote to a different path.");
            return AgencyState.Parse(File.ReadAllText(path));
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
