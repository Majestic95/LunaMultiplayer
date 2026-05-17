using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Systems.VesselRemoveSys;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using System.Collections.Concurrent;

namespace LmpClient.Systems.VesselPinnedSys
{
    public class VesselPinnedMessageHandler : SubSystem<VesselPinnedSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is VesselPinnedMsgData msgData)) return;

            //Don't pin a vessel that's about to be removed — keeps the immortal flip from
            //resurrecting a corpse if the leaver had a recover/terminate in flight.
            //Singleton null-guard matches the PqsAlignmentRoutine pattern: a pinned
            //broadcast that lands during system init/teardown is safe to drop.
            if (VesselRemoveSystem.Singleton?.VesselWillBeKilled(msgData.VesselId) == true) return;

            System.TryPin(msgData.VesselId, msgData.AbsentPlayerName, msgData.Reason);
        }
    }
}
