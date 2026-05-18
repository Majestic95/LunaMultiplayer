using LmpCommon.Locks;
using Server.Client;
using Server.Log;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Linq;

namespace Server.System
{
    public class LockSystem
    {
        private static readonly LockStore LockStore = new LockStore();
        public static readonly LockQuery LockQuery = new LockQuery(LockStore);

        /// <summary>
        /// Test-only helper. Drops every lock so successive tests don't carry state across
        /// (BugN tests that plant locks via <see cref="AcquireLock"/> with <c>force:true</c>
        /// directly into the store would otherwise leak into the next test's connection
        /// flow and trigger spurious <c>VesselPinned</c> broadcasts on disconnect).
        /// Visible to <c>ServerTest</c> and <c>MockClientTest</c> via
        /// <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/> on the
        /// Server assembly. Never call from production code.
        /// </summary>
        internal static void Reset() => LockStore.ClearAllLocks();

        public static bool AcquireLock(LockDefinition lockDef, bool force, out bool repeatedAcquire, int requesterSubspace = 0)
        {
            repeatedAcquire = false;

            //BUG-005/006: for vessel-tied locks, refuse when the requester's subspace is strictly
            //past the vessel's AuthoritativeSubspaceId. The check is skipped when requesterSubspace
            //is 0 (legacy caller / sentinel) or when the lock type carries no vessel dimension.
            //See docs/research/02-analysis/bug-005-006-cross-subspace-lock.md.
            if (requesterSubspace > 0
                && IsVesselScopedLockType(lockDef.Type)
                && lockDef.VesselId != Guid.Empty
                && VesselStoreSystem.CurrentVessels.TryGetValue(lockDef.VesselId, out var vessel)
                && WarpSystem.IsStrictlyPast(requesterSubspace, vessel.AuthoritativeSubspaceId))
            {
                LunaLog.Debug($"[fix:BUG-005/006] refusing {lockDef.Type} lock on {lockDef.VesselId} for {lockDef.PlayerName} " +
                              $"(requester subspace {requesterSubspace} is past vessel auth subspace {vessel.AuthoritativeSubspaceId})");
                return false;
            }

            //[Stage 5.17a] Cross-agency rejection. When per-agency career is on, refuse a
            //vessel-scoped lock acquire when the requester's agency does not match the vessel's
            //OwningAgencyId. Bypass-only cases:
            //  - Gate off: agency surface is invisible; never reject (spec §11).
            //  - Non-vessel-scoped lock type (AsteroidComet / Contract / Kerbal / Spectator):
            //    no vessel dimension, no agency boundary to enforce.
            //  - lockDef.VesselId == Guid.Empty: caller didn't pin a vessel; cannot compare.
            //  - Vessel.OwningAgencyId == Guid.Empty: spec §10 Q3 Unassigned-sentinel — any
            //    agency may interact. Operator transferagency (Stage 5.18d) flips this on
            //    individual vessels when ownership becomes definite.
            //  - Requester has no agency registered: defensive bypass for direct test
            //    AcquireLock(force:true) calls (e.g. Bug010PinnedBroadcastTest plants locks).
            //    Production path is safe: HandshakeSystem sets Authenticated=true and runs
            //    OnPlayerAuthenticated (which calls RegisterAgency) on the same Lidgren receive
            //    thread before returning; per-connection Lidgren message ordering then ensures
            //    the next CliMsg from this player cannot arrive in the gap.
            //
            //Reject-rather-than-bypass case (closes the ingest-vs-acquire race documented
            //in round-1 consumer-lens review):
            //  - Vessel not yet in CurrentVessels under gate=on: the proto-ingest path
            //    (VesselDataUpdater.RawConfigNodeInsertOrUpdate) stamps the owning agency
            //    inside a fire-and-forget Task.Run, but MessageQueuer.RelayMessage broadcasts
            //    the original wire bytes synchronously on the receive thread. A malicious
            //    Bob receiving Alice's relayed proto could race a LockAcquireMsgData against
            //    Alice's stamp Task and win the bypass before the vessel lands in the store.
            //    Defensively refuse the acquire — legitimate clients delay LockAcquire until
            //    after VesselSync (standard KSP flight init flow), so the rejection only bites
            //    racing peers. Symmetric with the spec's "server is authoritative" stance.
            //
            //**Force-vs-agency interaction (Stage 5.18d transferagency forward-note).** The
            //force:true flag at line ~107 below only overrides existing-holder conflicts; it
            //does NOT bypass this cross-agency authority check (test pinned). When 5.18d's
            //transferagency admin command lands, it must mutate Vessel.OwningAgencyId AND
            //the affected players' AgencyByPlayerName entries directly (not via this lock
            //path), then call ReleasePlayerLocks for cross-agency-now-violating holders on
            //the transferred vessel. Do NOT extend force:true to bypass — that would be
            //weaponisable by any client passing force=true on a LockAcquireMsgData.
            // [Stage 5.18d slice (c)] Cross-agency disposition delegated to the
            // shared classifier so AcquireLock + LockSystem.IsCrossAgencyReject
            // can't drift in the cross-agency check. The classifier returns one
            // of five outcomes; only VesselNotInStoreReject and
            // CrossAgencyReject cause AcquireLock to refuse.
            switch (ClassifyCrossAgency(lockDef, out var crossAgencyOwnerForLog))
            {
                case CrossAgencyClassification.VesselNotInStoreReject:
                    LunaLog.Debug($"[fix:per-agency-career] refusing {lockDef.Type} lock on {lockDef.VesselId} for {lockDef.PlayerName} " +
                                  "(vessel not yet in store under gate=on; closes ingest-vs-acquire race)");
                    return false;
                case CrossAgencyClassification.CrossAgencyReject:
                    LunaLog.Debug($"[fix:per-agency-career] refusing {lockDef.Type} lock on {lockDef.VesselId} for {lockDef.PlayerName} " +
                                  $"(requester agency != vessel owning agency {crossAgencyOwnerForLog:N})");
                    return false;
                // The remaining classifications (NotApplicable / Allowed*) fall
                // through to the legacy already-owned / existing-holder check
                // below — they do not block the acquire on cross-agency grounds.
            }

            //Player tried to acquire a lock that they already own
            if (LockQuery.LockBelongsToPlayer(lockDef.Type, lockDef.VesselId, lockDef.KerbalName, lockDef.PlayerName))
            {
                repeatedAcquire = true;
                return true;
            }

            if (force || !LockQuery.LockExists(lockDef))
            {
                if (lockDef.Type == LockType.Control)
                {
                    //If they acquired a control lock they probably switched vessels or something like that and they can only have one control lock.
                    //So remove the other control locks just for safety...
                    var controlLocks = LockQuery.GetAllPlayerLocks(lockDef.PlayerName).Where(l => l.Type == LockType.Control);
                    foreach (var control in controlLocks)
                        ReleaseLock(control);
                }

                LockStore.AddOrUpdateLock(lockDef);
                return true;
            }
            return false;
        }

