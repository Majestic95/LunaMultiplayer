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
    /// Stage 5.17a write-path counterpart (session 19 soak Finding 2). The lock-acquire
    /// guard at <see cref="LockSystem.AcquireLock"/> refuses cross-agency vessel-scoped
    /// lock acquires, but the per-message vessel-relay path in
    /// <see cref="VesselMsgReader"/> was previously unconditional — a cross-agency
    /// player could broadcast Position / Flightstate / Update / Resource / PartSync* /
    /// ActionGroup / Fairing / Decouple / Undock for any vessel they had the id of,
    /// regardless of lock state. KSP's tracking-station "Fly" loads another player's
    /// vessel into the local Flight scene; the relayed (unauthorised) state then
    /// collides with the owning agency's authoritative simulation = physics jitter on
    /// the owner's instance.
    ///
    /// <see cref="VesselMsgReader.RejectIfCrossAgencyWrite"/> is the new guard. Tests
    /// below pin each bypass branch and the rejection path. End-to-end wire coverage
    /// is in <c>MockClientTest/CrossAgencyVesselRelayTest</c>.
    /// </summary>
    [TestClass]
    public class VesselMsgReaderCrossAgencyTest
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
        public void Reject_SameAgencyOwnsVessel_NotRejected()
        {
            // Baseline: Alice's agency owns the vessel; Alice broadcasts a position. Pass-through.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;

            var msg = MakePositionMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("alice", msg),
                "Same-agency sender must not be rejected.");
        }

        [TestMethod]
        public void Reject_DifferentAgencyOwnsVessel_IsRejected()
        {
            // Alice's vessel; Bob (different agency) broadcasts state. Server drops the relay.
            // This is the soak Finding 2 hazard: without the guard, Bob's tracking-station-Fly
            // position updates would broadcast and jitter Alice's authoritative simulation.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakePositionMessage(_vesselId);
            Assert.IsTrue(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Cross-agency sender must be rejected.");
        }

        [TestMethod]
        public void Reject_VesselOwnedByEmptySentinel_NotRejected()
        {
            // Spec §10 Q3: pre-0.31 vessels (lmpOwningAgency absent → OwningAgencyId == Empty)
            // are Unassigned. Any agency may interact until transferagency (Stage 5.18d).
            SeedVessel(_vesselId, Guid.Empty);
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakePositionMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Unassigned-sentinel vessel must allow relay from any agency.");
        }

        [TestMethod]
        public void Reject_GateOff_NotRejected()
        {
            // PerAgencyCareer=false: agency surface is invisible; rejection never fires
            // (spec §11 dual-mode silence). Cross-agency relay continues as in vanilla.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakePositionMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Gate-off must bypass rejection (spec §11 dual-mode silence).");
        }

        [TestMethod]
        public void Reject_GateOnButSandboxMode_NotRejected()
        {
            // PerAgencyEnabled also requires GameMode=Career (5.17e-1). Sandbox mode with
            // the operator-facing flag on should still skip the rejection — the agency
            // registry isn't authoritative in non-Career modes.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakePositionMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Sandbox mode must bypass rejection even with PerAgencyCareer=true.");
        }

        [TestMethod]
        public void Reject_VesselNotInStore_NotRejected()
        {
            // Differs from 5.17a's lock-acquire defense. The ingest race on the relay
            // path is asymmetric — a peer would need to know the vessel id (via prior
            // proto relay) AND a legitimate KSP client wouldn't broadcast state without
            // a lock anyway. Bypass keeps fall-through behaviour for the rare race.
            // (Pinned because changing this contract risks dropping legitimate first
            // ticks of a brand-new vessel under per-agency mode.)
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var msg = MakePositionMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                "Vessel-not-in-store must fall through (bypass), unlike 5.17a's lock-acquire defense.");
        }

        [TestMethod]
        public void Reject_RequesterHasNoAgencyMapping_NotRejected()
        {
            // Defensive bypass — symmetric with 5.17a. Production path is safe because
            // OnPlayerAuthenticated runs RegisterAgency on the same Lidgren receive
            // thread before any vessel CliMsg can be processed.
            SeedVessel(_vesselId, _agencyAlice);
            // No AgencyByPlayerName entry for "stray-player".

            var msg = MakePositionMessage(_vesselId);
            Assert.IsFalse(VesselMsgReader.RejectIfCrossAgencyWrite("stray-player", msg),
                "Player without an agency mapping must fall through to the normal relay path.");
        }

        [TestMethod]
        public void Reject_AllRelayedVesselMessageTypes_AreGated()
        {
            // The helper is type-agnostic — every vessel-relay message type listed in
            // VesselMsgReader.HandleMessage's per-type cases must be rejected when the
            // sender's agency doesn't match the vessel's. Pin each of the 11.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            VesselBaseMsgData[] messages = new VesselBaseMsgData[]
            {
                MakeMessage<VesselPositionMsgData>(_vesselId),
                MakeMessage<VesselFlightStateMsgData>(_vesselId),
                MakeMessage<VesselUpdateMsgData>(_vesselId),
                MakeMessage<VesselResourceMsgData>(_vesselId),
                MakeMessage<VesselPartSyncFieldMsgData>(_vesselId),
                MakeMessage<VesselPartSyncUiFieldMsgData>(_vesselId),
                MakeMessage<VesselPartSyncCallMsgData>(_vesselId),
                MakeMessage<VesselActionGroupMsgData>(_vesselId),
                MakeMessage<VesselFairingMsgData>(_vesselId),
                MakeMessage<VesselDecoupleMsgData>(_vesselId),
                MakeMessage<VesselUndockMsgData>(_vesselId),
            };

            foreach (var msg in messages)
            {
                Assert.IsTrue(VesselMsgReader.RejectIfCrossAgencyWrite("bob", msg),
                    $"Cross-agency {msg.VesselMessageType} must be rejected.");
            }
        }

        private static VesselPositionMsgData MakePositionMessage(Guid vesselId) =>
            MakeMessage<VesselPositionMsgData>(vesselId);

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
