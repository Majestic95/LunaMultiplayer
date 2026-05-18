using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → all clients (BROADCAST). Stage 5.18d. Carries a batch of vessel-
    /// ownership transitions. Emitted by <c>AgencySystemSender.BroadcastVisibilityChange</c>
    /// when an authoritative server-side mutation changes a vessel's
    /// <c>OwningAgencyId</c>:
    /// <list type="bullet">
    ///   <item><b>transferagency X → Y</b> — admin command moves a vessel's
    ///         ownership from agency X to agency Y. One entry per vessel mutated;
    ///         a transfer that moves N vessels in one command emits one
    ///         visibility batch with N entries.</item>
    ///   <item><b>deleteagency cascade</b> — admin command removes a registered
    ///         agency; every vessel previously owned by that agency is demoted
    ///         to <see cref="System.Guid.Empty"/> (the Unassigned-vessel
    ///         sentinel, spec §10 Q3). One batch with one entry per cascade
    ///         vessel.</item>
    /// </list>
    ///
    /// <para><b>Why broadcast, not owner-only.</b> Vessel ownership is PUBLIC
    /// state (it's already visible to every client via the relay path's
    /// <c>lmpOwningAgency</c> proto field; the relay strips it on resend, which
    /// is exactly why this push exists). Stage 5.18c UI labels and Stage 5.18d
    /// economy guards both need ownership transitions visible to every connected
    /// client, not just the new owner. spec §10 Q1's
    /// <c>PrivateAgencyResources=true</c> applies to resource scalars
    /// (funds/sci/rep) — NOT to ownership identity, which has always been a
    /// public concern.</para>
    ///
    /// <para><b>ARRIVAL CONDITIONS (Stage 5.18d author note — consumer-lens
    /// recipe step 7).</b> This message arrives at the client ONLY mid-session
    /// in response to an admin command. There is no connect-time catch-up;
    /// ownership state on connect is reconstructed via the existing pathways
    /// (VesselSync replies serialise <c>lmpOwningAgency</c> from the
    /// authoritative store via <c>GetVesselInConfigNodeFormat</c>, and the
    /// relay-path proto bytes carry the original sender's value on first sight).
    /// A returning player thus sees the correct post-transfer ownership
    /// immediately on reconnect without needing a Visibility replay. A
    /// dedicated catch-up surface (like 5.17d's <c>SendContractCatchupTo</c>)
    /// would be unnecessary; this message exists strictly for the mid-session
    /// push case.</para>
    ///
    /// <para><b>CLIENT WRITE PATH (recipe step 7).</b> CLIENT WRITE PATH IS
    /// NONE — clients NEVER originate ownership transitions. The 5.18d
    /// <c>transferagency</c> / <c>deleteagency</c> commands are operator-only
    /// (server console). Clients only receive this message; they do not
    /// emit any sibling C→S subtype. The <c>AgencyCliMsg.SubTypeDictionary</c>
    /// entry exists for wire-symmetry (BUG-010 rule) only; an inbound
    /// Visibility on the server side is log-dropped by
    /// <c>AgencyMsgReader</c>.</para>
    ///
    /// <para><b>ORTHOGONAL CONCERNS (recipe step 7).</b> Several adjacent
    /// concerns flow through OTHER messages, NOT this one:
    /// <list type="bullet">
    ///   <item><b>Funds/Science/Reputation transfers.</b> If the operator
    ///         intends to also move the agency's scalars when transferring
    ///         vessels (e.g. "give Bob's Mun base to Alice along with the
    ///         funds she earned doing it"), that flows through
    ///         <see cref="AgencyStateMsgData"/> (owner-only, separate
    ///         message). 5.18d slice (f) / (e) decides the policy.</item>
    ///   <item><b>Vessel-level lock release.</b> When a vessel's ownership
    ///         changes, the prior owner's vessel-scoped locks (Control,
    ///         Update, UnloadedUpdate) must be released or transferred —
    ///         that flows through the existing <c>LockSystem</c> messages
    ///         (Release + Acquire), not this surface. The 5.18d
    ///         <c>transferagency</c> command implementation will call
    ///         <c>LockSystem.ReleasePlayerLocks</c> on the source player as
    ///         part of the mutation; clients see the lock changes via the
    ///         existing lock wire and the ownership changes via THIS
    ///         message.</item>
    ///   <item><b>Vessel deletion.</b> Demoting a vessel to Unassigned via
    ///         the deleteagency cascade does NOT delete the vessel — it
    ///         marks it owner-less. Actual deletion goes through
    ///         <c>VesselRemoveMsgData</c> as today. 5.18d does not couple
    ///         them; deleteagency leaves vessels recoverable by any agency
    ///         per spec §10 Q3 Unassigned-sentinel rule.</item>
    /// </list></para>
    ///
    /// <para><b>Cross-channel ordering hazard (network-lens v1 + consumer-lens v1).</b>
    /// Visibility rides channel 22; vessel-proto relay rides channel 8. Lidgren's
    /// <c>ReliableOrdered</c> guarantee is PER-CHANNEL, so after a server-side
    /// <c>transferagency X → Y</c>:
    /// <list type="number">
    ///   <item>Server mutates <c>Vessel.OwningAgencyId</c> in the canonical store
    ///         and broadcasts Visibility on ch 22.</item>
    ///   <item>X's next periodic vessel-proto resend arrives at peers on ch 8
    ///         carrying STRIPPED <c>lmpOwningAgency</c> (KSP's <c>BackupVessel</c>
    ///         drops unknown top-level fields on every local-owner resend — see
    ///         the 5.18b relay-vs-store stack note).</item>
    ///   <item>Peer may receive the ch-8 proto BEFORE the ch-22 Visibility
    ///         (cross-channel order undefined). Peer's <c>VesselProto.CreateProtoVessel</c>
    ///         parses Empty → <c>RecordOwnership</c> Empty-input branch → preserves
    ///         the prior real id (X) via the 5.18b preservation rule. THEN
    ///         Visibility arrives → <c>ForceRecordOwnership</c> unconditionally
    ///         writes Y. End-state correct.</item>
    /// </list>
    /// End-state is correct because <c>ForceRecordOwnership</c> is unconditional
    /// and <c>RecordOwnership</c>'s preservation rule prevents an in-flight
    /// relay-strip from clobbering X with Empty during the transient window. A
    /// peer may briefly render the stale "owned by X" label between steps 2 and
    /// 3 — bounded, recoverable, no operator action required. Both client-side
    /// helpers in <see cref="LmpClient.Systems.Agency.AgencyMembership"/> were
    /// designed for this race exactly.</para>
    ///
    /// <para><b>Forward-compatibility.</b> Future fields append at the message
    /// tail with a <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c>
    /// end-of-message guard (the <c>VesselProtoMsgData.Reason</c> precedent).
    /// Reordering or renumbering existing fields requires a protocol bump.</para>
    /// </summary>
    public class AgencyVisibilityMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyVisibilityMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.Visibility;

        public int ChangeCount;
        public VesselOwnershipChange[] Changes = new VesselOwnershipChange[0];

        public override string ClassName { get; } = nameof(AgencyVisibilityMsgData);

        /// <summary>
        /// Upper bound on a single <see cref="AgencyVisibilityMsgData"/>'s
        /// <see cref="ChangeCount"/>. Two roles: (a) DoS guard — the deserialize
        /// path throws on a higher value instead of allocating a 64 GB array;
        /// (b) batch-chunk size for the sender — when a deleteagency cascade
        /// involves more vessels than this cap, <c>AgencySystemSender.BroadcastVisibilityChange</c>
        /// splits the batch into multiple consecutive messages on channel 22
        /// (Lidgren's per-channel ReliableOrdered guarantee preserves the
        /// across-batch apply order on the client side).
        ///
        /// <para><b>Public-not-internal.</b> Exposed for the sender's chunking
        /// logic in <c>AgencySystemSender</c>. Same DoS-amplification class as
        /// <see cref="AgencyContractMsgData.MaxContractCount"/> +
        /// <see cref="AgencyHandshakeMsgData.MaxOtherAgencyCount"/>.</para>
        /// </summary>
        public const int MaxChangeCount = 4096;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(ChangeCount);
            // Caller contract: Changes[0..ChangeCount-1] is the populated slice. A
            // sender that skipped entries while emitting the original count would
            // corrupt subsequent channel-22 bytes. Same pattern as
            // AgencyContractMsgData.InternalSerialize.
            for (var i = 0; i < ChangeCount; i++)
            {
                Changes[i].Serialize(lidgrenMsg);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            ChangeCount = lidgrenMsg.ReadInt32();
            if (ChangeCount < 0 || ChangeCount > MaxChangeCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyVisibility ChangeCount out of range: {ChangeCount} (allowed 0..{MaxChangeCount})");

            if (Changes.Length < ChangeCount)
                Changes = new VesselOwnershipChange[ChangeCount];

            for (var i = 0; i < ChangeCount; i++)
            {
                Changes[i].Deserialize(lidgrenMsg);
            }
        }

        internal override int InternalGetMessageSize()
        {
            // VesselOwnershipChange is a fixed-size struct (two Guids = 32 bytes).
            return base.InternalGetMessageSize()
                + sizeof(int)
                + (ChangeCount * (GuidUtil.ByteSize * 2));
        }
    }
}