        private static bool IsVesselScopedLockType(LockType type) =>
            type == LockType.Control || type == LockType.Update || type == LockType.UnloadedUpdate;

        /// <summary>
        /// Stage 5.18d slice (c) — peek helper for <see cref="LockSystemSender.SendLockAcquireMessage"/>
        /// to detect that a just-rejected <see cref="AcquireLock"/> call would have
        /// been refused specifically by the cross-agency guard (and not for any
        /// of the other reasons in <see cref="AcquireLock"/>: cross-subspace past,
        /// vessel-not-in-store under gate=on, or an existing-holder conflict).
        /// The sender then emits a <see cref="LmpCommon.Message.Data.Lock.LockRejectMsgData"/>
        /// to surface the reason to the originating client; other reject reasons
        /// stay silent as they did pre-5.18d.
        ///
        /// <para><b>Race window.</b> Called AFTER <see cref="AcquireLock"/> returned
        /// false, the vessel could in principle be removed between the two calls.
        /// In that case this method returns false (vessel not in store) and the
        /// sender falls through to the legacy silent-reject path — same UX the
        /// player got pre-5.18d, no regression. Production path: <see cref="LockSystemSender"/>
        /// processes lock-acquire messages on the Lidgren receive thread; vessel
        /// removal happens on a different thread and the window is microsecond-
        /// scale.</para>
        ///
        /// <para><b>Return value semantics.</b> Returns true ONLY when the lock
        /// would be refused by the cross-agency guard specifically. Lower-precedence
        /// reject paths (cross-subspace past, vessel-not-in-store, existing
        /// holder) all return false here even if those are the actual reason the
        /// acquire was refused; the sender treats those as the silent legacy
        /// path.</para>
        ///
        /// <para>Outputs the vessel's <see cref="System.Agency.AgencyState.AgencyId"/>
        /// in <paramref name="owningAgencyId"/> when the return is true, so the
        /// sender can populate the wire message without an extra registry
        /// lookup. <see cref="Guid.Empty"/> when the return is false.</para>
        /// </summary>
        public static bool IsCrossAgencyReject(LockDefinition lockDef, out Guid owningAgencyId)
        {
            return ClassifyCrossAgency(lockDef, out owningAgencyId) == CrossAgencyClassification.CrossAgencyReject;
        }

