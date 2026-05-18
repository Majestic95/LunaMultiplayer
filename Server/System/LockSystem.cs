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
            if (AgencySystem.PerAgencyEnabled
                && IsVesselScopedLockType(lockDef.Type)
                && lockDef.VesselId != Guid.Empty)
            {
                if (!VesselStoreSystem.CurrentVessels.TryGetValue(lockDef.VesselId, out var vesselForAgency))
                {
                    LunaLog.Debug($"[fix:per-agency-career] refusing {lockDef.Type} lock on {lockDef.VesselId} for {lockDef.PlayerName} " +
                                  $"(vessel not yet in store under gate=on; closes ingest-vs-acquire race)");
                    return false;
                }
                if (vesselForAgency.OwningAgencyId != Guid.Empty
                    && AgencySystem.AgencyByPlayerName.TryGetValue(lockDef.PlayerName, out var requesterAgencyId)
                    && requesterAgencyId != vesselForAgency.OwningAgencyId)
                {
                    LunaLog.Debug($"[fix:per-agency-career] refusing {lockDef.Type} lock on {lockDef.VesselId} for {lockDef.PlayerName} " +
                                  $"(requester agency {requesterAgencyId:N} != vessel owning agency {vesselForAgency.OwningAgencyId:N})");
                    return false;
                }
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
