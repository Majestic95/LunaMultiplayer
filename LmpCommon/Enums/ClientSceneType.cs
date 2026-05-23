namespace LmpCommon.Enums
{
    /// <summary>
    /// Client's current top-level KSP scene, communicated to the server via the
    /// <see cref="Message.Data.PlayerStatus.PlayerStatusInfo.Scene"/> tail field
    /// on the existing PlayerStatusSet message.
    ///
    /// Drives <c>perf:relay-scene</c> (Phase 1 of the server-side-offload workstream
    /// — see <c>docs/research/11-server-side-offload-spec.md</c>): the server drops
    /// continuous vessel-state relays (Position / Flightstate / Update / Resource /
    /// PartSync* / ActionGroup / Fairing) to recipients whose current scene cannot
    /// render them. Catch-up / structural relays (Proto / Sync / Couple / Remove /
    /// Decouple / Undock) are NEVER filtered on scene — they populate FlightGlobals
    /// before the recipient enters Flight.
    ///
    /// Backward-compat: pre-Phase-1 clients do not write the Scene byte. Server's
    /// <c>InternalDeserialize</c> reads tail-or-default and treats
    /// <see cref="Unknown"/> as "relay always" (the pre-Phase-1 baseline behaviour).
    /// </summary>
    public enum ClientSceneType : byte
    {
        Unknown = 0,
        MainMenu = 1,
        SpaceCenter = 2,
        TrackingStation = 3,
        Editor = 4,
        Flight = 5,
        ResearchAndDevelopment = 6,
        Mission = 7,
        Other = 99
    }
}
