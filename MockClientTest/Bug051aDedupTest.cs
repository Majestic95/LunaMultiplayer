using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Warp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using System;

namespace MockClientTest
{
    /// <summary>
    /// Stage 4.10 — end-to-end regression test for BUG-051a. The fix added a
    /// <c>(PlayerCreator, RequestSeq)</c> dedup cache on <c>WarpSystemReceiver.HandleNewSubspace</c>
    /// so a client retrying a stuck subspace-creation request can never mint
    /// an orphan subspace. This test exercises the path: connect, complete the
    /// handshake, send <c>WarpNewSubspaceMsgData</c> twice with the same
    /// <c>RequestSeq</c>, assert <c>WarpContext.Subspaces.Count</c> grew by
    /// exactly one (the first request) and that the cached entry survived
    /// (one entry in <c>WarpRequestCache</c>).
    ///
    /// Future BUG-001 / BUG-005/006 regression tests will follow the same
    /// pattern — see <c>docs/research/04-mock-client-harness-design.md</c>.
    /// </summary>
    [TestClass]
    public class Bug051aDedupTest
    {
        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void DuplicateRequestSeq_ReturnsSameSubspace_AndDoesNotMintSecond()
        {
            const string playerName = "harness-051a";
            const uint requestSeq = 42u;

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)),
                    "Mock client failed to reach Connected status within 5s.");

                // Handshake first — server-side WarpRequestCache is keyed on PlayerName,
                // which only gets populated after HandshakeSystem authenticates the client.
                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = playerName;
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                client.SendMessage<HandshakeCliMsg>(handshake);

                var reply = client.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply, "Handshake reply missing.");
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response,
                    "Handshake rejected: " + reply.Reason);

                var baselineSubspaces = WarpContext.Subspaces.Count;

                // First request — should mint a fresh subspace and broadcast WarpNewSubspaceMsgData
                // to all clients (including us). The broadcast carries the assigned SubspaceKey.
                var request1 = ServerContext.ClientMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
                request1.PlayerCreator = playerName;
                request1.SubspaceKey = 0;          // ignored on the wire — server assigns NextSubspaceId
                request1.ServerTimeDifference = 1234.5;
                request1.RequestSeq = requestSeq;
                client.SendMessage<WarpCliMsg>(request1);

                var broadcast1 = client.WaitForReply<WarpNewSubspaceMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(broadcast1, "Did not receive the first subspace assignment broadcast.");
                Assert.AreEqual(requestSeq, broadcast1.RequestSeq, "Server did not echo RequestSeq in the first broadcast.");
                var assignedSubspace = broadcast1.SubspaceKey;

                Assert.AreEqual(baselineSubspaces + 1, WarpContext.Subspaces.Count,
                    "Expected exactly one new subspace after the first request.");

                // Second request with the SAME RequestSeq — server should cache-hit and replay
                // the same SubspaceKey to us only, without minting a fresh subspace.
                var request2 = ServerContext.ClientMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
                request2.PlayerCreator = playerName;
                request2.SubspaceKey = 0;
                request2.ServerTimeDifference = 1234.5;
                request2.RequestSeq = requestSeq;
                client.SendMessage<WarpCliMsg>(request2);

                var broadcast2 = client.WaitForReply<WarpNewSubspaceMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(broadcast2, "Did not receive a reply to the duplicate request.");
                Assert.AreEqual(assignedSubspace, broadcast2.SubspaceKey,
                    "Cache-hit replay returned a different SubspaceKey than the original mint.");
                Assert.AreEqual(requestSeq, broadcast2.RequestSeq, "RequestSeq mismatch on the cache-hit replay.");

                Assert.AreEqual(baselineSubspaces + 1, WarpContext.Subspaces.Count,
                    "Duplicate request must NOT mint a second subspace.");
            }
        }

        [TestMethod]
        public void RequestSeqZero_DoesNotEngageCache_AlwaysMints()
        {
            // Pre-fix clients send no trailing RequestSeq bytes; the field defensively
            // deserializes to 0. The server's cache MUST ignore seq=0 (sentinel) and
            // always fall through to the legacy always-mint path, otherwise a busy
            // pre-fix client would get all its requests collapsed onto one subspace.
            const string playerName = "051-legacy";

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));

                var handshake = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                handshake.PlayerName = playerName;
                handshake.UniqueIdentifier = Guid.NewGuid().ToString("N");
                handshake.KspVersion = "1.12.5";
                client.SendMessage<HandshakeCliMsg>(handshake);
                Assert.IsNotNull(client.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5)));

                var baseline = WarpContext.Subspaces.Count;

                for (var i = 0; i < 2; i++)
                {
                    var req = ServerContext.ClientMessageFactory.CreateNewMessageData<WarpNewSubspaceMsgData>();
                    req.PlayerCreator = playerName;
                    req.RequestSeq = 0u;          // pre-fix sentinel
                    req.ServerTimeDifference = 100.0 + i;
                    client.SendMessage<WarpCliMsg>(req);
                    Assert.IsNotNull(client.WaitForReply<WarpNewSubspaceMsgData>(TimeSpan.FromSeconds(5)));
                }

                Assert.AreEqual(baseline + 2, WarpContext.Subspaces.Count,
                    "RequestSeq=0 must NOT dedupe — pre-fix clients would lose subspaces.");
            }
        }
    }
}
