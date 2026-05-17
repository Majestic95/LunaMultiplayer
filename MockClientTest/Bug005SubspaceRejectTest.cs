using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.System;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 4.10 — end-to-end regression test for BUG-005/006. The fix added
    /// <c>Vessel.AuthoritativeSubspaceId</c>; <c>VesselMsgReader.HandleVesselProto</c>
    /// synchronously rejects (no store, no relay) any proto-update whose client
    /// subspace is strictly past the vessel's recorded authority. This test
    /// plants a vessel in the store, connects two mock clients in different
    /// subspaces, and verifies both directions over the wire:
    ///   * past-subspace client sending to a future-auth vessel → rejected
    ///     (vessel reference in <see cref="VesselStoreSystem.CurrentVessels"/> unchanged,
    ///     no relay to the other client)
    ///   * future-subspace client sending to a past-auth vessel → accepted
    ///     (relay reaches the other client)
    /// Unit coverage for <c>IsStrictlyPast</c> + <c>AuthoritativeSubspaceId</c>
    /// round-trip lives in <c>ServerTest/VesselAuthorityTest</c>; this test
    /// exclusively exercises the wire path.
    /// </summary>
    [TestClass]
    public class Bug005SubspaceRejectTest
    {
        // Sample vessel ConfigNode loaded once per process from ServerTest's fixtures.
        // Both test projects live next to each other in the repo so we just walk up.
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void PastSubspaceProto_IsRejected_VesselUntouched_AndNotRelayed()
        {
            const string pastPlayer = "h-005-past";
            const string futurePlayer = "h-005-future";

            SeedSubspace(1, time: 100d);
            SeedSubspace(2, time: 1000d);

            var vesselId = Guid.NewGuid();
            var vessel = new Server.System.Vessel.Classes.Vessel(SampleVesselText.Value);
            vessel.AuthoritativeSubspaceId = 2;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));

            using (var pastClient = new MockNetClient())
            using (var futureClient = new MockNetClient())
            {
                Assert.IsTrue(pastClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(pastClient, pastPlayer);
                ServerContext.Clients.Values.Single(c => c.PlayerName == pastPlayer).Subspace = 1;

                Assert.IsTrue(futureClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(futureClient, futurePlayer);
                ServerContext.Clients.Values.Single(c => c.PlayerName == futurePlayer).Subspace = 2;

                // Reject fires BEFORE RawConfigNodeInsertOrUpdate runs, so the bytes never
                // get parsed. A single non-zero byte is enough to satisfy the NumBytes>0
                // guard and is small enough to make the intent obvious.
                var proto = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                proto.VesselId = vesselId;
                proto.Data = new byte[] { 0x6D };
                proto.NumBytes = proto.Data.Length;
                pastClient.SendMessage<VesselCliMsg>(proto);

                var leaked = futureClient.WaitForReply<VesselProtoMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(leaked, "Past-subspace proto was relayed to the future-subspace client.");

                // RawConfigNodeInsertOrUpdate's AddOrUpdate swaps the reference. If the
                // handler skipped that call (i.e. reject path took), the stored entry is
                // still the exact instance we planted.
                Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var afterReject));
                Assert.AreSame(vessel, afterReject, "Vessel reference changed — RawConfigNodeInsertOrUpdate ran on a reject path.");
                Assert.AreEqual(2, afterReject.AuthoritativeSubspaceId,
                    "AuthoritativeSubspaceId mutated despite reject.");
            }
        }

        [TestMethod]
        public void FutureSubspaceProto_IsAccepted_AndRelayed()
        {
            // Positive control: same wiring, opposite direction. A client in subspace 2
            // sending a proto for a vessel currently authoritative in subspace 1 is NOT
            // strictly past, so the handler must accept and relay to other clients.
            const string pastPlayer = "h-005-rx";
            const string futurePlayer = "h-005-tx";

            SeedSubspace(1, time: 100d);
            SeedSubspace(2, time: 1000d);

            var vesselId = Guid.NewGuid();
            var vessel = new Server.System.Vessel.Classes.Vessel(SampleVesselText.Value);
            vessel.AuthoritativeSubspaceId = 1;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));

            using (var pastClient = new MockNetClient())
            using (var futureClient = new MockNetClient())
            {
                Assert.IsTrue(pastClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(pastClient, pastPlayer);
                ServerContext.Clients.Values.Single(c => c.PlayerName == pastPlayer).Subspace = 1;

                Assert.IsTrue(futureClient.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(futureClient, futurePlayer);
                ServerContext.Clients.Values.Single(c => c.PlayerName == futurePlayer).Subspace = 2;

                // Bytes need to be a valid ConfigNode this time because the accept path
                // launches the parser. Round-trip the loaded sample through ToString so
                // the format is exactly what LunaConfigNode produces.
                var validBytes = Encoding.UTF8.GetBytes(vessel.ToString());
                var proto = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                proto.VesselId = vesselId;
                proto.Data = validBytes;
                proto.NumBytes = validBytes.Length;
                futureClient.SendMessage<VesselCliMsg>(proto);

                var relayed = pastClient.WaitForReply<VesselProtoMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(relayed, "Future-subspace proto was not relayed to the past-subspace client.");
                Assert.AreEqual(vesselId, relayed.VesselId);
            }
        }

        private static void SeedSubspace(int id, double time)
        {
            WarpContext.Subspaces.TryAdd(id, new Subspace(id, time, "test"));
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

        private static string LoadSampleVesselText()
        {
            var probe = new DirectoryInfo(AppContext.BaseDirectory);
            while (probe != null && !Directory.Exists(Path.Combine(probe.FullName, "ServerTest", "XmlExampleFiles", "Others")))
                probe = probe.Parent;

            Assert.IsNotNull(probe, "Could not locate ServerTest/XmlExampleFiles/Others — repo layout drift?");
            var fixtureDir = Path.Combine(probe.FullName, "ServerTest", "XmlExampleFiles", "Others");
            var samplePath = Directory.GetFiles(fixtureDir).OrderBy(p => p, StringComparer.Ordinal).First();
            return File.ReadAllText(samplePath);
        }
    }
}
