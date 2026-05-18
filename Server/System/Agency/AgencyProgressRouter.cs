using LmpCommon.Message.Data.ShareProgress;
using Server.Client;
using Server.Log;
using System;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17e-6 — per-agency routing for the three remaining
    /// non-R&amp;D career surfaces:
    /// <list type="bullet">
    ///   <item><b>Strategy</b> (Mission Control's economic strategies) — splices
    ///        into the <c>StrategySystem</c> scenario as <c>STRATEGY</c> child
    ///        nodes under <c>STRATEGIES</c>.</item>
    ///   <item><b>Achievement / Progress tracking</b> (world firsts) — splices
    ///        into the <c>ProgressTracking</c> scenario as named child nodes
    ///        under <c>Progress</c>.</item>
    ///   <item><b>Facility upgrade levels</b> (Launchpad / VAB / SPH tiers) —
    ///        overrides matching facility nodes' <c>lvl</c> values in the
    ///        <c>ScenarioUpgradeableFacilities</c> scenario; unmentioned
    ///        facilities keep the shared scenario's default.</item>
    /// </list>
    /// Same gate-then-lock-then-mutate-then-save pattern as
    /// <see cref="AgencyResearchRouter"/>. Returns true when handled (caller
    /// must NOT relay/write shared); returns false on gate-off / non-Career /
    /// missing agency / null inputs so the caller runs the unchanged legacy
    /// shared-agency path.
    ///
    /// **Spec §2 product decision.** Strategies, world firsts, and facility
    /// tiers are all explicitly per-agency: each agency runs their own Mission
    /// Control / progression history / KSC tier set. Operator workflow remains
    /// the same fresh-start-only path for upgrade-in-place universes (see
    /// <see cref="AgencySystem.LoadExistingAgencies"/>'s WarnAbout* diagnostics).
    /// </summary>
    public static class AgencyProgressRouter
    {
        public static bool TryRouteStrategy(ClientStructure client, ShareProgressStrategyMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled) return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null || msg.Strategy == null) return false;
            var name = msg.Strategy.Name;
            if (string.IsNullOrEmpty(name)) return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId)) return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency)) return false;

            var dataLen = Math.Min(msg.Strategy.NumBytes, msg.Strategy.Data?.Length ?? 0);
            var dataCopy = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
            if (dataLen > 0) Buffer.BlockCopy(msg.Strategy.Data, 0, dataCopy, 0, dataLen);

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                // Upsert (matches shared writer's ReplaceNode-on-match behavior).
                agency.Strategies[name] = new AgencyStrategyEntry
                {
                    StrategyName = name,
                    Data = dataCopy,
                    NumBytes = dataLen,
                };
                AgencySystem.SaveAgency(agencyId);
            }

            LunaLog.Normal($"[fix:per-agency-career] Routed strategy {name} into agency {agencyId:N} for {client.PlayerName}");
            return true;
        }

        public static bool TryRouteAchievement(ClientStructure client, ShareProgressAchievementsMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled) return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null) return false;
            if (string.IsNullOrEmpty(msg.Id)) return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId)) return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency)) return false;

            var dataLen = Math.Min(msg.NumBytes, msg.Data?.Length ?? 0);
            var dataCopy = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
            if (dataLen > 0) Buffer.BlockCopy(msg.Data, 0, dataCopy, 0, dataLen);

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                agency.Achievements[msg.Id] = new AgencyAchievementEntry
                {
                    Id = msg.Id,
                    Data = dataCopy,
                    NumBytes = dataLen,
                };
                AgencySystem.SaveAgency(agencyId);
            }

            LunaLog.Normal($"[fix:per-agency-career] Routed achievement {msg.Id} into agency {agencyId:N} for {client.PlayerName}");
            return true;
        }

        public static bool TryRouteFacilityUpgrade(ClientStructure client, ShareProgressFacilityUpgradeMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled) return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null) return false;
            if (string.IsNullOrEmpty(msg.FacilityId)) return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId)) return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency)) return false;

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                agency.FacilityLevels[msg.FacilityId] = msg.NormLevel;
                AgencySystem.SaveAgency(agencyId);
            }

            LunaLog.Normal($"[fix:per-agency-career] Routed facility upgrade {msg.FacilityId}->{msg.NormLevel} into agency {agencyId:N} for {client.PlayerName}");
            return true;
        }
    }
}
