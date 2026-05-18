using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.IO;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.16a — end-to-end harness coverage for the per-agency registration
    /// wire surface shipped across 5.15a/b/c. Pins the contract that
    /// <see cref="Server.System.HandshakeSystem"/> + <see cref="AgencySystemSender"/> +
    /// <see cref="Server.Message.AgencyMsgReader"/> jointly implement against a real
    /// in-process Server, complementing the field-level unit tests in
    /// <c>ServerTest/AgencySystemTest.cs</c> and the wire round-trip tests in
    /// <c>LmpCommonTest/SerializationTests.cs</c>.
    ///
    /// All tests assume the harness reset (run in <c>[TestInitialize]</c>) has cleared
    /// the agency registries and reset the <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>
    /// gate to false. Tests that exercise the on-path flip it to true explicitly; the
    /// disabled-mode test leaves it false to assert dual-mode silence.
    /// </summary>
    [TestClass]
    public class AgencyHandshakeTest
    {
        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void PerAgencyCareerEnabled_NewPlayer_ReceivesHandshakeAndState()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            const string playerName = "h-016a-alpha";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)),
                    "Mock client failed to reach Connected status within 5s.");

                Handshake(client, playerName);

                var agencyHandshake = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(agencyHandshake,
                    "Server did not push AgencyHandshakeMsgData after the LMP handshake reply.");
                Assert.AreNotEqual(Guid.Empty, agencyHandshake.AssignedAgencyId,
                    "AssignedAgencyId must be a real GUID, not Guid.Empty.");
                Assert.AreEqual(0, agencyHandshake.OtherAgencyCount,
                    "First connecting player should see zero other agencies in the handshake summary.");

                var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(state, "Server did not push AgencyStateMsgData after AgencyHandshake.");
                Assert.AreEqual(agencyHandshake.AssignedAgencyId, state.AgencyId,
                    "AgencyStateMsgData.AgencyId did not match the AssignedAgencyId from the handshake.");
                Assert.AreEqual(playerName, state.OwningPlayerName);
                Assert.AreEqual($"{playerName} Space Agency", state.DisplayName,
                    "Default display name should be derived from the player name on first registration.");
                Assert.AreEqual((double)GameplaySettings.SettingsStore.StartingFunds, state.Funds, 0.0001,
                    "Initial Funds must seed from GameplaySettings.StartingFunds (spec §3 — starting economy inherited).");
                Assert.AreEqual((double)GameplaySettings.SettingsStore.StartingScience, state.Science, 0.0001);
                Assert.AreEqual((double)GameplaySettings.SettingsStore.StartingReputation, state.Reputation, 0.0001);

                Assert.IsTrue(AgencySystem.AgencyByPlayerName.ContainsKey(playerName),
                    "AgencySystem registry should have the new player's agency indexed by name.");
            }
        }

        [TestMethod]
        public void SecondPlayer_HandshakeIncludesFirstPlayersAgency()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            const string playerA = "h-016a-alice";
            const string playerB = "h-016a-bob";

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(clientA, playerA);
                var aHandshake = clientA.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(aHandshake, "Player A never received the AgencyHandshake.");

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(clientB, playerB);

                var bHandshake = clientB.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bHandshake, "Player B never received the AgencyHandshake.");
                Assert.AreNotEqual(aHandshake.AssignedAgencyId, bHandshake.AssignedAgencyId,
                    "Each player must receive a distinct agency id.");
                Assert.AreEqual(1, bHandshake.OtherAgencyCount,
                    "Player B's handshake should surface exactly one other agency (player A's).");
                Assert.AreEqual(aHandshake.AssignedAgencyId, bHandshake.OtherAgencies[0].AgencyId,
                    "OtherAgencies[0].AgencyId should match player A's AssignedAgencyId.");
                Assert.AreEqual(playerA, bHandshake.OtherAgencies[0].OwningPlayerName);
                Assert.AreEqual($"{playerA} Space Agency", bHandshake.OtherAgencies[0].DisplayName,
                    "Public summary should expose the default display name.");

                // [Privacy rule, spec §10 Q1 PrivateAgencyResources=true]. Player B's
                // inbox should contain exactly ONE AgencyStateMsgData (B's own). Player A's
                // funds/science/reputation must never leak through this channel. Pull B's own
                // State first, then assert no second one is sitting in the inbox.
                var bState = clientB.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bState, "Player B did not receive their own AgencyStateMsgData.");
                Assert.AreEqual(bHandshake.AssignedAgencyId, bState.AgencyId,
                    "Player B's AgencyStateMsgData carried someone else's id.");
                var strayForB = clientB.WaitForReply<AgencyStateMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayForB,
                    $"Player B received a second AgencyStateMsgData (likely player A's — privacy rule violated). " +
                    $"AgencyId on the stray = {strayForB?.AgencyId:N}, OwningPlayerName = '{strayForB?.OwningPlayerName}'.");
            }
        }

        [TestMethod]
        public void CreateRequest_ValidName_AppliesAndPersistsToDisk()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            const string playerName = "h-016a-rena";
            const string newDisplayName = "Rena Space Industries";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(client, playerName);

                // Drain the auto-registration handshake + state so the post-rename
                // CreateReply + State are the next agency messages the assertions read.
                var initialHandshake = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(initialHandshake);
                var initialState = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(initialState);

                var request = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyCreateRequestMsgData>();
                request.DisplayName = newDisplayName;
                client.SendMessage<AgencyCliMsg>(request);

                var reply = client.WaitForReply<AgencyCreateReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply, "Server did not return an AgencyCreateReplyMsgData within 5s.");
                Assert.IsTrue(reply.Success, $"CreateRequest unexpectedly rejected: {reply.Reason}");
                Assert.AreEqual(initialHandshake.AssignedAgencyId, reply.AgencyId,
                    "CreateReply.AgencyId must equal the assigned agency on rename, not a fresh GUID.");
                Assert.AreEqual(newDisplayName, reply.DisplayName);

                var updatedState = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(updatedState, "Server did not push an updated AgencyStateMsgData after CreateRequest.");
                Assert.AreEqual(newDisplayName, updatedState.DisplayName,
                    "Updated state must carry the renamed display name so the client UI resyncs.");

                // Persistence assertion — read the canonical Universe/Agencies/{id}.txt
                // file the server's SaveAgency wrote and confirm the rename round-trips
                // through ConfigNode serialization. Read disk DIRECTLY rather than via
                // AgencySystem.LoadAgency — that path is registry-first and would serve
                // the in-memory object even if SaveAgency never wrote (round-2 review).
                var diskPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies", reply.AgencyId.ToString("N") + ".txt");
                Assert.IsTrue(File.Exists(diskPath),
                    $"Universe/Agencies/{reply.AgencyId:N}.txt was not created — SaveAgency did not run.");
                var persistedState = AgencyState.Parse(File.ReadAllText(diskPath));
                Assert.AreEqual(newDisplayName, persistedState.DisplayName,
                    "On-disk DisplayName did not match the CreateRequest payload — SaveAgency wrote but with stale content.");
                Assert.AreEqual(reply.AgencyId, persistedState.AgencyId,
                    "On-disk AgencyId did not match the assigned id.");
            }
        }

        [TestMethod]
        public void CreateRequest_EmptyName_RejectsWithReason_AndDoesNotMutateState()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            const string playerName = "h-016a-empt";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(client, playerName);

                var initialHandshake = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(initialHandshake);
                var initialState = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(initialState);

                var request = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyCreateRequestMsgData>();
                request.DisplayName = "";
                client.SendMessage<AgencyCliMsg>(request);

                var reply = client.WaitForReply<AgencyCreateReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply, "Server did not return an AgencyCreateReplyMsgData for an empty-name request.");
                Assert.IsFalse(reply.Success, "Empty display name must be rejected.");
                Assert.AreEqual(Guid.Empty, reply.AgencyId,
                    "Validation failures use Guid.Empty as the failure marker so the client decoder can branch on it.");
                Assert.IsFalse(string.IsNullOrEmpty(reply.Reason),
                    "Reply must carry a non-empty Reason explaining the rejection.");

                // No State follow-up on rejection — the server only sends State on a
                // successful rename. The auto-registered state must remain intact.
                var strayState = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayState,
                    "Server emitted a State message after rejecting a CreateRequest.");

                // Read disk directly (not via AgencySystem.LoadAgency which is registry-first).
                // The auto-registered agency was persisted at registration time; an empty-name
                // CreateRequest must NOT have triggered a second SaveAgency with the rejected
                // value.
                var diskPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies", initialHandshake.AssignedAgencyId.ToString("N") + ".txt");
                Assert.IsTrue(File.Exists(diskPath),
                    "Auto-registered agency file is missing — RegisterAgency did not persist.");
                var persisted = AgencyState.Parse(File.ReadAllText(diskPath));
                Assert.AreEqual($"{playerName} Space Agency", persisted.DisplayName,
                    "Auto-registered DisplayName must not change when CreateRequest is rejected.");
            }
        }

        [TestMethod]
        public void PerAgencyCareerDisabled_HandshakeDoesNotEmitAgencyMessages()
        {
            // Gate stays at the reset default (false). This pins the dual-mode
            // guarantee from spec §11 — with the setting off, the wire is invisible.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test pre-condition: reset must leave PerAgencyCareer=false.");

            const string playerName = "h-016a-off";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(client, playerName);

                var stray = client.WaitForReply<AgencyBaseMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray,
                    $"Server emitted an unexpected Agency message ({stray?.GetType().Name}) while PerAgencyCareer is off.");

                Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey(playerName),
                    "AgencySystem must not register a player when PerAgencyCareer is off.");
            }
        }

        [TestMethod]
        public void PerAgencyCareerEnabled_NonCareerGameMode_DoesNotEmitAgencyMessages()
        {
            // [Stage 5.17e-1, spec §10 Q-Mode Career-only sign-off] When the operator has
            // mis-configured the combination (PerAgencyCareer=true with GameMode=Science),
            // AgencySystem.PerAgencyEnabled returns false and the per-agency wire surface
            // stays silent — same observable behaviour as PerAgencyCareer=false. The boot
            // log warns the operator separately (LoadExistingAgencies). Test both Science
            // and Sandbox here so the no-op contract isn't accidentally restricted to one
            // mode.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;

            const string playerScience = "h-017e1-sci";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(client, playerScience);

                var stray = client.WaitForReply<AgencyBaseMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray,
                    $"Server emitted an unexpected Agency message ({stray?.GetType().Name}) while GameMode=Science (per-agency career is Career-only).");

                Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey(playerScience),
                    "AgencySystem must not register a player when GameMode != Career, even with PerAgencyCareer=true.");
            }

            // Same shape under Sandbox — closes the door on a future regression where
            // someone special-cases Sandbox but forgets Science.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            AgencySystem.Reset();

            const string playerSandbox = "h-017e1-sbx";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(client, playerSandbox);

                var stray = client.WaitForReply<AgencyBaseMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray,
                    $"Server emitted an unexpected Agency message ({stray?.GetType().Name}) while GameMode=Sandbox (per-agency career is Career-only).");

                Assert.IsFalse(AgencySystem.AgencyByPlayerName.ContainsKey(playerSandbox),
                    "AgencySystem must not register a player when GameMode=Sandbox, even with PerAgencyCareer=true.");
            }
        }

        private static void Handshake(MockNetClient client, string playerName)
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
