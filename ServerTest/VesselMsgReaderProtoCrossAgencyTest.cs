using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.Message;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel.Classes;
using System;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// v4 VesselProto cross-agency write guard. The Stage 5.17a write-path counterpart
    /// at <see cref="VesselMsgReader.RejectIfCrossAgencyWrite"/> gates 11 relayed
    /// message types + Remove + Couple but was NOT extended to
    /// <see cref="VesselMsgReader.HandleVesselProto"/> when shipped in session 19. The
    /// omission was rationalised by the [[5.18b relay-vs-store note]]'s "relayed proto
    /// bytes are advisory" framing — but that framing concerns peer-client interpretation
    /// of the relay, NOT the server's authoritative store, which DOES get overwritten
    /// by the proto bytes. The v4 fix adds <c>RejectIfCrossAgencyWrite</c> to the proto
    /// dispatch path, closing the broad vessel-state-write exploit class (modified
    /// clients crafting protos for other agencies' vessel-ids to mutate crew / parts /
    /// resources / position).
    ///
    /// These tests pin the helper's behavior when invoked with
    /// <see cref="VesselProtoMsgData"/>. The helper itself is type-agnostic (reads only
    /// the base <c>VesselId</c>), so behavior is identical to the 11 relayed types
    /// already pinned in <see cref="VesselMsgReaderCrossAgencyTest"/> — but a dedicated
    /// test file aids discoverability + documents the proto-specific intent + future-
    /// proofs against divergence if the proto path's bypass cases ever need to differ.
    /// End-to-end wire coverage is in <c>MockClientTest/ProtoCrossAgencyRejectionTest</c>.
    ///
    /// See <c>docs/research/v4-vessel-proto-cross-agency-write-guard.md</c> for the
    /// full threat model + race-craft-pre-create documented limitation.
    /// </summary>
    [TestClass]
    public class VesselMsgReaderProtoCrossAgencyTest
    {
        private static readonly string XmlExamplePath = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();

        private readonly Guid _vesselId = Guid.NewGuid();
        private readonly Guid _agencyAlice = Guid.NewGuid();
        private readonly Guid _agencyBob = Guid.NewGuid();

        [TestInitialize]
        public void Setup()
        {
            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();
            // [Stage 5.17e-1] Combined gate requires both PerAgencyCareer AND Career.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestCleanup]
        public void Teardown()
        {
            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
        }

        [TestMethod]
        public void GateOff_AnyProto_AllowedThrough()
        {
            // PerAgencyCareer=false: agency surface is invisible; rejection never fires
            // (spec §11 dual-mode silence). Cross-agency proto continues as in vanilla.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakeProtoMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Gate-off must bypass rejection on the proto path (spec §11 dual-mode silence).");
        }

        [TestMethod]
        public void GateOnButSandboxMode_AnyProto_AllowedThrough()
        {
            // PerAgencyEnabled also requires GameMode=Career (5.17e-1). Sandbox mode
            // with the operator-facing flag on should still skip the rejection — the
            // agency registry isn't authoritative in non-Career modes.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakeProtoMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Sandbox mode must bypass rejection even with PerAgencyCareer=true.");
        }

        [TestMethod]
        public void GateOn_SameAgencyProto_AllowedThrough()
        {
            // Baseline: Alice's agency owns the vessel; Alice broadcasts a proto. Pass-through.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;

            var msg = MakeProtoMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("alice", msg),
                "Same-agency proto sender must not be rejected.");
        }

        [TestMethod]
        public void GateOn_CrossAgencyProto_Rejected()
        {
            // The closed exploit: Bob crafts a proto for Alice's vessel-id (modified
            // crew list / parts / resources / position). Without the guard, the server
            // would overwrite Alice's vessel state on disk + broadcast Bob's bytes to
            // all clients. With the guard, the proto is dropped at the receive thread
            // before RawConfigNodeInsertOrUpdate fires + before any relay.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakeProtoMessage(_vesselId);
            Assert.IsTrue(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Cross-agency proto sender must be rejected (closes the v4 proto-write hole).");
        }

        [TestMethod]
        public void GateOn_UnassignedSentinelVessel_AllowedThrough()
        {
            // Spec §10 Q3: pre-0.31 vessels (lmpOwningAgency absent → OwningAgencyId == Empty)
            // are Unassigned. Any agency may interact until transferagency (Stage 5.18d).
            // Proto path must honour the same sentinel bypass as the 11 relayed types.
            SeedVessel(_vesselId, Guid.Empty);
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakeProtoMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Unassigned-sentinel vessel must allow proto from any agency.");
        }

        [TestMethod]
        public void GateOn_NewVesselId_AllowedThrough()
        {
            // Vessel-not-in-store bypass. CORRECT default for the proto path — proto is
            // the legitimate entry point for brand-new vessels. The per-agency stamp
            // logic in RawConfigNodeInsertOrUpdate already routes new vessels to the
            // sender's own agency (5.16b branch (b) at VesselDataUpdater.cs:152-154),
            // so a same-sender first-proto can't accidentally land in a wrong agency.
            //
            // KNOWN LIMITATION — race-craft-pre-create: Bob could race-send a proto
            // for a vessel-id Alice is about to create, locking her out. See
            // v4-vessel-proto-cross-agency-write-guard.md §3.a. Narrow exploit; not
            // closed by this fix.
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakeProtoMessage(_vesselId);  // vessel NOT seeded
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "New (not-in-store) vessel id must allow the first proto through " +
                "(per-agency stamp routes to sender's own agency).");
        }

        [TestMethod]
        public void GateOn_SenderHasNoAgencyMapping_AllowedThrough()
        {
            // Defensive bypass — symmetric with 5.17a + the 11 relayed types. Production
            // path is safe because OnPlayerAuthenticated runs RegisterAgency on the
            // same Lidgren receive thread before any vessel CliMsg can be processed;
            // a stray sender without a mapping is a defensive-only edge case.
            SeedVessel(_vesselId, _agencyAlice);
            // No AgencyByPlayerName entry for "stray-player".

            var msg = MakeProtoMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("stray-player", msg),
                "Sender without an agency mapping must fall through (defensive bypass).");
        }

        [TestMethod]
        public void RaceCraftPreCreate_BobFirstProtoWins_AliceSubsequentlyLockedOut_DocumentedLimitation()
        {
            // DOCUMENTED LIMITATION — race-craft-pre-create attack. The v4 proto-guard
            // does NOT close this exploit; see breakage-analyses/proto-cross-agency-
            // write-guard.md §17 + docs/research/v4-vessel-proto-cross-agency-write-
            // guard.md §3.a. This test pins the current behavior so that a future
            // Stage 6 / VesselReserveMsgData design that closes the race MUST update
            // or remove this test — preventing silent erosion of the documented
            // limitation contract.
            //
            // Scenario: Alice's KSP locally creates vessel V_A and is about to send
            // her first proto (~2.5s window pre-broadcast). Bob (modified client,
            // somehow knows V_A's freshly-minted Guid) race-broadcasts a proto for
            // V_A FIRST. Bob's proto lands via the vessel-not-in-store bypass;
            // RawConfigNodeInsertOrUpdate stamps V_A to Bob's agency per 5.16b
            // branch (b). When Alice's first proto arrives, V_A is now stamped to
            // Bob's agency and Alice's send is rejected as cross-agency.
            //
            // The test simulates the race at the helper level: (1) Bob's pre-craft
            // proto would pass the guard (vessel-not-in-store fallthrough); (2)
            // after the simulated stamp-to-Bob completes, Alice's proto is now
            // rejected.
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            // (1) Bob's race-broadcast proto for a not-in-store vessel id. Guard
            // bypasses via the vessel-not-in-store branch.
            var bobRaceMsg = MakeProtoMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", bobRaceMsg),
                "Race-craft-pre-create attack — Bob's first proto on an unknown vessel " +
                "id passes through (vessel-not-in-store fallthrough). DOCUMENTED LIMITATION.");

            // Simulate the post-bypass effect of RawConfigNodeInsertOrUpdate's
            // 5.16b branch (b): the not-in-store vessel is now added to the store
            // and stamped to Bob's agency. In production this happens in a Task.Run
            // on a separate thread; the unit-level simulation skips the latency.
            SeedVessel(_vesselId, _agencyBob);

            // (2) Alice's subsequent legitimate proto for the same vessel id is now
            // rejected as cross-agency — Alice's own vessel is locked out by Bob's
            // race. The Warning log floods until the operator deletes Bob's agency
            // or the cohort is shut down. The breakage analysis §17 + scoping doc
            // §3.a both document the operator mitigation (grep for the Warning
            // burst targeting the locked-out vessel id + deleteagency Bob).
            var aliceLegitMsg = MakeProtoMessage(_vesselId);
            Assert.IsTrue(VesselMsgReader.RejectIfCrossAgencyWrite("alice", aliceLegitMsg),
                "Alice's legitimate proto on her own freshly-created vessel id is " +
                "REJECTED because Bob's race-craft stamped it first. DOCUMENTED LIMITATION " +
                "— closing requires Stage 6 / VesselReserveMsgData pre-registration.");
        }

        private static VesselProtoMsgData MakeProtoMessage(Guid vesselId) =>
            MakeMessage<VesselProtoMsgData>(vesselId);

        private static T MakeMessage<T>(Guid vesselId) where T : VesselBaseMsgData
        {
            var msg = ClientFactory.CreateNewMessageData<T>();
            msg.VesselId = vesselId;
            return msg;
        }

        private static void SeedVessel(Guid vesselId, Guid owningAgencyId)
        {
            var vessel = LoadSampleVessel();
            vessel.OwningAgencyId = owningAgencyId;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel),
                "Test setup: vessel must not already be in the store.");
        }

        private static Vessel LoadSampleVessel()
        {
            return new Vessel(File.ReadAllText(Directory.GetFiles(XmlExamplePath).OrderBy(p => p, StringComparer.Ordinal).First()));
        }
    }
}
