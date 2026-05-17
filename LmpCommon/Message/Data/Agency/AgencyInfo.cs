using Lidgren.Network;
using LmpCommon.Message.Base;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Lightweight summary of one agency, used inside <see cref="AgencyHandshakeMsgData"/>
    /// to enumerate other players' agencies without sending each player's full state.
    /// Mirrors the <see cref="PlayerStatus.PlayerStatusInfo"/> pattern — owns its own
    /// Serialize / Deserialize / GetByteCount so the parent message can compose an
    /// array of these.
    ///
    /// Funds/Science/Reputation are NOT included here — they're private per the
    /// <c>PrivateAgencyResources = true</c> sign-off (spec §10 Q1). Only id / owner /
    /// display name leak across agencies, which is what the tracking-station label
    /// needs in Stage 5.18c.
    /// </summary>
    public class AgencyInfo
    {
        public Guid AgencyId;
        public string OwningPlayerName = string.Empty;
        public string DisplayName = string.Empty;

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(OwningPlayerName ?? string.Empty);
            lidgrenMsg.Write(DisplayName ?? string.Empty);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            AgencyId = GuidUtil.Deserialize(lidgrenMsg);
            OwningPlayerName = lidgrenMsg.ReadString();
            DisplayName = lidgrenMsg.ReadString();
        }

        public int GetByteCount()
        {
            // StringUtil.GetByteCount is the canonical size accountant: extension method,
            // null-safe (returns sizeof(int) for null/empty to cover Lidgren's length prefix),
            // so no `?.` guard is needed and a `?? 0` here would under-count by 4 bytes for
            // null entries. Match the convention used everywhere else in LmpCommon/Message/Data/.
            return GuidUtil.ByteSize + OwningPlayerName.GetByteCount() + DisplayName.GetByteCount();
        }
    }
}
