using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 4 Slice B] Client → server sender for per-agency WOLF depot
    /// mutations. Emits <see cref="AgencyWolfDepotStateMsgData"/> on slot 9
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.WolfDepotState"/>)
    /// when WOLF's <c>ScenarioPersister.CreateDepot</c> / <c>Depot.Establish</c>
    /// / <c>Depot.Survey</c> Harmony postfixes fire under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Threading pattern</b> (pre-spec §3.e): the established
    /// <c>TaskFactory.StartNew</c> offload moves message-object construction
    /// off the Unity main thread so the Lidgren serialization + send-queue
    /// work doesn't eat <c>FixedUpdate</c> frame budget. Mirrors the
    /// established <see cref="AgencyKolonySender"/> two-line convention.</para>
    ///
    /// <para><b>Server trust posture.</b> The server's
    /// <c>AgencyWolfDepotRouter.TryRoute</c> ignores the wire-supplied
    /// <see cref="AgencyWolfDepotStateMsgData.AgencyId"/> on inbound and
    /// derives the sender's agency authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. We leave
    /// <see cref="AgencyWolfDepotStateMsgData.AgencyId"/> as
    /// <see cref="System.Guid.Empty"/> on C→S — explicit signal that the
    /// field is server-derived. Same posture as the Phase 3 Slice B
    /// <see cref="AgencyKolonySender"/>.</para>
    ///
    /// <para><b>Negotiate postfix deferred to Slice B-2.</b> The
    /// <c>Depot.NegotiateProvider</c> / <c>NegotiateConsumer</c> postfixes
    /// (pre-spec §3.e hotspot) need per-depot debouncing (collect on tick,
    /// batch-emit on 1s timer); they're scheduled for Slice B-2 alongside
    /// the debounce layer. Until then, resource-stream sync lags behind
    /// WOLF UI by the 30s SHA cadence — operator-visible during heavy
    /// production but functionally correct on the per-agency router's read
    /// side (ResourceStreams round-trip through AgencyState persistence
    /// + projector emit per the Slice A shape).</para>
    /// </summary>
    public static class AgencyWolfDepotSender
    {
        /// <summary>
        /// Sends a batch of depot entry mutations. Builds
        /// <see cref="AgencyWolfDepotStateMsgData"/>, offloads to the network
        /// thread.
        /// </summary>
        /// <param name="entries">Non-null; may be empty. Implementation
        /// no-ops on empty batches as a defensive belt-and-braces — caller
        /// should still pre-filter when possible to avoid the no-op
        /// allocation hit.</param>
        public static void SendBatch(IReadOnlyList<AgencyWolfDepotEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyWolfDepotStateMsgData>();
            // AgencyId left at Guid.Empty intentionally — server ignores it
            // and derives the sender's agency authoritatively (class XML).
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyWolfDepotEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            Base.SystemBase.TaskFactory.StartNew(() =>
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Convenience single-entry overload — the most common postfix call
        /// shape.
        /// </summary>
        public static void SendMutation(AgencyWolfDepotEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }
    }
}
