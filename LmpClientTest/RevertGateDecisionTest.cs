using LmpClient.Systems.Revert;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LmpClientTest
{
    /// <summary>
    /// E-fix (2026-05-22). Pure decision coverage for
    /// <see cref="RevertGate.DecideFromInputs"/> — the helper that decides
    /// whether the local player may revert the active vessel.
    ///
    /// <para>Each branch in the helper has its own test so a careless flip of
    /// any guard fires loud. The production wrapper
    /// <see cref="RevertGate.Decide"/> reads from <c>FlightGlobals.ActiveVessel</c>
    /// / <c>RevertSystem.Singleton</c> / <c>VesselCommon.IsSpectating</c> /
    /// <c>SettingsSystem.ServerSettings</c> / <c>AgencySystem.Singleton</c>,
    /// none of which are reachable from a net472 unit test. The pure helper
    /// is the only test-reachable surface for the decision contract.</para>
    ///
    /// <para>Stage 4.10 pure-helper extraction pattern (see
    /// <c>SteadyStateRetryDecisionTest</c>, <c>InterpolationCapDecisionTest</c>,
    /// <c>AgencyLabelFormatterTest</c>).</para>
    /// </summary>
    [TestClass]
    public class RevertGateDecisionTest
    {
        // Reference values used as a "happy path" baseline. Tests below modify
        // a single field to pin one branch.
        private static readonly Guid LocalAgency = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid ForeignAgency = new Guid("22222222-2222-2222-2222-222222222222");
        private static readonly Guid VesselA = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid VesselB = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        // -- Step 1: defensive Empty-active-vessel guard. ------------------

        [TestMethod]
        public void DecideFromInputs_NoActiveVessel_ReturnsBlock()
        {
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: Guid.Empty,
                startingVesselId: VesselA,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: LocalAgency);
            Assert.AreEqual(RevertDecision.Block, decision);
        }

        // -- Step 2: legacy LMP happy path. --------------------------------

        [TestMethod]
        public void DecideFromInputs_GateOff_IdMatch_NotSpectating_ReturnsAllowFreely()
        {
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselA,
                spectating: false,
                perAgencyCareerEnabled: false,
                localAgencyId: Guid.Empty,
                ownershipKnown: false,
                owningAgencyId: Guid.Empty);
            Assert.AreEqual(RevertDecision.AllowFreely, decision);
        }

        [TestMethod]
        public void DecideFromInputs_GateOn_IdMatch_NotSpectating_ReturnsAllowFreely()
        {
            // Same legacy short-circuit fires under gate=on too — the per-agency
            // path doesn't even run for the common just-launched case.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselA,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: LocalAgency);
            Assert.AreEqual(RevertDecision.AllowFreely, decision);
        }

        // -- Step 3: pre-per-agency block. ---------------------------------

        [TestMethod]
        public void DecideFromInputs_GateOff_IdMatch_Spectating_ReturnsBlock()
        {
            // Legacy: id-match doesn't save you if you're spectating, and there's
            // no per-agency fallback under gate=off.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselA,
                spectating: true,
                perAgencyCareerEnabled: false,
                localAgencyId: Guid.Empty,
                ownershipKnown: false,
                owningAgencyId: Guid.Empty);
            Assert.AreEqual(RevertDecision.Block, decision);
        }

        [TestMethod]
        public void DecideFromInputs_GateOff_IdMismatch_ReturnsBlock()
        {
            // Legacy: switched vessels means no revert.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselB,
                spectating: false,
                perAgencyCareerEnabled: false,
                localAgencyId: Guid.Empty,
                ownershipKnown: false,
                owningAgencyId: Guid.Empty);
            Assert.AreEqual(RevertDecision.Block, decision);
        }

        // -- Step 4: gate=on but agency not yet assigned. ------------------

        [TestMethod]
        public void DecideFromInputs_GateOn_LocalAgencyEmpty_ReturnsBlock()
        {
            // Per-agency career on but this client's handshake hasn't landed
            // (or per-agency was disabled server-side mid-session). Defensive
            // block — we have no agency identity to compare against.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselB,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: Guid.Empty,
                ownershipKnown: true,
                owningAgencyId: LocalAgency);
            Assert.AreEqual(RevertDecision.Block, decision);
        }

        // -- Step 5: ownership mirror miss. --------------------------------

        [TestMethod]
        public void DecideFromInputs_GateOn_OwnershipUnknown_ReturnsBlock()
        {
            // 5.18b registry hasn't received an ownership stamp for this vessel
            // — the relay path could clobber Empty over real, so absent !=
            // "own-agency by default." Block per the registry's documented
            // "deny under gate=on for absent entries" guidance.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselB,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: false,
                owningAgencyId: Guid.Empty);
            Assert.AreEqual(RevertDecision.Block, decision);
        }

        // -- Step 6: cross-agency. -----------------------------------------

        [TestMethod]
        public void DecideFromInputs_GateOn_ForeignAgencyVessel_ReturnsBlock()
        {
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselB,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: ForeignAgency);
            Assert.AreEqual(RevertDecision.Block, decision);
        }

        [TestMethod]
        public void DecideFromInputs_GateOn_UnassignedSentinelVessel_ReturnsBlock()
        {
            // Stage 5.18d transferagency will mint an explicit owner; until
            // then Unassigned-sentinel vessels are nobody's craft. Don't let
            // the local player revert one — the operator-level transfer is
            // the only authoritative path.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselB,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: Guid.Empty);
            Assert.AreEqual(RevertDecision.Block, decision);
        }

        // -- Step 7: own-agency, no signal to confirm against. -------------

        [TestMethod]
        public void DecideFromInputs_GateOn_OwnAgency_StartingIdEmpty_ReturnsAllowFreely()
        {
            // The bug-victim path. Assembly event never fired (quickload,
            // reconnect, race) — player thinks they just launched. Allow
            // without confirm friction.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: Guid.Empty,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: LocalAgency);
            Assert.AreEqual(RevertDecision.AllowFreely, decision);
        }

        [TestMethod]
        public void DecideFromInputs_GateOn_OwnAgency_IdMatch_Spectating_ReturnsAllowFreely()
        {
            // Per-agency idMatch overrides the spectating gate. Today's 1:1
            // player-per-agency means "spectating own vessel" is a stale-lock
            // / race artifact, not a real co-tenant scenario. Allow.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselA,
                spectating: true,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: LocalAgency);
            Assert.AreEqual(RevertDecision.AllowFreely, decision);
        }

        // -- Step 7 (default branch): own-agency switched-to-older-vessel
        // also resolves to AllowFreely. The earlier draft had a soft-confirm
        // path here but it was unreachable in practice (RevertEvents.OnVesselChange
        // clears StartingVesselId on every vessel switch). Dropped in favour
        // of the simpler "own-agency = allow" rule.

        [TestMethod]
        public void DecideFromInputs_GateOn_OwnAgency_IdMismatch_ReturnsAllowFreely()
        {
            // Player switched to a different own-agency vessel and pressed
            // Revert. Allowed without confirm — the cross-agency stamp
            // already prevents other agencies' craft from being affected.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselB,
                spectating: false,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: LocalAgency);
            Assert.AreEqual(RevertDecision.AllowFreely, decision);
        }

        [TestMethod]
        public void DecideFromInputs_GateOn_OwnAgency_IdMismatch_Spectating_ReturnsAllowFreely()
        {
            // Same as above with spectating=true. The per-agency relaxation
            // suppresses the spectating gate for own-agency vessels.
            var decision = RevertGate.DecideFromInputs(
                activeVesselId: VesselA,
                startingVesselId: VesselB,
                spectating: true,
                perAgencyCareerEnabled: true,
                localAgencyId: LocalAgency,
                ownershipKnown: true,
                owningAgencyId: LocalAgency);
            Assert.AreEqual(RevertDecision.AllowFreely, decision);
        }
    }
}
