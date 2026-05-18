using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → client, OWNER-ONLY. Carries one batch of contract entries scoped to a
    /// single agency. The Stage 5.17d <c>AgencyContractRouter</c> (Q6 hybrid) emits this
    /// in place of the shared-agency <c>ShareProgressContractsMsgData</c> broadcast when
    /// <see cref="Server.Settings.Structures.GameplaySettingsDefinition.PerAgencyCareer"/>
    /// is on. Non-Offered contracts (Active / Completed / Failed / Cancelled /
    /// DeadlineExpired / Withdrawn) flow exclusively through this channel; Offered
    /// contracts continue to use the shared scenario path so Contract Configurator's
    /// <c>ContractPreLoader</c> ScenarioModule sees the world it expects (Q6 commitment c).
    ///
    /// **When this message arrives at the client (Stage 5.18a author note):**
    /// <list type="bullet">
    ///   <item>On <b>connect / reconnect</b>, immediately after the
    ///        <see cref="AgencyHandshakeMsgData"/> + <see cref="AgencyStateMsgData"/>
    ///        pair, the server fires <c>AgencySystemSender.SendContractCatchupTo</c>
    ///        with the OWNER's persisted <c>AgencyState.Contracts</c> list. A returning
    ///        player thus receives their entire Active+Finished pool in one batch before
    ///        gameplay starts; subsequent mid-session mutations arrive incrementally.</item>
    ///   <item>On <b>mid-session mutation</b>, after the local player accepts /
    ///        completes / fails a contract, the existing
    ///        <c>ShareProgress.Share*Sender</c> on the client emits the standard
    ///        <see cref="ShareProgressContractsMsgData"/> (CLIENT WRITE PATH IS
    ///        UNCHANGED — do NOT add a new <c>AgencyContract*</c> C→S subtype in 5.18a;
    ///        the existing <c>ShareProgress</c> wire is the intent channel). The
    ///        <c>AgencyContractRouter</c> intercepts on the server, classifies, and
    ///        echoes this owner-only <c>AgencyContractMsgData</c> back to the originator.</item>
    /// </list>
    ///
    /// **Privacy rule (spec §10 Q1 — PrivateAgencyResources = true).** Contracts are
    /// agency-private under gate=on. The router only ever <c>SendToClient</c>s this to
    /// the agency owner; peers never receive another agency's per-agency contracts.
    /// Stage 5.18a client mirror authors: defensive-discard any inbound whose
    /// <see cref="AgencyId"/> does not match the local player's assigned agency. (The
    /// server's owner-only path makes this defensive check unreachable in practice; the
    /// discard rule is a defence-in-depth gate against a misrouted send or wire
    /// corruption — same shape as <see cref="AgencyStateMsgData"/>.)
    ///
    /// **Wire shape.** Reuses <see cref="ContractInfo"/> from the
    /// <c>LmpCommon.Message.Data.ShareProgress</c> namespace so the client side already
    /// has a deserializer for contract bytes (consumed in Stage 5.18a). Each entry's
    /// <see cref="ContractInfo.Data"/> field is <b>QuickLZ-compressed on the wire and
    /// decompressed in-place by <see cref="ContractInfo.Deserialize"/></b>, so when the
    /// 5.18a handler receives this message the <c>Data[0..NumBytes]</c> slice holds the
    /// raw decompressed ConfigNode bytes for the contract. <see cref="ContractInfo.ContractGuid"/>
    /// is the stable id; the router upserts into <c>AgencyState.Contracts</c> by this
    /// key so two batches with the same guid update one entry, never duplicate.
    ///
    /// **Reward routing (spec §10 Q4) is orthogonal to this message.** Contract
    /// completion credits funds + reputation; those scalar deltas flow through the
    /// existing Share*Funds / Share*Reputation paths (intercepted server-side by the
    /// Stage 5.17b per-agency routing). This message carries only the contract
    /// state-machine transitions — not the reward funds. Do not implement reward
    /// distribution from this handler in 5.18a.
    ///
    /// **Forward-compatibility.** No room for new fields without a protocol bump —
    /// the trailing read is a count-driven array. Future additions append at the
    /// message tail with a <c>lidgrenMsg.Position &lt; lidgrenMsg.LengthBits</c>
    /// end-of-message guard, matching the <c>VesselProtoMsgData.Reason</c> precedent.
    /// </summary>
    public class AgencyContractMsgData : AgencyBaseMsgData
    {
        /// <inheritdoc />
        internal AgencyContractMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.Contract;

        /// <summary>
        /// The agency these contracts belong to. Always equals the receiving client's
        /// own <c>AssignedAgencyId</c> (from <see cref="AgencyHandshakeMsgData"/>) under
        /// the owner-only contract — the server never legitimately ships another agency's
        /// per-agency contracts to a non-owner. Stage 5.18a should defensive-discard any
        /// inbound where <c>AgencyId != localPlayer.AssignedAgencyId</c> as defence in
        /// depth (catches server bug / wire corruption / misrouted send).
        /// </summary>
        public Guid AgencyId;
        public int ContractCount;
        public ContractInfo[] Contracts = new ContractInfo[0];

        public override string ClassName { get; } = nameof(AgencyContractMsgData);

        /// <summary>
        /// Upper bound on <see cref="ContractCount"/> on the wire. CC + stock career can
        /// realistically produce tens-to-low-hundreds of per-agency contracts across the
        /// Active + Finished split; 4096 leaves generous headroom while still preventing
        /// a malicious peer from forcing a 64 GB allocation by shipping
        /// <see cref="int.MaxValue"/>. Same DoS-amplification class round-2 wire review
        /// caught on <see cref="AgencyHandshakeMsgData.MaxOtherAgencyCount"/>.
        /// </summary>
        internal const int MaxContractCount = 4096;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(AgencyId, lidgrenMsg);
            lidgrenMsg.Write(ContractCount);
            // Caller contract: Contracts[0..ContractCount-1] must be non-null. Same
            // "trust the caller" convention used by AgencyHandshakeMsgData — a sender
            // that null-skipped while emitting the original count would desync with
            // a receiver that reads ContractCount entries unconditionally, corrupting
            // subsequent bytes on channel 22.
            for (var i = 0; i < ContractCount; i++)
            {
                Contracts[i].Serialize(lidgrenMsg);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            AgencyId = GuidUtil.Deserialize(lidgrenMsg);
            ContractCount = lidgrenMsg.ReadInt32();
            if (ContractCount < 0 || ContractCount > MaxContractCount)
                throw new System.IO.InvalidDataException(
                    $"AgencyContract ContractCount out of range: {ContractCount} (allowed 0..{MaxContractCount})");

            if (Contracts.Length < ContractCount)
                Contracts = new ContractInfo[ContractCount];

            for (var i = 0; i < ContractCount; i++)
            {
                if (Contracts[i] == null)
                    Contracts[i] = new ContractInfo();

                Contracts[i].Deserialize(lidgrenMsg);
            }
        }

        internal override int InternalGetMessageSize()
        {
            // Caller contract (see InternalSerialize): entries 0..ContractCount-1 non-null.
            var arraySize = 0;
            for (var i = 0; i < ContractCount; i++)
            {
                arraySize += Contracts[i].GetByteCount();
            }

            return base.InternalGetMessageSize() + GuidUtil.ByteSize + sizeof(int) + arraySize;
        }
    }
}
