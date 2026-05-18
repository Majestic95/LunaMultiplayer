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
        /// <para><b>Bypass helper for authoritative writes.</b> Stage 5.18d ships
        /// <see cref="ForceRecordOwnership"/> for the two server-pushed mutations
        /// listed below. Use the bypass when the inbound value comes from an
        /// authoritative path (admin command echo, dedicated visibility push)
        /// — not from the relay path. The bypass is required because the
        /// preservation rule in this method drops legitimate demotions to
        /// Unassigned (transferagency-to-sentinel, deleteagency cascade), and
        /// it would silently absorb transfer-X→Y pushes that happen to encode
        /// the new value as Empty for any reason.</para>
        ///
        /// <para><b>Cases that route through <see cref="ForceRecordOwnership"/>:</b>
        /// <list type="bullet">
        ///   <item><b>Transfer X → Y mid-session:</b> 5.18d <c>transferagency</c>
        ///         updates server-side <c>Vessel.OwningAgencyId</c> from agency
        ///         X to agency Y in the canonical store, but mid-session
        ///         propagation to peer clients goes through the relay path —
        ///         which strips <c>lmpOwningAgency</c> on every owner resend,
        ///         so peers keep seeing the stale prior value (X) until the
        ///         next VesselSync (i.e., reconnect or scene change). 5.18c
        ///         <c>AgencyVisibilityMsgData</c> is the explicit "ownership
        ///         changed" push that bypasses this preservation rule and
        ///         forces the new value through.</item>
        ///   <item><b>Demote to Unassigned:</b> 5.18d <c>deleteagency --confirm</c>
        ///         removes a registered agency; the cascade reassigns its
        ///         vessels to <see cref="Guid.Empty"/> (the Unassigned-vessel
        ///         sentinel per spec §10 Q3) on both server and clients. The
        ///         visibility push carries Empty as the authoritative value
        ///         and must overwrite — preservation here would leave peer
        ///         clients seeing a stale "owned by deleted agency" stamp.</item>
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

        /// <summary>
        /// Authoritative-write companion to <see cref="RecordOwnership"/>. Stage
        /// 5.18d. Unconditionally records <paramref name="authoritativeAgencyId"/>
        /// for <paramref name="vesselId"/>, BYPASSING the relay-safety preservation
        /// rule that <see cref="RecordOwnership"/> enforces. Callers must only
        /// route values from authoritative paths through this helper — never the
        /// relay path. See <see cref="RecordOwnership"/>'s XML for the list of
        /// authoritative paths (Stage 5.18d <c>transferagency</c> and
        /// <c>deleteagency</c> cascade via the 5.18c
        /// <c>AgencyVisibilityMsgData</c> push).
        ///
        /// <para><b>Why a separate method, not a flag on <see cref="RecordOwnership"/>.</b>
        /// The two paths have different correctness contracts and different
        /// failure modes. Putting them on one method behind a <c>bool force</c>
        /// flag makes it trivially easy for a future caller to pass <c>true</c>
        /// from a relay-path site (the wrong place), silently corrupting peer
        /// registries with relay-stripped Empty values. Two named methods make
        /// the choice explicit and grep-friendly; reviewers can verify each call
        /// site routes through the correct one.</para>
        ///
        /// <para><b>Demote semantics.</b> Passing <see cref="Guid.Empty"/> is a
        /// legitimate authoritative demotion (the deleteagency cascade pushes
        /// Empty as the new owner). The bypass is required precisely so this
        /// demotion lands; <see cref="RecordOwnership"/>'s preservation rule
        /// would silently absorb it.</para>
        /// </summary>
        public static void ForceRecordOwnership(ConcurrentDictionary<Guid, Guid> registry, Guid vesselId, Guid authoritativeAgencyId)
        {
            if (registry == null) return;
            // Unconditional write — caller has asserted this is authoritative.
            // No Empty-input preservation, no prior-value check. Indexer is
            // ConcurrentDictionary's atomic upsert; safe under contention.
            registry[vesselId] = authoritativeAgencyId;
        }

        /// <summary>
        /// Stage 5.18d slice (h) — economy ownership guard. Pure decision helper
        /// for client-side <c>VesselRemoveEvents.OnVesselRecovered</c> /
        /// <c>OnVesselTerminated</c>. Returns <c>true</c> when the local player
        /// should be BLOCKED from recovering / terminating the vessel because it
        /// belongs to a different agency.
        ///
        /// <para><b>Why client-side.</b> The server's Stage 5.17a write-path
        /// counterpart (<c>RejectIfCrossAgencyWrite</c> in
        /// <c>Server/Message/VesselMsgReader.cs</c>) already refuses
        /// cross-agency <c>VesselRemoveMsgData</c>; the vessel stays in the
        /// canonical store. But by the time the server rejects, the LOCAL KSP
        /// has already credited recovery funds via
        /// <c>Funding.Instance.AddFunds(value, TransactionReasons.VesselRecovery)</c>
        /// and the client has emitted a Share*Funds broadcast carrying the
        /// post-credit total. The 5.17e <c>AgencyCurrencyRouter</c> routes
        /// that total to the local agency — the player keeps the funds for a
        /// recovery that didn't actually happen server-side. This guard
        /// prevents the local credit from happening at all.</para>
        ///
        /// <para><b>Bypass rules.</b> Same shape as Stage 5.17a's
        /// cross-agency lock guard:
        /// <list type="bullet">
        ///   <item><paramref name="perAgencyEnabledClientGate"/> false → gate
        ///         is off; permit (dual-mode silence).</item>
        ///   <item><paramref name="localAgencyId"/> = <see cref="Guid.Empty"/>
        ///         → local player has no agency mapping (pre-handshake, or
        ///         post-transferagency / post-deleteagency where their old
        ///         agency is gone); permit (existing 5.17a "requester has no
        ///         agency mapping" bypass).</item>
        ///   <item><paramref name="vesselKnown"/> false → vessel not in the
        ///         client's <c>VesselOwnership</c> registry (relay path
        ///         hasn't supplied a stamp yet); permit. The server-side
        ///         guard catches this case if it's a real cross-agency
        ///         attempt; client-side erring permissive keeps the
        ///         interactable surface honest about what the client knows.</item>
        ///   <item><paramref name="vesselOwningAgencyId"/> =
        ///         <see cref="Guid.Empty"/> → Unassigned-sentinel (spec §10
        ///         Q3); ANY agency can recover Unassigned vessels by design.
        ///         Permit.</item>
        ///   <item><paramref name="vesselOwningAgencyId"/> equals
        ///         <paramref name="localAgencyId"/> → same agency; permit.</item>
        ///   <item>Otherwise (gate on, non-Empty local agency, vessel known
        ///         with non-Empty + different agency) → BLOCK.</item>
        /// </list></para>
        /// </summary>
        public static bool IsRecoveryBlockedByAgency(
            Guid localAgencyId,
            bool vesselKnown,
            Guid vesselOwningAgencyId,
            bool perAgencyEnabledClientGate)
        {
            if (!perAgencyEnabledClientGate) return false;
            if (localAgencyId == Guid.Empty) return false;
            if (!vesselKnown) return false;
            if (vesselOwningAgencyId == Guid.Empty) return false;
            return vesselOwningAgencyId != localAgencyId;
        }
    }
}
