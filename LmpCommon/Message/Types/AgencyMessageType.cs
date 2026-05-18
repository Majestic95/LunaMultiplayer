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
    }
}
