using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.ShareProgress;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.System;
using System;
using System.Globalization;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 4.10 — end-to-end regression test for BUG-025. The server's
    /// duplicate-tech-purchase detection runs synchronously inside
    /// <c>ScenarioDataUpdater.TryAddTechnologyAtomic</c>; when a tech is already
    /// in the canonical scenario, the server sends a
    /// <c>ShareProgressTechnologyRejectedMsgData</c> back to the sender (so the
    /// client refunds the science it locally deducted) and does NOT relay the
    /// duplicate broadcast to other clients.
    ///
    /// This test pins the two halves of the contract:
    /// duplicate → rejection + no relay; first-time → relay + no rejection.
    /// The client-side refund (calling <c>ResearchAndDevelopment.Instance.AddScience</c>)
    /// is KSP-bound and cannot be harness-tested — that's a one-line call covered
    /// by the architecture-review rubric. See
    /// docs/research/02-analysis/bug-025-rd-double-purchase.md.
    /// </summary>
    [TestClass]
    public class Bug025RejectionTest
    {
        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void DuplicatePurchase_IsRejected_SenderReceivesRefund()
        {
            const string sender = "h-025-sender";
            const string observer = "h-025-observer";
            const string techId = "engineering101";
            const float cost = 45f;

            // Seed the canonical scenario with the tech ID the sender will try to "buy".
            SeedTechIntoScenario(techId, cost);

            using (var senderClient = new MockNetClient())
            using (var observerClient = new MockNetClient())
            {
                Assert.IsTrue(senderClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(senderClient, sender);

                Assert.IsTrue(observerClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(observerClient, observer);

                // Sender broadcasts the duplicate purchase.
                var techMsg = BuildTechMessage(techId, cost);
                senderClient.SendMessage<ShareProgressCliMsg>(techMsg);

                // Server should reject the duplicate and tell the sender to refund.
                var rejection = senderClient.WaitForReply<ShareProgressTechnologyRejectedMsgData>(TimeSpan.FromSeconds(3));
                Assert.IsNotNull(rejection, "Sender did not receive a TechnologyRejected message within 3s.");
                Assert.AreEqual(techId, rejection.TechId, "Rejection TechId did not match the duplicate purchase.");
                Assert.AreEqual(cost, rejection.RefundScience, 0.001f, "Rejection RefundScience should match the cost the sender claimed.");

                // Observer must NOT receive the TechnologyUpdate relay — the duplicate was suppressed at the server.
                var leak = observerClient.WaitForReply<ShareProgressTechnologyMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(leak, "Observer received a TechnologyUpdate relay — the duplicate should have been suppressed at the server.");
            }
        }

        [TestMethod]
        public void FirstPurchase_IsRelayedNotRejected()
        {
            // Server caps player names at 15 chars (GeneralSettings.MaxUsernameLength
            // default). Keep test names short — otherwise the handshake reply gets sent
            // but disappears under the immediate disconnect on the unauthenticated peer.
            const string sender = "h-025-snd-1st";
            const string observer = "h-025-obs-1st";
            const string techId = "start";
            const float cost = 0f;

            using (var senderClient = new MockNetClient())
            using (var observerClient = new MockNetClient())
            {
                Assert.IsTrue(senderClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(senderClient, sender);

                Assert.IsTrue(observerClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(observerClient, observer);

                var techMsg = BuildTechMessage(techId, cost);
                senderClient.SendMessage<ShareProgressCliMsg>(techMsg);

                // Observer should see the relay.
                var relay = observerClient.WaitForReply<ShareProgressTechnologyMsgData>(TimeSpan.FromSeconds(3));
                Assert.IsNotNull(relay, "Observer did not receive the TechnologyUpdate relay within 3s.");
                Assert.AreEqual(techId, relay.TechNode.Id, "Relayed TechId did not match the original purchase.");

                // Sender must NOT receive a rejection.
                var stray = senderClient.WaitForReply<ShareProgressTechnologyRejectedMsgData>(TimeSpan.FromMilliseconds(500));
                Assert.IsNull(stray, "Sender received a spurious TechnologyRejected for a first-time purchase.");
            }
        }

        private static void SeedTechIntoScenario(string techId, float cost)
        {
            // The R&D scenario lives in ScenarioStoreSystem.CurrentScenarios under
            // the key "ResearchAndDevelopment". We add a single Tech child node so
            // TryAddTechnologyAtomic finds it on lookup. The exact payload shape
            // matches what ScenarioContractsDataUpdater / ScenarioTechnologyDataUpdater
            // would write — `<Tech>{ id = ... cost = ... state = ... }` style.
            var scenarioNode = new ConfigNode("") { Name = "ResearchAndDevelopment" };
            var techNode = new ConfigNode("") { Name = "Tech" };
            techNode.CreateValue(new CfgNodeValue<string, string>("id", techId));
            techNode.CreateValue(new CfgNodeValue<string, string>("state", "Available"));
            techNode.CreateValue(new CfgNodeValue<string, string>("cost", cost.ToString(CultureInfo.InvariantCulture)));
            scenarioNode.AddNode(techNode);
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = scenarioNode;
        }

        private static ShareProgressTechnologyMsgData BuildTechMessage(string techId, float cost)
        {
            // Wire payload: a UTF-8 raw text fragment that the server's
            // ParseClientConfigNode reconstructs into a Tech ConfigNode. The
            // outer { } wrapper is optional — ParseClientConfigNode strips it
            // either way. We omit it to match KSP-client output sans wrapper.
            var rawText = $"id = {techId}\nstate = Available\ncost = {cost.ToString(CultureInfo.InvariantCulture)}\n";
            var bytes = Encoding.UTF8.GetBytes(rawText);

            var msg = ServerContext.ClientMessageFactory.CreateNewMessageData<ShareProgressTechnologyMsgData>();
            msg.TechNode.Id = techId;
            msg.TechNode.NumBytes = bytes.Length;
            msg.TechNode.Data = bytes;
            return msg;
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
            Assert.AreEqual(LmpCommon.Enums.HandshakeReply.HandshookSuccessfully, reply.Response,
                $"Handshake rejected for {playerName}: " + reply.Reason);
        }
    }
}
