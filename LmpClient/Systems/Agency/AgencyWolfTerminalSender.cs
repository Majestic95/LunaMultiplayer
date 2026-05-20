using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 4 Slice D] Client → server sender for per-agency WOLF terminal
    /// mutations. Emits <see cref="AgencyWolfTerminalStateMsgData"/> on slot
    /// 12
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.WolfTerminalState"/>)
    /// when WOLF's <c>ScenarioPersister.CreateTerminal</c> /
    /// <c>RemoveTerminal</c> Harmony postfixes fire under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Threading + server-trust posture</b> identical to sibling
    /// <see cref="AgencyWolfHopperSender"/>. No debouncer (operator-driven
    /// UI cadence).</para>
    ///
    /// <para><b>Key form preservation.</b> Terminal.Id is Guid in
    /// <c>ToString("N")</c> form per <c>TerminalMetadata.cs:15</c> — no
    /// hyphens. Wire payloads carry the string verbatim; the sender does
    /// NOT normalize.</para>
    /// </summary>
    public static class AgencyWolfTerminalSender
    {
        /// <summary>
        /// Sends a batch of terminal entry mutations (inserts/updates).
        /// </summary>
        public static void SendBatch(IReadOnlyList<AgencyWolfTerminalEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyWolfTerminalStateMsgData>();
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyWolfTerminalEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            Base.SystemBase.TaskFactory.StartNew(() =>
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Convenience single-entry overload for Create.
        /// </summary>
        public static void SendMutation(AgencyWolfTerminalEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }

        /// <summary>
        /// Sends a terminal removal via
        /// <see cref="AgencyWolfTerminalStateMsgData.RemovedKeys"/>.
        /// </summary>
        public static void SendRemoval(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyWolfTerminalStateMsgData>();
            msgData.EntryCount = 0;
            msgData.Entries = new AgencyWolfTerminalEntry[0];
            msgData.RemovedKeyCount = 1;
            msgData.RemovedKeys = new[] { id };

            Base.SystemBase.TaskFactory.StartNew(() =>
                NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }
    }
}
