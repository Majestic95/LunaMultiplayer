using LmpClient.Base;
using System;

namespace LmpClient.Systems.Revert
{
    public class RevertEvents : SubSystem<RevertSystem>
    {
        private static bool _revertingToLaunch = false;

        public void OnVesselChange(Vessel data)
        {
            if (_revertingToLaunch)
            {
                _revertingToLaunch = false;
                // [diag:revert] Mid-revert vessel-change is the only path that
                // preserves StartingVesselId. Log so we can tell post-revert
                // from a switched-and-cleared transition.
                LunaLog.Log($"[diag:revert] starting-vessel-preserved-on-revert vessel={System.StartingVesselId:N} new-active={(data ? data.id : Guid.Empty):N}");
                return;
            }

            var previousId = System.StartingVesselId;
            System.StartingVesselId = Guid.Empty;
            // [diag:revert] Only emit the "cleared" line when StartingVesselId
            // actually held a vessel before — saves a log line per scene-init
            // onVesselChange burst (Empty → Empty is the noisy idle case).
            // A logged "cleared" event after a "set" event is the smoking gun
            // for Gate-1 failure on a vessel the player thought they "just
            // launched."
            if (previousId != Guid.Empty)
            {
                LunaLog.Log($"[diag:revert] starting-vessel-cleared previous={previousId:N} new-active={(data ? data.id : Guid.Empty):N} trigger=OnVesselChange");
            }
        }

        public void VesselAssembled(Vessel vessel, ShipConstruct construct)
        {
            System.StartingVesselId = vessel.id;
            // [diag:revert] The expected "set" event for legitimate VAB/SPH
            // launches — fired by ShipConstruction.AssembleForLaunch Postfix
            // when fromShipAssembly==true. Quickload/savegame paths do NOT
            // fire this event, so its absence in the log is itself a clue.
            LunaLog.Log($"[diag:revert] starting-vessel-set vessel={vessel.id:N} trigger=VesselAssembled");
        }

        public void OnRevertToLaunch()
        {
            _revertingToLaunch = true;
            if (FlightGlobals.ActiveVessel)
            {
                System.StartingVesselId = FlightGlobals.ActiveVessel.id;
                LunaLog.Log($"[diag:revert] starting-vessel-set vessel={FlightGlobals.ActiveVessel.id:N} trigger=OnRevertToLaunch");
            }
        }

        public void GameSceneLoadRequested(GameScenes data)
        {
            if (data != GameScenes.FLIGHT && _revertingToLaunch)
                _revertingToLaunch = false;
        }
    }
}
