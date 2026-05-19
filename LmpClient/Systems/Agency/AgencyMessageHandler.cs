using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Systems.ShareCareer;
using LmpClient.Systems.ShareContracts;
using LmpClient.Systems.ShareFunds;
using LmpClient.Systems.ShareReputation;
using LmpClient.Systems.ShareScience;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Inbound dispatcher for the four per-agency S→C subtypes (Stage 5.18a).
    /// Routes <see cref="AgencyMessageType"/> entries to the appropriate apply path:
    /// <list type="bullet">
    ///   <item>Handshake → populate <see cref="AgencySystem.LocalAgencyId"/> +
    ///         <see cref="AgencySystem.OtherAgencies"/> snapshot.</item>
    ///   <item>State → defensive filter against LocalAgencyId; apply Funds/Sci/Rep
    ///         via the bracket-wrapped <c>Set*WithoutTriggeringEvent</c> helpers.</item>
    ///   <item>Contract → defensive filter; route through
    ///         <see cref="ShareContractsMessageHandler.ApplyContractBatch"/> (queued
    ///         via <see cref="ShareCareerSystem"/> so it waits for
    ///         <c>ContractSystem.Instance</c>).</item>
    ///   <item>CreateReply → on success, update
    ///         <see cref="AgencySystem.LocalAgencyDisplayName"/>; on failure, log
    ///         the reason for the 5.18c UI to surface to the player.</item>
    /// </list>
    /// </summary>
    public class AgencyMessageHandler : SubSystem<AgencySystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } =
            new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is AgencyBaseMsgData msgData)) return;

            switch (msgData.AgencyMessageType)
            {
                case AgencyMessageType.Handshake:
                    HandleHandshake((AgencyHandshakeMsgData)msgData);
                    break;
                case AgencyMessageType.State:
                    HandleState((AgencyStateMsgData)msgData);
                    break;
                case AgencyMessageType.Contract:
                    HandleContract((AgencyContractMsgData)msgData);
                    break;
                case AgencyMessageType.Visibility:
                    HandleVisibility((AgencyVisibilityMsgData)msgData);
                    break;
                case AgencyMessageType.CreateReply:
                    HandleCreateReply((AgencyCreateReplyMsgData)msgData);
                    break;
                case AgencyMessageType.CreateRequest:
                    // CreateRequest is C→S only; arriving inbound means a misrouted
                    // peer message or wire corruption. Drop quietly — the message
                    // family's dictionary entry on the Srv side exists only for
                    // wire-symmetry (BUG-010 rule) and the server's AgencyMsgReader
                    // already log-drops it.
                    LunaLog.LogWarning("[Agency]: Dropping inbound CreateRequest (S→C-illegal subtype).");
                    break;
                case AgencyMessageType.KolonyState:
                    // [Phase 3 Slice B] Stub forward-compat. The kolony client mirror
                    // is deferred to a 5.18-series follow-up; the catch-up path on the
                    // server (HandshakeSystem) sends an AgencyKolonyStateMsgData
                    // unconditionally under gate=on (even an empty dict, by design —
                    // see AgencyKolonyStateMsgData XML). Without this case the
                    // existing 0.31-per-agency client would log "Unknown
                    // AgencyMessageType KolonyState — dropping." every connect AND
                    // every kolony mutation echo. Debug-level (not Warning) because
                    // the message is a server-known-correct push; a future Slice B+
                    // client mirror author moves real apply logic into this branch
                    // following the AgencyContractMsgData/HandleContract precedent.
                    // [Phase 3 Slice C / consumer-lens MUST FIX #4 — partial
                    // apply.] The reviewer flagged log noise on every connect
                    // (kolony + planetary stubs both fire under gate=on
                    // unconditional catchup). The right downgrade target
                    // would be LogDebug — but client-side LunaLog has no
                    // LogDebug method (only Log / LogWarning / LogError;
                    // verified at LmpClient/LunaLog.cs:43-67). Defer the
                    // proper downgrade to a follow-up that adds
                    // LunaLog.LogDebug as a sibling. Until then accept the
                    // 2-lines-per-connect log presence: it surfaces the
                    // "client mirror is deferred" status to 5.18-series
                    // testers and operators investigating per-agency MKS
                    // sync gaps, which is operationally useful information.
                    LunaLog.Log("[Agency]: KolonyState received — client mirror not yet wired (Phase 3 Slice B, 5.18-series follow-up).");
                    break;
                case AgencyMessageType.PlanetaryState:
                    // [Phase 3 Slice C] Same forward-compat stub as KolonyState.
                    // HandshakeSystem ships AgencyPlanetaryStateMsgData
                    // unconditionally under gate=on (even an empty dict) — without
                    // this case existing 0.31-per-agency clients would log
                    // "Unknown AgencyMessageType PlanetaryState" every connect.
                    // A future Slice C+ client mirror author moves real apply
                    // logic into this branch.
                    // Same LogDebug-deferral rationale as the kolony stub
                    // above (consumer-lens MUST FIX #4).
                    LunaLog.Log("[Agency]: PlanetaryState received — client mirror not yet wired (Phase 3 Slice C, 5.18-series follow-up).");
                    break;
                default:
                    LunaLog.LogWarning($"[Agency]: Unknown AgencyMessageType {msgData.AgencyMessageType} — dropping.");
                    break;
            }
        }

        #region Handlers

        private static void HandleHandshake(AgencyHandshakeMsgData data)
        {
            // Empty AssignedAgencyId is a server bug — RegisterAgency returns null
            // under gate=off, but a misconfigured dual-mode path could ship Empty.
            // Log so operators/testers see the symptom in KSP.log instead of "client
            // silently goes zombie." We do NOT set LocalAgencyId to Empty here; the
            // existing default (Empty from construction or prior OnDisabled) already
            // covers that state, and re-setting would mask the prior server's value
            // on a reconnect race where the prior connection was clean.
            if (data.AssignedAgencyId == Guid.Empty)
            {
                LunaLog.LogWarning(
                    "[Agency]: Handshake received with AssignedAgencyId=Empty — " +
                    "server-side bug (likely misconfigured dual-mode). Ignoring.");
                return;
            }

            // Last-Handshake-wins by design (see AgencyHandshakeMsgData XML — this
            // message is a one-shot snapshot at the receiving player's connect time).
            // A second Handshake on the same connection would be a server bug, but the
            // semantics here are still well-defined: re-populate the registry from the
            // newer snapshot. Reset DisplayName + OwningPlayerName at the same time
            // so the brief Handshake→State window doesn't expose the PREVIOUS
            // connection's values to any reader. The values are repopulated by the
            // immediately-following AgencyStateMsgData on channel 22 (Lidgren per-
            // channel ordering guarantees the State arrives next).
            System.LocalAgencyId = data.AssignedAgencyId;
            System.LocalAgencyDisplayName = string.Empty;
            System.LocalAgencyOwningPlayerName = string.Empty;
            System.OtherAgencies.Clear();
            for (var i = 0; i < data.OtherAgencyCount; i++)
            {
                var info = data.OtherAgencies[i];
                if (info == null) continue;
                // Don't overwrite the local agency entry if (somehow) the server
                // included it in its own OtherAgencies list — the canonical local
                // identity lives on LocalAgencyId / LocalAgencyDisplayName /
                // LocalAgencyOwningPlayerName, set by the AgencyState that
                // immediately follows this Handshake on the same channel.
                if (info.AgencyId == data.AssignedAgencyId) continue;
                // De-duplicate by id — a server bug that listed the same agency twice
                // would otherwise produce a "key already exists" exception on the
                // second add. Last entry wins.
                System.OtherAgencies[info.AgencyId] = info;
            }

            LunaLog.Log(
                $"[Agency]: Handshake received — LocalAgencyId={data.AssignedAgencyId:N}, " +
                $"{System.OtherAgencies.Count} other agency/agencies known.");
        }

        private static void HandleState(AgencyStateMsgData data)
        {
            if (!AgencyMembership.IsForLocalAgency(System.LocalAgencyId, data.AgencyId))
            {
                // Privacy + defence-in-depth: the server's owner-only contract means
                // we should never see another agency's full state. If we do, drop
                // without applying — applying would corrupt the local KSP singletons
                // with values from someone else's agency.
                LunaLog.LogWarning(
                    $"[Agency]: Dropping State for non-local agency {data.AgencyId:N} " +
                    $"(local={System.LocalAgencyId:N}).");
                return;
            }

            // Update the identity bookkeeping. The State message is the canonical
            // source for LocalAgencyDisplayName + LocalAgencyOwningPlayerName — the
            // Handshake message only carries the id (and a privacy-summary list of
            // OTHER agencies, never this client's own).
            System.LocalAgencyDisplayName = data.DisplayName ?? string.Empty;
            System.LocalAgencyOwningPlayerName = data.OwningPlayerName ?? string.Empty;

            // Capture the scalars on the message-handling thread so the queued action
            // sees the values at receive-time, not whatever the message data field
            // might be at apply-time (defensive against message recycle / reuse).
            var funds = data.Funds;
            var science = data.Science;
            var reputation = data.Reputation;

            // ShareCareerSystem.QueueAction defers the apply until ContractSystem +
            // Funding + R&D + Reputation singletons are alive (typically post
            // SpaceCenter load). Without this gate, calling Set*WithoutTriggeringEvent
            // pre-scene NREs because Funding.Instance / R&D.Instance / Reputation.Instance
            // are null. The existing ShareFundsMessageHandler uses the exact same
            // pattern (commit f9... etc.).
            ShareCareerSystem.Singleton.QueueAction(() =>
            {
                // BUG-025 v2 bracketing is INSIDE each Set*WithoutTriggeringEvent —
                // do not double-bracket here. The helpers call StartIgnoringEvents
                // on their owning Share*System, apply the value via
                // <Singleton>.SetFunds/SetScience/SetReputation with
                // TransactionReasons.None, and StopIgnoringEvents. This is the
                // canonical no-feedback-loop path.
                ShareFundsSystem.Singleton.SetFundsWithoutTriggeringEvent(funds);
                ShareScienceSystem.Singleton.SetScienceWithoutTriggeringEvent((float)science);
                ShareReputationSystem.Singleton.SetReputationWithoutTriggeringEvent((float)reputation);
            });

            LunaLog.Log($"[Agency]: State applied — funds={funds}, sci={science}, rep={reputation}, displayName='{System.LocalAgencyDisplayName}'.");
        }

        private static void HandleContract(AgencyContractMsgData data)
        {
            if (!AgencyMembership.IsForLocalAgency(System.LocalAgencyId, data.AgencyId))
            {
                LunaLog.LogWarning(
                    $"[Agency]: Dropping Contract batch for non-local agency {data.AgencyId:N} " +
                    $"(local={System.LocalAgencyId:N}).");
                return;
            }

            // Defensive copy off the wire buffer — same pattern as the existing
            // ShareContractsMessageHandler, so a future message recycle doesn't
            // corrupt the queued apply.
            var contractInfos = ShareContractsMessageHandler.CopyContracts(data.Contracts);

            LunaLog.Log($"[Agency]: Queueing Contract batch — {contractInfos.Length} contract(s) for local agency.");

            // Ordering note: this batch enters ShareCareerSystem's single FIFO queue,
            // shared with the same queue ShareProgressContractsMsgData applies use.
            // Under gate=on the server's AgencyContractRouter (Stage 5.17d) intercepts
            // ShareProgressContractsMsgData in ShareContractsSystem and emits this
            // AgencyContractMsgData INSTEAD — so the two wire types should not
            // interleave for the same scenario in practice. The FIFO queue keeps order
            // safe regardless: if a future server bug ever interleaves them, the apply
            // order matches the receive order. Don't add a separate queue for agency
            // contracts; that would introduce a cross-queue race against the legacy
            // contract path.
            ShareCareerSystem.Singleton.QueueAction(() =>
            {
                // ApplyContractBatch handles its own bracketing on the relevant
                // Share*Systems (Contracts + Funds + Science + Reputation +
                // ExperimentalParts). Same KSP-side machinery the shared-agency
                // ShareProgressContractsMsgData path uses — correct regardless of
                // which wire envelope delivered the batch.
                ShareContractsMessageHandler.ApplyContractBatch(contractInfos);
            });
        }

        private static void HandleVisibility(AgencyVisibilityMsgData data)
        {
            // Stage 5.18d. Server-broadcast batch of vessel-ownership transitions
            // (transferagency X→Y push; deleteagency cascade demoting to Empty).
            // Authoritative — route through ForceRecordOwnership which BYPASSES
            // the relay-safety preservation rule. See AgencyMembership.cs XML for
            // the call-site contract distinguishing this path from RecordOwnership.
            //
            // No defensive IsForLocalAgency filter: this message is intentionally
            // broadcast (ownership is public state). Every connected client applies
            // every entry — Stage 5.18c UI labels and Stage 5.18d economy guards
            // need the full transition surface, not just changes affecting the
            // local agency.
            if (data == null || data.ChangeCount == 0) return;

            // Log on demote-to-Empty specifically — operators investigating "my
            // vessel went Unassigned" need a grep target in KSP.log. Non-Empty
            // transitions (X → Y or first-sight X) are routine and stay silent
            // UNLESS the local player is currently piloting the affected vessel,
            // in which case the transition will surface in the 5.18c UI label
            // mid-flight and the operator deserves an explanation in KSP.log.
            // Defers the optional LunaLog from the Stage 5.18d slice (b)
            // ForceRecordOwnership CONSIDER review finding to this consumer call
            // site, where it belongs.
            var registry = System?.VesselOwnership;
            var activeVesselId = FlightGlobals.ActiveVessel?.id ?? Guid.Empty;
            for (var i = 0; i < data.ChangeCount; i++)
            {
                var change = data.Changes[i];
                var prior = Guid.Empty;
                registry?.TryGetValue(change.VesselId, out prior);
                AgencyMembership.ForceRecordOwnership(registry, change.VesselId, change.NewOwningAgencyId);

                if (change.NewOwningAgencyId == Guid.Empty && prior != Guid.Empty)
                {
                    LunaLog.Log(
                        $"[Agency]: Visibility — vessel {change.VesselId:N} demoted from " +
                        $"agency {prior:N} to Unassigned (deleteagency cascade).");
                }
                else if (change.VesselId == activeVesselId
                    && prior != Guid.Empty
                    && change.NewOwningAgencyId != Guid.Empty
                    && prior != change.NewOwningAgencyId)
                {
                    // Operator-visible signal for the consumer-lens "active vessel
                    // transferred out from under me" case. Without this log, a player
                    // mid-mission sees their vessel's UI label change (5.18c) with no
                    // KSP.log breadcrumb explaining why.
                    LunaLog.Log(
                        $"[Agency]: Visibility — ACTIVE vessel {change.VesselId:N} " +
                        $"transferred from agency {prior:N} to {change.NewOwningAgencyId:N} (admin transferagency).");
                }
            }

            LunaLog.Log($"[Agency]: Visibility applied — {data.ChangeCount} ownership change(s).");
        }

        private static void HandleCreateReply(AgencyCreateReplyMsgData data)
        {
            // Capture the server's verdict for the AgencyCreateWindow to surface
            // (Stage 5.18c). Empty Reason on success is fine — the window only
            // renders the reason when LastCreateReplySuccess is false.
            System.LastCreateReplySuccess = data.Success;
            System.LastCreateReplyReason = data.Reason ?? string.Empty;

            if (data.Success)
            {
                // Server applied the rename; mirror it locally. The reply's AgencyId
                // is guaranteed to equal LocalAgencyId on success (see
                // AgencyCreateRequestMsgData XML — CreateRequest is a rename-on-connect,
                // not a mint), so we don't need to re-anchor LocalAgencyId here. The
                // defensive equality check is a sanity assertion: if the server ever
                // sends a Success reply with a mismatched id, that's a server bug
                // worth surfacing.
                if (data.AgencyId != System.LocalAgencyId)
                {
                    LunaLog.LogWarning(
                        $"[Agency]: CreateReply Success but AgencyId {data.AgencyId:N} " +
                        $"!= LocalAgencyId {System.LocalAgencyId:N} — server bug? " +
                        $"Accepting display name anyway.");
                }
                System.LocalAgencyDisplayName = data.DisplayName ?? string.Empty;
                LunaLog.Log($"[Agency]: Agency renamed to '{System.LocalAgencyDisplayName}'.");
            }
            else
            {
                // Failure — leave LocalAgencyDisplayName untouched (still the auto-
                // registered default or the prior accepted custom name). The window
                // reads LastCreateReplyReason to render the rejection to the user.
                LunaLog.LogWarning(
                    $"[Agency]: Rename rejected by server — Reason='{System.LastCreateReplyReason}'.");
            }
        }

        #endregion
    }
}
