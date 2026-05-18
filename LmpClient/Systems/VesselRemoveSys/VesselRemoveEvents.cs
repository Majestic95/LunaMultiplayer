using LmpClient.Base;
using LmpClient.Events;
using LmpClient.Localization;
using LmpClient.Systems.Agency;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.VesselUtilities;
using System;
using UniLinq;

namespace LmpClient.Systems.VesselRemoveSys
{
    public class VesselRemoveEvents : SubSystem<VesselRemoveSystem>
    {
        private static Guid _recoveringTerminatingVesselId = Guid.Empty;

        /// <summary>
        /// This event is called when the vessel gone BOOM (the Vessel.Die() is called)
        /// If we have the update lock of it we kill it
        /// It doesn't matter if we own the control lock or not as perhaps we are killing a vessel of a player who disconnected.
        /// </summary>
        public void OnVesselWillDestroy(Vessel dyingVessel)
        {
            //Only send the vessel remove msg if we own the unloaded update lock
            if (!LockSystem.LockQuery.UnloadedUpdateLockExists(dyingVessel.id) ||
                LockSystem.LockQuery.UnloadedUpdateLockBelongsToPlayer(dyingVessel.id, SettingsSystem.CurrentSettings.PlayerName) || dyingVessel.id == _recoveringTerminatingVesselId)
            {
                var ownVesselDying = FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.id == dyingVessel.id;

                var reason = dyingVessel.id == _recoveringTerminatingVesselId ? "Recovered/Terminated" : "Destroyed";
                LunaLog.Log($"[LMP]: Removing vessel {dyingVessel.id}-{dyingVessel.persistentId}, Name: {dyingVessel.vesselName} from the server: {reason}");

                if (!ownVesselDying)
                {
                    //Add to the kill list so it's also removed from the store later on!
                    System.KillVessel(dyingVessel.id, true, "OnVesselWillDestroy - " + reason);
                    System.MessageSender.SendVesselRemove(dyingVessel);
                }
                else
                {
                    //We do not add our OWN vessel to the kill list as then if we revert we won't be able to send the vessel proto again
                    //As the "VesselWillBeKilled" method will return true.
                    //For this reason we also tell the other players to NOT keep it in the remove list
                    System.MessageSender.SendVesselRemove(dyingVessel, false);
                }

                //Vessel is dead so remove the locks. Do not remove the kerbal locks as that's done in the Kerbal system
                LockSystem.Singleton.ReleaseAllVesselLocks(null, dyingVessel.id, 0.5f);
                RemoveEvent.onLmpDestroyVessel.Fire(dyingVessel);

                VesselCommon.RemoveVesselFromSystems(dyingVessel.id);
            }
        }

        /// <summary>
        /// This event is called when requesting a recovery FROM FLIGHT
        /// </summary>
        public void OnVesselRecovering(Vessel recoveredVessel)
        {
            OnVesselRecovered(recoveredVessel.protoVessel, false);
        }

