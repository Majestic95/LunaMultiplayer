using LmpClient.Base;
using LmpCommon.Locks;

namespace LmpClient.Systems.VesselPinnedSys
{
    public class VesselPinnedEvents : SubSystem<VesselPinnedSystem>
    {
        /// <summary>
        /// Any Control / Update lock acquire on a pinned vessel signals "somebody is now
        /// driving this." Either the original pilot reconnected and re-asserted, or the
        /// local player switched to it and KSP's flow acquired the lock for them. Either
        /// way, drop the pin and let normal SetImmortalStateBasedOnLock take over.
        ///
        /// UnloadedUpdate intentionally does NOT clear the pin — that lock churns when
        /// vessels enter/leave physics distance on remaining clients without implying
        /// anyone is taking the helm.
        ///
        /// We deliberately do NOT hook onVesselChange to unpin on local-player switch:
        /// the lock-acquire round-trip is short (RTT + server grant), and unpinning at
        /// vessel-switch time would call SetImmortalStateBasedOnLock BEFORE the local
        /// lock arrives — flipping the vessel mortal for one or more physics ticks while
        /// the leaver's last stressed pose is still settling. Immortal-for-RTT is the
        /// safer side of that race.
        /// </summary>
        public void OnLockAcquire(LockDefinition lockDefinition)
        {
            if (lockDefinition.Type != LockType.Control && lockDefinition.Type != LockType.Update) return;
            System.TryUnpin(lockDefinition.VesselId, $"{lockDefinition.PlayerName} acquired {lockDefinition.Type}");
        }
    }
}
