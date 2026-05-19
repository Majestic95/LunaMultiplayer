using Lidgren.Network;
using System.Collections.Generic;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — per-agency depot record. Single class used both as the wire
    /// entry inside <see cref="AgencyWolfDepotStateMsgData.Entries"/> AND as the
    /// value type for the server-side <c>AgencyState.WolfDepots</c> dictionary.
    /// Mirrors WOLF's <c>Depot</c> shape persisted via <c>Depot.OnSave</c> at MKS
    /// SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Depot.cs:245-264</c>).
    ///
    /// <para><b>Partition key in <c>AgencyState.WolfDepots</c>:</b>
    /// <c>$"{Body}|{Biome}"</c> (Ordinal compare). Two agencies can each have a
    /// depot at the same (Body, Biome) — they live in separate per-agency dicts;
    /// the projector splice emits only the requesting agency's depots into
    /// outgoing scenario blobs.</para>
    ///
    /// <para><b>Persisted form (in <c>{guid}.txt</c>):</b> one <c>WOLF_DEPOT</c>
    /// sub-node per entry under the parent <c>WOLF_DEPOTS</c> node, with
    /// <see cref="ResourceStreams"/> emitted as a nested <c>WOLF_RESOURCE_STREAMS</c>
    /// child containing one <c>WOLF_RESOURCE_STREAM</c> sub-node per entry.
    /// Fields verified at pre-spec §2.f.i.</para>
    /// </summary>
    public class AgencyWolfDepotEntry
    {
        public string Body { get; set; } = string.Empty;
        public string Biome { get; set; } = string.Empty;
        public bool IsEstablished { get; set; }
        public bool IsSurveyed { get; set; }
        public List<AgencyWolfResourceStreamEntry> ResourceStreams { get; set; } = new List<AgencyWolfResourceStreamEntry>();

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(Body ?? string.Empty);
            lidgrenMsg.Write(Biome ?? string.Empty);
            lidgrenMsg.Write(IsEstablished);
            lidgrenMsg.Write(IsSurveyed);

            var streams = ResourceStreams ?? new List<AgencyWolfResourceStreamEntry>();
            lidgrenMsg.Write(streams.Count);
            foreach (var s in streams)
            {
                (s ?? new AgencyWolfResourceStreamEntry()).Serialize(lidgrenMsg);
            }
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            Body = lidgrenMsg.ReadString();
            Biome = lidgrenMsg.ReadString();
            IsEstablished = lidgrenMsg.ReadBoolean();
            IsSurveyed = lidgrenMsg.ReadBoolean();

            var streamCount = lidgrenMsg.ReadInt32();
            if (streamCount < 0 || streamCount > MaxStreamsPerDepot)
                throw new System.IO.InvalidDataException(
                    $"AgencyWolfDepotEntry stream count out of range: {streamCount} (allowed 0..{MaxStreamsPerDepot})");

            ResourceStreams = new List<AgencyWolfResourceStreamEntry>(streamCount);
            for (var i = 0; i < streamCount; i++)
            {
                var entry = new AgencyWolfResourceStreamEntry();
                entry.Deserialize(lidgrenMsg);
                ResourceStreams.Add(entry);
            }
        }

        public int GetByteCount()
        {
            var bodyLen = Body?.Length ?? 0;
            var biomeLen = Biome?.Length ?? 0;
            var streamsSize = sizeof(int);    // count prefix
            if (ResourceStreams != null)
            {
                foreach (var s in ResourceStreams)
                {
                    streamsSize += (s ?? new AgencyWolfResourceStreamEntry()).GetByteCount();
                }
            }
            return 5 + bodyLen * 4    // Body
                + 5 + biomeLen * 4     // Biome
                + 2                    // 2 bools
                + streamsSize;         // nested list
        }

        /// <summary>
        /// DoS-amplification cap on the per-depot resource-stream list. WOLF has
        /// ~13-30 stock resource types; a megabase running CRP + mods could push
        /// this higher but 256 is generously above any realistic per-depot count.
        /// </summary>
        public const int MaxStreamsPerDepot = 256;
    }
}
