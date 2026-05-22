using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.VesselUtilities;
using System;

namespace LmpClient.Systems.Revert
{
    /// <summary>
    /// Outcome of the revert gate check. AllowFreely lets KSP's stock revert
    /// path run untouched. Block disables the revert buttons and shows the
    /// legacy LMP "CannotRevert" popup.
    /// </summary>
    public enum RevertDecision { AllowFreely, Block }

    /// <summary>
    /// Single source of truth for whether the local player may revert the
    /// active vessel. Drives <see cref="LmpClient.Harmony.PauseMenu_DrawRevertOptions"/>
    /// and <see cref="LmpClient.Harmony.FlightResultsDialog_SetupGUI"/> so the
    /// two patches can't drift.
    ///
    /// <para><b>Decision matrix.</b>
    /// <list type="bullet">
    ///   <item>Legacy LMP-vanilla pass (id-match AND !spectating): AllowFreely.
    ///         Same as upstream behaviour — unchanged.</item>
    ///   <item>PerAgencyCareer=off and the legacy gate failed: Block. Pre-per-
    ///         agency semantics preserved bit-for-bit.</item>
    ///   <item>PerAgencyCareer=on AND the vessel's owning agency matches the
    ///         local player's: AllowFreely. The cross-agency vessel stamp at
    ///         <c>VesselDataUpdater.RawConfigNodeInsertOrUpdate</c> + the
    ///         Stage 5.17a LockSystem guard already prevent other agencies
    ///         from being affected, so the legacy gate's id-match /
    ///         spectating restrictions are structurally redundant for own-
    ///         agency vessels.</item>
    ///   <item>Everything else (foreign agency, Unassigned-sentinel, mirror
    ///         miss, no local agency yet): Block.</item>
    /// </list></para>
    ///
    /// <para><b>Spectating bypass.</b> Under PerAgencyCareer=on the spectating
    /// flag is intentionally bypassed for own-agency vessels. Today's 1:1
    /// player-per-agency model makes this safe (no co-agency teammate to yank
    /// from). When multi-player-per-agency lands (deferred), reverting while
    /// a teammate is interacting with the vessel would surprise them — at
    /// that point this branch needs a co-agency-control-lock check before
    /// allowing.</para>
    ///
    /// <para><b>No soft-confirm.</b> An earlier draft of this gate had a
    /// "switched to your own older vessel" confirm dialog, but
    /// <see cref="RevertEvents.OnVesselChange"/> clears
    /// <c>RevertSystem.StartingVesselId</c> on every vessel switch, making
    /// the confirm trigger condition unreachable in player-driven paths. The
    /// confirm was dropped in favour of the simpler "own-agency = allow" rule.
    /// The tradeoff (no warning when reverting one of your own older vessels)
    /// is bounded to your own agency's work — matches the v10 design intent.
    /// </para>
    /// </summary>
    public static class RevertGate
    {
        /// <summary>
        /// Production entrypoint. Captures the relevant state from the KSP +
        /// LMP singletons and delegates the pure decision logic to
        /// <see cref="DecideFromInputs"/>. Returns the active vessel name
        /// alongside the decision so diagnostic logging at the call site can
        /// surface it without a second lookup.
        ///
        /// <para>The capture-and-delegate split exists so the decision matrix
        /// can be pinned by <c>LmpClientTest</c> (Stage 4.10 pure-helper
        /// pattern). The KSP singletons are untestable in net472 unit tests;
        /// the pure helper takes their values as parameters.</para>
        /// </summary>
        public static RevertDecision Decide(out string vesselName)
        {
            vesselName = string.Empty;
            var active = FlightGlobals.ActiveVessel;
            if (!active) return RevertDecision.Block;

            vesselName = string.IsNullOrEmpty(active.vesselName) ? "(unnamed vessel)" : active.vesselName;

            var revertSys = RevertSystem.Singleton;
            var startingId = revertSys != null ? revertSys.StartingVesselId : Guid.Empty;
            var spectating = VesselCommon.IsSpectating;

            var perAgencyEnabled = SettingsSystem.ServerSettings != null
                                   && SettingsSystem.ServerSettings.PerAgencyCareerEnabled;

            var agencySys = AgencySystem.Singleton;
            var localAgency = agencySys != null ? agencySys.LocalAgencyId : Guid.Empty;
            var ownershipKnown = false;
            var owningAgency = Guid.Empty;
            if (agencySys != null)
                ownershipKnown = agencySys.TryGetOwningAgency(active.id, out owningAgency);

            return DecideFromInputs(
                activeVesselId: active.id,
                startingVesselId: startingId,
                spectating: spectating,
                perAgencyCareerEnabled: perAgencyEnabled,
                localAgencyId: localAgency,
                ownershipKnown: ownershipKnown,
                owningAgencyId: owningAgency);
        }

