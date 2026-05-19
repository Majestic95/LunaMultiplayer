using System;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 3 Slice D-2] Pure decision helper for the
    /// <c>OrbitalLogisticsTransferRequest.Deliver</c> Harmony prefix.
    /// Decides whether the local peer should execute the delivery or
    /// delegate to a different peer that owns the destination vessel.
    /// Closes the per-frame double-spend hazard documented in pre-spec
    /// §1.c — under pre-Phase-3 baseline every peer in physics range of
    /// the destination would mutate <c>Destination.ExchangeResources</c>
    /// in lockstep, multi-applying the delivery.
    ///
    /// <para><b>Gate-state-independent.</b> Unlike Slice B kolony / Slice C
    /// planetary postfixes (gated on <c>PerAgencyCareerEnabled</c>), this
    /// helper runs under both gates. Under gate=off the Update-lock-holder
    /// check is the gate=off authority (KSP enforces single-Control-per-vessel,
    /// so only one peer holds the lock); under gate=on the agency check is
    /// the primary authority and the lock check is defensive (1-player-per-
    /// agency invariant — see pre-spec §2.d note on the lock-check
    /// redundancy under gate=on). One code path serves both.</para>
    ///
    /// <para><b>Why not a <c>Vessel</c>-typed signature.</b> Pre-spec §2.d
    /// proposed <c>Func&lt;TTransfer, Vessel&gt;</c> but
    /// <see cref="LmpClientTest"/> can't reference KSP DLLs (no
    /// <c>UnityEngine</c> / <c>Assembly-CSharp</c> in the test reference set).
    /// Same constraint as <see cref="AgencyMembership"/>'s
    /// <c>TryParseAgencyId</c> taking a string instead of a ConfigNode. The
    /// caller (the Harmony prefix in <c>LmpClient.Harmony</c>) resolves the
    /// destination vessel id + the active-status flag from the live
    /// transfer reference before invoking this helper; the helper itself
    /// stays KSP-free.</para>
    ///
    /// <para><b>Decision table (pre-spec §2.d, revised per 1:1 invariant).</b>
    /// Inputs map to outputs as follows; columns are the active-status case
    /// (<paramref name="isActiveStatus"/>=true). The non-active passthrough
    /// row short-circuits at the top of the method — stock <c>Deliver</c>'s
    /// own failure paths handle <c>Status=Cancelled / Delivered / Failed /
    /// Partial / PreLaunch</c> without our help.</para>
    /// <list type="bullet">
    ///   <item><b>Gate OFF, lock holder == local player:</b> Execute. Under
    ///        shared mode, the lock holder is the single writer authority
    ///        (KSP's single-Control-per-vessel invariant).</item>
    ///   <item><b>Gate OFF, lock holder != local player OR empty:</b> Skip.
    ///        Empty owner is transient post-unload; never assume go-ahead
    ///        in MP.</item>
    ///   <item><b>Gate ON, destination's OwningAgencyId == local agency,
    ///        lock holder == local player:</b> Execute. 1-player-per-agency
    ///        invariant: we are THE player for this agency.</item>
    ///   <item><b>Gate ON, destination's OwningAgencyId == local agency,
    ///        lock holder != local player:</b> Skip (defensive). Under 1:1
    ///        this case is structurally impossible for stamped vessels
    ///        (LockSystem 5.17a guard rejects cross-agency lock acquires).
    ///        Covers transient post-unload empty-owner + connect-race
    ///        windows.</item>
    ///   <item><b>Gate ON, destination's OwningAgencyId != local agency,
    ///        non-Empty:</b> Skip. Cross-agency — owning agency's player
    ///        executes.</item>
    ///   <item><b>Gate ON, destination's OwningAgencyId == Empty
    ///        (Unassigned sentinel, spec §10 Q3):</b> Tie-break by lock
    ///        holder. Local lock → Execute; other / empty lock → Skip.</item>
    ///   <item><b>Gate ON, destination's agency unknown
    ///        (<paramref name="getOwningAgency"/> returns null):</b>
    ///        5.18a mirror not yet populated (connect-race window). Tie-break
    ///        by lock holder — same shape as <c>LockSystem.cs:83-86</c>'s
    ///        defensive bypass when the requester's agency is unmapped.</item>
    ///   <item><b>Destination id <see cref="Guid.Empty"/>:</b> Execute
    ///        (passthrough). The caller failed to resolve a vessel; stock
    ///        <c>Deliver</c>'s own "destination no longer exists" failure
    ///        path will fire on the next yield and mutate Status to Failed
    ///        cleanly. Pre-empting here would set Status=Failed via the
    ///        delegating-skip path instead, masking the legitimate
    ///        "destination gone" diagnostic stock writes.</item>
    /// </list>
    ///
    /// <para><b>Caller contract on skip (CRITICAL — next-author hazard).</b>
    /// When this helper returns <c>false</c>, the calling Harmony prefix MUST
    /// mutate <c>__instance.Status = DeliveryStatus.Failed</c> AND set a brief
    /// <c>__instance.StatusMessage</c> BEFORE returning false. A naive
    /// prefix-skip that returns false without the Status mutation leaves
    /// <c>ScenarioOrbitalLogistics.cs:194</c>'s
    /// <c>while (Status == Launched || Status == Returning) yield return null;</c>
    /// loop yielding forever — ProcessTransfers' outer for never advances, and
    /// the every-2s <c>Update</c> keeps starting fresh ProcessTransfers
    /// coroutines on top of the hung one, accumulating coroutine stacks until
    /// KSP runs out of memory. Pre-spec §2.d ("general-review finding #1")
    /// documents this hazard in full; the prefix author is the one place
    /// where the rule lands in code, so it is also stamped here so
    /// next-authors hitting this helper cold see it without round-tripping
    /// the spec.</para>
    ///
    /// <para><b>Trust posture for cross-agency relay-write.</b> Under gate=on
    /// a client that bypasses this prefix locally and runs <c>Deliver()</c>
    /// would mutate <c>r.amount</c> on the destination vessel and broadcast
    /// via <see cref="LmpCommon.Message.Data.Vessel.VesselResourceMsgData"/>.
    /// The server-side <c>VesselMsgReader.RejectIfCrossAgencyWrite</c> (Stage
    /// 5.17a write-path counterpart, soak Finding-2) drops that relay before
    /// peers see it. Per-agency career state stays consistent even when this
    /// client-side gate is bypassed; this gate's job is preventing the
    /// owning peer's own state from being double-counted by a Karbonite
    /// drift-correction loop with no relay (which the server cannot police
    /// from outside).</para>
    /// </summary>
    public static class OrbitalDeliveryGate
    {
        /// <summary>
        /// True iff the local peer is the elected executor for the delivery.
        /// See class XML for the decision table.
        /// </summary>
        /// <param name="destinationVesselId">Resolved destination vessel
        ///   <c>Vessel.id</c> (canonical Guid form, NOT
        ///   <c>vessel.persistentId.ToString()</c>). Caller resolves via
        ///   <see cref="ResolveDestinationVesselGuid(System.Func{System.Guid})"/>
        ///   or equivalent. <see cref="Guid.Empty"/> means the caller
        ///   couldn't resolve a destination — passthrough (return true).</param>
        /// <param name="isActiveStatus">True iff transfer.Status is
        ///   <c>DeliveryStatus.Launched</c> or
        ///   <c>DeliveryStatus.Returning</c>. False for any other status,
        ///   which the helper passes through to stock <c>Deliver</c>.</param>
        /// <param name="perAgencyEnabled">
        ///   <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled</c>
        ///   at the call site. Gate-state-independent in the sense that
        ///   the helper runs under both — but the decision table branches
        ///   on this value.</param>
        /// <param name="localPlayerName">
        ///   <c>SettingsSystem.CurrentSettings.PlayerName</c>. Compared
        ///   against the Update-lock owner string from
        ///   <paramref name="getUpdateLockOwner"/>.</param>
        /// <param name="localAgencyId">
        ///   <c>AgencySystem.Singleton.LocalAgencyId</c>. <see cref="Guid.Empty"/>
        ///   means the 5.18a Handshake hasn't arrived yet (connect-race
        ///   window); under gate=on we fall back to the defensive lock-
        ///   holder check.</param>
        /// <param name="getOwningAgency">Lookup for the destination
        ///   vessel's <c>OwningAgencyId</c>. Returns
        ///   <c>(Guid?)null</c> when the vessel is not in the 5.18b
        ///   <c>AgencySystem.VesselOwnership</c> registry (connect-race or
        ///   genuinely-new vessel before first VesselSync). Returns
        ///   <see cref="Guid.Empty"/> when the vessel is registered as
        ///   Unassigned (spec §10 Q3 sentinel). Returns the real agency
        ///   id otherwise.</param>
        /// <param name="getUpdateLockOwner">Lookup for the destination
        ///   vessel's current Update-lock holder's player name. Returns
        ///   null/empty when no lock holder exists (vessel just unloaded /
        ///   pre-acquire window).</param>
        public static bool ShouldExecuteDelivery(
            Guid destinationVesselId,
            bool isActiveStatus,
            bool perAgencyEnabled,
            string localPlayerName,
            Guid localAgencyId,
            Func<Guid, Guid?> getOwningAgency,
            Func<Guid, string> getUpdateLockOwner)
        {
            // Status != Launched && Status != Returning → passthrough. Stock
            // Deliver's own validation will handle PreLaunch / Cancelled /
            // Failed / Delivered / Partial — pre-empting would mask
            // diagnostic Status messages stock writes.
            if (!isActiveStatus) return true;

            // Empty destination id → couldn't resolve a vessel at the call
            // site. Pass through so stock Deliver's "destination no longer
            // exists" failure path runs cleanly.
            if (destinationVesselId == Guid.Empty) return true;

            // Defensive null-callable guards. The production prefix always
            // supplies real callables; this protects test surfaces and a
            // hypothetical future caller that wired the helper into a
            // non-prefix path.
            if (getOwningAgency == null || getUpdateLockOwner == null) return true;

            var lockOwner = getUpdateLockOwner(destinationVesselId);
            var lockHolderIsLocal =
                !string.IsNullOrEmpty(lockOwner) &&
                !string.IsNullOrEmpty(localPlayerName) &&
                string.Equals(lockOwner, localPlayerName, StringComparison.Ordinal);

            if (!perAgencyEnabled)
            {
                // Gate OFF: lock-holder authority is the sole decision.
                // Empty lock owner = post-unload transient; never assume.
                return lockHolderIsLocal;
            }

            // Gate ON. Resolve destination's owning agency.
            var destAgency = getOwningAgency(destinationVesselId);

            if (destAgency == null)
            {
                // Unknown destination ownership — 5.18a mirror not yet
                // populated (connect-race window). Defensive bypass: fall
                // back to lock-holder check, same shape as
                // LockSystem.cs:83-86 unmapped-requester branch.
                return lockHolderIsLocal;
            }

            if (destAgency == Guid.Empty)
            {
                // Unassigned-sentinel vessel (spec §10 Q3): any agency may
                // interact. Tie-break by lock holder.
                return lockHolderIsLocal;
            }

            if (localAgencyId == Guid.Empty)
            {
                // Local agency unknown (handshake not yet processed) but the
                // destination has a real agency id. Defensive bypass on the
                // lock check — same shape as the destAgency=null branch
                // above. Avoid blanket-skip here because the local player
                // may legitimately hold the Update lock in this connect-race
                // window (e.g. mid-flight reconnect to a vessel they already
                // own); blanket-skip would falsely flag them as a delegating
                // peer for one or more frames.
                return lockHolderIsLocal;
            }

            if (destAgency.Value == localAgencyId)
            {
                // Same agency. Under 1:1 the local player is THE deliverer
                // for this agency. The lock-holder check is structurally
                // redundant (LockSystem 5.17a guard rejects cross-agency
                // lock acquires, so any non-local lock holder under same
                // agency means transient unload state, not a different
                // agency's player). Retained as a defensive gate per
                // pre-spec §2.d decision-table row 4.
                return lockHolderIsLocal;
            }

            // Cross-agency, non-Empty other agency owns the destination.
            // Skip — owning agency's player executes.
            return false;
        }

        /// <summary>
        /// Pure-helper variant of the destination-vessel-id resolution.
        /// Production call site in the Harmony prefix supplies a closure
        /// that calls <c>OrbitalLogisticsTransferRequest.Destination</c>
        /// (the property runs both the persistentId match AND the Guid
        /// match per MKS source SHA <c>ed0f6aa6</c>
        /// <c>OrbitalLogisticsTransferRequest.cs:127-145</c>) and reads
        /// <c>vessel.id</c>. The helper exists for testability — the
        /// resolution chain is one line at the call site, but pinning it
        /// here lets us assert the
        /// "return Empty when resolver returns null" invariant in the
        /// test surface without standing up a KSP scene.
        /// </summary>
        /// <param name="resolveVessel">Closure that calls
        ///   <c>transfer.Destination</c> at the call site and converts to
        ///   the canonical Guid. Returns <see cref="Guid.Empty"/> when the
        ///   underlying KSP <c>Vessel</c> reference is null (the
        ///   <c>FindVesselByOrbLogModuleId</c> fallback at MKS
        ///   <c>OrbitalLogisticsTransferRequest.cs:141</c> failed too).</param>
        public static Guid ResolveDestinationVesselGuid(Func<Guid> resolveVessel)
        {
            if (resolveVessel == null) return Guid.Empty;
            try
            {
                return resolveVessel();
            }
            catch
            {
                // KSP's FlightGlobals.Vessels access can throw during scene
                // transition or shutdown; the prefix call site is in a
                // FixedUpdate-driven coroutine and must not propagate.
                return Guid.Empty;
            }
        }
    }
}
