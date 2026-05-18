using LmpClient.Base;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using System;
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

        protected override void OnDisabled()
        {
            base.OnDisabled();

            // Clear local state on disconnect so reconnect doesn't see stale identity.
            // The MessageSystem base re-creates the IncomingMessages queue so any
            // pending agency messages from the old connection are dropped.
            LocalAgencyId = Guid.Empty;
            LocalAgencyDisplayName = string.Empty;
            LocalAgencyOwningPlayerName = string.Empty;
            OtherAgencies.Clear();
        }
    }
}
