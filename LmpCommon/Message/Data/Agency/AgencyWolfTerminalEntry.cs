using Lidgren.Network;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — per-agency terminal record. Single class used both as the
    /// wire entry inside <see cref="AgencyWolfTerminalStateMsgData.Entries"/> AND
    /// as the value type for the server-side <c>AgencyState.WolfTerminals</c>
    /// dictionary. Mirrors WOLF's <c>TerminalMetadata</c> shape persisted via
    /// <c>TerminalMetadata.OnSave</c> at MKS SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\TerminalMetadata.cs:31-37</c>).
    ///
    /// <para><b>Partition key in <c>AgencyState.WolfTerminals</c>:</b>
    /// <see cref="Id"/> (Guid string in <c>ToString("N")</c> form — 32 hex chars,
    /// NO hyphens, matches WOLF's <c>Guid.NewGuid().ToString("N")</c> at
    /// <c>TerminalMetadata.cs:15</c>). Distinct from
    /// <see cref="AgencyWolfHopperEntry"/>'s with-hyphens form — preserve the
    /// difference at the wire boundary; do NOT normalize.</para>
    ///
    /// <para><b>Persisted form (in <c>{guid}.txt</c>):</b> one <c>WOLF_TERMINAL</c>
    /// sub-node per entry under the parent <c>WOLF_TERMINALS</c> node. Fields
    /// verified at pre-spec §2.f.vi.</para>
    /// </summary>
    public class AgencyWolfTerminalEntry
    {
        /// <summary>WOLF's <c>TerminalMetadata.Id</c> — Guid in <c>ToString("N")</c> form (no hyphens).</summary>
        public string Id { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Biome { get; set; } = string.Empty;

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(Id ?? string.Empty);
            lidgrenMsg.Write(Body ?? string.Empty);
            lidgrenMsg.Write(Biome ?? string.Empty);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            Id = lidgrenMsg.ReadString();
            Body = lidgrenMsg.ReadString();
            Biome = lidgrenMsg.ReadString();
        }

        public int GetByteCount()
        {
            var idLen = Id?.Length ?? 0;
            var bodyLen = Body?.Length ?? 0;
            var biomeLen = Biome?.Length ?? 0;
            return 5 + idLen * 4
                + 5 + bodyLen * 4
                + 5 + biomeLen * 4;
        }
    }
}
