using LmpClient.Base;
using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 3 Slice B] Client → server sender for per-agency kolony mutations.
    /// Emits <see cref="AgencyKolonyStateMsgData"/> on slot 6
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.KolonyState"/>) when the
    /// MKS-side <c>KolonyTools.KolonizationManager.TrackLogEntry</c> Harmony postfix
    /// fires under <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Threading pattern</b> (pre-spec §3.e — verified against
    /// <see cref="AgencyMessageSender.SendMessage"/>):
    /// <c>TaskFactory.StartNew(() =&gt; NetworkSender.QueueOutgoingMessage(
    /// MessageFactory.CreateNew&lt;AgencyCliMsg&gt;(msg)))</c>. The offload moves
    /// message-object construction off KSP's Unity main thread (postfix fires on
    /// FixedUpdate) so the Lidgren serialization + send-queue work doesn't eat
    /// FixedUpdate frame budget under heavy MKS load. Matches the established
    /// <c>*MessageSender.cs</c> two-line convention used uniformly across the
    /// codebase.</para>
    ///
    /// <para><b>Why a separate sender class?</b> Per pre-spec §2.e sender naming
    /// clarification (review-revision logic-pass finding #4), each Phase 3 router's
    /// client outbound gets its own sender alongside the existing
    /// <see cref="AgencyMessageSender"/> (Stage 5.18a). Keeping them
    /// one-class-per-mutation-surface makes per-router profiling + future per-
    /// batch coalescing (pre-spec §11 Q6 cadence soak) a localised change.</para>
    ///
    /// <para><b>Server trust posture.</b> The server's
    /// <c>AgencyKolonyRouter.TryRoute</c> ignores the wire-supplied
    /// <see cref="AgencyKolonyStateMsgData.AgencyId"/> on inbound and derives the
    /// sender's agency authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. We leave
    /// <see cref="AgencyKolonyStateMsgData.AgencyId"/> as <c>Guid.Empty</c> on
    /// C→S — explicit signal that the field is server-derived. Same posture as
    /// the Stage 5.17d <c>ShareProgress.ShareProgressContractsMsgData</c> path.</para>
    /// </summary>
    public static class AgencyKolonySender
    {
        /// <summary>
        /// Sends a batch of kolony entry mutations. Builds
        /// <see cref="AgencyKolonyStateMsgData"/>, offloads to the network thread.
        /// </summary>
        /// <param name="entries">Non-null; may be empty (caller is expected to
        /// pre-filter empty cases — empty batches still allocate a wire message,
        /// so the postfix should skip the send when the entry list is empty).</param>
        public static void SendBatch(IReadOnlyList<AgencyKolonyEntry> entries)
        {
            if (entries == null) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyKolonyStateMsgData>();
            // AgencyId left at Guid.Empty intentionally — server ignores it and
            // derives the sender's agency authoritatively (see class XML).
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyKolonyEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            // SystemBase exposes the shared TaskFactory used by every existing
            // *MessageSender; the static class form here references it explicitly
            // since we don't inherit from SystemBase (matches the offloading
            // contract documented in AgencyMessageSender.SendMessage at line 32).
            Base.SystemBase.TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Convenience single-entry overload — the most common postfix call shape.
        /// </summary>
        public static void SendMutation(AgencyKolonyEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }
    }
}
