using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using System;

namespace MockClientTest
{
    /// <summary>
    /// The Stage 4.9 v1 smoke test. Proves a mock Lidgren client can complete
    /// the LMP handshake against a real in-process Server. If this test
    /// passes, the harness is wired correctly and future regression tests
    /// (BUG-051a dedup, BUG-001 solo detection, BUG-005/006 past-subspace
    /// rejection) can be layered on top.
    /// </summary>
    [TestClass]
    public class HandshakeSmokeTest
    {
        [AssemblyInitialize]
        public static void StartHarness(TestContext _)
        {
            ServerHarness.Start();
            Assert.IsTrue(ServerHarness.WaitUntilListening(TimeSpan.FromSeconds(5)),
                "Server failed to enter Running state within 5s of Start().");
        }

        [AssemblyCleanup]
        public static void StopHarness() => ServerHarness.Stop();

        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void Client_CompletesHandshake_AndServerRegistersIt()
        {
            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)),
                    "Mock client failed to reach Connected status within 5s.");

                var request = ServerContext.ClientMessageFactory.CreateNewMessageData<HandshakeRequestMsgData>();
                request.PlayerName = "harness-player";
                request.UniqueIdentifier = Guid.NewGuid().ToString("N");
                request.KspVersion = "1.12.5";

                client.SendMessage<HandshakeCliMsg>(request);

                var reply = client.WaitForReply<HandshakeReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(reply, "Did not receive HandshakeReplyMsgData within 5s.");
                Assert.AreEqual(HandshakeReply.HandshookSuccessfully, reply.Response,
                    "Server rejected the handshake with reason: " + reply.Reason);

                // Server should have created exactly one ClientStructure for this peer.
                Assert.AreEqual(1, ServerContext.Clients.Count,
                    "Server did not register a single ClientStructure after handshake.");
            }
        }
    }
}
