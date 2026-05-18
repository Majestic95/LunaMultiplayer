using LmpClient.Base;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Client-side mirror for the per-agency career wire surface (Stage 5.18a).
    /// Tracks the local player's assigned agency identity + a snapshot of every other
    /// agency known at handshake time. The mirror is deliberately thin: career state
    /// (Funds / Science / Reputation / Tech / Contracts / etc.) is delivered through
    /// scenario projection at scene-load time (the projector populates KSP's
    /// singletons directly) plus the owner-only <see cref="AgencyStateMsgData"/> /
    /// <see cref="AgencyContractMsgData"/> echoes for mid-session deltas. We don't
    /// duplicate the per-agency state into a parallel client-side store — KSP's own
    /// <c>Funding.Instance</c> / <c>ResearchAndDevelopment.Instance</c> /
    /// <c>Reputation.Instance</c> / <c>ContractSystem.Instance</c> are the authoritative
    /// view for the local player's agency, and other agencies' private state is
    /// privacy-rule-blocked from ever reaching this client (spec §10 Q1).
    ///
    /// **Lifecycle.** <see cref="EnableStage"/> is <see cref="ClientState.Handshaked"/>
    /// so the queue-drain routine is set up immediately after the LMP handshake reply
    /// transitions client state — that's exactly when the server's
    /// <c>AgencySystemSender.SendHandshakeTo</c> + <c>SendStateTo</c> +
    /// <c>SendContractCatchupTo</c> traffic starts arriving on channel 22 (Lidgren
    /// per-channel ordering preserves Handshake → State → Contract). Messages that
    /// race the state transition (network thread enqueueing before the Unity-thread
    /// enable event fires) are safe: the <see cref="MessageHandler"/>'s
    /// <c>IncomingMessages</c> queue is field-initialised to an empty concurrent
    /// queue, so EnqueueMessage works pre-enable; OnEnabled's SetupRoutine then
    /// drains whatever's pending on the next Update tick.
    /// <see cref="MessageSystem{T,TS,TH}.ProcessMessagesInUnityThread"/> defaults to
    /// true — necessary because <see cref="AgencyStateMsgData"/> /
    /// <see cref="AgencyContractMsgData"/> apply paths touch KSP singletons which
    /// must run on the Unity main thread.
    ///
    /// **Dual-mode silence.** When <c>PerAgencyCareer=false</c> the server never ships
    /// any <c>Agency*MsgData</c> (spec §11 dual-mode acceptance), so this system's
    /// state stays at its defaults (<see cref="LocalAgencyId"/> =
    /// <see cref="Guid.Empty"/>, <see cref="OtherAgencies"/> empty) and the apply
    /// paths never trip. No defensive client-side gate is required — the silent
    /// server is the contract.
    ///
    /// **BUG-025 v2 bracketing (CRITICAL).** Every owner-only apply that mutates KSP
    /// career singletons must bracket with <c>StartIgnoringEvents</c> /
    /// <c>StopIgnoringEvents</c> on the relevant <c>Share*System</c> — otherwise the
    /// re-apply fires <c>OnFundsChanged</c> / <c>OnScienceChanged</c> /
    /// <c>OnReputationChanged</c>, which the <c>Share*Events</c> handler re-broadcasts
    /// to the server, which routes back to us as another State echo, producing an
    /// infinite re-broadcast loop. The existing
    /// <see cref="ShareFunds.ShareFundsSystem.SetFundsWithoutTriggeringEvent"/> /
    /// <see cref="ShareScience.ShareScienceSystem.SetScienceWithoutTriggeringEvent"/> /
    /// <see cref="ShareReputation.ShareReputationSystem.SetReputationWithoutTriggeringEvent"/>
    /// helpers already wrap the bracket internally, so calling them is the canonical
    /// path — do NOT bypass to <c>Funding.Instance.SetFunds</c> directly. Contracts
    /// apply via <c>ShareContractsMessageHandler.ApplyContractBatch</c> which carries
    /// the same bracketing convention forward (Funds + Science + Reputation +
    /// ExperimentalParts all bracketed inside the apply path).
    /// </summary>
    public class AgencySystem :
        MessageSystem<AgencySystem, AgencyMessageSender, AgencyMessageHandler>
    {
        public override string SystemName { get; } = nameof(AgencySystem);

        protected override ClientState EnableStage => ClientState.Handshaked;

        /// <summary>
        /// The local player's assigned agency id, set when
        /// <see cref="AgencyHandshakeMsgData"/> arrives. <see cref="Guid.Empty"/> means
        /// "no agency yet" — either pre-handshake or per-agency career disabled on
        /// the server. The Stage 5.18c UI and 5.18b write-path patches both check
        /// this before exposing per-agency surfaces.
        /// </summary>
        public Guid LocalAgencyId { get; internal set; } = Guid.Empty;

        /// <summary>
        /// Display name shown in the UI. Populated by <see cref="AgencyStateMsgData"/>,
        /// not by <see cref="AgencyHandshakeMsgData"/> — the Handshake message only
        /// carries the assigned agency id + a public summary of OTHER agencies,
        /// never this client's own display name. The State message ships immediately
        /// after Handshake on the same channel (22), so the window where
        /// <see cref="LocalAgencyId"/> is set but <see cref="LocalAgencyDisplayName"/>
        /// is still empty is bounded to one Lidgren message cycle (single Update tick
        /// at worst). Stage 5.18c UI authors should treat empty display name as
        /// "still loading" / fall back to <c>"{OwningPlayerName} Space Agency"</c> if
        /// the player name is available, rather than rendering the empty string.
        /// </summary>
        public string LocalAgencyDisplayName { get; internal set; } = string.Empty;

        public string LocalAgencyOwningPlayerName { get; internal set; } = string.Empty;

        /// <summary>
        /// One-shot snapshot of every other agency known at this player's connect
        /// time. <see cref="AgencyHandshakeMsgData"/>'s XML calls out that late-joining
        /// agencies do NOT show up here — the future Stage 5.18c
        /// <c>AgencyVisibilityMsgData</c> fills that gap with incremental updates.
        /// Keyed by agency id; values carry the public summary (id + owner +
        /// display name, no scalars per spec §10 Q1).
        ///
        /// **5.18c UI author note.** A vessel whose <c>OwningAgencyId</c> is NOT a
        /// key in this dictionary is owned by an agency that came online AFTER this
        /// player connected. Until 5.18c's incremental visibility message lands,
        /// render those as <c>"Unknown Agency"</c> (or similar fallback) — never
        /// NRE on the missing dictionary entry, and never expose Guid-as-string to
        /// the player as a display label.
        ///
        /// **Thread:** populated from the Unity-thread message handler. Readers
        /// (5.18c UI) should also run on the Unity thread; if cross-thread reads are
        /// ever needed, swap to <c>ConcurrentDictionary</c> here.
        /// </summary>
        public Dictionary<Guid, AgencyInfo> OtherAgencies { get; } =
            new Dictionary<Guid, AgencyInfo>();

        /// <summary>
        /// vessel-id → owning-agency-id mirror, populated from incoming VesselProto
        /// ConfigNodes at deserialize time (Stage 5.18b —
        /// <see cref="VesselProtoSys.VesselProto.CreateProtoVessel"/> via
        /// <see cref="AgencyMembership.RecordOwnership"/>). Used by future 5.18c UI
        /// tracking-station labels + 5.18d recovery-economy / transfer-agency
        /// guards to resolve "who owns this vessel" without re-parsing wire bytes
        /// or asking the server. The authoritative source is the server's
        /// <c>Vessel.OwningAgencyId</c>
        /// (<c>Server/System/Vessel/Classes/Vessel.cs</c>); this is the client-side
        /// mirror, fed by the same wire field that <c>VesselDataUpdater</c>'s
        /// first-sight stamp (5.16b) writes.
        ///
        /// <para><b>Two-state sentinel.</b> Consumers MUST distinguish these:
        /// <list type="bullet">
        ///   <item><b>Vessel-id absent from dict:</b> ownership unknown — the
        ///         vessel proto hasn't arrived yet, the wire payload was
        ///         malformed, or the vessel was loaded from a local save without
        ///         ever going through a wire round (rare under LMP's
        ///         server-authoritative model; VesselSync at connect-time covers
        ///         the common case). The local player's own freshly-launched
        ///         vessels are also absent until the first server VesselSync
        ///         round-trip stamps and re-sends them. <b>Reconnect gap:</b>
        ///         <see cref="OnDisabled"/> clears the registry on disconnect;
        ///         on reconnect, <c>HandleVesselsSync</c> only ships vessels
        ///         the client doesn't already have (<c>Server/Message/
        ///         VesselMsgReader.cs:202-230</c>), so vessels that survived
        ///         the disconnect in <see cref="FlightGlobals.Vessels"/> will
        ///         sit at dict-miss until the next periodic owner-relay (which
        ///         may be "never" for parked vessels of offline players). The
        ///         clean fix is a force-full-sync on reconnect, deferred to
        ///         Stage 5.18d when economy guards make this gap user-visible.
        ///         UI should render as "loading" or "unknown"; 5.18d economy
        ///         guards SHOULD adopt a "deny under gate=on for absent
        ///         entries" policy as defense-in-depth (an absent entry
        ///         post-connect is a hazard, not a benign blank) and SHOULD
        ///         emit an operator-visible diagnostic message — a silent
        ///         refusal is UX-hostile when the underlying cause (server
        ///         doesn't know about this vessel, or VesselSync didn't
        ///         repopulate it) is invisible to the player.</item>
        ///   <item><b>Vessel-id present, value = <see cref="Guid.Empty"/>:</b>
        ///         Unassigned (spec §10 Q3 pre-0.31 sentinel). Any agency may
        ///         interact pending Stage 5.18d <c>transferagency</c> admin
        ///         flow. UI should render as "Unassigned."</item>
        ///   <item><b>Vessel-id present, value = real Guid:</b> claimed by that
        ///         agency. Cross-reference with <see cref="OtherAgencies"/> for
        ///         the display name; a missing key here means the owning agency
        ///         came online AFTER this client connected — render as "Unknown
        ///         Agency" until 5.18c's incremental visibility wire arrives.</item>
        /// </list></para>
        ///
        /// <para><b>Relay-safety.</b> Writes from the relay path go through
        /// <see cref="AgencyMembership.RecordOwnership"/>, which preserves a known
        /// real agency id when an incoming wire payload has no
        /// <c>lmpOwningAgency</c> field (parses to <see cref="Guid.Empty"/>). This
        /// is mandatory because the server's relay path forwards the original
        /// sender bytes (which KSP's <c>BackupVessel</c>/<c>protoVessel.Save</c>
        /// strips on every local-owner resend), so without the preservation rule
        /// every periodic resend would clobber peer-side ownership state. The
        /// authoritative write path (VesselSync replies serialised from
        /// <c>GetVesselInConfigNodeFormat</c>) DOES carry the field, so initial
        /// connect + scene-load sync populates correctly; the preservation rule
        /// guards the subsequent steady state. See
        /// <c>Server/Message/VesselMsgReader.cs:188-198</c> for the server-side
        /// relay-vs-store contract this mirror honours.</para>
        ///
        /// <para><b>Authoritative-write path (Stage 5.18d).</b> Server-pushed
        /// mutations — the <c>AgencyVisibilityMsgData</c> handler for
        /// <c>transferagency</c> X→Y and <c>deleteagency</c> cascade-to-Unassigned
        /// — MUST route through <see cref="AgencyMembership.ForceRecordOwnership"/>
        /// instead of <see cref="AgencyMembership.RecordOwnership"/>. The Force
        /// helper bypasses the preservation rule above so a legitimate authoritative
        /// demotion to <see cref="Guid.Empty"/> actually lands (otherwise the
        /// preservation rule would silently absorb the demotion and leave peers
        /// seeing a stale "owned by deleted agency" stamp). See that method's XML
        /// for the full call-site routing rules.</para>
        ///
        /// <para><b>Thread.</b> Populated from the Unity-thread vessel-proto drain
        /// (<see cref="VesselProtoSys.VesselProtoSystem"/>'s <c>CheckVesselsToLoad</c>
        /// coroutine). Future 5.18c <c>AgencyVisibilityMsgData</c> updates may write
        /// from the network thread (before the Unity-thread switch in the message
        /// handler), so this is <see cref="ConcurrentDictionary{TKey, TValue}"/> from
        /// the start — readers don't need any switch-discipline.</para>
        ///
        /// <para><b>Cross-dict thread discipline.</b> The 5.18c UI render path
        /// will read this registry plus <see cref="OtherAgencies"/> (a non-thread-
        /// safe <see cref="Dictionary{TKey, TValue}"/>). Both reads must happen on
        /// the Unity thread today. When the future 5.18c <c>AgencyVisibilityMsgData</c>
        /// adds incremental writes that may originate on the network thread,
        /// <see cref="OtherAgencies"/> must be promoted to
        /// <see cref="ConcurrentDictionary{TKey, TValue}"/> in lockstep with this
        /// one; do not let the two dicts diverge on thread-safety.</para>
        ///
        /// <para><b>Eviction.</b> No mid-session eviction; entries persist until
        /// disconnect (<see cref="OnDisabled"/>). Memory cost is two Guids per
        /// vessel (~48 bytes); even a long session with thousands of vessels stays
        /// negligible. Vessel-removal eviction was deliberately not wired in —
        /// stale entries are harmless because consumers always cross-check against
        /// KSP's <see cref="FlightGlobals.Vessels"/> (vessel-id reuse is precluded
        /// by KSP's GUID minting), and the extra coupling into <c>VesselRemoveSys</c>
        /// isn't justified.</para>
        /// </summary>
        public ConcurrentDictionary<Guid, Guid> VesselOwnership { get; } =
            new ConcurrentDictionary<Guid, Guid>();

        /// <summary>
        /// Last <see cref="AgencyCreateReplyMsgData"/>.Success flag — true on first
        /// load (until the user submits anything) so the AgencyCreateWindow doesn't
        /// render a misleading error banner before the user has tried to rename.
        /// Set by <see cref="AgencyMessageHandler.HandleCreateReply"/>; read by the
        /// Stage 5.18c <c>AgencyCreateWindow</c> to decide whether to render
        /// <see cref="LastCreateReplyReason"/> as a rejection message or a success
        /// confirmation.
        /// </summary>
        public bool LastCreateReplySuccess { get; internal set; } = true;

        /// <summary>
        /// Last <see cref="AgencyCreateReplyMsgData"/>.Reason payload (server-supplied
        /// rejection reason on failure, may be empty on success). Read by the Stage
        /// 5.18c <c>AgencyCreateWindow</c> to surface the server's verdict to the
        /// player after a rename submit.
        /// </summary>
        public string LastCreateReplyReason { get; internal set; } = string.Empty;

        /// <summary>
        /// Convenience accessor over <see cref="VesselOwnership"/>. Returns true if
        /// the registry has any entry for <paramref name="vesselId"/>; false if the
        /// vessel id is absent. The out-param is set to <see cref="Guid.Empty"/> on
        /// miss for caller convenience. <b>A HIT with <paramref name="agencyId"/>
        /// = <see cref="Guid.Empty"/> means "Unassigned" (pre-0.31 sentinel), not
        /// "unknown"</b> — see <see cref="VesselOwnership"/> XML for the
        /// two-state distinction.
        /// </summary>
        public bool TryGetOwningAgency(Guid vesselId, out Guid agencyId)
        {
            return VesselOwnership.TryGetValue(vesselId, out agencyId);
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();

            // Clear local state on disconnect so reconnect doesn't see stale identity.
            // The MessageSystem base re-creates the IncomingMessages queue so any
            // pending agency messages from the old connection are dropped.
            // VesselOwnership count is logged so operators have visibility on
            // reconnect repopulation (see VesselOwnership XML reconnect-gap note —
            // a smaller-than-expected post-reconnect registry indicates VesselSync
            // didn't cover all previously-known vessels, which 5.18d economy
            // guards will surface as deny-on-absent).
            var clearedOwnerships = VesselOwnership.Count;
            LocalAgencyId = Guid.Empty;
            LocalAgencyDisplayName = string.Empty;
            LocalAgencyOwningPlayerName = string.Empty;
            OtherAgencies.Clear();
            VesselOwnership.Clear();
            LastCreateReplySuccess = true;
            LastCreateReplyReason = string.Empty;

            if (clearedOwnerships > 0)
                LunaLog.Log($"[Agency]: Cleared {clearedOwnerships} vessel-ownership entries on disconnect.");
        }
    }
}
