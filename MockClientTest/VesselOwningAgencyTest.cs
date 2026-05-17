using LmpCommon.Enums;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MockClientTest.Harness;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MockClientTest
{
    /// <summary>
    /// Stage 5.16b — end-to-end coverage for the server-side <c>lmpOwningAgency</c> stamp
    /// in <see cref="Server.System.Vessel.VesselDataUpdater.RawConfigNodeInsertOrUpdate"/>.
    /// Wire-level: a client's first proto for a vessel stamps the sender's agency;
    /// subsequent protos preserve the existing owner (admin-only transfer per spec
    /// §10 Q3 — Stage 5.18d's <c>transferagency</c> command is the future mutation
    /// path, not a re-proto from a different player).
    ///
    /// Field-level round-trip tests live in <c>ServerTest/VesselOwningAgencyTest.cs</c>;
    /// the mock-harness here pins the receive-thread → ingest pipeline.
    /// </summary>
    [TestClass]
    public class VesselOwningAgencyTest
    {
        private static readonly Lazy<string> SampleVesselText = new Lazy<string>(LoadSampleVesselText);

        [TestInitialize]
        public void ResetPerTest() => ServerHarness.ResetPerTestState();

        [TestMethod]
        public void PerAgencyCareerEnabled_FirstProto_StampsSenderAgency()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            const string playerName = "h-016b-alpha";
            SeedSubspace(1, time: 100d);

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var assignedAgencyId = HandshakeAndGetAgencyId(client, playerName);
                SetClientSubspace(playerName, 1);

                var vesselId = Guid.NewGuid();
                SendProto(client, vesselId, SampleVesselText.Value);

                var stored = WaitForVesselAuthStamp(vesselId, expectedAuthSubspace: 1,
                    TimeSpan.FromSeconds(3));
                Assert.IsNotNull(stored, "Vessel was never inserted into CurrentVessels with AuthSubspace=1.");
                Assert.AreEqual(assignedAgencyId, stored.OwningAgencyId,
                    "First-sight proto must stamp the sender's agency.");
            }
        }

        [TestMethod]
        public void PreExistingUnassignedVessel_KeepsEmpty_OnProtoFromAgencyMember()
        {
            // Round-5 upgrade-lens regression: an operator upgrading a pre-0.31 universe
            // in-place (despite the spec's "fresh-start" recommendation) ends up with
            // existing vessels that have NO lmpOwningAgency on disk. Vessel constructor
            // reads them with OwningAgencyId == Guid.Empty (the spec §10 Q3 "Unassigned
            // sentinel"). When an authenticated player sends the first proto for one of
            // those vessels under gate=on, the server MUST preserve the Empty sentinel
            // rather than first-proto-wins-stamping the sender's agency. Q3 says transfer
            // is admin-only via Stage 5.18d transferagency.
            //
            // Pre-fix this test would fail: stored.OwningAgencyId would be the player's
            // agency rather than Guid.Empty.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            const string playerName = "h-016b-pre";
            SeedSubspace(1, time: 100d);

            var vesselId = Guid.NewGuid();

            // Plant a pre-0.31 vessel: load the sample text directly into CurrentVessels.
            // The sample fixture has no lmpOwningAgency field, so OwningAgencyId is Empty.
            var preExisting = new Server.System.Vessel.Classes.Vessel(SampleVesselText.Value);
            Assert.AreEqual(Guid.Empty, preExisting.OwningAgencyId,
                "Test setup: fixture must lack the lmpOwningAgency field — pre-0.31 vessel.");
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, preExisting),
                "Test setup: vessel must not already be in the store.");

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var assignedAgencyId = HandshakeAndGetAgencyId(client, playerName);
                SetClientSubspace(playerName, 1);

                // Send a proto for the SAME pre-existing vesselId. Under the new rule,
                // the existing-vessel-preserve branch fires regardless of whether the
                // existing OwningAgencyId is Empty or non-Empty — Empty IS the Q3 sentinel.
                SendProto(client, vesselId, SampleVesselText.Value);

                var stored = WaitForVesselAuthStamp(vesselId, expectedAuthSubspace: 1,
                    TimeSpan.FromSeconds(3));
                Assert.IsNotNull(stored, "Proto was never processed (AuthSubspace did not flip to 1).");
                Assert.AreEqual(Guid.Empty, stored.OwningAgencyId,
                    $"Pre-existing vessel with Empty OwningAgencyId (Unassigned sentinel per spec §10 Q3) " +
                    $"must NOT be stamped with the proto sender's agency. Got {stored.OwningAgencyId:N} " +
                    $"instead — first-proto-wins regression contradicts the admin-only-transfer rule.");
                Assert.AreNotEqual(assignedAgencyId, stored.OwningAgencyId,
                    "Sender's agency leaked onto a pre-existing Unassigned vessel.");
            }
        }

        [TestMethod]
        public void ExistingOwner_NotOverwritten_OnProtoFromDifferentAgency()
        {
            // First-owner-wins: once a vessel has an lmpOwningAgency, a re-proto from a player
            // in a DIFFERENT agency must preserve the original owner (the only legitimate
            // ownership mutation is the admin transferagency command — Stage 5.18d).
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            const string playerA = "h-016b-alice";
            const string playerB = "h-016b-bob";
            SeedSubspace(1, time: 100d);
            SeedSubspace(2, time: 1000d);

            using (var clientA = new MockNetClient())
            using (var clientB = new MockNetClient())
            {
                Assert.IsTrue(clientA.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var agencyA = HandshakeAndGetAgencyId(clientA, playerA);
                SetClientSubspace(playerA, 1);

                var vesselId = Guid.NewGuid();
                SendProto(clientA, vesselId, SampleVesselText.Value);

                var afterA = WaitForVesselAuthStamp(vesselId, expectedAuthSubspace: 1,
                    TimeSpan.FromSeconds(3));
                Assert.IsNotNull(afterA, "Player A's proto was never stored.");
                Assert.AreEqual(agencyA, afterA.OwningAgencyId);

                Assert.IsTrue(clientB.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var agencyB = HandshakeAndGetAgencyId(clientB, playerB);
                Assert.AreNotEqual(agencyA, agencyB, "Players must have distinct agencies for this test to be meaningful.");
                SetClientSubspace(playerB, 2);

                // Same vessel id, valid bytes, sent from player B. WarpSystem.IsStrictlyPast(2, 1)
                // is false (subspace 2 is future of subspace 1), so the proto is accepted —
                // ingest runs and the OwningAgency preserve-existing branch fires.
                SendProto(clientB, vesselId, SampleVesselText.Value);

                var afterB = WaitForVesselAuthStamp(vesselId, expectedAuthSubspace: 2,
                    TimeSpan.FromSeconds(3));
                Assert.IsNotNull(afterB, "Player B's proto was never processed (AuthSubspace did not flip to 2).");
                Assert.AreEqual(agencyA, afterB.OwningAgencyId,
                    "Subsequent proto from a different agency must NOT overwrite the original owner.");
                Assert.AreNotEqual(agencyB, afterB.OwningAgencyId,
                    "Sender's agency (B) leaked onto an already-owned vessel.");
            }
        }

        [TestMethod]
        public void WireSuppliedOwningAgency_IsIgnored_WhenSenderHasOwnAgency()
        {
            // Adversarial spoof case: gate=on, sender A is in agency Aa, but A's proto bytes
            // claim ownership by spoofedAgency != Aa. Server is authoritative: Aa's id (from
            // AgencySystem.AgencyByPlayerName) must land on the vessel, NOT the wire-supplied
            // spoofedAgency. Exercises the `senderOwningAgencyId != Guid.Empty` branch with
            // adversarial wire content (vs. the gate-off scrub path which exercises the
            // fall-through scrub).
            GameplaySettings.SettingsStore.PerAgencyCareer = true;

            const string playerName = "h-016b-spoo2";
            SeedSubspace(1, time: 100d);

            var spoofedAgency = Guid.NewGuid();

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                var realAgency = HandshakeAndGetAgencyId(client, playerName);
                Assert.AreNotEqual(realAgency, spoofedAgency, "Spoofed Guid happened to collide with the assigned agency — rerun.");
                SetClientSubspace(playerName, 1);

                var vesselId = Guid.NewGuid();
                var spoofedVesselText = InjectOwningAgency(SampleVesselText.Value, spoofedAgency);
                SendProto(client, vesselId, spoofedVesselText);

                var stored = WaitForVesselAuthStamp(vesselId, expectedAuthSubspace: 1,
                    TimeSpan.FromSeconds(3));
                Assert.IsNotNull(stored, "Vessel was never stored.");
                Assert.AreEqual(realAgency, stored.OwningAgencyId,
                    "Server must overwrite the wire-supplied lmpOwningAgency with the sender's actual agency id.");
                Assert.AreNotEqual(spoofedAgency, stored.OwningAgencyId,
                    "Spoofed agency id leaked through the ingest path.");
            }
        }

        [TestMethod]
        public void PerAgencyCareerDisabled_CleanProto_LeavesOwningAgencyFieldAbsent()
        {
            // Dual-mode silence (spec §11): with PerAgencyCareer=false, the entire
            // OwningAgency stamp block in VesselDataUpdater is skipped — the field is
            // neither written nor read by per-agency logic. A clean proto (no
            // lmpOwningAgency in wire bytes) must round-trip with the field still
            // absent from vessel.Fields, so nothing gets persisted to disk and
            // GetVesselInConfigNodeFormat doesn't leak the field on the wire to other
            // clients. Round-2 review caught the prior implementation writing
            // "00000000000000000000000000000000" to every ingested vessel under gate=off.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer,
                "Test pre-condition: reset must leave PerAgencyCareer=false.");

            const string playerName = "h-016b-silnt";
            SeedSubspace(1, time: 100d);

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeNoAgency(client, playerName);
                SetClientSubspace(playerName, 1);

                var vesselId = Guid.NewGuid();
                SendProto(client, vesselId, SampleVesselText.Value);

                var stored = WaitForVesselAuthStamp(vesselId, expectedAuthSubspace: 1,
                    TimeSpan.FromSeconds(3));
                Assert.IsNotNull(stored, "Vessel was never stored.");
                Assert.IsFalse(stored.Fields.Exists(Server.System.Vessel.Classes.Vessel.OwningAgencyFieldName),
                    "Under PerAgencyCareer=false, the lmpOwningAgency field must remain absent " +
                    "from vessel.Fields — the gate-off path is a true no-op (spec §11 dual-mode silence).");
                Assert.AreEqual(Guid.Empty, stored.OwningAgencyId,
                    "Getter on missing field must return Guid.Empty.");
            }
        }

        [TestMethod]
        public void PerAgencyCareerDisabled_SpoofedProtoBytes_PassThroughUnchanged()
        {
            // Documents the dual-mode-silence corollary: with PerAgencyCareer=false, the
            // VesselDataUpdater stamp block is a no-op, so a client that spoofs lmpOwningAgency
            // in its proto bytes has that value pass through to the store unchanged. This is
            // not a security issue because (a) no LockSystem/scenario-projection consumer
            // honors the field under gate=off, and (b) the gate is set-once-before-universe-
            // populated per spec §11 — a future flip to gate=on requires a fresh universe.
            // The test pins this behavior so a future "let's scrub on gate-off too" refactor
            // sees the dual-mode no-op contract is intentional.
            Assert.IsFalse(GameplaySettings.SettingsStore.PerAgencyCareer);

            const string playerName = "h-016b-pass";
            SeedSubspace(1, time: 100d);

            var spoofedAgency = Guid.NewGuid();
            var spoofedVesselText = InjectOwningAgency(SampleVesselText.Value, spoofedAgency);

            using (var client = new MockNetClient())
            {
                Assert.IsTrue(client.Connect(ServerHarness.Port, TimeSpan.FromSeconds(5)));
                HandshakeNoAgency(client, playerName);
                SetClientSubspace(playerName, 1);

                var vesselId = Guid.NewGuid();
                SendProto(client, vesselId, spoofedVesselText);

                var stored = WaitForVesselAuthStamp(vesselId, expectedAuthSubspace: 1,
                    TimeSpan.FromSeconds(3));
                Assert.IsNotNull(stored, "Vessel was never stored.");
                Assert.AreEqual(spoofedAgency, stored.OwningAgencyId,
                    "Under PerAgencyCareer=false the stamp block is a no-op; wire-supplied " +
                    "lmpOwningAgency passes through unchanged. (Not a security issue — no consumer " +
                    "honors the field under gate=off, and gate flip mid-universe is disallowed by spec §11.)");
            }
        }


        private static Guid HandshakeAndGetAgencyId(MockNetClient client, string playerName)
        {
            // Helper precondition — the State message only ships when the gate is on.
            // Without this assert, a misuse with gate=off would silently time out 5s
            // waiting for a message the server will never send.
            Assert.IsTrue(GameplaySettings.SettingsStore.PerAgencyCareer,
                "HandshakeAndGetAgencyId requires PerAgencyCareer=true — set it before calling.");

            HandshakeNoAgency(client, playerName);

            // PerAgencyCareer=true path: handshake reply is followed by AgencyHandshake +
            // AgencyState on channel 22. The state message carries the assigned id; pull
            // it directly so we don't depend on cross-channel ordering against any other
            // post-handshake traffic.
            var state = client.WaitForReply<AgencyStateMsgData>(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(state, $"Did not receive AgencyStateMsgData for {playerName}.");
            return state.AgencyId;
        }

        private static void HandshakeNoAgency(MockNetClient client, string playerName)
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

        private static void SetClientSubspace(string playerName, int subspace)
        {
            var registered = ServerContext.Clients.Values.SingleOrDefault(c => c.PlayerName == playerName);
            Assert.IsNotNull(registered, $"Server did not register {playerName}.");
            registered.Subspace = subspace;
        }

        private static void SendProto(MockNetClient client, Guid vesselId, string vesselText)
        {
            var bytes = Encoding.UTF8.GetBytes(vesselText);
            var proto = ServerContext.ClientMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
            proto.VesselId = vesselId;
            proto.Data = bytes;
            proto.NumBytes = bytes.Length;
            client.SendMessage<VesselCliMsg>(proto);
        }

        /// <summary>
        /// Polls <see cref="VesselStoreSystem.CurrentVessels"/> until the entry for
        /// <paramref name="vesselId"/> reports the expected <c>AuthoritativeSubspaceId</c>.
        /// The ingest path inside <c>RawConfigNodeInsertOrUpdate</c> is fire-and-forget
        /// (<c>Task.Run</c>), so the wire-relay can land on the caller before the store
        /// write commits. Waiting on AuthSubspace gives us a definitive "the new vessel
        /// object is now in the store" signal that doesn't depend on a watcher peer.
        /// </summary>
        private static Server.System.Vessel.Classes.Vessel WaitForVesselAuthStamp(Guid vesselId, int expectedAuthSubspace, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var v)
                    && v.AuthoritativeSubspaceId == expectedAuthSubspace)
                {
                    return v;
                }
                Thread.Sleep(20);
            }
            return null;
        }

        private static void SeedSubspace(int id, double time)
        {
            WarpContext.Subspaces.TryAdd(id, new Subspace(id, time, "test"));
        }

        /// <summary>
        /// Prepends a fake <c>lmpOwningAgency</c> top-level value to the sample vessel text.
        /// Used by <see cref="WireSuppliedOwningAgency_IsScrubbed_WhenSenderHasNone"/> to
        /// simulate a malicious / misbehaving client that ships ownership claims on the wire.
        /// The injected value uses the canonical "N" 32-char hex format the production setter
        /// writes, so the parser sees it as a legitimate-looking field.
        /// </summary>
        private static string InjectOwningAgency(string vesselText, Guid agency)
        {
            return $"{Server.System.Vessel.Classes.Vessel.OwningAgencyFieldName} = {agency:N}\n{vesselText}";
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
