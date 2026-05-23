using Lidgren.Network;
using LmpCommon.Message.Base;

namespace LmpCommon.Message.Data.PlayerStatus
{
    public class PlayerStatusInfo
    {
        public string PlayerName;
        public string VesselText;
        public string StatusText;

        //IMPORTANT — DO NOT add new fields directly to this struct.
        //
        //PlayerStatusInfo is embedded back-to-back inside PlayerStatusReplyMsgData's
        //array (see PlayerStatusReplyMsgData.InternalSerialize) with NO per-element
        //framing — only a leading PlayerStatusCount. The tail-bit-read pattern
        //(positionInBits < lengthBits) does NOT work here because every element's
        //"end of payload" is followed by the NEXT element's start, never by end-of-
        //message. Phase 1 of server-side-offload originally added a Scene field
        //here; the upgrade-lens review caught that this would corrupt every
        //subsequent element in the Reply array on cross-version peers. Scene was
        //moved to PlayerStatusSetMsgData (where it sits at message-terminal
        //position and the tail-bit-read is safe).
        //
        //If you need to add a per-player field that should travel on the Reply
        //path, EITHER bump the protocol version cleanly, OR add per-element
        //framing here (length-prefixed elements), OR add the new field to
        //PlayerStatusSetMsgData and accept that peers won't catch it up on join
        //(they'll see it on the next per-player Set broadcast).

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(PlayerName);
            lidgrenMsg.Write(VesselText);
            lidgrenMsg.Write(StatusText);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            PlayerName = lidgrenMsg.ReadString();
            VesselText = lidgrenMsg.ReadString();
            StatusText = lidgrenMsg.ReadString();
        }

        public int GetByteCount()
        {
            return PlayerName.GetByteCount() + VesselText.GetByteCount() + StatusText.GetByteCount();
        }
    }
}
