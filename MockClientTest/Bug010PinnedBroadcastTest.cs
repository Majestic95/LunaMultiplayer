using LmpCommon.Locks;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.System;
using System;
using System.Linq;

namespace MockClientTest
{
    /// <summary>
    /// Stage 4.10 — end-to-end regression test for BUG-010 Part A. When a player
    /// disconnects, the server must broadcast a <c>VesselPinnedMsgData</c> for every
    /// vessel that was under the leaving player's Control / Update / UnloadedUpdate
    /// locks, BEFORE the lock-release storm fans out. The client-side pin/unpin
    /// behaviour (immortal hold, OnVesselChange-driven release) is covered manually
    /// in-game; this test exclusively pins down the server-side broadcast contract.
    /// See docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md.
    /// </summary>
    [TestClass]
    public class Bug010PinnedBroadcastTest
    {
        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void Disconnect_BroadcastsVesselPinned_ForEachLockedVessel()
        {
            const string leavingPlayer = "h-010-leaver";
            const string watcherPlayer = "h-010-watcher";

            var pinnedVesselId = Guid.NewGuid();
            var alsoPinnedVesselId = Guid.NewGuid();

            using (var leaver = new MockNetClient())
            using (var watcher = new MockNetClient())
            {
                Assert.IsTrue(leaver.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(leaver, leavingPlayer);

                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(watcher, watcherPlayer);

                // Plant locks server-side on behalf of the leaving player. We can't seed
                // the private LockStore directly — go through AcquireLock with no vessel
                // in CurrentVessels so the BUG-005/006 strictly-past gate short-circuits
                // on the missing-vessel branch. Subspace 0 is the legacy sentinel that
                // also bypasses the gate, used by AcquireLock's <= 0 guard.
                AcquireOrThrow(LockType.Control, pinnedVesselId, leavingPlayer);
                AcquireOrThrow(LockType.Update, pinnedVesselId, leavingPlayer);
                AcquireOrThrow(LockType.Control, alsoPinnedVesselId, leavingPlayer);

                // Drain any spurious inbound (the lock-acquire broadcasts on the watcher
                // are background noise for this assertion — we want the first
                // VesselPinned the server sends after we disconnect).
                while (watcher.WaitForReply<VesselPinnedMsgData>(TimeSpan.FromMilliseconds(50)) != null) { }

                // Trigger the disconnect path.
                leaver.Dispose();

                // The server may broadcast either pinned vessel first. Collect both.
                var first = watcher.WaitForReply<VesselPinnedMsgData>(TimeSpan.FromSeconds(3));
                Assert.IsNotNull(first, "watcher did not receive any VesselPinned broadcast within 3s of disconnect.");
                Assert.AreEqual(leavingPlayer, first.AbsentPlayerName,
                    "VesselPinned.AbsentPlayerName should name the leaving pilot.");
                Assert.IsFalse(string.IsNullOrEmpty(first.Reason),
                    "Reason should carry a diagnostic string for the VesselSyncLog trace.");

                var second = watcher.WaitForReply<VesselPinnedMsgData>(TimeSpan.FromSeconds(3));
                Assert.IsNotNull(second, "Only one VesselPinned arrived — second locked vessel was not pinned.");
                Assert.AreEqual(leavingPlayer, second.AbsentPlayerName);

                var pinnedIds = new[] { first.VesselId, second.VesselId };
                CollectionAssert.AreEquivalent(
                    new[] { pinnedVesselId, alsoPinnedVesselId },
                    pinnedIds,
                    "Pinned vessel ids did not match the lock-set of the leaving player.");
            }
        }

        [TestMethod]
        public void Disconnect_BeforeAuthentication_DoesNotBroadcast()
        {
            // The pin broadcast lives behind `if (client.Authenticated)` in
            // ClientConnectionHandler.DisconnectClient — a peer that drops mid-handshake
            // has no locks to enumerate and should produce no traffic on the wire.
            using (var watcher = new MockNetClient())
            using (var leaver = new MockNetClient())
            {
                Assert.IsTrue(watcher.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                Handshake(watcher, "h-010-w2");

                Assert.IsTrue(leaver.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                // Intentionally skip handshake. Disconnect before authenticating.
                leaver.Dispose();

                var stray = watcher.WaitForReply<VesselPinnedMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(stray, "Unauthenticated peer disconnect emitted a VesselPinned broadcast.");
            }
        }

        private static void AcquireOrThrow(LockType type, Guid vesselId, string player)
        {
            var def = new LockDefinition(type, player, vesselId);
            var ok = LockSystem.AcquireLock(def, force: true, out _, requesterSubspace: 0);
            Assert.IsTrue(ok, $"Test setup: could not acquire {type} for {player}/{vesselId}.");
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
