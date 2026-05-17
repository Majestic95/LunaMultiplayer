using LmpCommon.Locks;
using Server.Client;
using Server.Log;
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
