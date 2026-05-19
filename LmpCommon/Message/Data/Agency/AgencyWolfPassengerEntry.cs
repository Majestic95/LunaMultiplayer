using Lidgren.Network;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Phase 4 WOLF — nested passenger entry inside
    /// <see cref="AgencyWolfCrewRouteEntry.Passengers"/>. Mirrors WOLF's
    /// <c>Passenger</c> shape persisted via <c>Passenger.OnSave</c> at MKS
    /// SHA <c>ed0f6aa6</c>
    /// (<c>F:\tmp\mks-external\MKS\Source\WOLF\WOLF\Passenger.cs:68-76</c>).
    ///
    /// <para>Fields verified at pre-spec §2.f.viii. Five flat values; persistence
    /// node name is <c>WOLF_PASSENGER</c> under the parent <c>WOLF_PASSENGERS</c>
    /// child of the crew route. <see cref="Name"/> is the
    /// <c>ProtoCrewMember.name</c> identifier used for
    /// <c>KerbalAgencyResolver</c> lookups at the §2.b.v cross-agency reject
    /// gate (Slice E).</para>
    /// </summary>
    public class AgencyWolfPassengerEntry
    {
        /// <summary><c>ProtoCrewMember.name</c> — the canonical kerbal identifier. Used by <c>KerbalAgencyResolver</c> for vessel-proxy authority lookups.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary><c>ProtoCrewMember.displayName</c> — UI-friendly display string. Informational; not used for routing.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary><c>ProtoCrewMember.type == KerbalType.Tourist</c>. WOLF berth allocation distinguishes Economy vs Luxury based on this flag.</summary>
        public bool IsTourist { get; set; }

        /// <summary><c>ProtoCrewMember.experienceTrait.Title</c> — "Pilot" / "Engineer" / "Scientist" etc.</summary>
        public string Occupation { get; set; } = string.Empty;

        /// <summary><c>ProtoCrewMember.experienceLevel</c> — KSP star count (0-5).</summary>
        public int Stars { get; set; }

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(Name ?? string.Empty);
            lidgrenMsg.Write(DisplayName ?? string.Empty);
            lidgrenMsg.Write(IsTourist);
            lidgrenMsg.Write(Occupation ?? string.Empty);
            lidgrenMsg.Write(Stars);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            Name = lidgrenMsg.ReadString();
            DisplayName = lidgrenMsg.ReadString();
            IsTourist = lidgrenMsg.ReadBoolean();
            Occupation = lidgrenMsg.ReadString();
            Stars = lidgrenMsg.ReadInt32();
        }

        public int GetByteCount()
        {
            var nameLen = Name?.Length ?? 0;
            var displayLen = DisplayName?.Length ?? 0;
            var occLen = Occupation?.Length ?? 0;
            return 5 + nameLen * 4    // Name
                + 5 + displayLen * 4   // DisplayName
                + 1                    // IsTourist (bool serialized as 1 byte)
                + 5 + occLen * 4       // Occupation
                + sizeof(int);         // Stars
        }
    }
}
