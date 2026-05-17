using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpCommon.Message.Interface;
using System;

namespace LmpClient.Systems.VesselPinnedSys
{
    public class VesselPinnedMessageSender : SubSystem<VesselPinnedSystem>, IMessageSender
    {
        //The client never originates a VesselPinned message — pinning is a server-side
        //decision driven by detected disconnects. Matches the PlayerConnection sender
        //pattern (server-broadcast-only message type).
        public void SendMessage(IMessageData msg)
        {
            throw new InvalidOperationException("VesselPinnedSystem is receive-only on the client.");
        }
    }
}
