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
using System.Text;

namespace MockClientTest
{
    /// <summary>
    /// Stage 6 Phase 6.5 — end-to-end harness coverage for the per-agency
    /// write-path routing in <see cref="KerbalSystem.HandleKerbalProto"/> +
    /// <see cref="KerbalSystem.HandleKerbalRemove"/>. Companion to
    /// <c>ServerTest/PerAgencyKerbalWriteRoutingTest.cs</c> which pins the
    /// pure helpers; this file proves the full wire round-trip from
    /// <see cref="KerbalProtoMsgData"/> through the handler to disk + relay
    /// against an in-process Server.
    ///
    /// Two cases:
    /// <list type="number">
    ///   <item>Both gates on → Alice's KerbalProto for "Aurora Test-Kerman" lands
    ///         in Alice's subdir; NOT in Bob's subdir; Bob's subsequent
    ///         KerbalsRequest does not contain Aurora; Bob never receives an
    ///         inbound KerbalProto for Aurora (no relay leak).</item>
    ///   <item>PerAgencyKerbalRoster=off → Alice's KerbalProto writes to the
    ///         legacy shared dir AND relays to Bob (preserves v7 cross-client
    ///         behaviour, dual-mode silence proof).</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class PerAgencyKerbalWriteRoutingE2eTest
    {
        [TestInitialize]
        public void ResetPerTest()
        {
            ServerHarness.ResetPerTestState();
            // ServerHarness reset only touches PerAgencyCareer; the kerbal-roster
            // flag is independent and we own the cleanup ourselves.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestCleanup]
        public void Cleanup()
        {
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;

            // ServerHarness is single-instance per process; the legacy
            // Universe/Kerbals/ directory persists across tests. The gate-off
            // case plants a unique kerbal there; wipe it on cleanup so it
            // doesn't bleed into adjacent tests.
            try
            {
                if (Directory.Exists(KerbalSystem.KerbalsPath))
                    Directory.Delete(KerbalSystem.KerbalsPath, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }

        // ------------------------------------------------------------------
        // 1. Both gates on — Alice's proto stays in Alice's agency
        // ------------------------------------------------------------------

        [TestMethod]
        public void TwoClients_BothGatesOn_AlicesKerbalProto_StaysInAlicesAgency_NoRelayLeakToBob()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            const string playerA = "k65-alice";
            const string playerB = "k65-bob";
            const string uniqueKerbal = "Aurora Test-Kerman";

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientA, playerA);

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientB, playerB);

                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerA, out var aliceAgency));
                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerB, out var bobAgency));

                var aliceSubdir = AgencySystem.GetKerbalsPathForAgency(aliceAgency);
                var bobSubdir = AgencySystem.GetKerbalsPathForAgency(bobAgency);

                // Alice sends a KerbalProto for the unique name. KerbalInfo's
                // Serialize/Deserialize pair carries the bytes through the wire;
                // the receiving server writes them via WriteAtomic to Alice's
                // agency subdir.
                var proto = ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalProtoMsgData>();
                proto.Kerbal.KerbalName = uniqueKerbal;
                proto.Kerbal.KerbalData = Encoding.UTF8.GetBytes($"name = {uniqueKerbal}\nstate = Available\nToD = 0\n");
                proto.Kerbal.NumBytes = proto.Kerbal.KerbalData.Length;
                clientA.SendMessage<KerbalCliMsg>(proto);

                // Bob waits briefly for any inbound KerbalProto. Under gate=on
                // the relay must NOT reach Bob — distinct agencies. Assert no
                // stray KerbalProto arrives.
                var strayProtoForBob = clientB.WaitForReply<KerbalProtoMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayProtoForBob,
                    $"Per-agency relay leaked Alice's KerbalProto for '{uniqueKerbal}' to Bob (different agency). Phase 6.5 must scope relay to same-agency peers only.");

                // Disk side: Alice's subdir contains the unique file; Bob's doesn't.
                var alicePath = Path.Combine(aliceSubdir, uniqueKerbal + ".txt");
                var bobPath = Path.Combine(bobSubdir, uniqueKerbal + ".txt");
                Assert.IsTrue(File.Exists(alicePath),
                    "Alice's KerbalProto must land in Alice's agency subdir under gate=on.");
                Assert.IsFalse(File.Exists(bobPath),
                    "Alice's KerbalProto must NOT land in Bob's subdir — cross-agency write isolation broken.");

                // Bob's KerbalsRequest must NOT include Aurora.
                clientB.SendMessage<KerbalCliMsg>(
                    ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalsRequestMsgData>());
                var bobReply = clientB.WaitForReply<KerbalReplyMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobReply);
                for (var i = 0; i < bobReply.KerbalsCount; i++)
                {
                    Assert.AreNotEqual(uniqueKerbal, bobReply.Kerbals[i].KerbalName,
                        $"Bob's roster contains Alice-only kerbal '{uniqueKerbal}' — write-path leak.");
                }
            }
        }

        // ------------------------------------------------------------------
        // 2. Both gates on — Alice's KerbalRemove deletes from Alice's agency only
        // ------------------------------------------------------------------

        [TestMethod]
        public void TwoClients_BothGatesOn_AlicesKerbalRemove_OnlyDeletesFromAlicesAgency_NoRelayToBob()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            const string playerA = "k65-arielle";
            const string playerB = "k65-byron";

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientA, playerA);

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientB, playerB);

                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerA, out var aliceAgency));
                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerB, out var bobAgency));

                // Pre-seed: both subdirs contain a "Jebediah Kerman.txt" from
                // Phase 6.3's stock seed. Alice removes hers; Bob's must stay.
                var aliceJeb = Path.Combine(AgencySystem.GetKerbalsPathForAgency(aliceAgency), "Jebediah Kerman.txt");
                var bobJeb = Path.Combine(AgencySystem.GetKerbalsPathForAgency(bobAgency), "Jebediah Kerman.txt");
                Assert.IsTrue(File.Exists(aliceJeb), "Test precondition: Alice's seeded Jeb present.");
                Assert.IsTrue(File.Exists(bobJeb), "Test precondition: Bob's seeded Jeb present.");

                var remove = ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalRemoveMsgData>();
                remove.KerbalName = "Jebediah Kerman";
                clientA.SendMessage<KerbalCliMsg>(remove);

                // No relay leak to Bob.
                var strayRemoveForBob = clientB.WaitForReply<KerbalRemoveMsgData>(TimeSpan.FromMilliseconds(800));
                Assert.IsNull(strayRemoveForBob,
                    "Per-agency relay must not deliver Alice's KerbalRemove to Bob (different agency).");

                Assert.IsFalse(File.Exists(aliceJeb),
                    "Alice's KerbalRemove must delete from Alice's subdir.");
                Assert.IsTrue(File.Exists(bobJeb),
                    "Alice's KerbalRemove must NOT touch Bob's subdir — cross-agency delete isolation broken.");
            }
        }

        // ------------------------------------------------------------------
        // 3. PerAgencyKerbalRoster=off — Alice's proto writes legacy + relays
        // ------------------------------------------------------------------

        [TestMethod]
        public void TwoClients_KerbalRosterGateOff_AlicesProto_WritesLegacy_AndRelaysToBob_DualModeSilence()
        {
            // PerAgencyCareer ON but PerAgencyKerbalRoster OFF — combined gate
            // false. Writes hit legacy Universe/Kerbals/; relay reaches Bob
            // (preserves v7 cross-client behaviour).
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;

            const string playerA = "k65-claire";
            const string playerB = "k65-derek";
            const string uniqueKerbal = "Sentinel Test-Kerman";

            // Make sure the legacy dir exists so HandleKerbalProto's write path
            // can land its file (in production MainServer.Main creates the
            // legacy dir at boot via GenerateDefaultKerbals; the harness skips
            // MainServer.Main).
            Directory.CreateDirectory(KerbalSystem.KerbalsPath);

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientA, playerA);

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientB, playerB);

                var proto = ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalProtoMsgData>();
                proto.Kerbal.KerbalName = uniqueKerbal;
                proto.Kerbal.KerbalData = Encoding.UTF8.GetBytes($"name = {uniqueKerbal}\nstate = Available\nToD = 0\n");
                proto.Kerbal.NumBytes = proto.Kerbal.KerbalData.Length;
                clientA.SendMessage<KerbalCliMsg>(proto);

                // Under gate=off the relay must reach Bob (v7 behaviour).
                var bobReceived = clientB.WaitForReply<KerbalProtoMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobReceived,
                    "Under gate=off, KerbalProto must relay to all other clients (v7 behaviour).");
                Assert.AreEqual(uniqueKerbal, bobReceived.Kerbal.KerbalName);

                // Disk: legacy path holds the file; no per-agency subdir copy.
                var legacyPath = Path.Combine(KerbalSystem.KerbalsPath, uniqueKerbal + ".txt");
                Assert.IsTrue(File.Exists(legacyPath),
                    "Under gate=off, KerbalProto must write to legacy Universe/Kerbals/.");

                if (AgencySystem.AgencyByPlayerName.TryGetValue(playerA, out var aliceAgency))
                {
                    var perAgencyPath = Path.Combine(
                        AgencySystem.GetKerbalsPathForAgency(aliceAgency),
                        uniqueKerbal + ".txt");
                    Assert.IsFalse(File.Exists(perAgencyPath),
                        "Under gate=off, KerbalProto must NOT also write to the per-agency subdir.");
                }
            }
        }

        // ------------------------------------------------------------------
        // 4. Positive parity for the same-agency relay forward-compat claim
        // ------------------------------------------------------------------

        [TestMethod]
        public void GateOn_SameAgencyMultiClient_RelayReachesPeer_ForwardCompatProof()
        {
            // The current 1:1 OwningPlayerName design means production never
            // has two clients in the same agency. But the
            // RelayToSameAgencyClients filter (KerbalSystem.cs ~line 330) is
            // designed to forward-compat a future multi-player-per-agency
            // change. Without a POSITIVE test pinning that the filter actually
            // selects same-agency peers, a refactor could silently break the
            // forward-compat claim and the gate-on negative tests above (which
            // only assert Bob doesn't receive) would NOT catch the regression.
            // Per [[feedback-negative-assertions-lock-in-bugs]] — gate-on
            // negative assertions need positive parity proofs against the
            // same machinery.
            //
            // We fake the multi-player-per-agency state by wiring TWO
            // AgencyByPlayerName entries to the same AgencyId in the registry.
            // Both clients then send + observe through the same Lidgren-driven
            // handler path; the second client receives Alice's KerbalProto.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            const string playerA = "k65-zoe";
            const string playerB = "k65-yoshi";
            const string uniqueKerbal = "Relay Test-Kerman";

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientA, playerA);

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeAndDrainAgencyMessages(clientB, playerB);

                // Reassign Bob's AgencyByPlayerName to point at Alice's agency.
                // This synthesises the future multi-player-per-agency state
                // without changing production code. Bob's AgencyState dict
                // entry stays untouched (we only need the AgencyByPlayerName
                // index for the relay filter; Bob's local-side mutations
                // wouldn't write here anyway).
                Assert.IsTrue(AgencySystem.AgencyByPlayerName.TryGetValue(playerA, out var aliceAgency));
                AgencySystem.AgencyByPlayerName[playerB] = aliceAgency;

                var proto = ServerContext.ClientMessageFactory.CreateNewMessageData<KerbalProtoMsgData>();
                proto.Kerbal.KerbalName = uniqueKerbal;
                proto.Kerbal.KerbalData = Encoding.UTF8.GetBytes($"name = {uniqueKerbal}\nstate = Available\nToD = 0\n");
                proto.Kerbal.NumBytes = proto.Kerbal.KerbalData.Length;
                clientA.SendMessage<KerbalCliMsg>(proto);

                // Bob now shares Alice's agency mapping — RelayToSameAgencyClients
                // MUST deliver Alice's KerbalProto to Bob. If a future refactor
                // breaks the filter's positive-match path, this test fails.
                var bobReceived = clientB.WaitForReply<KerbalProtoMsgData>(TimeSpan.FromSeconds(5));
                Assert.IsNotNull(bobReceived,
                    "RelayToSameAgencyClients must positively relay to a same-agency peer. " +
                    "This pins the forward-compat claim that the filter selects peers correctly when " +
                    "multi-player-per-agency lands; without this test the negative gate-on assertions " +
                    "above would lock in a silent regression (per feedback-negative-assertions-lock-in-bugs).");
                Assert.AreEqual(uniqueKerbal, bobReceived.Kerbal.KerbalName);
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
            var agencyHandshake = client.WaitForReply<AgencyHandshakeMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(agencyHandshake, $"AgencyHandshake missing for {playerName}.");
            var agencyState = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(agencyState, $"AgencyState missing for {playerName}.");
        }
    }
}
