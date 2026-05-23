using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.PlayerStatus;
using LmpCommon.Message.Interface;

namespace LmpClient.Systems.Status
{
    public class StatusMessageSender : SubSystem<StatusSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<PlayerStatusCliMsg>(msg)));
        }

        public void SendPlayersRequest()
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<PlayerStatusCliMsg, PlayerStatusRequestMsgData>()));
        }

        public void SendOwnStatus()
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<PlayerStatusSetMsgData>();
            msgData.PlayerStatus.PlayerName = SettingsSystem.CurrentSettings.PlayerName;
            msgData.PlayerStatus.StatusText = System.MyPlayerStatus.StatusText;
            msgData.PlayerStatus.VesselText = System.MyPlayerStatus.VesselText;
            //Phase 1 of server-side-offload — include current KSP scene so server
            //can filter continuous vessel-state relays (Position / Flightstate /
            //etc.) to recipients whose scene will actually render them.
            //StatusSystem.CheckPlayerStatus's StatusIsDifferent comparator already
            //includes Scene, so a scene change automatically triggers SendOwnStatus.
            //Set on the message directly (NOT on the embedded PlayerStatusInfo)
            //because PlayerStatusInfo is also embedded in PlayerStatusReplyMsgData
            //as an unframed array — a tail-bit-read field there would corrupt
            //subsequent array elements. See PlayerStatusSetMsgData.Scene XML.
            msgData.Scene = System.MyPlayerStatus.Scene;

            SendMessage(msgData);
        }
    }
}