using Lidgren.Network;
using LmpCommon.Message.Base;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// One vessel-ownership transition entry carried inside
    /// <see cref="AgencyVisibilityMsgData"/>. Stage 5.18d. Pair of
    /// <c>(VesselId, NewOwningAgencyId)</c>; <see cref="NewOwningAgencyId"/>
    /// = <see cref="Guid.Empty"/> is the Unassigned-sentinel demotion (spec
    /// §10 Q3) and is a legitimate authoritative value — see
    /// <c>AgencyMembership.ForceRecordOwnership</c>'s XML for the client-side
    /// apply rule.
    ///
    /// <para>Same struct-with-Serialize pattern as <see cref="AgencyInfo"/>;
    /// reuses <see cref="GuidUtil"/> for the on-wire bytes. Kept as a struct
    /// rather than two parallel <c>Guid[]</c> arrays so the wire shape is
    /// self-documenting at the call site.</para>
    /// </summary>
    public struct VesselOwnershipChange
    {
        public Guid VesselId;
        public Guid NewOwningAgencyId;

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            GuidUtil.Serialize(VesselId, lidgrenMsg);
            GuidUtil.Serialize(NewOwningAgencyId, lidgrenMsg);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            VesselId = GuidUtil.Deserialize(lidgrenMsg);
            NewOwningAgencyId = GuidUtil.Deserialize(lidgrenMsg);
        }

        public int GetByteCount() => GuidUtil.ByteSize * 2;
    }
}
