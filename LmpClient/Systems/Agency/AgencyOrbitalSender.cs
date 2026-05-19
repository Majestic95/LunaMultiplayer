using LmpClient.Base;
using LmpClient.Network;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using System.Collections.Generic;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 3 Slice D-2] Client → server sender for per-agency orbital-
    /// logistics transfer state-machine echoes. Emits
    /// <see cref="AgencyOrbitalStateMsgData"/> on slot 8
    /// (<see cref="LmpCommon.Message.Types.AgencyMessageType.OrbitalState"/>)
    /// when the MKS-side
    /// <c>KolonyTools.OrbitalLogisticsTransferRequest</c> state-machine
    /// Harmony postfixes fire under
    /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled = true</c>.
    ///
    /// <para><b>Three call sites</b> (pre-spec §2.b.iii + AgencyOrbitalStateMsgData
    /// XML lines 25-39):</para>
    /// <list type="bullet">
    ///   <item><c>OrbitalLogisticsTransferRequest.DoFinalLaunchTasks</c>
    ///        postfix → emits Status=Launched. Catches BOTH the fresh-launch
    ///        path (<c>DoLaunchTasks</c> calls <c>DoFinalLaunchTasks</c> at
    ///        MKS line 700) AND the resumed-from-Returning path
    ///        (<c>Launch</c> calls <c>DoFinalLaunchTasks</c> directly at MKS
    ///        line 248 when Status is Returning). The picked anchor is
    ///        <c>DoFinalLaunchTasks</c>, NOT <c>DoLaunchTasks</c>, precisely
    ///        because the resumed-launch path would otherwise be missed.</item>
    ///   <item><c>OrbitalLogisticsTransferRequest.Abort</c> postfix → emits
    ///        Status=Returning. Stock <c>Abort</c> at MKS line 363-369 only
    ///        mutates Status when current Status == Launched, so the
    ///        postfix observes the actual transition.</item>
    ///   <item><c>OrbitalLogisticsTransferRequest.Deliver</c> postfix with
    ///        <c>ref IEnumerator __result</c> → wraps the returned
    ///        coroutine; the wrapper emits the terminal Status
    ///        (Delivered / Partial / Failed / Cancelled) on coroutine
    ///        completion. The wrap is the canonical Harmony technique for
    ///        coroutine-completion hooks because Status is a public FIELD
    ///        at MKS line 78 (no <c>set_Status</c> setter to patch). Skip
    ///        the wrap when <c>__result</c> is null (the Deliver-prefix
    ///        returned false to skip the body, so there's nothing to
    ///        observe — the skipping peer's terminal echo is the prefix's
    ///        Status=Failed mutation, not the wrap).</item>
    /// </list>
    ///
    /// <para><b>Empty-batch asymmetry contract</b>
    /// (AgencyOrbitalStateMsgData.cs lines 158-179): this sender follows
    /// the <c>SendOrbitalStateToOwner</c> shape — <b>EARLY-RETURNS on
    /// empty/null batch</b>. The state-machine postfixes never have
    /// anything to emit when empty (each postfix emits exactly one entry
    /// per transition), so the early-return is structurally correct. The
    /// separate <c>SendOrbitalCatchupTo</c> server-side path is the
    /// counterpart that DOES ship a zero-entry message to distinguish
    /// "no per-agency transfers yet" from "unsynced" on the client mirror
    /// — that path is NOT this sender's concern. Calling
    /// <see cref="SendBatch"/> with a zero-entry list is a no-op.</para>
    ///
    /// <para><b>Threading pattern</b> (pre-spec §3.e — verified against
    /// <see cref="AgencyMessageSender.SendMessage"/> + the Slice B
    /// <see cref="AgencyKolonySender"/> + the Slice C
    /// <see cref="AgencyPlanetarySender"/>):
    /// <c>TaskFactory.StartNew(() =&gt; NetworkSender.QueueOutgoingMessage(
    /// MessageFactory.CreateNew&lt;AgencyCliMsg&gt;(msg)))</c>. The offload
    /// moves message-object construction off KSP's Unity main thread
    /// (postfix fires on FixedUpdate via
    /// <c>ScenarioOrbitalLogistics.Update → ProcessTransfers</c>) so
    /// Lidgren serialization + send-queue work doesn't eat FixedUpdate
    /// frame budget. Matches the established <c>*MessageSender.cs</c>
    /// two-line convention.</para>
    ///
    /// <para><b>Why a separate sender class?</b> Per pre-spec §2.e sender
    /// naming clarification, each Phase 3 router's client outbound gets
    /// its own sender alongside <see cref="AgencyMessageSender"/>
    /// (Stage 5.18a) + <see cref="AgencyKolonySender"/> +
    /// <see cref="AgencyPlanetarySender"/>. Keeping them
    /// one-class-per-mutation-surface makes per-router profiling +
    /// future per-batch coalescing a localised change.</para>
    ///
    /// <para><b>Server trust posture.</b> The server's
    /// <c>AgencyOrbitalRouter.TryRoute</c> ignores the wire-supplied
    /// <see cref="AgencyOrbitalStateMsgData.AgencyId"/> on inbound and
    /// derives the sender's agency authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. We
    /// leave <see cref="AgencyOrbitalStateMsgData.AgencyId"/> as
    /// <c>Guid.Empty</c> on C→S — explicit signal that the field is
    /// server-derived. Same posture as the Slice B kolony / Slice C
    /// planetary senders + the Stage 5.17d
    /// <c>ShareProgressContractsMsgData</c> path.</para>
    ///
    /// <para><b>Wire-arrival order ≡ stored Status (NOT transition order).</b>
    /// The server's <c>AgencyOrbitalRouter</c> upserts by
    /// <c>TransferGuid</c>; last echo wins. Multi-echo cases — Abort
    /// (Returning) followed by Deliver-completion (Cancelled), or a
    /// rare scene-load race that re-emits an already-terminal Status —
    /// all converge correctly to the latest-arrived value at the server.
    /// Order on the wire is per-channel ReliableOrdered (channel 22), so
    /// echoes from a single client preserve their emit order. Cross-
    /// client interleaving is bounded by Slice 5.17a's cross-agency
    /// guard (other clients can't emit echoes for this transfer's
    /// destination at all) AND by KSP's single-Control-per-vessel
    /// invariant (only one client elects to execute Deliver per
    /// transfer). The stored Status reflects the latest authoritative
    /// emit, not necessarily the latest local transition observed by
    /// any one client — small window in pathological scene-load races
    /// but the projector strip + reconnect catchup converge state
    /// regardless.</para>
    ///
    /// <para><b>PayloadBytes aliasing.</b> The state-machine postfixes
    /// pass an <see cref="AgencyOrbitalTransferEntry"/> with
    /// <c>PayloadBytes</c> derived from the original MKS
    /// <c>OrbitalLogisticsTransferRequest.Save</c> ConfigNode (UTF-8
    /// bytes). The sender stores the reference directly — no defensive
    /// copy at the sender boundary because (a) Lidgren's serialization
    /// path reads-and-emits without mutating, and (b) the postfix
    /// caller constructed the array freshly per-mutation and doesn't
    /// retain it. The router (server side) copies on store per Slice
    /// D-1's <c>AgencyOrbitalRouter</c> contract.</para>
    /// </summary>
    public static class AgencyOrbitalSender
    {
        /// <summary>
        /// Sends a batch of orbital-transfer state mutations. Builds
        /// <see cref="AgencyOrbitalStateMsgData"/>, offloads to the network
        /// thread.
        /// </summary>
        /// <param name="entries">Non-null preferred; null/empty returns
        /// immediately per the empty-batch asymmetry contract documented
        /// in <see cref="AgencyOrbitalStateMsgData"/> XML lines 158-179.</param>
        public static void SendBatch(IReadOnlyList<AgencyOrbitalTransferEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            // AgencyId left at Guid.Empty intentionally — server ignores it
            // and derives the sender's agency authoritatively (see class XML).
            msgData.EntryCount = entries.Count;
            msgData.Entries = new AgencyOrbitalTransferEntry[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                msgData.Entries[i] = entries[i];

            // SystemBase exposes the shared TaskFactory used by every
            // existing *MessageSender; the static class form here references
            // it explicitly since we don't inherit from SystemBase (matches
            // the offloading contract documented in
            // AgencyMessageSender.SendMessage).
            Base.SystemBase.TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(NetworkMain.CliMsgFactory.CreateNew<AgencyCliMsg>(msgData)));
        }

        /// <summary>
        /// Single-entry overload — the most common postfix call shape.
        /// State-machine postfixes (<c>DoFinalLaunchTasks</c>,
        /// <c>Abort</c>, the Deliver-IEnumerator-wrapper postfix) each
        /// emit exactly one entry per transition; this is their natural
        /// entry point.
        /// </summary>
        public static void SendTransferStateChange(AgencyOrbitalTransferEntry entry)
        {
            if (entry == null) return;
            SendBatch(new[] { entry });
        }

        // TODO[Slice E] — when transferagency MKS-aware extension ships
        // (pre-spec §4.e), the SOURCE agency's AgencyState.OrbitalTransfers
        // dict needs a per-transfer migration helper: pop the TransferGuid
        // entry from source, push into destination, emit an
        // AgencyOrbitalStateMsgData to BOTH owners' clients (source learns
        // the removal — needs the Slice E "RemovedTransferGuids" tail
        // documented in AgencyOrbitalStateMsgData XML lines 181-209). The
        // TransferGuid stays stable because DeriveTransferGuid hashes only
        // (origin, destination, startTime, duration) — none of which the
        // transferagency command mutates. Lives on the server side in
        // AgencySystemSender, not here; client-side sender stays unchanged.
    }
}
