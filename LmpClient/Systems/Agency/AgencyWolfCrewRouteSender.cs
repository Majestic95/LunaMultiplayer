using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 4 Slice E] Client → server sender for per-agency WOLF crew-
    /// route mutations. Emits <see cref="AgencyWolfCrewRouteStateMsgData"/>
    /// on slot 13
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.WolfCrewRouteState"/>)
    /// when WOLF's <c>ScenarioPersister.CreateCrewRoute</c> /
    /// <c>CrewRoute.Embark</c> / <c>CrewRoute.Disembark</c> /
    /// <c>CrewRoute.Launch</c> Harmony postfixes fire under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Threading pattern</b> (pre-spec §3.e): the established
    /// <c>TaskFactory.StartNew</c> offload moves message-object
    /// construction off the Unity main thread so the Lidgren serialization
    /// + send-queue work doesn't eat <c>FixedUpdate</c> frame budget.
    /// Mirrors the established <see cref="AgencyWolfHopperSender"/>
    /// convention.</para>
    ///
    /// <para><b>Server trust posture.</b> The server's
    /// <c>AgencyWolfCrewRouter.TryRoute</c> ignores the wire-supplied
    /// <see cref="AgencyWolfCrewRouteStateMsgData.AgencyId"/> on inbound
    /// and derives the sender's agency authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. We leave
    /// <see cref="AgencyWolfCrewRouteStateMsgData.AgencyId"/> as
    /// <see cref="System.Guid.Empty"/> on C→S — explicit signal that the
    /// field is server-derived. Same posture as the Slice B-D
    /// sibling senders.</para>
    ///
    /// <para><b>No debouncer.</b> CrewRoute mutations are UI-driven (the
    /// operator clicks Create / Embark / Launch / Disembark — discrete
    /// events at human cadence, not the 50 Hz Negotiate hot path that
    /// motivated Slice B-3's <see cref="WolfDepotDebouncer"/>).</para>
    ///
    /// <para><b>No SendRemoval method.</b> WOLF has no
    /// <c>ScenarioPersister.RemoveCrewRoute</c> API (verified s41 source
    /// walk against MKS SHA <c>ed0f6aa6</c> at
    /// <c>ScenarioPersister.cs:432-449</c> — only <c>RemoveHopper</c> +
    /// <c>RemoveTerminal</c> exist). The wire surface's
    /// <see cref="AgencyWolfCrewRouteStateMsgData.RemovedKeys"/> tail is
    /// reserved for Slice F admin / migration paths (deleteagency cascade,
    /// transferagency MKS-aware companion). Same posture as Slice C
    /// Routes.</para>
    /// </summary>
    public static class AgencyWolfCrewRouteSender
    {
        /// <summary>
        /// Sends a batch of crew-route entry mutations. Builds
        /// <see cref="AgencyWolfCrewRouteStateMsgData"/>, offloads to the
        /// network thread.
        /// </summary>
        /// <param name="entries">Non-null; may be empty. Implementation
        /// no-ops on empty batches as a defensive belt-and-braces.</param>
        public static void SendBatch(IReadOnlyList<AgencyWolfCrewRouteEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyWolfCrewRouteStateMsgData>();
            // AgencyId left at Guid.Empty intentionally — server ignores it
            // and derives the sender's agency authoritatively (class XML).
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyWolfCrewRouteEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            Base.SystemBase.TaskFactory.StartNew(() =>
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Convenience single-entry overload — the most common postfix
        /// call shape across all four Slice E hook points.
        /// </summary>
        public static void SendMutation(AgencyWolfCrewRouteEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }
    }
}
