using Lidgren.Network;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — nested route-resource entry inside
    /// <see cref="AgencyWolfRouteEntry.Resources"/>. Mirrors WOLF's
    /// <c>Route._resources</c> dict (string → int) emitted as paired
    /// <c>ResourceName</c>/<c>Quantity</c> nodes by
    /// <c>Route.OnSave</c> at MKS SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Route.cs:197-204</c>).
    ///
    /// <para>Fields verified at pre-spec §2.f.iv. Two flat values; persistence
    /// node name is <c>WOLF_ROUTE_RESOURCE</c> under the parent
    /// <c>WOLF_ROUTE_RESOURCES</c> child of the route.</para>
    /// </summary>
    public class AgencyWolfRouteResourceEntry
    {
        public string ResourceName { get; set; } = string.Empty;
        public int Quantity { get; set; }

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(ResourceName ?? string.Empty);
            lidgrenMsg.Write(Quantity);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            ResourceName = lidgrenMsg.ReadString();
            Quantity = lidgrenMsg.ReadInt32();
        }

        public int GetByteCount()
        {
            var nameLen = ResourceName?.Length ?? 0;
            return 5 + nameLen * 4    // VarInt length prefix + UTF-8 upper bound
                + sizeof(int);         // Quantity
        }
    }
}
