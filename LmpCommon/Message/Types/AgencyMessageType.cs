namespace LmpCommon.Message.Types
{
    /// <summary>
    /// Subtype enum for the per-agency career wire surface (Stage 5).
    /// Values are wire-protocol-relevant — adding or renumbering requires a protocol bump.
    /// Stage 5.15b ships only the registration set (Handshake / CreateRequest / CreateReply / State);
    /// mutation + visibility subtypes land alongside their consumers in Stage 5.17b / 5.17d / 5.18c
    /// and will be appended (never inserted) to preserve the wire ordering of existing peers.
    /// </summary>
    public enum AgencyMessageType
    {
        Handshake = 0,
        CreateRequest = 1,
        CreateReply = 2,
        State = 3,
        Contract = 4,
        Visibility = 5,
        // [Phase 3 Slice B] MKS kolonization per-agency state. Used both directions:
        // owner-only S→C echo + connect catch-up, AND C→S mutation emit from the
        // KolonizationManager.TrackLogEntry postfix. Server ignores wire-supplied
        // AgencyId on inbound; derives from authenticated sender.
        KolonyState = 6,
        // [Phase 3 Slice C] MKS planetary-logistics per-agency state. Same trust
        // posture + dual-direction usage as KolonyState; the partition is
        // body-and-resource keyed (NOT vessel-keyed). C→S emit fires from the
        // ModulePlanetaryLogistics.LevelResources postfix at warehouse-tick cadence.
        PlanetaryState = 7,
    }
}
