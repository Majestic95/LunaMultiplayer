using LmpClient.Base;
using LmpClient.Systems.Lock;
using LmpClient.Systems.VesselProtoSys;
using LmpClient.Systems.VesselRemoveSys;
using LmpClient.VesselUtilities;

namespace LmpClient.Systems.VesselEvaEditorSys
{
    public class VesselEvaEditorEvents : SubSystem<VesselEvaEditorSystem>
    {
        public void EVAConstructionModePartAttached(Vessel vessel, Part part)
        {
            if (VesselCommon.IsSpectating) return;
            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, reason: "EVA construction: part attached");
        }

        public void EVAConstructionModePartDetached(Vessel vessel, Part part)
        {
            if (VesselCommon.IsSpectating) return;
            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, reason: "EVA construction: part detached");
        }

        public void VesselCreated(Vessel vessel)
        {
            if (vessel == null || VesselCommon.IsSpectating) return;

            // Two creation paths route through GameEvents.onNewVesselCreated and both need to be
            // sent to the server:
            //   1. EVA Construction Mode part drops — gated by System.DetachingPart, which is set
            //      by EVAConstructionEvent.onDroppingPart/onDroppedPart.
            //   2. Breaking Ground deployable science placements (BUG-045) — the kerbal places a
            //      science part from inventory and KSP spins up a new vessel of type
            //      DeployedSciencePart / DeployedScienceController. This path raises NO
            //      EVAConstructionEvent, so without this branch the vessel was created locally,
            //      had its locks acquired by VesselLockSystem's bulk pass, but its proto was
            //      never transmitted — leaving an orphan UnloadedUpdate lock on the server with
            //      no matching vessel record. Ported from upstream Release/0_29_2 commit 2526e15a.
            var isEvaConstructionDrop = System.DetachingPart;
            var isDeployableScience = vessel.vesselType == VesselType.DeployedSciencePart
                                   || vessel.vesselType == VesselType.DeployedScienceController;

            if (!isEvaConstructionDrop && !isDeployableScience) return;

            LockSystem.Singleton.AcquireUpdateLock(vessel.id, true, true);
            LockSystem.Singleton.AcquireUnloadedUpdateLock(vessel.id, true, true);

            var reason = isEvaConstructionDrop
                ? "EVA construction: new vessel from detached part"
                : "Breaking Ground: deployable science placed";
            LunaLog.Log($"[fix:BUG-045] Sending new vessel {vessel.id} ({reason})");

            VesselProtoSystem.Singleton.MessageSender.SendVesselMessage(vessel, reason: reason);
        }

        public void OnDroppingPart()
        {
            System.DetachingPart = true;
        }

        public void OnDroppedPart()
        {
            System.DetachingPart = false;
        }

        public void OnAttachingPart(Part part)
        {
            if (part.vessel)
                VesselRemoveSystem.Singleton.MessageSender.SendVesselRemove(part.vessel);
        }
    }
}
