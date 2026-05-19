using LmpClient.Base;
using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 3 Slice C] Client → server sender for per-agency planetary-logistics
    /// mutations. Emits <see cref="AgencyPlanetaryStateMsgData"/> on slot 7
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.PlanetaryState"/>)
    /// when the MKS-side
    /// <c>KolonyTools.PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources</c>
    /// Harmony postfix fires under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Threading pattern</b> (pre-spec §3.e — verified against
    /// <see cref="AgencyMessageSender.SendMessage"/> + the Slice B
    /// <see cref="AgencyKolonySender"/>):
    /// <c>TaskFactory.StartNew(() =&gt; NetworkSender.QueueOutgoingMessage(
    /// MessageFactory.CreateNew&lt;AgencyCliMsg&gt;(msg)))</c>. The offload
    /// moves message-object construction off KSP's Unity main thread (postfix
    /// fires on FixedUpdate via <c>ModulePlanetaryLogistics.FixedUpdate</c> →
    /// <c>LevelResources</c>) so Lidgren serialization + send-queue work
    /// doesn't eat FixedUpdate frame budget. Matches the established
    /// <c>*MessageSender.cs</c> two-line convention.</para>
    ///
    /// <para><b>Why a separate sender class?</b> Per pre-spec §2.e sender
    /// naming clarification, each Phase 3 router's client outbound gets its
    /// own sender alongside <see cref="AgencyMessageSender"/> (Stage 5.18a) +
    /// <see cref="AgencyKolonySender"/> (Slice B). Keeping them
    /// one-class-per-mutation-surface makes per-router profiling + future
    /// per-batch coalescing (pre-spec §11 Q6 cadence soak) a localised
    /// change.</para>
    ///
    /// <para><b>Server trust posture.</b> The server's
    /// <c>AgencyPlanetaryRouter.TryRoute</c> ignores the wire-supplied
    /// <see cref="AgencyPlanetaryStateMsgData.AgencyId"/> on inbound and
    /// derives the sender's agency authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. We leave
    /// <see cref="AgencyPlanetaryStateMsgData.AgencyId"/> as <c>Guid.Empty</c>
    /// on C→S — explicit signal that the field is server-derived. Same posture
    /// as the Slice B <see cref="AgencyKolonySender"/>.</para>
    /// </summary>
    public static class AgencyPlanetarySender
    {
        /// <summary>
        /// Sends a batch of planetary entry mutations. Builds
        /// <see cref="AgencyPlanetaryStateMsgData"/>, offloads to the network
        /// thread.
        /// </summary>
        /// <param name="entries">Non-null; may be empty (caller is expected to
        /// pre-filter empty cases — empty batches still allocate a wire
        /// message, so the postfix should skip the send when the entry list
        /// is empty).</param>
        public static void SendBatch(IReadOnlyList<AgencyPlanetaryEntry> entries)
        {
            if (entries == null) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyPlanetaryStateMsgData>();
            // AgencyId left at Guid.Empty intentionally — server ignores it and
            // derives the sender's agency authoritatively (see class XML).
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyPlanetaryEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            // SystemBase exposes the shared TaskFactory used by every existing
            // *MessageSender; the static class form here references it
            // explicitly since we don't inherit from SystemBase (matches the
            // offloading contract documented in
            // AgencyMessageSender.SendMessage).
            Base.SystemBase.TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Convenience single-entry overload — the most common postfix call shape.
        /// </summary>
        public static void SendMutation(AgencyPlanetaryEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }
    }
}
