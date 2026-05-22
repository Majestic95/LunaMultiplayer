using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Localization;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.VesselUtilities;
using LmpCommon.Locks;
using System;
using System.Runtime.CompilerServices;

namespace LmpClient.Systems.VesselLockSys
{
    /// <summary>
    /// This class handles the locks in the vessel
    /// </summary>
    public class VesselLockSystem : System<VesselLockSystem>
    {
        #region Fields & properties

        public const string SpectateLock = "LMP_Spectating";
        public const ControlTypes BlockAllControls = ControlTypes.ALLBUTCAMERAS ^ ControlTypes.MAP ^ ControlTypes.PAUSE ^
                                                     ControlTypes.APPLAUNCHER_BUTTONS ^ ControlTypes.VESSEL_SWITCHING ^ ControlTypes.GUI;

        private ScreenMessage _spectateMessage;

        private string GetVesselOwner => VesselCommon.IsSpectating ?
            LockSystem.LockQuery.GetControlLockOwner(FlightGlobals.ActiveVessel.id) :
            "";

        private VesselLockEvents VesselLockEvents { get; } = new VesselLockEvents();

        private string SpectatingMessage => VesselCommon.IsSpectating ? LocalizationContainer.ScreenText.Spectating + $" {GetVesselOwner}." : "";

        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(VesselLockSystem);

        protected override void OnEnabled()
        {
            base.OnEnabled();
            GameEvents.onVesselChange.Add(VesselLockEvents.OnVesselChange);
            GameEvents.onLevelWasLoadedGUIReady.Add(VesselLockEvents.LevelLoaded);
            GameEvents.onVesselLoaded.Add(VesselLockEvents.VesselLoaded);
            LockEvent.onLockAcquire.Add(VesselLockEvents.LockAcquire);
            LockEvent.onLockRelease.Add(VesselLockEvents.LockReleased);
            VesselUnloadEvent.onVesselUnloading.Add(VesselLockEvents.VesselUnloading);
            FlightDriverEvent.onFlightStarted.Add(VesselLockEvents.FlightStarted);

            SetupRoutine(new RoutineDefinition(1000, RoutineExecution.Update, UpdateOnScreenSpectateMessage));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            GameEvents.onVesselChange.Remove(VesselLockEvents.OnVesselChange);
            GameEvents.onLevelWasLoadedGUIReady.Remove(VesselLockEvents.LevelLoaded);
            GameEvents.onVesselLoaded.Remove(VesselLockEvents.VesselLoaded);
            LockEvent.onLockAcquire.Remove(VesselLockEvents.LockAcquire);
            LockEvent.onLockRelease.Remove(VesselLockEvents.LockReleased);
            VesselUnloadEvent.onVesselUnloading.Remove(VesselLockEvents.VesselUnloading);
            FlightDriverEvent.onFlightStarted.Remove(VesselLockEvents.FlightStarted);
        }

        #endregion

        #region Update methods

        /// <summary>
        /// Show a message on the screen if we are spectating
        /// </summary>
        private void UpdateOnScreenSpectateMessage()
        {
            if (VesselCommon.IsSpectating)
            {
                if (_spectateMessage != null)
                    _spectateMessage.duration = 0f;
                _spectateMessage = LunaScreenMsg.PostScreenMessage(SpectatingMessage, 1000 * 2, ScreenMessageStyle.UPPER_CENTER);
            }
            else
            {
                if (_spectateMessage != null)
                {
                    _spectateMessage.duration = 0f;
                    _spectateMessage = null;
                }
            }
        }

        #endregion

        #region Public methods

        public void StartSpectating(Guid spectatingVesselId, [CallerMemberName] string trigger = null)
        {
            // [diag:revert] Capture which caller flipped IsSpectating=true so we
            // can correlate post-launch spectating bites with the trigger path
            // (OnVesselChange vs. LockAcquire). Includes the current Control-lock
            // owner — for a just-launched vessel, the lock should be either us
            // or "(none)"; anything else is a smoking gun for a cross-agency or
            // stale-lock race.
            var lockOwner = LockSystem.LockQuery.GetControlLockOwner(spectatingVesselId);
            LunaLog.Log($"[diag:revert] start-spectating vessel={spectatingVesselId:N} " +
                        $"trigger={trigger ?? "(unknown)"} " +
                        $"lock-owner='{(string.IsNullOrEmpty(lockOwner) ? "(none)" : lockOwner)}'");

            VesselCommon.IsSpectating = true;

            //Lock all vessel controls
            InputLockManager.SetControlLock(BlockAllControls, SpectateLock);

            LockSystem.Singleton.AcquireSpectatorLock();
            LockSystem.Singleton.ReleaseAllPlayerSpecifiedLocks(LockType.Control, LockType.Update, LockType.Kerbal, LockType.UnloadedUpdate);

            //Disable "EVA" button
            HighLogic.CurrentGame.Parameters.Flight.CanEVA = false;
            SpectateEvent.onStartSpectating.Fire();
        }

        public void StopSpectating([CallerMemberName] string trigger = null)
        {
            // [diag:revert] Symmetric exit event so the spectating window is
            // bracketable in the log. CallerMemberName tells us whether the
            // exit was triggered by vessel-change, scene-load, or lock-acquire.
            if (VesselCommon.IsSpectating)
            {
                LunaLog.Log($"[diag:revert] stop-spectating trigger={trigger ?? "(unknown)"}");
            }

            VesselCommon.IsSpectating = false;

            //Unlock all vessel controls
            InputLockManager.RemoveControlLock(SpectateLock);

            if (LockSystem.LockQuery.SpectatorLockExists(SettingsSystem.CurrentSettings.PlayerName))
                LockSystem.Singleton.ReleaseSpectatorLock();

            //We are not spectating anymore so try to get as many unloaded update locks as possible
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (!LockSystem.LockQuery.UnloadedUpdateLockExists(vessel.id))
                    LockSystem.Singleton.AcquireUnloadedUpdateLock(vessel.id);
            }

            if (HighLogic.CurrentGame?.Parameters?.Flight != null)
                HighLogic.CurrentGame.Parameters.Flight.CanEVA = true;

            if (PauseMenu.exists && PauseMenu.isOpen)
                PauseMenu.canSaveAndExit = FlightGlobals.ClearToSave();

            SpectateEvent.onFinishedSpectating.Fire();
        }

        #endregion
    }
}
