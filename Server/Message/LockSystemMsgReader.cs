using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Log;
using Server.Message.Base;
using Server.System;

namespace Server.Message
{
    public class LockSystemMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var data = (LockBaseMsgData)message.Data;
            switch (data.LockMessageType)
            {
                case LockMessageType.ListRequest:
                    LockSystemSender.SendAllLocks(client);
                    //We don't use this message anymore so we can recycle it
                    message.Recycle();
                    break;
                case LockMessageType.Acquire:
                    var acquireData = (LockAcquireMsgData)data;
                    if (acquireData.Lock.PlayerName == client.PlayerName)
                        LockSystemSender.SendLockAcquireMessage(client, acquireData.Lock, acquireData.Force);
                    break;
                case LockMessageType.Release:
                    var releaseData = (LockReleaseMsgData)data;
                    if (releaseData.Lock.PlayerName == client.PlayerName)
                        LockSystemSender.ReleaseAndSendLockReleaseMessage(client, releaseData.Lock);
                    break;
                case LockMessageType.ListReply:
                case LockMessageType.Reject:
                    // [Stage 5.18d slice (c)] These subtypes are server-→-client only. The
                    // CliMsg dictionary lists them so MessageBase.GetMessageData can
                    // deserialise them without throwing (BUG-010 wire-symmetry rule); a
                    // misbehaving / malicious client that ships one of them should be
                    // log+dropped rather than crashing the Lidgren receive thread via
                    // the prior `default: throw` arm. Same pattern as
                    // AgencyMsgReader's S-only-subtype log-drop arm.
                    LunaLog.Warning($"[fix:per-agency-career] Dropping inbound {data.LockMessageType} from {client.PlayerName} (server-→-client subtype)");
                    break;
                default:
                    // Future-unknown subtypes — log + drop, never throw.
                    LunaLog.Warning($"[fix:per-agency-career] Unknown LockMessageType {data.LockMessageType} from {client.PlayerName}; dropping");
                    break;
            }
        }
    }
}
