using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 4 Slice D] Client → server sender for per-agency WOLF hopper
    /// mutations. Emits <see cref="AgencyWolfHopperStateMsgData"/> on slot 11
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.WolfHopperState"/>)
    /// when WOLF's <c>ScenarioPersister.CreateHopper</c> /
    /// <c>RemoveHopper</c> Harmony postfixes fire under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Threading pattern</b> (pre-spec §3.e): the established
    /// <c>TaskFactory.StartNew</c> offload moves message-object construction
    /// off the Unity main thread so the Lidgren serialization + send-queue
    /// work doesn't eat <c>FixedUpdate</c> frame budget. Mirrors the
    /// established <see cref="AgencyWolfRouteSender"/> convention.</para>
    ///
    /// <para><b>Server trust posture.</b> The server's
    /// <c>AgencyWolfHopperRouter.TryRoute</c> ignores the wire-supplied
    /// <see cref="AgencyWolfHopperStateMsgData.AgencyId"/> on inbound and
    /// derives the sender's agency authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. We leave
    /// <see cref="AgencyWolfHopperStateMsgData.AgencyId"/> as
    /// <see cref="System.Guid.Empty"/> on C→S — explicit signal that the
    /// field is server-derived. Same posture as <see cref="AgencyWolfRouteSender"/>.</para>
    ///
    /// <para><b>No debouncer (vs Slice B-3's Negotiate path).</b> Hoppers are
    /// operator-created via the WOLF UI's recipe-pick + decommission flows
    /// — low-frequency clicks, no per-tick mutation cadence. The Slice B-3
    /// Negotiate hot path at ~50 Hz needed <see cref="WolfDepotDebouncer"/>;
    /// hopper mutations do not.</para>
    ///
    /// <para><b>Create vs Remove asymmetry.</b> Unlike Slice C Routes
    /// (which never delete via WOLF's API),
    /// <c>ScenarioPersister.RemoveHopper</c> at
    /// <c>ScenarioPersister.cs:432-440</c> is a normal-operation API.
    /// <see cref="SendRemoval(string)"/> ships the removed Id through the
    /// <see cref="AgencyWolfHopperStateMsgData.RemovedKeys"/> tail so the
    /// server-side router can drop it from <c>AgencyState.WolfHoppers</c>.
    /// A future combined-mutation API could batch creates + removals into
    /// one message; for now, sending them separately matches the postfix
    /// firing order and is simple to reason about.</para>
    /// </summary>
    public static class AgencyWolfHopperSender
    {
        /// <summary>
        /// Sends a batch of hopper entry mutations (inserts/updates). Builds
        /// <see cref="AgencyWolfHopperStateMsgData"/>, offloads to the
        /// network thread.
        /// </summary>
        /// <param name="entries">Non-null; may be empty. Implementation
        /// no-ops on empty batches as a defensive belt-and-braces.</param>
        public static void SendBatch(IReadOnlyList<AgencyWolfHopperEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyWolfHopperStateMsgData>();
            // AgencyId left at Guid.Empty intentionally — server ignores it
            // and derives the sender's agency authoritatively (class XML).
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyWolfHopperEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            Base.SystemBase.TaskFactory.StartNew(() =>
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Convenience single-entry overload — the most common postfix call
        /// shape for Create.
        /// </summary>
        public static void SendMutation(AgencyWolfHopperEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }

        /// <summary>
        /// Sends a hopper removal — the
        /// <see cref="ScenarioPersister_RemoveHopperPostfix"/> companion
        /// shape. Encoded as an empty-Entries +
        /// <see cref="AgencyWolfHopperStateMsgData.RemovedKeys"/>-populated
        /// message so the server-side router's RemovedKeys loop fires.
        /// </summary>
        public static void SendRemoval(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyWolfHopperStateMsgData>();
            msgData.EntryCount = 0;
            msgData.Entries = new AgencyWolfHopperEntry[0];
            msgData.RemovedKeyCount = 1;
            msgData.RemovedKeys = new[] { id };

            Base.SystemBase.TaskFactory.StartNew(() =>
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }
    }
}
