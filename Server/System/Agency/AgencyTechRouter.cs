using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.System.Scenario;
using System;
using System.Globalization;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17e-4 — per-agency Technology routing. Sits between
    /// <see cref="ShareTechnologySystem.TechnologyReceived"/> and the existing
    /// shared-scenario BUG-025 dedup path. Under <see cref="AgencySystem.PerAgencyEnabled"/>
    /// the router:
    /// <list type="number">
    ///   <item>Resolves the sender's <see cref="AgencyState"/>.</item>
    ///   <item>Acquires <see cref="AgencySystem.GetAgencyLock"/>.</item>
    ///   <item>Checks <see cref="AgencyState.TechNodes"/> for the inbound
    ///        <see cref="TechNodeInfo.Id"/>. If present → per-agency duplicate
    ///        purchase race: sends <see cref="ShareProgressTechnologyRejectedMsgData"/>
    ///        owner-only with the science cost extracted from the payload, returns
    ///        <c>true</c>. The client's existing BUG-025-v2 refund handler applies
    ///        the science back to <c>ResearchAndDevelopment.Instance</c>.</item>
    ///   <item>If absent → adds an <see cref="AgencyTechNodeEntry"/> with the
    ///        decompressed wire payload, calls <see cref="AgencySystem.SaveAgency"/>,
    ///        and returns <c>true</c>. NO peer relay — under per-agency mode each
    ///        agency owns its own tech tree (spec §2 "Per-agency independent" sign-off).
    ///        NO owner echo — the client already locally unlocked the tech before
    ///        broadcasting; the server-side persist is what makes the unlock survive
    ///        scene-loads via the <see cref="AgencyScenarioProjector"/> R&amp;D Tech
    ///        splice (also Stage 5.17e-4, this same commit). Returning owners receive
    ///        their tree through the scenario projection at handshake, not via a wire
    ///        catch-up — the projector extension makes the projection IS the catch-up.</item>
    /// </list>
    /// Returns <c>true</c> when this method handled the inbound — caller must NOT
    /// then run the shared-scenario BUG-025 dedup. Returns <c>false</c> when:
    /// <list type="bullet">
    ///   <item><see cref="AgencySystem.PerAgencyEnabled"/> is false (dual-mode
    ///        silence — gate off OR non-Career game mode).</item>
    ///   <item>Client / message / TechNode null (defensive).</item>
    ///   <item>No agency mapped for the sender (HandshakeSystem auto-registers
    ///        under the gate; fall-through is safer than NRE on a registry miss).</item>
    /// </list>
    ///
    /// **Why per-agency BUG-025 matters.** The legacy shared-scenario dedup
    /// (<see cref="ScenarioDataUpdater.TryAddTechnologyAtomic"/>) blocks Alice and
    /// Bob from independently unlocking the same tech — the second purchaser is
    /// refunded as a duplicate. Under per-agency mode that's wrong: each agency
    /// runs its own independent tech tree, and "same tech in two trees" is the
    /// normal case, not the BUG-025 race. By scoping the dedup to the sender's
    /// per-agency tree, both players can unlock the same node without one
    /// silently getting refunded.
    ///
    /// **I/O cadence (round-1 review precedent from 5.17e-3).** Tech unlocks are
    /// rare (humans unlock a node every 10-30 minutes during career play), so the
    /// per-mutation <see cref="FileHandler.WriteAtomic"/> via SaveAgency is
    /// negligible. Compare to the higher-frequency Funds/Sci/Rep mutations in
    /// 5.17e-3 where the same cadence was deemed acceptable for the v1 soak.
    /// </summary>
    public static class AgencyTechRouter
    {
        /// <summary>Per-agency Technology routing. See <see cref="AgencyTechRouter"/>
        /// XML for the design + locking + privacy + BUG-025 contract.</summary>
        public static bool TryRoute(ClientStructure client, ShareProgressTechnologyMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null || msg.TechNode == null)
                return false;
            if (string.IsNullOrEmpty(msg.TechNode.Id))
                return false; // defensive — malformed inbound; let shared path log/handle
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            var techId = msg.TechNode.Id;

            // The read-check-and-write block runs under one lock acquisition so a
            // same-agency double-purchase race resolves deterministically: the
            // second arrival sees the first's add and rejects. Snapshot-inside-lock
            // for the rejection send too (5.17e-3 round-1 precedent — both reviewers
            // caught the same pattern there; apply pre-emptively here).
            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                if (agency.TechNodes.ContainsKey(techId))
                {
                    // Per-agency BUG-025: this agency already has this tech.
                    // Refund the sender; do NOT add a duplicate; do NOT relay.
                    var cost = ParseScienceCostSafe(msg.TechNode);
                    SendRejection(client, techId, cost);
                    LunaLog.Normal($"[fix:per-agency-career][fix:BUG-025] Rejected duplicate tech purchase {techId} from {client.PlayerName} (agency {agencyId:N}); refunding {cost} science");
                    return true;
                }

                // First-time purchase for this agency. Store the decompressed
                // wire bytes verbatim; the Stage 5.17e-4 AgencyScenarioProjector
                // R&D extension (same commit) splices them back into outgoing
                // ResearchAndDevelopment scenario blobs (read-path counterpart to
                // this write-path routing). Together the two halves close the
                // mid-session tech-unlock loop under per-agency mode.
                var dataLen = Math.Min(msg.TechNode.NumBytes, msg.TechNode.Data?.Length ?? 0);
                var dataCopy = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
                if (dataLen > 0)
                    Buffer.BlockCopy(msg.TechNode.Data, 0, dataCopy, 0, dataLen);

                agency.TechNodes[techId] = new AgencyTechNodeEntry
                {
                    TechId = techId,
                    Data = dataCopy,
                    NumBytes = dataLen,
                };
                AgencySystem.SaveAgency(agencyId);
            }

            // [Round-1 consumer-lens review] Bump to Normal log level so operators
            // grepping [fix:per-agency-career] see successful tech unlocks (matches
            // the rejection-path log-level, balances against the rare-event cadence
            // — humans unlock a tech every 10-30 min during career play, not flood-
            // worthy at the Debug-vs-Normal threshold).
            LunaLog.Normal($"[fix:per-agency-career] Routed tech unlock {techId} into agency {agencyId:N} for {client.PlayerName}");
            return true;
        }

        /// <summary>Reads the <c>cost</c> field from the inbound tech ConfigNode.
        /// Returns 0f on any parse failure — same shape as
        /// <see cref="ScenarioDataUpdater.TryAddTechnologyAtomic"/>'s helper.
        ///
        /// **Player-visible consequence (round-1 review).** A 0-cost rejection
        /// means the client locally deducted science before broadcasting, the
        /// server rejected as duplicate, and the refund returns ZERO — the player
        /// silently loses the science they spent on this attempted unlock. The
        /// failure surface is intentionally narrow (KSP's RDTech payload format
        /// has been stable since the BUG-025 ship; the only realistic failure path
        /// is a hostile / mod-corrupted payload). Logged at Warning level so a
        /// pattern of failures is visible in operator logs without flooding them.
        /// If a CC-installed mod produces non-conforming tech payloads in
        /// practice, the recovery path is the legacy
        /// <see cref="ScenarioDataUpdater.TryAddTechnologyAtomic"/> fall-through
        /// (deliberately not chained here today to keep the dual-mode contract
        /// clean — falling through to the legacy path under gate=on would re-
        /// activate the cross-agency leak we're closing).</summary>
        private static float ParseScienceCostSafe(TechNodeInfo techNode)
        {
            try
            {
                var receivedNode = ScenarioDataUpdater.ParseClientConfigNode(techNode.Data, techNode.NumBytes, "Tech");
                if (receivedNode.IsEmpty())
                    return 0f;
                var costValue = receivedNode.GetValue("cost");
                if (costValue == null)
                    return 0f;
                return float.TryParse(costValue.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
            }
            catch (Exception e)
            {
                LunaLog.Warning($"[fix:per-agency-career] Failed to parse science cost from tech payload (TechId={techNode?.Id}): {e.GetType().Name}: {e.Message}. Player will be refunded 0 science.");
                return 0f;
            }
        }

        /// <summary>Sends an owner-only <see cref="ShareProgressTechnologyRejectedMsgData"/>
        /// to the requester. Built inline rather than going through a sender helper
        /// because this is the one place under per-agency mode where the legacy
        /// BUG-025 rejection wire is reused — adding a SenderHelper.SendRejection
        /// would be one-call dead weight.</summary>
        private static void SendRejection(ClientStructure client, string techId, float refundScience)
        {
            var rejection = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressTechnologyRejectedMsgData>();
            rejection.TechId = techId;
            rejection.RefundScience = refundScience;
            MessageQueuer.SendToClient<ShareProgressSrvMsg>(client, rejection);
        }
    }
}
