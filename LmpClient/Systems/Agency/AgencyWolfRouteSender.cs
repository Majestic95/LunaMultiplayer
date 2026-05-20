using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 4 Slice C] Client → server sender for per-agency WOLF cargo-
    /// route mutations. Emits <see cref="AgencyWolfRouteStateMsgData"/> on
    /// slot 10
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.WolfRouteState"/>)
    /// when WOLF's <c>ScenarioPersister.CreateRoute</c> / <c>Route.AddResource</c>
    /// / <c>Route.RemoveResource</c> Harmony postfixes fire under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Threading pattern</b> (pre-spec §3.e): the established
    /// <c>TaskFactory.StartNew</c> offload moves message-object construction
    /// off the Unity main thread so the Lidgren serialization + send-queue
    /// work doesn't eat <c>FixedUpdate</c> frame budget. Mirrors the
    /// established <see cref="AgencyWolfDepotSender"/> two-line convention.</para>
    ///
    /// <para><b>Server trust posture.</b> The server's
    /// <c>AgencyWolfRouteRouter.TryRoute</c> ignores the wire-supplied
    /// <see cref="AgencyWolfRouteStateMsgData.AgencyId"/> on inbound and
    /// derives the sender's agency authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. We leave
    /// <see cref="AgencyWolfRouteStateMsgData.AgencyId"/> as
    /// <see cref="System.Guid.Empty"/> on C→S — explicit signal that the
    /// field is server-derived. Same posture as the Slice B
    /// <see cref="AgencyWolfDepotSender"/>.</para>
    ///
    /// <para><b>No debouncer (vs Slice B-3's Negotiate path).</b> Routes
    /// are operator-created: the postfixes hook
    /// <c>ScenarioPersister.CreateRoute</c> (UI-driven, low frequency) and
    /// <c>Route.AddResource</c> / <c>Route.RemoveResource</c> (per WOLF UI
    /// resource-allocation clicks). The Slice B-3 Negotiate hot path at
    /// ~50 Hz needed <see cref="WolfDepotDebouncer"/>; route mutations do
    /// not.</para>
    /// </summary>
    public static class AgencyWolfRouteSender
    {
        /// <summary>
        /// Sends a batch of route entry mutations. Builds
        /// <see cref="AgencyWolfRouteStateMsgData"/>, offloads to the network
        /// thread.
        /// </summary>
        /// <param name="entries">Non-null; may be empty. Implementation
        /// no-ops on empty batches as a defensive belt-and-braces.</param>
        public static void SendBatch(IReadOnlyList<AgencyWolfRouteEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyWolfRouteStateMsgData>();
            // AgencyId left at Guid.Empty intentionally — server ignores it
            // and derives the sender's agency authoritatively (class XML).
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyWolfRouteEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            Base.SystemBase.TaskFactory.StartNew(() =>
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Convenience single-entry overload — the most common postfix call
        /// shape.
        /// </summary>
        public static void SendMutation(AgencyWolfRouteEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }
    }
}
