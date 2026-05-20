using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Kerbal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.IO;
using System.Linq;

namespace MockClientTest
{
    /// <summary>
    /// Stage 6 Phase 6.4 — end-to-end harness coverage for
    /// <see cref="KerbalSystem.HandleKerbalsRequest"/> per-agency filtering.
    /// Companion to <c>ServerTest/PerAgencyKerbalRequestFilterTest.cs</c> which
    /// pins the pure path-resolution helper; this file proves the full wire
    /// round-trip from <see cref="KerbalsRequestMsgData"/> to
    /// <see cref="KerbalReplyMsgData"/> against an in-process Server.
    ///
    /// Two cases:
    /// <list type="number">
    ///   <item>Both gates on → Alice and Bob each receive only their own
    ///         agency's kerbal subdir. Proven by planting a unique extra
    ///         kerbal file in Alice's subdir and asserting Bob does NOT see
    ///         it in his reply.</item>
    ///   <item>PerAgencyCareer=on but PerAgencyKerbalRoster=off → both
    ///         clients receive the same SHARED <c>Universe/Kerbals/</c>
    ///         roster. Proves the combined-gate precondition: dual-mode
    ///         silence when only one of the two pieces is on. Also confirms
    ///         per-agency subdirs are still created on disk (Phase 6.3 mint)
    ///         but the request handler ignores them under this gate state.</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class PerAgencyKerbalRequestE2eTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            // ServerHarness reset only touches PerAgencyCareer; the kerbal-roster
            // flag is independent and we own the cleanup ourselves.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
            // Career mode is required for AgencySystem.PerAgencyEnabled.
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestCleanup]
        public void Cleanup()
        {
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;

            // ServerHarness is single-instance per process (CLAUDE.md), so the
            // legacy Universe/Kerbals/ directory persists across tests in this
            // assembly. The gate-off case plants a unique kerbal file there;
            // wipe it on cleanup so it doesn't bleed into adjacent tests that
            // read the legacy dir under a different gate state.
            try
            {
                if (Directory.Exists(KerbalSystem.KerbalsPath))
                    Directory.Delete(KerbalSystem.KerbalsPath, recursive: true);
            }
            catch
            {
                // Best-effort — a stuck file lock is the next test's problem to surface.
            }
        }

        // ------------------------------------------------------------------
        // 1. Both gates on — distinct per-agency rosters, no cross-leak
        // ------------------------------------------------------------------

        [TestMethod]
        public void TwoClients_BothGatesOn_EachReceivesOwnAgencySubdir_AndNoCrossLeak()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            const string playerA = "k64-alice";
            const string playerB = "k64-bob";
            const string uniqueAliceKerbal = "Aurora Test-Kerman";

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientA, playerA);

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientB, playerB);

                // Resolve each client's agency-subdir on disk. Phase 6.3 minted
                // both at handshake time. Plant the unique kerbal into Alice's
                // subdir directly; Bob's subdir is left at the seeded stock 4.
                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerA, out var aliceAgency),
                    "Alice's agency must be auto-registered before we can locate her subdir.");
                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerB, out var bobAgency),
                    "Bob's agency must be auto-registered.");

                var aliceSubdir = AgencySystem.GetKerbalsPathForAgency(aliceAgency);
                var bobSubdir = AgencySystem.GetKerbalsPathForAgency(bobAgency);
                Assert.IsTrue(Directory.Exists(aliceSubdir),
                    "Phase 6.3 mint must have created Alice's Kerbals subdir.");
                Assert.IsTrue(Directory.Exists(bobSubdir));

                File.WriteAllText(
                    Path.Combine(aliceSubdir, uniqueAliceKerbal + ".txt"),
                    "name = " + uniqueAliceKerbal + "\nstate = Available\n");

                // Each client sends their own KerbalsRequest. Inbox-by-type so
                // there's no ordering concern between A and B's replies.
                clientA.SendMessage<KerbalCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalsRequestMsgData>());
                var aliceReply = clientA.WaitForReply<KerbalReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(aliceReply, "Alice never received a KerbalReplyMsgData.");

                clientB.SendMessage<KerbalCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalsRequestMsgData>());
                var bobReply = clientB.WaitForReply<KerbalReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobReply, "Bob never received a KerbalReplyMsgData.");

                var aliceNames = aliceReply.Kerbals.Take(aliceReply.KerbalsCount).Select(k => k.KerbalName).OrderBy(n => n).ToArray();
                var bobNames = bobReply.Kerbals.Take(bobReply.KerbalsCount).Select(k => k.KerbalName).OrderBy(n => n).ToArray();

                // Stock 4 are present in BOTH (each agency has its own seeded copy).
                CollectionAssert.Contains(aliceNames, "Jebediah Kerman", "Alice's roster must include the seeded Jeb.");
                CollectionAssert.Contains(aliceNames, "Bill Kerman");
                CollectionAssert.Contains(aliceNames, "Bob Kerman");
                CollectionAssert.Contains(aliceNames, "Valentina Kerman");
                CollectionAssert.Contains(bobNames, "Jebediah Kerman", "Bob's roster must include his own seeded Jeb.");
                CollectionAssert.Contains(bobNames, "Valentina Kerman");

                // The unique kerbal lives ONLY in Alice's reply.
                CollectionAssert.Contains(aliceNames, uniqueAliceKerbal,
                    $"Alice's reply must include the planted unique kerbal '{uniqueAliceKerbal}'.");
                CollectionAssert.DoesNotContain(bobNames, uniqueAliceKerbal,
                    $"Bob's reply must NOT contain Alice-only kerbal '{uniqueAliceKerbal}' — per-agency filter leaked.");

                // Sanity: Alice has exactly stock 4 + 1 planted = 5. Bob has stock 4.
                Assert.AreEqual(5, aliceReply.KerbalsCount,
                    "Alice's roster should contain exactly stock-4 + the planted unique kerbal.");
                Assert.AreEqual(4, bobReply.KerbalsCount,
                    "Bob's roster should contain exactly the seeded stock 4 — no leakage from Alice.");
            }
        }

        // ------------------------------------------------------------------
        // 2. Combined gate off (PerAgencyKerbalRoster=false) — shared roster
        // ------------------------------------------------------------------

        [TestMethod]
        public void TwoClients_KerbalRosterGateOff_BothReceiveSharedRoster()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;

            const string playerA = "k64-claire";
            const string playerB = "k64-david";
            const string sharedUniqueKerbal = "Custodian Test-Kerman";

            // Pre-populate the legacy shared Universe/Kerbals/ with the unique
            // kerbal so both clients can see it under the dual-mode-silence
            // path. Stock 4 is NOT seeded here because under gate=off the server
            // would normally have called KerbalSystem.GenerateDefaultKerbals at
            // boot — the harness skips MainServer.Main so we do it ourselves.
            Directory.CreateDirectory(KerbalSystem.KerbalsPath);
            File.WriteAllText(
                Path.Combine(KerbalSystem.KerbalsPath, sharedUniqueKerbal + ".txt"),
                "name = " + sharedUniqueKerbal + "\nstate = Available\n");

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientA, playerA);

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientB, playerB);

                // Confirm per-agency subdirs DID get minted (Phase 6.3 runs on
                // PerAgencyEnabled, which is on here — the disk layout exists
                // even though the handler doesn't read it).
                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerA, out var claireAgency));
                Assert.IsTrue(Directory.Exists(AgencySystem.GetKerbalsPathForAgency(claireAgency)),
                    "Phase 6.3 lifecycle hook must still mint per-agency subdirs under PerAgencyCareer=true regardless of PerAgencyKerbalRoster.");

                // Plant something distinct in Claire's subdir to prove it's
                // ignored (gate=off must NOT route reads here).
                File.WriteAllText(
                    Path.Combine(AgencySystem.GetKerbalsPathForAgency(claireAgency), "Should-Not-Appear-Kerman.txt"),
                    "name = Should-Not-Appear-Kerman\n");

                clientA.SendMessage<KerbalCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalsRequestMsgData>());
                var claireReply = clientA.WaitForReply<KerbalReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(claireReply);

                clientB.SendMessage<KerbalCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalsRequestMsgData>());
                var davidReply = clientB.WaitForReply<KerbalReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(davidReply);

                var claireNames = claireReply.Kerbals.Take(claireReply.KerbalsCount).Select(k => k.KerbalName).ToArray();
                var davidNames = davidReply.Kerbals.Take(davidReply.KerbalsCount).Select(k => k.KerbalName).ToArray();

                // Both clients see the legacy-shared planted kerbal.
                CollectionAssert.Contains(claireNames, sharedUniqueKerbal,
                    "Under gate=off, Claire's reply must include the legacy-shared kerbal.");
                CollectionAssert.Contains(davidNames, sharedUniqueKerbal,
                    "Under gate=off, David must see the SAME legacy-shared kerbal as Claire (dual-mode silence).");

                // Neither client sees the kerbal planted in Claire's per-agency subdir
                // (gate=off must NOT enumerate per-agency dirs).
                CollectionAssert.DoesNotContain(claireNames, "Should-Not-Appear-Kerman",
                    "Gate=off must enumerate legacy dir only; the per-agency-subdir-planted file leaked through.");
                CollectionAssert.DoesNotContain(davidNames, "Should-Not-Appear-Kerman");

                // Both clients should have the SAME number of kerbals.
                Assert.AreEqual(claireReply.KerbalsCount, davidReply.KerbalsCount,
                    "Under gate=off the legacy shared roster must be byte-identical between any two clients.");
            }
        }

        private static void HandshakeAndDrainAgencyMessages(MockNetClient client, string playerName)
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

            // PerAgencyCareer is on in both test cases, so the auto-registered
            // AgencyHandshake + AgencyState arrive after the LMP handshake.
            // Drain both before issuing the KerbalsRequest so the inbox is clean.
            var agencyHandshake = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(agencyHandshake, $"AgencyHandshake missing for {playerName}.");
            var agencyState = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(agencyState, $"AgencyState missing for {playerName}.");
        }
    }
}
