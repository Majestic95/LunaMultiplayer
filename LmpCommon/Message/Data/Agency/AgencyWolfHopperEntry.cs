using Lidgren.Network;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — per-agency hopper record. Single class used both as the wire
    /// entry inside <see cref="AgencyWolfHopperStateMsgData.Entries"/> AND as the
    /// value type for the server-side <c>AgencyState.WolfHoppers</c> dictionary.
    /// Mirrors WOLF's <c>HopperMetadata</c> shape persisted via
    /// <c>HopperMetadata.OnSave</c> at MKS SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\HopperMetadata.cs:37-49</c>).
    ///
    /// <para><b>Partition key in <c>AgencyState.WolfHoppers</c>:</b>
    /// <see cref="Id"/> (Guid string in <c>ToString()</c> form WITH hyphens —
    /// matches WOLF's <c>Guid.NewGuid().ToString()</c> at
    /// <c>HopperMetadata.cs:18</c>). Distinct from
    /// <see cref="AgencyWolfTerminalEntry"/>'s "N" form — preserve the difference
    /// at the wire boundary; do NOT normalize.</para>
    ///
    /// <para><b>Recipe encoding:</b> WOLF serializes the
    /// <c>IRecipe.InputIngredients</c> dict as a flat comma-separated
    /// <c>"resource,qty,resource,qty,..."</c> string at
    /// <c>HopperMetadata.cs:44-48</c>. Phase 4 carries the same flat string;
    /// no nested structure on the wire. The output side of the recipe is
    /// always empty per the WOLF code (<c>new Dictionary&lt;string,int&gt;()</c>
    /// at <c>HopperMetadata.cs:34</c>); LMP does not represent it.</para>
    ///
    /// <para><b>Persisted form (in <c>{guid}.txt</c>):</b> one <c>WOLF_HOPPER</c>
    /// sub-node per entry under the parent <c>WOLF_HOPPERS</c> node. Fields
    /// verified at pre-spec §2.f.v.</para>
    /// </summary>
    public class AgencyWolfHopperEntry
    {
        /// <summary>WOLF's <c>HopperMetadata.Id</c> — Guid in <c>ToString()</c> form WITH hyphens.</summary>
        public string Id { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Biome { get; set; } = string.Empty;

        /// <summary>Flat <c>"resource,qty,resource,qty,..."</c> recipe ingredient list. Matches WOLF's persistence format directly.</summary>
        public string Recipe { get; set; } = string.Empty;

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(Id ?? string.Empty);
            lidgrenMsg.Write(Body ?? string.Empty);
            lidgrenMsg.Write(Biome ?? string.Empty);
            lidgrenMsg.Write(Recipe ?? string.Empty);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            Id = lidgrenMsg.ReadString();
            Body = lidgrenMsg.ReadString();
            Biome = lidgrenMsg.ReadString();
            Recipe = lidgrenMsg.ReadString();
        }

        public int GetByteCount()
        {
            var idLen = Id?.Length ?? 0;
            var bodyLen = Body?.Length ?? 0;
            var biomeLen = Biome?.Length ?? 0;
            var recipeLen = Recipe?.Length ?? 0;
            return 5 + idLen * 4
                + 5 + bodyLen * 4
                + 5 + biomeLen * 4
                + 5 + recipeLen * 4;
        }
    }
}
