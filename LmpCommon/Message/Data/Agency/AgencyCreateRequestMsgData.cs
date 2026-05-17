using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Client → server. **Despite the "Create" name, this is a rename-on-connect
    /// message** — it does NOT mint a new agency. The server has already auto-
    /// registered the requesting player's agency via
    /// <see cref="Server.System.Agency.AgencySystem.OnPlayerAuthenticated"/> at
    /// handshake time, with the default display name <c>"{PlayerName} Space Agency"</c>.
    /// This message lets the client override that default with a custom display
    /// name; the server's response (<see cref="AgencyCreateReplyMsgData"/>) carries
    /// the SAME <c>AgencyId</c> as the prior <see cref="AgencyHandshakeMsgData.AssignedAgencyId"/>,
    /// not a fresh one. Stage 5.18a authors: do NOT write decoder logic that treats
    /// <see cref="AgencyCreateReplyMsgData.AgencyId"/> as a "potentially new agency
    /// id" — it's always the auto-registered one (or <see cref="System.Guid.Empty"/>
    /// on failure).
    ///
    /// Subsequent renames (post-create) will use a dedicated rename message in
    /// Stage 5.18a; this one is the create-or-rename-on-handshake path only.
    ///
    /// **Recommended client flow.** Send this only if the user explicitly customises
    /// the display name in the create-window UI. The auto-registered default is
    /// canonical for any player who skips the dialog — no need to roundtrip a
    /// CreateRequest just to confirm the default.
    ///
    /// **Validation.** The server-side <c>AgencyMsgReader.ValidateDisplayName</c>
    /// enforces: not whitespace-only, ≤64 chars, no <c>= { } \n \r</c> or
    /// <c>char.IsControl</c>. Until that validator hoists to <c>LmpCommon</c> (Stage
    /// 5.18a deferred item), the Stage 5.18a UI should either duplicate this check
    /// locally or accept the server's rejection round-trip.
    /// </summary>
    public class AgencyCreateRequestMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyCreateRequestMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CreateRequest;

        public string DisplayName = string.Empty;

        public override string ClassName { get; } = nameof(AgencyCreateRequestMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(DisplayName ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            DisplayName = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + DisplayName.GetByteCount();
        }
    }
}