        /// <summary>
        /// Cross-agency disposition shared by <see cref="AcquireLock"/> and
        /// <see cref="IsCrossAgencyReject"/>. Encodes the full Stage 5.17a
        /// guard's decision tree in one place so future bypass cases or new
        /// reject paths can't introduce silent drift between the two callers
        /// — the prior implementation duplicated the cross-agency branch
        /// across two methods and already had begun to diverge (server-systems-
        /// review v1 SS-2).
        /// </summary>
        private enum CrossAgencyClassification
        {
            /// <summary>
            /// Gate off / non-vessel-scoped lock type / Empty VesselId — the
            /// cross-agency guard never fires.
            /// </summary>
            NotApplicable,

            /// <summary>
            /// Permitted: vessel is in store AND (same agency OR Unassigned-
            /// sentinel per spec §10 Q3).
            /// </summary>
            AllowedSameAgencyOrUnassigned,

            /// <summary>
            /// Permitted: requester has no <see cref="Agency.AgencySystem.AgencyByPlayerName"/>
            /// mapping. 5.17a bypass for defensive direct-AcquireLock calls (test
            /// harness) + the small pre-handshake window between Authenticated
            /// = true and OnPlayerAuthenticated → RegisterAgency.
            /// </summary>
            AllowedRequesterAgencyless,

            /// <summary>
            /// Rejected SILENTLY (pre-5.18d behavior). Gate=on but the vessel
            /// isn't in <see cref="VesselStoreSystem.CurrentVessels"/> yet —
            /// closes the ingest-vs-acquire race documented in 5.17a's
            /// round-1 consumer-lens review. The client gets no LockReject
            /// message because this isn't a cross-agency reason — it's a
            /// race that's expected to resolve on the next VesselSync.
            /// </summary>
            VesselNotInStoreReject,

            /// <summary>
            /// Rejected on cross-agency grounds. Server emits a
            /// <see cref="LmpCommon.Message.Data.Lock.LockRejectMsgData"/> to
            /// the originating client (Stage 5.18d slice (c)) so the player's
            /// UI can surface the reason via a toast.
            /// </summary>
            CrossAgencyReject,
        }

        /// <summary>
        /// Single source of truth for the cross-agency decision. Both
        /// <see cref="AcquireLock"/> and <see cref="IsCrossAgencyReject"/>
        /// call this; the peek returns true exclusively when the result is
        /// <see cref="CrossAgencyClassification.CrossAgencyReject"/>.
        ///
        /// <para><b>Locking note for slice (e) <c>/transferagency</c>
        /// interaction.</b> The classifier reads
        /// <see cref="Agency.AgencySystem.AgencyByPlayerName"/> without
        /// holding the per-name lock the rename writer uses. Slice (e)'s
        /// Add-then-Remove swap means a concurrent reader during a rename
        /// either resolves the old name (returns the same agency id — both
        /// names mapped briefly) or the new name (same agency id) —
        /// idempotent outcome; never a transient null window.</para>
        /// </summary>
        private static CrossAgencyClassification ClassifyCrossAgency(LockDefinition lockDef, out Guid owningAgencyId)
        {
            owningAgencyId = Guid.Empty;

            if (!Agency.AgencySystem.PerAgencyEnabled) return CrossAgencyClassification.NotApplicable;
            if (!IsVesselScopedLockType(lockDef.Type)) return CrossAgencyClassification.NotApplicable;
            if (lockDef.VesselId == Guid.Empty) return CrossAgencyClassification.NotApplicable;

            if (!VesselStoreSystem.CurrentVessels.TryGetValue(lockDef.VesselId, out var vessel))
                return CrossAgencyClassification.VesselNotInStoreReject;

            if (vessel.OwningAgencyId == Guid.Empty)
                return CrossAgencyClassification.AllowedSameAgencyOrUnassigned;

            if (!Agency.AgencySystem.AgencyByPlayerName.TryGetValue(lockDef.PlayerName, out var requesterAgencyId))
                return CrossAgencyClassification.AllowedRequesterAgencyless;

            if (requesterAgencyId == vessel.OwningAgencyId)
                return CrossAgencyClassification.AllowedSameAgencyOrUnassigned;

            owningAgencyId = vessel.OwningAgencyId;
            return CrossAgencyClassification.CrossAgencyReject;
        }

        public static bool ReleaseLock(LockDefinition lockDef)
        {
            if (LockQuery.LockBelongsToPlayer(lockDef.Type, lockDef.VesselId, lockDef.KerbalName, lockDef.PlayerName))
            {
                LockStore.RemoveLock(lockDef);
                return true;
            }

            return false;
        }

        public static void ReleasePlayerLocks(ClientStructure client)
        {
            var removeList = LockQuery.GetAllPlayerLocks(client.PlayerName);

            foreach (var lockToRemove in removeList)
            {
                LockSystemSender.ReleaseAndSendLockReleaseMessage(client, lockToRemove);
            }
        }
    }
}
