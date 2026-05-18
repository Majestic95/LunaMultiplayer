using LmpCommon.Message.Data.ShareProgress;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17e-5 — per-agency routing for the three secondary R&amp;D
    /// surfaces (Science subjects / part purchases / experimental parts).
    /// Sits between <see cref="ShareScienceSubjectSystem"/> /
    /// <see cref="SharePartPurchaseSystem"/> /
    /// <see cref="ShareExperimentalPartSystem"/> and the legacy
    /// shared-scenario writers. Under <see cref="AgencySystem.PerAgencyEnabled"/>:
    /// <list type="bullet">
    ///   <item>Resolves the sender's <see cref="AgencyState"/>.</item>
    ///   <item>Acquires <see cref="AgencySystem.GetAgencyLock"/>.</item>
    ///   <item>Upserts the corresponding per-agency collection
    ///        (<see cref="AgencyState.ScienceSubjects"/> /
    ///        <see cref="AgencyState.PurchasedParts"/> /
    ///        <see cref="AgencyState.ExperimentalParts"/>).</item>
    ///   <item>Calls <see cref="AgencySystem.SaveAgency"/> + returns true.</item>
    /// </list>
    /// NO peer relay; NO owner echo — <see cref="AgencyScenarioProjector"/>'s
    /// R&amp;D splice (also Stage 5.17e-5, same commit) carries the per-agency
    /// state back to the owning client on next scene-load. Returns false when
    /// gate=off / non-Career / sender has no agency, in which case the caller
    /// runs the unchanged shared-agency path.
    ///
    /// **Why one router class for three surfaces.** All three target the same
    /// <c>ResearchAndDevelopment</c> scenario (subjects splice as <c>Science</c>
    /// child nodes; purchased parts merge as <c>part = X</c> values inside
    /// per-agency Tech blocks; experimental parts splice as the <c>ExpParts</c>
    /// child node). The gate / lock / save / fall-through pattern is identical;
    /// three classes would duplicate ~30 lines each with no per-surface
    /// behaviour difference.
    /// </summary>
    public static class AgencyResearchRouter
    {
        public static bool TryRouteScienceSubject(ClientStructure client, ShareProgressScienceSubjectMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null || msg.ScienceSubject == null)
                return false;
            var subjectId = msg.ScienceSubject.Id;
            if (string.IsNullOrEmpty(subjectId))
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            // Defensive bytes copy: same shape as AgencyTechRouter.
            var dataLen = Math.Min(msg.ScienceSubject.NumBytes, msg.ScienceSubject.Data?.Length ?? 0);
            var dataCopy = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
            if (dataLen > 0)
                Buffer.BlockCopy(msg.ScienceSubject.Data, 0, dataCopy, 0, dataLen);

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                // Upsert — KSP fires ScienceSubject updates on re-completion (e.g.
                // running the same experiment again with more bonus science).
                // Latest-wins matches the shared-scenario writer's ReplaceNode behavior.
                agency.ScienceSubjects[subjectId] = new AgencyScienceSubjectEntry
                {
                    SubjectId = subjectId,
                    Data = dataCopy,
                    NumBytes = dataLen,
                };
                AgencySystem.SaveAgency(agencyId);
            }

            LunaLog.Normal($"[fix:per-agency-career] Routed science subject {subjectId} into agency {agencyId:N} for {client.PlayerName}");
            return true;
        }

        public static bool TryRoutePartPurchase(ClientStructure client, ShareProgressPartPurchaseMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (string.IsNullOrEmpty(msg.TechId) || string.IsNullOrEmpty(msg.PartName))
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                // [Round-1 review MUST FIX] Reject orphan part purchases where the
                // parent Tech isn't in this agency's per-agency tree. Matches the
                // shared-scenario writer's behavior in
                // ScenarioPartPurchaseDataUpdater.WritePartPurchaseDataToFile —
                // which does `techNodes.FirstOrDefault(...) → if (specificTechNode
                // != null)` and silently no-ops on a miss. Without this check, the
                // router would store an orphan PurchasedParts entry that the
                // projector splices NOWHERE (parts live inside Tech blocks; no
                // matching Tech, nowhere to attach) — the player would silently
                // lose the part inventory at next scene-load. Returning true (not
                // false) preserves dual-mode behavior under gate=on: never relay
                // to peers, even when the purchase is dropped.
                if (!agency.TechNodes.ContainsKey(msg.TechId))
                {
                    LunaLog.Warning($"[fix:per-agency-career] Dropping part purchase {msg.PartName} for {client.PlayerName}: parent tech {msg.TechId} not unlocked in agency {agencyId:N}. Matches shared-scenario writer's no-op on missing-Tech.");
                    return true;
                }

                if (!agency.PurchasedParts.TryGetValue(msg.TechId, out var partSet))
                {
                    partSet = new HashSet<string>(StringComparer.Ordinal);
                    agency.PurchasedParts[msg.TechId] = partSet;
                }
                // HashSet.Add returns false on duplicate — no SaveAgency cost when
                // the client re-broadcasts a purchase already known to the agency.
                if (!partSet.Add(msg.PartName))
                    return true;
                AgencySystem.SaveAgency(agencyId);
            }

            LunaLog.Normal($"[fix:per-agency-career] Routed part purchase {msg.PartName} (under {msg.TechId}) into agency {agencyId:N} for {client.PlayerName}");
            return true;
        }

        public static bool TryRouteExperimentalPart(ClientStructure client, ShareProgressExperimentalPartMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (string.IsNullOrEmpty(msg.PartName))
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                // Count==0 means remove (matches shared-scenario writer's behavior).
                if (msg.Count <= 0)
                {
                    if (!agency.ExperimentalParts.Remove(msg.PartName))
                        return true; // already absent — no save needed
                }
                else
                {
                    agency.ExperimentalParts[msg.PartName] = msg.Count;
                }
                AgencySystem.SaveAgency(agencyId);
            }

            LunaLog.Normal($"[fix:per-agency-career] Routed experimental part {msg.PartName} count={msg.Count} into agency {agencyId:N} for {client.PlayerName}");
            return true;
        }
    }
}
