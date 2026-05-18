namespace Server.System.Agency
{
    /// <summary>
    /// Pure decision helper for the Stage 5.18d slice (i) force-full-sync-on-
    /// reconnect rule. Extracted as a standalone static so ServerTest can pin
    /// every branch without spinning up <c>VesselStoreSystem</c> / a full
    /// <c>HandleVesselsSync</c> harness.
    ///
    /// <para>The rule: under <c>PerAgencyCareer</c> gate ON, the FIRST
    /// <c>VesselSyncMsgData</c> from a client (per-connection) ignores the
    /// client's claimed-vessel diff and ships ALL canonical vessels. Subsequent
    /// syncs in the same connection fall through to the legacy incremental
    /// diff. Under gate OFF, every sync uses the legacy diff.</para>
    ///
    /// <para><b>Why.</b> On reconnect, KSP's <c>FlightGlobals.Vessels</c>
    /// retains vessels from the prior connection but the relay-path proto
    /// resends from THIS client's KSP have stripped the <c>lmpOwningAgency</c>
    /// top-level field (per the Stage 5.18b relay-vs-store contract — KSP's
    /// <c>BackupVessel.protoVessel.Save</c> drops unknown top-level fields on
    /// every local-owner resend). The client's
    /// <c>AgencySystem.VesselOwnership</c> registry was cleared on
    /// <c>OnDisabled</c>; the legacy incremental diff sees "the client already
    /// claims these vessels" and skips them — registry stays empty for those
    /// vessels until SOME other path (relay from a different vessel-owner, or
    /// a future Stage 5.18d AgencyVisibilityMsgData broadcast) ships the
    /// stamp. Force-full-sync routes every vessel through
    /// <c>VesselStoreSystem.GetVesselInConfigNodeFormat</c> on the server,
    /// which serialises the canonical lmpOwningAgency, so the client
    /// repopulates its registry correctly on receipt.</para>
    /// </summary>
    internal static class AgencyVesselSyncPolicy
    {
        /// <summary>
        /// Returns <c>true</c> when <see cref="Server.Message.VesselMsgReader.HandleVesselsSync"/>
        /// should bypass the client's claimed-vessel diff and ship ALL canonical
        /// vessels for this sync request. Caller is expected to flip
        /// <c>ClientStructure.HasReceivedInitialVesselsSync</c> to true after the
        /// sync handler completes so the next sync on the same connection takes
        /// the legacy diff path.
        ///
        /// <para><b>Bypass-only behaviour.</b> Both conditions must hold —
        /// <paramref name="perAgencyGateOn"/> AND
        /// <c>!hasReceivedInitialVesselsSync</c>. Under gate-off, the legacy
        /// diff is correct (no per-agency stamps to propagate) and the
        /// bandwidth saving on reconnect is worth keeping. After the first
        /// sync per connection, the registry is populated; subsequent syncs
        /// are routine and can take the diff.</para>
        /// </summary>
        public static bool ShouldFullSync(bool perAgencyGateOn, bool hasReceivedInitialVesselsSync)
        {
            return perAgencyGateOn && !hasReceivedInitialVesselsSync;
        }
    }
}
