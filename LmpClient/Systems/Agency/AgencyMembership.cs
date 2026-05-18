using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Pure decision helpers for per-agency client-side filtering. Lives outside
    /// <see cref="AgencySystem"/> so the call sites can be unit-tested without spinning
    /// up the KSP-bound singleton (KSP DLLs aren't loaded in <c>LmpClientTest</c>).
    /// Same pattern as <c>VesselPositionUpdate.ComputeMaxInterpolationDuration</c>
    /// (BUG-003/004) and <c>WarpSystem.ShouldSteadyStateRetry</c> (BUG-051b): the
    /// instance call site becomes a one-line delegate; the decision math lives here.
    /// </summary>
    public static class AgencyMembership
    {
        /// <summary>
        /// Defensive filter for owner-only S→C messages (<c>AgencyStateMsgData</c>,
        /// <c>AgencyContractMsgData</c>). Returns true only when the inbound agency id
        /// matches the local player's assigned agency. The server's per-agency router
        /// is the primary contract (it only ever <c>SendToClient</c>s these to the
        /// owner), so this filter is defence-in-depth against a misrouted send / wire
        /// corruption / future server-side regression.
        ///
        /// <para><b>Empty-sentinel handling.</b> <see cref="Guid.Empty"/> is the
        /// Unassigned-vessel sentinel (spec §10 Q3) — it MUST NOT be treated as a
        /// valid agency match. The empty/empty case (handler called before Handshake
        /// arrived) returns false so we drop instead of applying a State carrying
        /// zeroed scalars.</para>
        ///
        /// <para><b>Note: not gated on PerAgencyCareerEnabled.</b> Under the server's
        /// dual-mode silence contract, no Agency*MsgData will arrive when the gate is
        /// off. If one does arrive (e.g. peer talking a future protocol version), the
        /// localAgencyId will be <see cref="Guid.Empty"/> because the client never
        /// processed an <c>AgencyHandshakeMsgData</c>, and this method returns false.
        /// Same observable outcome as an explicit gate check, with less coupling.</para>
        /// </summary>
        public static bool IsForLocalAgency(Guid localAgencyId, Guid incomingAgencyId)
        {
            if (localAgencyId == Guid.Empty) return false;
            if (incomingAgencyId == Guid.Empty) return false;
            return localAgencyId == incomingAgencyId;
        }

        /// <summary>
        /// Parses a raw <c>lmpOwningAgency</c> ConfigNode field value to a Guid. Stage
        /// 5.18b — used by <see cref="VesselProtoSys.VesselProto.CreateProtoVessel"/>
        /// to populate <see cref="AgencySystem.VesselOwnership"/> from the wire
        /// ConfigNode before KSP's ProtoVessel ctor drops the unknown top-level field.
        ///
        /// <para>Returns <see cref="Guid.Empty"/> for: null/empty input (field absent
        /// on pre-0.31 vessels or genuinely-new-server-side-unstamped vessels),
        /// malformed input (wire corruption / future schema mismatch). Both map to
        /// the Unassigned sentinel (spec §10 Q3); consumers that need to distinguish
        /// "unknown" from "Unassigned" should check dictionary presence on the
        /// registry, not the parsed value.</para>
        ///
        /// <para>Takes a <see cref="string"/> rather than a <c>ConfigNode</c> so the
        /// helper remains unit-testable without loading KSP DLLs — same constraint
        /// as the other LmpClientTest decision helpers
        /// (<c>VesselPositionUpdate.ComputeMaxInterpolationDuration</c>,
        /// <c>WarpSystem.ShouldSteadyStateRetry</c>).</para>
        /// </summary>
        public static Guid TryParseAgencyId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Guid.Empty;
            return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
        }

        /// <summary>
        /// Records an incoming wire-side agency id for a vessel into the client-side
        /// <see cref="AgencySystem.VesselOwnership"/> registry, applying the
        /// <b>relay-safety preservation rule</b>: never downgrade a known real
        /// agency id to <see cref="Guid.Empty"/> via a relayed proto. Stage 5.18b.
        ///
        /// <para><b>Why the rule exists.</b> Server-side relay (see
        /// <c>Server/Message/VesselMsgReader.cs</c> lines 188-198) forwards the
        /// ORIGINAL wire bytes the sending client supplied — and KSP's
        /// <c>BackupVessel</c>/<c>ProtoVessel.Save</c> path strips the unknown
        /// <c>lmpOwningAgency</c> top-level field on every local-owner resend
        /// (KSP doesn't know the field; ProtoVessel.Save only writes KSP-known
        /// fields). Without preservation, every periodic drift-correction
        /// resend from a vessel's owner would arrive at peer clients with no
        /// <c>lmpOwningAgency</c>, parse to <see cref="Guid.Empty"/>, and
        /// overwrite the peer's previously-recorded real agency id —
        /// corrupting Stage 5.18c UI labels and Stage 5.18d economy guards.
        /// The server's authoritative store is unaffected; the server's
        /// <c>VesselSync</c> reply path serialises from
        /// <c>GetVesselInConfigNodeFormat</c> which DOES emit the field, so
        /// authoritative writes still land correctly.</para>
        ///
        /// <para><b>Rule.</b> If <paramref name="incoming"/> is non-Empty,
        /// unconditionally write (insert or overwrite) — authoritative claim
        /// from a VesselSync reply, a genuinely-new vessel via the relay
        /// path, or future Stage 5.18c <c>AgencyVisibilityMsgData</c>. If
        /// <paramref name="incoming"/> is Empty, insert ONLY when no prior
        /// entry exists; never replace an existing real id with Empty.</para>
        ///
        /// <para><b>Future evolution (Stage 5.18d).</b> The next consumer needs
        /// a sibling helper that BYPASSES this preservation rule for two
        /// authoritative server-pushed mutations. Recommended shape:
        /// <c>public static void ForceRecordOwnership(registry, vesselId,
        /// authoritativeAgencyId)</c> = unconditional indexer write, with XML
        /// pointing back here for the rationale. Both cases below need it:</para>
        ///
        /// <para><b>Cases that need the bypass helper:</b>
        /// <list type="bullet">
        /// <list type="bullet">
        ///   <item><b>Demote to Unassigned:</b> if a future operator-facing flow
        ///         adds a "remove agency claim" path (none today — the planned
        ///         5.18d <c>deleteagency --confirm</c> reassigns to a sentinel
        ///         "Abandoned" agency, not Empty), this preservation rule must
        ///         be revisited in lockstep.</item>
        ///   <item><b>Transfer X → Y mid-session:</b> 5.18d <c>transferagency</c>
        ///         updates server-side <c>Vessel.OwningAgencyId</c> from agency
        ///         X to agency Y in the canonical store, but mid-session
        ///         propagation to peer clients goes through the relay path —
        ///         which strips <c>lmpOwningAgency</c> on every owner resend,
        ///         so peers keep seeing the stale prior value (X) until the
        ///         next VesselSync (i.e., reconnect or scene change). 5.18c
        ///         <c>AgencyVisibilityMsgData</c> is the intended remedy, with
        ///         an explicit "ownership changed" push that bypasses this
        ///         preservation rule and forces the new value through.</item>
        /// </list></para>
        /// </summary>
        public static void RecordOwnership(ConcurrentDictionary<Guid, Guid> registry, Guid vesselId, Guid incoming)
        {
            if (registry == null) return;

            if (incoming != Guid.Empty)
            {
                // Authoritative or first-sight real id: write through. Indexer
                // semantics match TryAdd-or-overwrite, which is what we want
                // when the wire supplies a real claim.
                registry[vesselId] = incoming;
            }
            else
            {
                // Empty incoming: ambiguous between "Unassigned (spec §10 Q3
                // sentinel)" and "relay path stripped the field." Insert only
                // if no prior entry exists; preserve any prior value (Empty
                // stays Empty idempotently; real id stays real — the
                // relay-safety contract).
                registry.TryAdd(vesselId, Guid.Empty);
            }
        }
    }
}