        /// <summary>
        /// This event is called when the vessel is recovered
        /// </summary>
        public void OnVesselRecovered(ProtoVessel recoveredVessel, bool quick)
        {
            //quick == true when you press "space center" from the inflight menu

            // Stage 5.18d slice (h): cross-agency recovery guard. Refuses the
            // local-side recovery before KSP's Funding.Instance.AddFunds runs
            // — preventing the leak window where the local player credits
            // recovery funds for a vessel the server's 5.17a write-path
            // counterpart would refuse to remove. The toast surfaces the
            // reason so the player understands why their recovery click
            // didn't take effect.
            if (TryBlockCrossAgencyAction(recoveredVessel.vesselID, "recover"))
                return;

            if (!LockSystem.LockQuery.CanRecoverOrTerminateTheVessel(recoveredVessel.vesselID, SettingsSystem.CurrentSettings.PlayerName))
            {
                LunaScreenMsg.PostScreenMessage(LocalizationContainer.ScreenText.CannotRecover, 5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            _recoveringTerminatingVesselId = recoveredVessel.vesselID;
            LunaLog.Log($"[LMP]: Removing vessel {recoveredVessel.vesselID}, Name: {recoveredVessel.vesselName} from the server: Recovered");

            System.MessageSender.SendVesselRemove(recoveredVessel.vesselID);

            //Vessel is recovered so remove the locks. Do not remove the kerbal locks as that's done in the Kerbal system
            LockSystem.Singleton.ReleaseAllVesselLocks(null, recoveredVessel.vesselID, 1);

            //We consider this vessel removed but we let KSP do the remove of the vessel
            System.RemovedVessels.TryAdd(recoveredVessel.vesselID, DateTime.Now);
            RemoveEvent.onLmpRecoveredVessel.Fire(recoveredVessel);

            VesselCommon.RemoveVesselFromSystems(recoveredVessel.vesselID);
        }

        /// <summary>
        /// This event is called when vessel is terminated from track station
        /// </summary>
        public void OnVesselTerminated(ProtoVessel terminatedVessel)
        {
            // Stage 5.18d slice (h): cross-agency termination guard. Mirrors the
            // recovery guard; the same justification applies (Termination
            // doesn't credit funds but it does remove the vessel server-side,
            // which the 5.17a guard rejects — refusing locally produces
            // clearer UX than letting the player click the button and have
            // nothing happen).
            if (TryBlockCrossAgencyAction(terminatedVessel.vesselID, "terminate"))
                return;

            if (!LockSystem.LockQuery.CanRecoverOrTerminateTheVessel(terminatedVessel.vesselID, SettingsSystem.CurrentSettings.PlayerName))
            {
                LunaScreenMsg.PostScreenMessage(LocalizationContainer.ScreenText.CannotTerminate, 5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            _recoveringTerminatingVesselId = terminatedVessel.vesselID;
            LunaLog.Log($"[LMP]: Removing vessel {terminatedVessel.vesselID}, Name: {terminatedVessel.vesselName} from the server: Terminated");

            System.MessageSender.SendVesselRemove(terminatedVessel.vesselID);

            //Vessel is terminated so remove locks Do not remove the kerbal locks as that's done in the Kerbal system
            LockSystem.Singleton.ReleaseAllVesselLocks(null, terminatedVessel.vesselID, 1);

            //We consider this vessel removed but we let KSP do the remove of the vessel
            System.RemovedVessels.TryAdd(terminatedVessel.vesselID, DateTime.Now);
            RemoveEvent.onLmpRecoveredVessel.Fire(terminatedVessel);

            VesselCommon.RemoveVesselFromSystems(terminatedVessel.vesselID);
        }

        /// <summary>
        /// Triggered when reverting back to the launchpad. The vessel id does NOT change
        /// </summary>
        public void OnRevertToLaunch()
        {
            if (FlightGlobals.ActiveVessel != null && !VesselCommon.IsSpectating)
            {
                LunaLog.Log("[LMP]: Detected a revert to launch!");
                RemoveOldVesselAndItsDebris(FlightGlobals.ActiveVessel, ProtoCrewMember.RosterStatus.Assigned);
                System.MessageSender.SendVesselRemove(FlightGlobals.ActiveVessel, false);
                VesselCommon.RemoveVesselFromSystems(FlightGlobals.ActiveVessel.id);
            }
        }

        /// <summary>
        /// Triggered when reverting back to the editor. The vessel id DOES change
        /// </summary>
        public void OnRevertToEditor(EditorFacility data)
        {
            if (FlightGlobals.ActiveVessel != null && !VesselCommon.IsSpectating)
            {
                LunaLog.Log($"[LMP]: Detected a revert to editor! {data}");
                RemoveOldVesselAndItsDebris(FlightGlobals.ActiveVessel, ProtoCrewMember.RosterStatus.Available);
                System.MessageSender.SendVesselRemove(FlightGlobals.ActiveVessel);

                //We consider this vessel removed but we let KSP do the remove of the vessel
                System.RemovedVessels.TryAdd(FlightGlobals.ActiveVessel.id, DateTime.Now);
                VesselCommon.RemoveVesselFromSystems(FlightGlobals.ActiveVessel.id);
            }
        }

        /// <summary>
        /// Consolidated bridge from the Unity-thread <see cref="OnVesselRecovered"/> /
        /// <see cref="OnVesselTerminated"/> callsites to the pure
        /// <see cref="AgencyMembership.IsRecoveryBlockedByAgency"/> helper. Reads
        /// the singleton state on the calling thread (helper itself is
        /// unit-testable in <c>LmpClientTest</c> without KSP DLLs); on a block,
        /// emits the operator-facing toast + the operator-greppable KSP.log line
        /// and returns true so the caller can early-return.
        ///
        /// <para><b>Identity-resolution.</b> When the owning agency is in the
        /// client's <see cref="AgencySystem.OtherAgencies"/> snapshot, the toast
        /// surfaces its display name + owner. When it's not (late-joiner whose
        /// handshake hasn't reached this client yet), falls back to a generic
        /// "different agency" — better than no toast.</para>
        ///
        /// <para><b>TODO Stage 5.18+ i18n.</b> The literal toast strings should
        /// be lifted to <c>LocalizationContainer.ScreenText.CannotRecoverCrossAgency</c>
        /// + <c>CannotTerminateCrossAgency</c> alongside the existing
        /// <c>CannotRecover</c> / <c>CannotTerminate</c> entries. The dynamic
        /// agency-name interpolation needs a format-string convention on the
        /// localization side; defer until a focused localization pass.</para>
        /// </summary>
        private static bool TryBlockCrossAgencyAction(Guid vesselId, string actionVerb)
        {
            var agencySystem = AgencySystem.Singleton;
            var gateOn = SettingsSystem.ServerSettings.PerAgencyCareerEnabled;
            var localAgencyId = agencySystem?.LocalAgencyId ?? Guid.Empty;

            var vesselKnown = false;
            var vesselOwningAgencyId = Guid.Empty;
            if (agencySystem != null && agencySystem.TryGetOwningAgency(vesselId, out var owner))
            {
                vesselKnown = true;
                vesselOwningAgencyId = owner;
            }

            if (!AgencyMembership.IsRecoveryBlockedByAgency(
                localAgencyId, vesselKnown, vesselOwningAgencyId, gateOn))
            {
                return false;
            }

            // Resolve the owning agency's friendly identity from the
            // OtherAgencies snapshot when available — surfaces display name +
            // owner so the player knows whose vessel they're trying to take.
            // Fall back to a generic literal when the snapshot misses (late-
            // joining peer agency; AgencyInfo not yet broadcast).
            var ownerLabel = "a different agency";
            if (agencySystem != null
                && agencySystem.OtherAgencies != null
                && agencySystem.OtherAgencies.TryGetValue(vesselOwningAgencyId, out var info)
                && info != null)
            {
                var displayName = string.IsNullOrEmpty(info.DisplayName) ? "an unnamed agency" : info.DisplayName;
                var owningPlayer = string.IsNullOrEmpty(info.OwningPlayerName) ? "unknown owner" : info.OwningPlayerName;
                ownerLabel = $"{displayName} ({owningPlayer})";
            }

            // Toast — dropped the leading slash from "transferagency" so the
            // player doesn't read it as a command they should type (they
            // can't run server admin commands). Consumer-lens v1 C2.
            LunaScreenMsg.PostScreenMessage(
                $"Cannot {actionVerb}: this vessel belongs to {ownerLabel}. " +
                "Ask the owning agency to give it to you, or ask a server admin to transfer it.",
                5f, ScreenMessageStyle.UPPER_CENTER);

            // KSP.log breadcrumb with the full triage payload: action verb,
            // vessel id, local player handle (so an operator triaging "Alice
            // says her recover button doesn't work" can grep), local agency,
            // and the owning agency id. Tagged [fix:per-agency-career] to
            // match the server-side per-agency log convention so cross-side
            // grep ("everything per-agency-career touched") returns both
            // server-side and client-side breadcrumbs. Consumer-lens v1 S2 +
            // client-harmony v1 C3.
            var localPlayer = SettingsSystem.CurrentSettings?.PlayerName ?? "(unknown)";
            LunaLog.Log(
                $"[fix:per-agency-career] cross-agency-{actionVerb}-blocked " +
                $"vessel={vesselId:N} local-player={localPlayer} local-agency={localAgencyId:N} " +
                $"owning-agency={vesselOwningAgencyId:N}");

            return true;
        }

        private static void RemoveOldVesselAndItsDebris(Vessel vessel, ProtoCrewMember.RosterStatus kerbalStatus)
        {
            if (vessel == null) return;

            if (FlightGlobals.ActiveVessel.isEVA)
            {
                var kerbal = HighLogic.CurrentGame.CrewRoster[FlightGlobals.ActiveVessel.vesselName];
                if (kerbal != null)
                    kerbal.rosterStatus = kerbalStatus;

                System.KillVessel(FlightGlobals.ActiveVessel.id, true, "Revert. Active vessel is a kerbal");
                System.MessageSender.SendVesselRemove(FlightGlobals.ActiveVessel);
            }

            //We detected a revert, now pick all the vessel parts (debris) that came from our main active 
            //vessel and remove them both from our game and server
            var vesselsToRemove = FlightGlobals.Vessels
                .Where(v => v != null && v.rootPart && v.rootPart.missionID == vessel.rootPart.missionID && v.id != vessel.id).Distinct();

            foreach (var vesselToRemove in vesselsToRemove)
            {
                if (vesselToRemove.isEVA)
                {
                    var kerbal = HighLogic.CurrentGame.CrewRoster[vesselToRemove.vesselName];
                    if (kerbal != null)
                        kerbal.rosterStatus = kerbalStatus;
                }

                System.MessageSender.SendVesselRemove(vesselToRemove);

                //We consider this vessel removed but we let KSP do the remove of the vessel
                System.RemovedVessels.TryAdd(vesselToRemove.id, DateTime.Now);
                VesselCommon.RemoveVesselFromSystems(vesselToRemove.id);
            }
        }
    }
}
