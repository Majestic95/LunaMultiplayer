using LmpCommon.Enums;

namespace LmpCommon
{
    public class PlayerStatus
    {
        public string PlayerName { get; set; }

        /// <summary>
        /// Client's current KSP scene. Phase 1 of server-side-offload
        /// (docs/research/11-server-side-offload-spec.md) — drives
        /// <c>MessageQueuer.RelayMessageToFlightScene</c> on the server side.
        /// Default <see cref="ClientSceneType.Unknown"/> = "scene not yet
        /// reported" = relay-always (backward-compat with pre-Phase-1 clients).
        ///
        /// NOTE: travels on the wire via <see cref="Message.Data.PlayerStatus.PlayerStatusSetMsgData.Scene"/>
        /// tail-byte — NOT via PlayerStatusInfo's payload. PlayerStatusInfo is
        /// embedded in PlayerStatusReplyMsgData as an unframed array; a tail-bit-read
        /// there would corrupt subsequent elements. The Scene field on this PlayerStatus
        /// class is shared client/server state (server.ClientStructure.PlayerStatus and
        /// client.StatusSystem.MyPlayerStatus); the wire carries it on the Set path only.
        /// </summary>
        public ClientSceneType Scene { get; set; } = ClientSceneType.Unknown;

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set
            {
                _statusText = value;
                DisplayText = !string.IsNullOrEmpty(_vesselText) ? $"{_statusText} ({_vesselText})" : _statusText;
            }
        }

        private string _vesselText;
        public string VesselText
        {
            get => _vesselText;
            set
            {
                _vesselText = value;
                DisplayText = !string.IsNullOrEmpty(_vesselText) ? $"{_statusText} ({_vesselText})" : _statusText;
            }
        }

        public string DisplayText { get; private set; }
    }
}
