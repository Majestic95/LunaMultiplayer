using System;
using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Log;
using Server.Message.Base;
using Server.System;

namespace Server.Message
{
    public class KerbalMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var data = message.Data as KerbalBaseMsgData;
            switch (data?.KerbalMessageType)
            {
                case KerbalMessageType.Request:
                    KerbalSystem.HandleKerbalsRequest(client);
                    //We don't use this message anymore so we can recycle it
                    message.Recycle();
                    break;
                case KerbalMessageType.Proto:
                    {
                        // [Stage 6 Phase 6.9-hardening] Wire-boundary kerbal-name
                        // validation. The kerbal-name field feeds Path.Combine
                        // directly in TryWriteKerbalProtoPerAgency — without this
                        // gate a modified client can write arbitrary .txt files
                        // outside Universe/ via path traversal or rooted-path
                        // tricks. See Server/System/KerbalNameValidator.cs class
                        // XML for the threat model.
                        var proto = (KerbalProtoMsgData)data;
                        if (!RejectIfInvalidKerbalName(client, "KerbalProto", proto.Kerbal?.KerbalName))
                            break;
                        KerbalSystem.HandleKerbalProto(client, proto);
                        break;
                    }
                case KerbalMessageType.Remove:
                    {
                        // Same wire-boundary validation as Proto. KerbalRemove's
                        // KerbalName flows to FileHandler.FileDelete via
                        // TryDeleteKerbalPerAgency — arbitrary .txt delete sink.
                        var remove = (KerbalRemoveMsgData)data;
                        if (!RejectIfInvalidKerbalName(client, "KerbalRemove", remove.KerbalName))
                            break;
                        KerbalSystem.HandleKerbalRemove(client, remove);
                        break;
                    }
                default:
                    throw new NotImplementedException("Kerbal type not implemented");
            }
        }

        /// <summary>
        /// [Stage 6 Phase 6.9-hardening] Returns true when <paramref name="kerbalName"/>
        /// passes <see cref="KerbalNameValidator.IsValid"/>. On false, logs a
        /// Warning with the validator's reason + the player who sent the
        /// message, then returns false (caller MUST skip dispatch). The
        /// Warning text deliberately includes a SHORT prefix of the name (up
        /// to 32 chars) rather than the full value — a malicious 100MB name
        /// would otherwise produce recursive log-amplification.
        /// </summary>
        private static bool RejectIfInvalidKerbalName(ClientStructure client, string messageKind, string kerbalName)
        {
            if (KerbalNameValidator.IsValid(kerbalName, out var reason))
                return true;

            var sender = client?.PlayerName ?? "<unknown>";
            // Truncate-and-quote for log safety. Length-cap reasons already
            // carry the rejected length; per-char reasons carry the offending
            // index. We add a 32-char snippet (or "<empty>" / "<null>") so the
            // operator has SOME context without enabling log-flooding.
            string snippet;
            if (kerbalName == null)
                snippet = "<null>";
            else if (kerbalName.Length == 0)
                snippet = "<empty>";
            else if (kerbalName.Length <= 32)
                snippet = $"'{kerbalName}'";
            else
                snippet = $"'{kerbalName.Substring(0, 32)}...' (truncated from {kerbalName.Length} chars)";

            LunaLog.Warning(
                $"[fix:per-agency-kerbal-roster] DROPPED {messageKind} from '{sender}': " +
                $"kerbal-name {snippet} failed wire-boundary validation. Reason: {reason}.");
            return false;
        }
    }
}
