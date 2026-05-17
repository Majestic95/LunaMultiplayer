using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Extensions;
using LmpClient.Localization;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselImmortalSys;
using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.VesselPinnedSys
{
    /// <summary>
    /// BUG-010: when the server broadcasts a <c>VesselPinnedMsgData</c> for a vessel whose
    /// pilot just disconnected, this system holds the vessel immortal so the lock-release
    /// → re-acquire → <see cref="VesselImmortalSystem.SetImmortalStateBasedOnLock"/> chain
    /// cannot hand KSP physics a stressed vessel that explodes on the next tick. The pin
    /// auto-clears when any player takes the Control or Update lock on the vessel — the
    /// original pilot reconnecting, or the local player explicitly switching to it.
    /// See docs/research/02-analysis/bug-010-disconnect-vessel-handoff.md.
    /// </summary>
    public class VesselPinnedSystem : MessageSystem<VesselPinnedSystem, VesselPinnedMessageSender, VesselPinnedMessageHandler>
    {
        private readonly ConcurrentDictionary<Guid, string> _pinnedVessels = new ConcurrentDictionary<Guid, string>();

        public override string SystemName { get; } = nameof(VesselPinnedSystem);

        private VesselPinnedEvents VesselPinnedEvents { get; } = new VesselPinnedEvents();

        protected override void OnEnabled()
        {
            base.OnEnabled();
            LockEvent.onLockAcquire.Add(VesselPinnedEvents.OnLockAcquire);
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            LockEvent.onLockAcquire.Remove(VesselPinnedEvents.OnLockAcquire);
            _pinnedVessels.Clear();
        }

        public bool IsPinned(Guid vesselId) => _pinnedVessels.ContainsKey(vesselId);

        public bool TryPin(Guid vesselId, string absentPlayerName, string reason)
        {
            //Active vessel of the local player must never be force-immortal — they are
            //flying it. The merged-couple Variant B case lands here: the docked vessel
            //is the leaver's by lock, but the local player is physically on it.
            if (FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.id == vesselId)
            {
                LunaLog.Log($"[fix:BUG-010] Skipping pin of {vesselId}: it is the local player's active vessel " +
                            $"(absent pilot {absentPlayerName ?? "?"})");
                return false;
            }

            if (string.IsNullOrEmpty(absentPlayerName)) absentPlayerName = "(unknown)";

            _pinnedVessels[vesselId] = absentPlayerName;

            var vessel = FlightGlobals.FindVessel(vesselId);
            if (vessel != null)
            {
                vessel.SetImmortal(true);
            }

            LunaLog.Log($"[fix:BUG-010] Vessel {vesselId} pinned until {absentPlayerName} returns" +
                        (string.IsNullOrEmpty(reason) ? "" : $" (reason: {reason})"));

            LunaScreenMsg.PostScreenMessage($"{absentPlayerName}'s craft pinned — will resume on reconnect",
                4f, ScreenMessageStyle.UPPER_CENTER);
            return true;
        }

        public bool TryUnpin(Guid vesselId, string reason)
        {
            if (!_pinnedVessels.TryRemove(vesselId, out var absentPlayerName)) return false;

            LunaLog.Log($"[fix:BUG-010] Vessel {vesselId} unpinned ({absentPlayerName} returning or new pilot took helm" +
                        (string.IsNullOrEmpty(reason) ? "" : $", {reason}") + ")");

            //Re-derive immortality from the now-current lock state. We do not flip mortal
            //blindly here: the new owner may be a remote player whose subspace is ahead of
            //ours, in which case SetImmortalStateBasedOnLock will correctly leave it
            //immortal for us via the existing !isOurs path.
            var vessel = FlightGlobals.FindVessel(vesselId);
            if (vessel != null)
            {
                VesselImmortalSystem.Singleton.SetImmortalStateBasedOnLock(vessel);
            }
            return true;
        }
    }
}
