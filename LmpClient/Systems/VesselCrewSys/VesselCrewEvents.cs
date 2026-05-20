using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.Utilities;
using System;

namespace LmpClient.Systems.VesselCrewSys
{
    public class VesselCrewEvents : SubSystem<VesselCrewSystem>
    {
        /// <summary>
        /// Event triggered when a kerbal boards a vessel
        /// </summary>
        public void OnCrewBoard(Guid kerbalId, string kerbalName, Vessel vessel)
        {
            LunaLog.Log("Crew boarding detected!");

            VesselRemoveSystem.Singleton.MessageSender.SendVesselRemove(kerbalId, false);
            LockSystem.Singleton.ReleaseAllVesselLocks(new[] { kerbalName }, kerbalId);
            VesselRemoveSystem.Singleton.KillVessel(kerbalId, true, "Killing kerbal-vessel as it boarded a vessel");

            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, true, reason: "crew boarded vessel");
        }

        /// <summary>
        /// Event triggered when a kerbal boards an external command seat (lawn chair).
        /// Mirrors OnCrewBoard for the external-seat flow that does NOT route through KerbalEVA.proceedAndBoard.
        /// Pre-existing gap: ExternalSeat subscribers were deleted in 2018-11 commit b7306514 (docking refactor collateral).
        /// </summary>
        public void OnExternalSeatBoard(Vessel vessel, Guid kerbalVesselId, string kerbalName)
        {
            if (vessel == null) return;
            LunaLog.Log("Crew-board to an external seat detected!");

            VesselRemoveSystem.Singleton.MessageSender.SendVesselRemove(kerbalVesselId, false);
            LockSystem.Singleton.ReleaseAllVesselLocks(new[] { kerbalName }, kerbalVesselId);
            VesselRemoveSystem.Singleton.KillVessel(kerbalVesselId, true, "Killing kerbal-vessel as it boarded an external seat");

            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, true, reason: "crew boarded external seat");
        }

        /// <summary>
        /// Event triggered when a kerbal unboards an external command seat.
        /// Sends the rover proto (kerbal removed from protoCrew) and primes the fresh EVA-kerbal vessel
        /// via EvaReady — KSP's OnDeboardSeat does not reliably fire GameEvents.onCrewOnEva.
        /// </summary>
        public void OnExternalSeatUnboard(Vessel unboardedVessel, KerbalEVA kerbal)
        {
            if (unboardedVessel == null || kerbal == null || kerbal.vessel == null) return;
            LunaLog.Log("Crew-unboard from an external seat detected!");

            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(unboardedVessel, true, reason: "crew unboarded external seat");
            EvaReady.FireOnCrewEvaReady(kerbal);
        }

        /// <summary>
        /// Trigger an event once the kerbal in EVA is ready to be sent
        /// </summary>
        public void OnCrewEva(GameEvents.FromToAction<Part, Part> data)
        {
            EvaReady.FireOnCrewEvaReady(data.to.FindModuleImplementing<KerbalEVA>());
        }

        /// <summary>
        /// Kerbal in eva is initialized with orbit data and ready to be sent to the server
        /// </summary>
        public void CrewEvaReady(Vessel evaVessel)
        {
            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(evaVessel, true, reason: "kerbal EVA ready");
        }

        /// <summary>
        /// Crew in the vessel has been modified so send the vessel to the server
        /// </summary>
        public void OnCrewModified(Vessel vessel)
        {
            if (!vessel.isEVA && LockSystem.LockQuery.UnloadedUpdateLockBelongsToPlayer(vessel.id, SettingsSystem.CurrentSettings.PlayerName))
                VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, true, reason: "crew modified");
        }
    }
}
