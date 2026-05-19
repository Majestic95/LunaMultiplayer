using Lidgren.Network;
using LmpCommon.Message.Base;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — per-agency planetary-logistics warehouse record. Single
    /// class used both as the wire entry inside <see cref="AgencyPlanetaryStateMsgData.Entries"/>
    /// AND as the value type for the server-side <c>AgencyState.PlanetaryEntries</c>
    /// dictionary. The 4-field shape mirrors MKS'
    /// <c>PlanetaryLogistics.PlanetaryLogisticsEntry</c> at pinned SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\KolonyTools\PlanetaryLogistics\PlanetaryLogisticsEntry.cs</c>)
    /// PLUS one fork-only addition: <see cref="OwningVesselId"/>. MKS' entry is
    /// body-resource-keyed only — no vessel-id field — but Phase 3 needs vessel-origin
    /// context to derive the owning agency, so the client postfix on
    /// <c>KolonyTools.PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources</c>
    /// populates this from <c>this.vessel.id</c> at the per-mutation call site.
    ///
    /// <para><b>Lives in LmpCommon, not Server.</b> The single-class-per-slot rule
    /// (pre-spec §2.e) requires the wire MsgData (LmpCommon) to reference the
    /// entry type directly, which forbids placing the entry in <c>Server.System.Agency</c>
    /// (Server depends on LmpCommon, not the other way). Slice A staged the file
    /// under <c>Server/System/Agency/</c> provisionally; Slice C moves it here as
    /// part of bringing the wire surface online — same MOVE pattern Slice B
    /// applied to <see cref="AgencyKolonyEntry"/>. The
    /// <c>AgencyState.PlanetaryEntries</c> dictionary continues to use this type,
    /// just via the existing <c>using LmpCommon.Message.Data.Agency;</c> import
    /// in <c>Server/System/Agency/AgencyState.cs</c>.</para>
    ///
    /// <para><b>Partition key in <c>AgencyState.PlanetaryEntries</c>:</b>
    /// <c>$"{bodyIndex}|{resourceName}"</c>. Body-resource-keyed, NOT vessel-keyed
    /// — multiple of an agency's vessels pumping the same resource on the same
    /// body collapse into one entry. Distinct from Slice B's
    /// <see cref="AgencyKolonyEntry"/> partition (vessel-and-body-keyed). Per
    /// pre-spec §4.e: planetary entries do NOT migrate on <c>transferagency</c>
    /// (the entry represents a body's logistics pool, not a vessel's
    /// contribution). Slice E's <c>setvesselagency</c> only stamps the vessel;
    /// existing planetary balances stay where they are.</para>
    ///
    /// <para><b>Persisted form (in <c>{guid}.txt</c>):</b> one <c>PLANETARY</c>
    /// sub-node per entry under the parent <c>PLANETARY_ENTRIES</c> node, shipped
    /// in Slice A. Plain numeric values (no Base64 — the entry is not a
    /// wire-compressed payload like contracts). Persistence shape lives in
    /// <c>Server/System/Agency/AgencyState.cs</c> Slice A's <c>ToConfigNode</c> /
    /// <c>FromConfigNode</c> blocks at lines 449-467 / 827-866; this class only
    /// owns the in-memory + wire-serialization shape. The
    /// <see cref="OwningVesselId"/> Guid is persisted as <c>"N"</c> form (32 hex
    /// chars, no hyphens) for round-trip consistency with the agency-file naming
    /// convention.</para>
    ///
    /// <para><b>Wire-serialization contract.</b> <see cref="Serialize"/> writes
    /// OwningVesselId (16 bytes via <see cref="GuidUtil"/>) + BodyIndex (int) +
    /// ResourceName (string) + StoredQuantity (double) in stable field order.
    /// <see cref="Deserialize"/> reads in the same order.
    /// <see cref="GetByteCount"/> upper-bounds the Lidgren write buffer; the
    /// per-string-byte calculation matches <see cref="NetOutgoingMessage.Write(string)"/>'s
    /// VarInt length-prefix + UTF-8 byte estimate (4 bytes per char is a safe
    /// upper bound for any UTF-16 code point through UTF-8). No compression
    /// — the entry's three small fields don't QuickLZ well at this size, and
    /// the per-message CPU cost on the postfix hot path is not worth it. Same
    /// non-compression choice as <see cref="AgencyKolonyEntry"/>.</para>
    /// </summary>
    public class AgencyPlanetaryEntry
    {
        public Guid OwningVesselId { get; set; }
        public int BodyIndex { get; set; }
        public string ResourceName { get; set; } = string.Empty;
        public double StoredQuantity { get; set; }

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            GuidUtil.Serialize(OwningVesselId, lidgrenMsg);
            lidgrenMsg.Write(BodyIndex);
            lidgrenMsg.Write(ResourceName ?? string.Empty);
            lidgrenMsg.Write(StoredQuantity);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            OwningVesselId = GuidUtil.Deserialize(lidgrenMsg);
            BodyIndex = lidgrenMsg.ReadInt32();
            ResourceName = lidgrenMsg.ReadString();
            StoredQuantity = lidgrenMsg.ReadDouble();
        }

        public int GetByteCount()
        {
            // Upper bound on serialized bytes. NetOutgoingMessage.Write(string)
            // writes a VarInt32 length prefix (1-5 bytes for typical strings) +
            // the UTF-8 byte payload. 4-bytes-per-char is the safe upper bound
            // for any UTF-16 code point through UTF-8. ResourceName is typically
            // a short identifier ("Hydrates", "Karbonite", "MetallicOre" — under
            // 32 chars), but operator hand-edits or mod-defined resources could
            // be longer.
            var resourceNameLen = ResourceName?.Length ?? 0;
            return GuidUtil.ByteSize          // OwningVesselId
                + sizeof(int)                  // BodyIndex
                + 5 + resourceNameLen * 4      // ResourceName (VarInt length-prefix + UTF-8 bytes)
                + sizeof(double);              // StoredQuantity
        }
    }
}
