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
    /// <see cref="AgencyWolfHopperRouter"/>'s wire path. Mirrors
    /// <see cref="WolfRouteRoutingTest"/> shape: same-agency upsert echoes
    /// to owner with no peer leak, connect-time catchup ships the dict
    /// (including empty), gate-off silences the wire entirely.
    /// </summary>
    [TestClass]
    public class WolfHopperRoutingTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestMethod]
        public void GateOn_SameAgencyHopperMutation_RoutesToOwner_NoPeerLeak()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            using (var bob = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-wolfh4-a1");

                Assert.IsTrue(bob.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var bobAgency = HandshakeAndDrainAgencyMessages(bob, "h-wolfh4-b1");
                Assert.AreNotEqual(aliceAgency, bobAgency);

                var hopperId = Guid.NewGuid().ToString();
                var msg = BuildHopperMsg(hopperId, "Duna", "Lowlands", "Hydrates,100,Substrate,50");
                alice.SendMessage<AgencyCliMsg>(msg);

                // Alice receives the owner-only echo.
                var echo = alice.WaitForReply<AgencyWolfHopperStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(echo, "Owner did not receive AgencyWolfHopperStateMsgData echo.");
                Assert.AreEqual(1, echo.EntryCount);
                Assert.AreEqual(hopperId, echo.Entries[0].Id,
                    "Echo's Id matches what we sent (ToString() form WITH hyphens preserved).");
                Assert.AreEqual("Duna", echo.Entries[0].Body);
                Assert.AreEqual("Hydrates,100,Substrate,50", echo.Entries[0].Recipe);
                Assert.AreEqual(aliceAgency, echo.AgencyId);

                // Bob receives nothing.
                var stray = bob.WaitForReply<AgencyWolfHopperStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Peer received AgencyWolfHopperStateMsgData — privacy rule violated.");

                // Alice's per-agency state holds the entry under the Id key.
                var aliceState = AgencySystem.Agencies[aliceAgency];
                Assert.AreEqual(1, aliceState.WolfHoppers.Count);
                Assert.IsTrue(aliceState.WolfHoppers.ContainsKey(hopperId));
            }
        }

        [TestMethod]
        public void GateOn_HopperRemoval_DropsFromAgencyStateAndEchoes()
        {
            // Slice D's distinguishing feature vs Slice C: a real Remove
            // path. The RemovedKeys tail on AgencyWolfHopperStateMsgData
            // ships from ScenarioPersister.RemoveHopper postfix and the
            // server-side router drops the entry from WolfHoppers.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var aliceAgency = HandshakeAndDrainAgencyMessages(alice, "h-wolfh4-r1");

                // Seed: send a hopper.
                var hopperId = Guid.NewGuid().ToString();
                var seed = BuildHopperMsg(hopperId, "Duna", "Lowlands", "Hydrates,100");
                alice.SendMessage<AgencyCliMsg>(seed);
                var seedEcho = alice.WaitForReply<AgencyWolfHopperStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, seedEcho.EntryCount);

                // Send a Remove via RemovedKeys.
                var removeMsg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyWolfHopperStateMsgData>();
                removeMsg.EntryCount = 0;
                removeMsg.Entries = new AgencyWolfHopperEntry[0];
                removeMsg.RemovedKeyCount = 1;
                removeMsg.RemovedKeys = new[] { hopperId };
                alice.SendMessage<AgencyCliMsg>(removeMsg);

                // Echo confirms the removal.
                var removeEcho = alice.WaitForReply<AgencyWolfHopperStateMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(removeEcho, "Server did not echo the removal.");
                Assert.AreEqual(0, removeEcho.EntryCount);
                Assert.AreEqual(1, removeEcho.RemovedKeyCount);
                Assert.AreEqual(hopperId, removeEcho.RemovedKeys[0]);

                // Agency state no longer holds the entry.
                Assert.AreEqual(0, AgencySystem.Agencies[aliceAgency].WolfHoppers.Count);
            }
        }

        [TestMethod]
        public void GateOff_NoHopperEcho_DualModeSilence()
        {
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test precondition: reset must leave PerAgencyCareer=false.");

            using (var alice = new MockNetClient())
            {
                Assert.IsTrue(alice.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = "h-wolfh4-go";
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                alice.SendMessage<HandshakeCliMsg>(handshake);
                var reply = alice.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response);

                // No catchup hopper state arrives under gate=off.
                var noCatchup = alice.WaitForReply<AgencyWolfHopperStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(noCatchup, "Catchup AgencyWolfHopperStateMsgData arrived under gate=off.");

                // Send a hopper mutation as a buggy-client simulation.
                var msg = BuildHopperMsg(Guid.NewGuid().ToString(), "Duna", "Lowlands", "Hydrates,100");
                alice.SendMessage<AgencyCliMsg>(msg);

                // No echo flows back.
                var stray = alice.WaitForReply<AgencyWolfHopperStateMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Server emitted AgencyWolfHopperStateMsgData echo under gate=off.");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfHopperStateMsgData BuildHopperMsg(string id, string body, string biome, string recipe)
        {
            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<AgencyWolfHopperStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfHopperEntry { Id = id, Body = body, Biome = biome, Recipe = recipe },
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

            // Drain the catchup chain. Inbox is type-buffered so order
            // doesn't matter. All four (depot, route, hopper, terminal) ship
            // unconditionally under gate=on.
            var hoppers = client.WaitForReply<AgencyWolfHopperStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(hoppers, $"Did not receive AgencyWolfHopperStateMsgData catchup for {playerName}.");

            return state.AgencyId;
        }
    }
}