        /// <summary>
        /// Pure decision helper. All inputs explicit; no singleton reads. The
        /// production <see cref="Decide"/> captures these from the live state;
        /// <c>LmpClientTest</c> exercises every branch with synthetic inputs.
        ///
        /// <para><b>Decision order.</b>
        /// <list type="number">
        ///   <item><c>activeVesselId == Guid.Empty</c> → Block (defensive — the
        ///         production wrapper short-circuits before this, but the
        ///         helper guards anyway).</item>
        ///   <item>Legacy LMP happy path: <c>idMatch &amp;&amp; !spectating</c>
        ///         → AllowFreely. Fires identically under gate=off and gate=on,
        ///         covering the common just-launched-and-flying case.</item>
        ///   <item><c>!perAgencyCareerEnabled</c> → Block. Pre-per-agency
        ///         semantics: anything that fails the legacy gate stays
        ///         blocked.</item>
        ///   <item><c>localAgencyId == Guid.Empty</c> → Block. Per-agency mode
        ///         is on but this client hasn't been assigned an agency yet
        ///         (pre-handshake or server-side per-agency disabled mid-
        ///         session). Defensive.</item>
        ///   <item><c>!ownershipKnown</c> → Block. Vessel ownership mirror
        ///         miss — the 5.18b registry says "we don't know who owns
        ///         this." Treat absent as hazard per the registry's own
        ///         "deny under gate=on for absent entries" guidance.</item>
        ///   <item><c>owningAgencyId != localAgencyId</c> → Block. Cross-
        ///         agency vessel (also catches Unassigned-sentinel
        ///         <c>owningAgencyId == Empty</c> since localAgencyId is
        ///         non-Empty by step 4).</item>
        ///   <item>Default → AllowFreely. Own-agency vessel — allow revert
        ///         regardless of the legacy id-match / spectating gates.</item>
        /// </list></para>
        /// </summary>
        public static RevertDecision DecideFromInputs(
            Guid activeVesselId,
            Guid startingVesselId,
            bool spectating,
            bool perAgencyCareerEnabled,
            Guid localAgencyId,
            bool ownershipKnown,
            Guid owningAgencyId)
        {
            if (activeVesselId == Guid.Empty) return RevertDecision.Block;

            var idMatch = activeVesselId == startingVesselId;

            // Step 2: legacy LMP happy path.
            if (idMatch && !spectating)
                return RevertDecision.AllowFreely;

            // Step 3: pre-per-agency block.
            if (!perAgencyCareerEnabled)
                return RevertDecision.Block;

            // Step 4: agency not yet assigned.
            if (localAgencyId == Guid.Empty)
                return RevertDecision.Block;

            // Step 5: ownership mirror miss.
            if (!ownershipKnown)
                return RevertDecision.Block;

            // Step 6: cross-agency (also catches Unassigned-sentinel
            // owningAgencyId == Empty since localAgencyId is non-Empty here).
            if (owningAgencyId != localAgencyId)
                return RevertDecision.Block;

            // Step 7: own-agency vessel. Allow regardless of the legacy
            // id-match / spectating gates. The cross-agency stamp +
            // 5.17a LockSystem guard structurally prevent other agencies'
            // craft from being affected, so the legacy LMP restrictions
            // are redundant for own-agency under 1:1 player-per-agency.
            return RevertDecision.AllowFreely;
        }
    }
}
