using LmpCommon.Message.Data.Agency;
using Server.Log;
using Server.System.Vessel;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S1] Reconciles <c>lmpOwningAgency</c> on the surviving vessel
    /// after a <c>Part.Couple</c> event collapses two vessels into one. Called
    /// from <see cref="Server.Message.VesselMsgReader.HandleVesselCouple"/>
    /// AFTER the existing cross-agency dominant-vessel guard
    /// (<c>RejectIfCrossAgencyWrite</c>, Stage 5.17a write-path counterpart) and
    /// the authority-subspace update, but BEFORE the merged (weak) vessel is
    /// removed from <see cref="VesselStoreSystem.CurrentVessels"/> — so this
    /// helper can still read the merged vessel's agency stamp.
    ///
    /// <para><b>Scope.</b> Covers <em>any</em> path that ends in stock KSP
    /// <c>Part.Couple</c>: stock docking, KAS pipe coupling (verified at
    /// <c>f:/tmp/mks-external/KAS/Source/api_impl/LinkUtilsImpl.cs:96</c> — KAS
    /// calls <c>srcPart.Couple(tgtPart)</c>), and any future mod that triggers
    /// the same KSP API. All such paths surface at the LMP wire as
    /// <c>VesselCoupleMsgData</c> via the
    /// <c>LmpClient/Harmony/Part_Couple.cs</c> patch.</para>
    ///
    /// <para><b>Reconciliation rule.</b> The surviving (kept) vessel's stamp
    /// wins, with one exception for upgrade-window continuity:
    /// <list type="bullet">
    ///   <item><description>Kept == merged (including both <see cref="Guid.Empty"/>)
    ///   — idempotent no-op.</description></item>
    ///   <item><description>Kept <c>Empty</c>, merged non-Empty — adopt the
    ///   merged stamp on kept. The pre-0.31 sentinel by spec §10 Q3 is "any
    ///   agency may interact"; under that posture, agency continuity is more
    ///   valuable than the operator's not-yet-acted-on intent. Debug log.
    ///   Operators can always re-stamp via <c>/setvesselagency</c>.</description></item>
    ///   <item><description>Merged <c>Empty</c>, kept non-Empty — no mutation,
    ///   kept retains. Debug log.</description></item>
    ///   <item><description>Both non-Empty + differ — true cross-agency couple.
    ///   Kept wins per KSP determinism (typically by mass / age); merged is
    ///   destroyed by the caller's <c>RemoveVessel</c>. Warning log for
    ///   operator visibility (Invariant 8).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Why this is read-only on the merged side.</b> The caller
    /// (<see cref="Server.Message.VesselMsgReader.HandleVesselCouple"/>) removes
    /// the merged vessel from the store immediately after this reconcile
    /// returns. Clearing the merged vessel's stamp here would be redundant —
    /// the vessel object goes away in microseconds.</para>
    ///
    /// <para><b>Per-vessel lock (M1 — race fix).</b> The read and mutation
    /// happen under <see cref="VesselDataUpdater.GetVesselLock"/> for the kept
    /// vessel id — the same lock the proto-ingest path
    /// (<see cref="VesselDataUpdater.RawConfigNodeInsertOrUpdate"/>) takes
    /// around its <c>existingStored</c> read + <c>AddOrUpdate</c> write. Without
    /// the lock, a concurrent proto-ingest can replace
    /// <c>CurrentVessels[id]</c> with a fresh <c>Vessel</c> object between our
    /// <c>TryGetValue</c> and our mutation, sending the mutation to an
    /// orphan instance — exactly the race the Stage 5.18d
    /// <see cref="Server.Command.Command.SetVesselAgencyCommand"/> precedent
    /// documents at its line ~308. The merged vessel's <c>TryGetValue</c> sits
    /// inside the same lock for atomicity of the read pair, even though
    /// <c>mergedVesselId</c> has its own per-vessel lock — taking only one is
    /// sufficient because the merged vessel is about to be destroyed anyway;
    /// a stale <c>mergedAgency</c> read at worst skips the adopt branch
    /// (treats merged as Empty), which is the same as if the merged vessel
    /// raced out of the store entirely.</para>
    ///
    /// <para><b>Broadcast + flush on adopt (M2).</b> When the adopt branch
    /// fires, the surviving vessel's stamp changes from <c>Empty</c> to a
    /// tracked agency. The client-side Stage 5.18b
    /// <c>AgencyMembership.RecordOwnership</c> preservation rule (relay bytes
    /// advisory; <c>Empty</c> never downgrades a tracked entry) means a relayed
    /// proto from the new owner does NOT propagate the new stamp to peer
    /// clients. We push <c>AgencyVisibilityMsgData</c> via
    /// <see cref="AgencySystemSender.BroadcastVisibilityChange"/> so 5.18c UI
    /// labels + 5.18d economy guards see the change immediately. A
    /// <see cref="BackupSystem.RunBackup"/> flush persists the change to disk
    /// so a server crash between the reconcile and the next periodic flush
    /// does not lose the adopt. Same shape as
    /// <see cref="Server.Command.Command.SetVesselAgencyCommand"/> step 6+8.</para>
    ///
    /// <para><b>Deferred follow-up: per-router partition cleanup.</b> When the
    /// merged vessel had per-agency entries in MKS routers (kolony, orbital)
    /// or in SCANsat S2 scanners, those entries are vessel-keyed and now
    /// reference a destroyed vessel. The reconciler does NOT clean them up;
    /// stale entries remain in the source agency's <c>AgencyState</c> until
    /// next admin intervention. This was a pre-S1 gap — couple-driven vessel
    /// destruction has always stranded per-router entries — and is not made
    /// worse by S1. Tracked as follow-up work; consider extending this helper
    /// to invoke router cleanup analogous to
    /// <c>SetVesselAgencyCommand</c>'s <c>MigrateForVesselTransfer</c> calls
    /// (with destination=null semantics for "remove without moving") once the
    /// router APIs are extended for the removal-only case.</para>
    ///
    /// <para><b>Dual-mode silence (spec §11).</b> Returns immediately when
    /// <see cref="AgencySystem.PerAgencyEnabled"/> is false (gate off OR
    /// non-Career game mode). Pre-0.31 vessels stay clean; the existing couple
    /// flow runs unchanged.</para>
    /// </summary>
    public static class AgencyVesselCoupleReconciler
    {
        /// <summary>
        /// Reconciles the kept vessel's <c>OwningAgencyId</c> after a couple.
        /// </summary>
        /// <param name="keptVesselId">The surviving (dominant) vessel id —
        /// <c>VesselCoupleMsgData.VesselId</c>.</param>
        /// <param name="mergedVesselId">The destroyed (weak) vessel id —
        /// <c>VesselCoupleMsgData.CoupledVesselId</c>.</param>
        public static void Reconcile(Guid keptVesselId, Guid mergedVesselId)
        {
            if (!AgencySystem.PerAgencyEnabled) return;

            Guid adoptedAgency = Guid.Empty;
            bool didAdopt = false;

            // M1: hold per-vessel lock around BOTH the lookup and the mutation.
            // Proto-ingest serialises on this lock; the mutation must not run
            // against an orphan Vessel instance left over from a racing
            // AddOrUpdate replacement.
            lock (VesselDataUpdater.GetVesselLock(keptVesselId))
            {
                if (!VesselStoreSystem.CurrentVessels.TryGetValue(keptVesselId, out var keptVessel))
                {
                    LunaLog.Debug($"[fix:S1-Couple] kept vessel {keptVesselId:N} not in store; skipping reconcile");
                    return;
                }

                var keptAgency = keptVessel.OwningAgencyId;
                var mergedAgency = VesselStoreSystem.CurrentVessels.TryGetValue(mergedVesselId, out var mergedVessel)
                    ? mergedVessel.OwningAgencyId
                    : Guid.Empty;

                if (keptAgency == mergedAgency)
                {
                    // Same agency or both Empty: idempotent no-op.
                    return;
                }

                if (keptAgency == Guid.Empty)
                {
                    // Kept was Unassigned; merged was tracked. Adopt the
                    // tracked stamp under the lock so a concurrent proto
                    // ingest's existingStored read sees the new value and
                    // preserves it on the replacement Vessel instance.
                    keptVessel.OwningAgencyId = mergedAgency;
                    adoptedAgency = mergedAgency;
                    didAdopt = true;
                    LunaLog.Debug($"[fix:S1-Couple] kept vessel {keptVesselId:N} was Unassigned; adopting merged vessel's agency {mergedAgency:N}");
                    // Fall through outside the lock for broadcast + flush.
                }
                else if (mergedAgency == Guid.Empty)
                {
                    // Merged was Unassigned; kept has the stamp. No mutation.
                    LunaLog.Debug($"[fix:S1-Couple] merged vessel {mergedVesselId:N} was Unassigned; kept vessel {keptVesselId:N} retains agency {keptAgency:N}");
                    return;
                }
                else
                {
                    // Both non-Empty + differ: true cross-agency couple. Kept
                    // wins per KSP determinism. Caller removes the merged
                    // vessel (and broadcasts VesselRemove) on return; we do
                    // not broadcast a visibility change because the kept
                    // stamp did not change.
                    LunaLog.Warning($"[fix:S1-Couple] cross-agency couple: kept vessel {keptVesselId:N} agency {keptAgency:N}; merged vessel {mergedVesselId:N} agency {mergedAgency:N} discarded");
                    return;
                }
            }

            // M2: outside the per-vessel lock — broadcast visibility + flush disk
            // on the adopt branch. Broadcast first so peer clients update the
            // 5.18b VesselOwnership mirror before the next proto ingest could
            // race; flush after so a server crash mid-broadcast still has the
            // disk record correct.
            //
            // BroadcastVisibilityChange is internally gated on PerAgencyEnabled
            // and chunks at AgencyVisibilityMsgData.MaxChangeCount — a single-
            // entry list never chunks.
            AgencySystemSender.BroadcastVisibilityChange(new List<VesselOwnershipChange>
            {
                new VesselOwnershipChange { VesselId = keptVesselId, NewOwningAgencyId = adoptedAgency }
            });

            // RunBackup wrapped in try/catch — mirrors SetVesselAgencyCommand's
            // pattern. A failed backup leaves the in-memory state correct but
            // the disk vessel still without the adopted stamp; log loudly so
            // operators can /backup manually.
            try
            {
                BackupSystem.RunBackup();
            }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:S1-Couple] kept vessel {keptVesselId:N} adopted agency {adoptedAgency:N} but BackupSystem.RunBackup failed; in-memory state correct, disk vessel may still carry no stamp until next periodic flush. Manual /backup recommended. Exception: {e.Message}");
            }
        }
    }
}
