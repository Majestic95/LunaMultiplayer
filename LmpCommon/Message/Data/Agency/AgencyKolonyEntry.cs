using Lidgren.Network;
using LmpCommon.Message.Base;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 3 ShareKolony — per-agency kolonization record. Single class used both
    /// as the wire entry inside <see cref="AgencyKolonyStateMsgData.Entries"/> AND
    /// as the value type for the server-side <c>AgencyState.KolonyEntries</c>
    /// dictionary. The 13-field shape mirrors MKS'
    /// <c>KolonyTools.KolonizationEntry</c> at pinned SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\KolonyTools\Kolonization\KolonizationEntry.cs</c>);
    /// a future MKS field rename is the Phase 3 brittleness surface flagged in
    /// <c>docs/research/mks-lmp-compatibility-phase-3-prespec.md</c> §6 item 4.
    ///
    /// <para><b>Lives in LmpCommon, not Server.</b> The single-class-per-slot rule
    /// (pre-spec §2.e) requires the wire MsgData (LmpCommon) to reference the
    /// entry type directly, which forbids placing the entry in <c>Server.System.Agency</c>
    /// (Server depends on LmpCommon, not the other way). Slice A staged the file
    /// under <c>Server/System/Agency/</c> provisionally; Slice B moves it here as
    /// part of bringing the wire surface online. The <c>AgencyState.KolonyEntries</c>
    /// dictionary continues to use this type, just via a <c>using
    /// LmpCommon.Message.Data.Agency;</c> import in <c>Server/System/Agency/AgencyState.cs</c>.</para>
    ///
    /// <para><b>Partition key in <c>AgencyState.KolonyEntries</c>:</b>
    /// <c>$"{vesselId:N}|{bodyIndex}"</c>. The vessel-keyed partition lets admin
    /// <c>transferagency</c> migrate kolony research with the vessel (operator
    /// sign-off session 25 Q1). <see cref="VesselId"/> is stored as a string
    /// matching the MKS-side field type, but populated by the client postfix as
    /// <c>vessel.id.ToString("N")</c> (Guid form) so router-emit + persisted-file
    /// keys converge regardless of which path produced the dictionary entry.</para>
    ///
    /// <para><b>Persisted form (in <c>{guid}.txt</c>):</b> one <c>KOLONY</c>
    /// sub-node per entry under the parent <c>KOLONY_ENTRIES</c> node. Plain numeric
    /// values (no Base64 — the entry is not a wire-compressed payload like contracts).
    /// Persistence shape lives in <c>Server/System/Agency/AgencyState.cs</c> Slice
    /// A's <c>ToConfigNode</c>/<c>FromConfigNode</c> blocks; this class only owns
    /// the in-memory + wire-serialization shape.</para>
    ///
    /// <para><b>Wire-serialization contract.</b> <see cref="Serialize"/> writes
    /// VesselId (string) + BodyIndex (int) + 9 doubles + 3 ints in stable field
    /// order. <see cref="Deserialize"/> reads in the same order.
    /// <see cref="GetByteCount"/> upper-bounds the Lidgren write buffer; the
    /// per-string-byte calculation matches <see cref="NetOutgoingMessage.Write(string)"/>'s
    /// VarInt length-prefix + UTF-8 byte estimate (4 bytes per char is a safe
    /// upper bound — most VesselId payloads are 32-char Guid "N" form, so the
    /// real footprint is ~33 bytes per entry on average). No compression — the
    /// entry's numeric fields don't QuickLZ well at this size and the extra
    /// CPU cost on hot-path postfix sends is not worth it.</para>
    /// </summary>
    public class AgencyKolonyEntry
    {
        public string VesselId { get; set; } = string.Empty;
        public int BodyIndex { get; set; }
        public double LastUpdate { get; set; }
        public double KolonyDate { get; set; }
        public double GeologyResearch { get; set; }
        public double BotanyResearch { get; set; }
        public double KolonizationResearch { get; set; }
        public double Science { get; set; }
        public double Reputation { get; set; }
        public double Funds { get; set; }
        public int RepBoosters { get; set; }
        public int FundsBoosters { get; set; }
        public int ScienceBoosters { get; set; }

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(VesselId ?? string.Empty);
            lidgrenMsg.Write(BodyIndex);
            lidgrenMsg.Write(LastUpdate);
            lidgrenMsg.Write(KolonyDate);
            lidgrenMsg.Write(GeologyResearch);
            lidgrenMsg.Write(BotanyResearch);
            lidgrenMsg.Write(KolonizationResearch);
            lidgrenMsg.Write(Science);
            lidgrenMsg.Write(Reputation);
            lidgrenMsg.Write(Funds);
            lidgrenMsg.Write(RepBoosters);
            lidgrenMsg.Write(FundsBoosters);
            lidgrenMsg.Write(ScienceBoosters);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            VesselId = lidgrenMsg.ReadString();
            BodyIndex = lidgrenMsg.ReadInt32();
            LastUpdate = lidgrenMsg.ReadDouble();
            KolonyDate = lidgrenMsg.ReadDouble();
            GeologyResearch = lidgrenMsg.ReadDouble();
            BotanyResearch = lidgrenMsg.ReadDouble();
            KolonizationResearch = lidgrenMsg.ReadDouble();
            Science = lidgrenMsg.ReadDouble();
            Reputation = lidgrenMsg.ReadDouble();
            Funds = lidgrenMsg.ReadDouble();
            RepBoosters = lidgrenMsg.ReadInt32();
            FundsBoosters = lidgrenMsg.ReadInt32();
            ScienceBoosters = lidgrenMsg.ReadInt32();
        }

        public int GetByteCount()
        {
            // Upper bound on serialized bytes. NetOutgoingMessage.Write(string)
            // writes a VarInt32 length prefix (1-5 bytes for typical strings) +
            // the UTF-8 byte payload. 4-bytes-per-char is the safe upper bound
            // for any UTF-16 code point through UTF-8. VesselId is canonically
            // a 32-char Guid "N" form (~33 bytes wire) but operator hand-edits
            // or pre-Guid VesselId strings could be longer.
            var vesselIdLen = VesselId?.Length ?? 0;
            return 5 + vesselIdLen * 4    // VesselId (VarInt length-prefix + UTF-8 bytes)
                + sizeof(int)              // BodyIndex
                + sizeof(double) * 9       // 9 doubles
                + sizeof(int) * 3;         // 3 booster ints
        }
    }
}
