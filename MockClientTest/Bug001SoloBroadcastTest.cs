using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Warp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.System;
using System;
using System.Linq;

namespace MockClientTest
{
    /// <summary>
    /// Stage 4.10 — end-to-end regression test for BUG-001. The fix added a
    /// periodic <c>WarpSystem.PerformSoloSubspaceChecksAsync</c> task that
    /// refreshes each subspace's solo-occupancy flag and broadcasts
    /// <c>WarpSubspaceSoloStatusMsgData</c> on every transition. This test
    /// drives <c>RefreshSoloStatuses</c> directly (the periodic task is just a
    /// scheduler over the same call) so the test doesn't have to wait on the
    /// <c>SoloSubspaceCheckMs</c> interval, and verifies that both directions
    /// of the transition reach a connected mock client over the wire.
    ///
    /// Per-subspace detection correctness is already covered by
    /// <c>ServerTest/WarpSoloDetectionTest.cs</c>; this test exclusively
    /// exercises the commit + broadcast path that the unit tests can't reach.
    /// </summary>
    [TestClass]
    public class Bug001SoloBroadcastTest
    {
        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void SoloDetected_BroadcastsToConnectedClient()
        {
            const string playerName = "h-001-solo";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)),
                    "Mock client failed to reach Connected status within 5s.");

                Handshake(client, playerName);

                // Mint a fresh subspace so we have a known id. HandleNewSubspace adds it to
                // WarpContext.Subspaces and broadcasts; it does NOT move client.Subspace
                // (that's HandleChangeSubspace's job). We poke client.Subspace directly
                // below so the detector sees exactly one occupant.
                var newSubspaceId = RequestNewSubspace(client, playerName, requestSeq: 1u, serverTimeDifference: 100.0);

                var registered = ServerContext.Clients.Values.SingleOrDefault(c => c.PlayerName == playerName);
                Assert.IsNotNull(registered, "Server did not register our handshake.");
                registered.Subspace = newSubspaceId;

                Assert.IsFalse(WarpContext.Subspaces[newSubspaceId].Solo,
                    "Fresh subspace should start with Solo=false.");

                WarpSystem.RefreshSoloStatuses();

                var broadcast = client.WaitForReply<WarpSubspaceSoloStatusMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(broadcast, "Did not receive WarpSubspaceSoloStatusMsgData broadcast.");
                Assert.AreEqual(newSubspaceId, broadcast.SubspaceId,
                    "Solo broadcast carried the wrong SubspaceId.");
                Assert.IsTrue(broadcast.IsSolo,
                    "Single-occupant subspace should broadcast IsSolo=true.");

                Assert.IsTrue(WarpContext.Subspaces[newSubspaceId].Solo,
                    "RefreshSoloStatuses did not commit the Solo flag.");
            }
        }

        [TestMethod]
        public void SoloFlipsFalse_WhenSecondClientJoinsSubspace()
        {
            const string playerA = "harness-001-a";
            const string playerB = "harness-001-b";

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(clientA, playerA);

                var subspaceId = RequestNewSubspace(clientA, playerA, requestSeq: 1u, serverTimeDifference: 200.0);

                var aClient = ServerContext.Clients.Values.Single(c => c.PlayerName == playerA);
                aClient.Subspace = subspaceId;

                // First pass: one occupant → Solo flips to true. Drain the broadcast so the
                // next WaitForReply call only sees the transition we actually care about.
                WarpSystem.RefreshSoloStatuses();
                var firstBroadcast = clientA.WaitForReply<WarpSubspaceSoloStatusMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(firstBroadcast, "First refresh did not produce a Solo=true broadcast.");
                Assert.IsTrue(firstBroadcast.IsSolo);

                // Second client joins the same subspace. The detector now sees 2 occupants
                // and must flip the flag back to false, broadcasting the transition to all.
                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(clientB, playerB);
                var bClient = ServerContext.Clients.Values.Single(c => c.PlayerName == playerB);
                bClient.Subspace = subspaceId;

                WarpSystem.RefreshSoloStatuses();

                var secondBroadcast = clientA.WaitForReply<WarpSubspaceSoloStatusMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(secondBroadcast,
                    "Client A did not receive the non-solo transition broadcast.");
                Assert.AreEqual(subspaceId, secondBroadcast.SubspaceId);
                Assert.IsFalse(secondBroadcast.IsSolo,
                    "Two occupants in the same subspace must flip Solo back to false.");
                Assert.IsFalse(WarpContext.Subspaces[subspaceId].Solo,
                    "RefreshSoloStatuses did not commit the Solo=false flip.");
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

        private static int RequestNewSubspace(MockNetClient client, string playerName, uint requestSeq, double serverTimeDifference)
        {
            var request = ServerContext.ClientMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
            request.PlayerCreator = playerName;
            request.SubspaceKey = 0;
            request.ServerTimeDifference = serverTimeDifference;
            request.RequestSeq = requestSeq;
            client.SendMessage<WarpCliMsg>(request);

            var broadcast = client.WaitForReply<WarpNewSubspaceMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(broadcast, $"{playerName} never received NewSubspace broadcast.");
            return broadcast.SubspaceKey;
        }
    }
}
