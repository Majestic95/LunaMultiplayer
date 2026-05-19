using Lidgren.Network;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — nested resource-stream entry inside
    /// <see cref="AgencyWolfDepotEntry.ResourceStreams"/>. Mirrors WOLF's
    /// <c>IResourceStream</c> shape persisted via
    /// <c>Depot.OnSave</c> at MKS SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Depot.cs:253-263</c>).
    ///
    /// <para>Fields verified at pre-spec §2.f.ii. Three flat values; no nested
    /// structure. The persistence node name is <c>WOLF_RESOURCE_STREAM</c>
    /// under the parent <c>WOLF_RESOURCE_STREAMS</c> child of the depot
    /// (see <see cref="AgencyWolfDepotEntry"/> persisted form). No
    /// culture-sensitive numerics — Incoming/Outgoing are <c>int</c>.</para>
    /// </summary>
    public class AgencyWolfResourceStreamEntry
    {
        public string ResourceName { get; set; } = string.Empty;
        public int Incoming { get; set; }
        public int Outgoing { get; set; }

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(ResourceName ?? string.Empty);
            lidgrenMsg.Write(Incoming);
            lidgrenMsg.Write(Outgoing);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            ResourceName = lidgrenMsg.ReadString();
            Incoming = lidgrenMsg.ReadInt32();
            Outgoing = lidgrenMsg.ReadInt32();
        }

        public int GetByteCount()
        {
            var nameLen = ResourceName?.Length ?? 0;
            return 5 + nameLen * 4    // VarInt length prefix + UTF-8 upper bound
                + sizeof(int) * 2;     // Incoming + Outgoing
        }
    }
}
