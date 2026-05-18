using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Log;
using Server.System.Scenario;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17c — server-side per-player scenario projection. Replaces the canonical
    /// shared-agency career scalars in outgoing ScenarioModule text with the requesting
    /// player's <see cref="AgencyState"/> values before the bytes leave the server. The
    /// projector lives at the wire boundary in <see cref="ScenarioSystem.SendScenarioModules"/> —
    /// the canonical <see cref="ScenarioStoreSystem.CurrentScenarios"/> dictionary is never
    /// mutated by projection, only its serialized snapshot. This means concurrent
    /// <c>Share*</c> writers (Stage 5.17b future) and concurrent peer broadcasts continue
    /// to operate on the shared career state; projection is a read-side rewrite.
    ///
    /// Spec §5 (Career-data projection strategy) + §6 (write-path-only Harmony patching)
    /// + spec §10 Q1 (PrivateAgencyResources=true — Funds/Science/Reputation never leak
    /// across agencies on the wire). The Q5 audit hybrid: server-side projection alone
    /// covers Funds/Science/Reputation, deleting the ~83-site client-Harmony read-path
    /// work the original spec assumed. Tech tree / contracts / facilities project in
    /// later stages (Stage 5.17b/5.17d).
    ///
    /// Scope today: three KSP career root-level keys:
    /// <list type="bullet">
    ///   <item><c>funds</c> in the <c>Funding</c> scenario module.</item>
    ///   <item><c>sci</c> in the <c>ResearchAndDevelopment</c> scenario module.</item>
    ///   <item><c>rep</c> in the <c>Reputation</c> scenario module.</item>
    /// </list>
    /// Other scenarios pass through unchanged.
    ///
    /// Dual-mode silence (spec §11): with <see cref="AgencySystem.PerAgencyEnabled"/>
    /// false (gate off OR non-Career game mode), <see cref="ProjectForClient"/> returns
    /// the input string unchanged. The Career-only product decision (Stage 5.17e-1, spec
    /// §10) folds Sandbox and Science skips into the single <c>PerAgencyEnabled</c> check
    /// so this projector cannot fire outside Career — preserves shared-agency behaviour
    /// bit-for-bit under any mode/gate combination other than Career+PerAgencyCareer=true.
    /// </summary>
    internal static class AgencyScenarioProjector
    {
        /// <summary>Scenario module names this projector knows about. Used by the
        /// fast-path skip in <see cref="ProjectForClient"/> so non-career scenarios don't
        /// pay the regex cost.
        ///
        /// Future-extension scenarios (NOT projected here — owned by later steps):
        ///   - <c>ResearchAndDevelopment</c> tech-node child blobs and
        ///     <c>ResearchAndDevelopmentParts</c> — Stage 5.17b (per-agency tech routing).
        ///   - <c>ContractSystem</c> Active / Completed / Declined lists — Stage 5.17d
        ///     shipped the AgencyContractRouter + per-agency persistence + owner-only
        ///     AgencyContractMsgData wire echo (commit shipping with this comment
        ///     update); the matching <c>ContractSystem</c> scenario projection that
        ///     substitutes per-agency Active+Finished into the outgoing
        ///     <see cref="ScenarioStoreSystem.GetScenarioInConfigNodeFormat"/> blob
        ///     is deferred to Stage 5.18a alongside the client mirror so the
        ///     projection has an observable consumer. Until then, post-Accept
        ///     contracts reach the client through the wire echo only.
        ///   - <c>ScenarioUpgradeableFacilities</c> facility tiers — Stage 5.18b.
        ///   - <c>StrategySystem</c>, <c>KerbalRoster</c>, <c>ScenarioAchievements</c>,
        ///     <c>ScenarioDestructibles</c> — Stage 5.18b.
        /// </summary>
        private static readonly HashSet<string> CareerScenarios = new HashSet<string>(StringComparer.Ordinal)
        {
            "Funding",
            "ResearchAndDevelopment",
            "Reputation",
        };

        // Precompiled regexes — one per key. Re-using compiled instances across every
        // SendScenarioModules call avoids per-call regex compilation (round-1
        // server-systems review). RegexOptions.Multiline anchors ^ to start-of-line
        // (column 0 root-level keys); CultureInvariant keeps matching deterministic
        // under non-en thread cultures.
        private static readonly Regex FundsRegex = new Regex(
            @"^funds\s*=\s*[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex SciRegex = new Regex(
            @"^sci\s*=\s*[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex RepRegex = new Regex(
            @"^rep\s*=\s*[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Project the given serialized scenario text for the requesting client. Returns
        /// the input unchanged when (a) <see cref="AgencySystem.PerAgencyEnabled"/> is
        /// false (gate off OR non-Career game mode — Stage 5.17e-1, spec §10), (b) the
        /// scenario is not one we project, (c) the client has no agency registered, or
        /// (d) the agency state has been removed mid-projection. Otherwise returns a
        /// fresh string with the corresponding career scalar overwritten.
        ///
        /// The serialized input must be the bare-key-value-pair form
        /// <see cref="ScenarioStoreSystem.GetScenarioInConfigNodeFormat"/> produces (no
        /// outer braces). Indentation matters: root-level keys appear at column 0 with
        /// no leading whitespace, child-node values are tab-indented — the regex anchors
        /// to start-of-line + key-at-column-0 so a same-named key nested in a child node
        /// (rare for career scalars but defensively defended) does not get replaced.
        /// </summary>
        internal static string ProjectForClient(string scenarioName, string serializedText, ClientStructure client)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return serializedText;
            if (!CareerScenarios.Contains(scenarioName))
                return serializedText;
            if (client == null || string.IsNullOrEmpty(client.PlayerName))
                return serializedText;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return serializedText;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var state))
                return serializedText;

            return Project(scenarioName, serializedText, state);
        }

        /// <summary>
        /// Pure-helper overload that operates on a known <see cref="AgencyState"/>. Lets
        /// ServerTest pin the regex / format behaviour without bringing up the full client +
        /// AgencySystem registry. Returns the input unchanged for unknown scenario names
        /// or null agency state.
        /// </summary>
        internal static string Project(string scenarioName, string serializedText, AgencyState targetAgency)
        {
            if (targetAgency == null || serializedText == null)
                return serializedText;

            switch (scenarioName)
            {
                case "Funding":
                    return ReplaceRootValue(serializedText, FundsRegex, "funds", targetAgency.Funds);
                case "ResearchAndDevelopment":
                    // [Stage 5.17e-4] Two-pass projection. First replace the `sci` root
                    // scalar via the existing 5.17c regex; then splice per-agency Tech
                    // child nodes via ConfigNode mutation. Both passes are necessary —
                    // without the scalar pass the player's science total reverts to the
                    // shared value; without the Tech splice their unlocked nodes
                    // disappear on the next scene-load (Stage 5.17e-4 review caught the
                    // bug pre-ship). Each agency sees ONLY their own unlocked techs;
                    // the shared scenario's pre-existing Tech entries are stripped
                    // out so an upgrade-in-place universe doesn't bleed its accumulated
                    // shared tree into every fresh per-agency client.
                    var sciReplaced = ReplaceRootValue(serializedText, SciRegex, "sci", targetAgency.Science);
                    return SpliceAgencyTechIntoResearchAndDevelopment(sciReplaced, targetAgency);
                case "Reputation":
                    return ReplaceRootValue(serializedText, RepRegex, "rep", targetAgency.Reputation);
                default:
                    return serializedText;
            }
        }

        /// <summary>
        /// [Stage 5.17e-4] Strips all <c>Tech</c> child nodes from the
        /// <c>ResearchAndDevelopment</c> scenario text and re-adds one
        /// <c>Tech { ... }</c> child per <see cref="AgencyState.TechNodes"/> entry.
        /// Round-trips the scenario through <see cref="ConfigNode"/> so the splice
        /// preserves all OTHER child nodes (e.g. <c>ResearchAndDevelopmentParts</c>
        /// when 5.17e-5 adds part-purchase projection) and root scalars (sci was
        /// already replaced by the regex pass before this is called).
        ///
        /// **Per-entry exception isolation.** A malformed stored payload (operator
        /// hand-edit; future schema drift) is logged and skipped — same shape as
        /// <see cref="AgencyContractRouter"/>'s per-contract isolation. The agency
        /// is missing that one tech node from the projection but the rest of the
        /// scene-load proceeds normally; the player sees an incomplete tree rather
        /// than a hung connect.
        ///
        /// **Why ConfigNode round-trip rather than regex.** Tech entries are
        /// nested <c>Tech { ... }</c> blocks with arbitrary inner content (part
        /// lists, prereq lists, mod-extension fields). Brace-aware mutation needs
        /// a real parser. The round-trip cost is paid per <c>SendScenarioModules</c>
        /// per client per scene-load — meaningfully more expensive than the regex
        /// scalar replacement but still cheap (handshake + scene-load are not high-
        /// frequency events). If telemetry shows the round-trip becomes a hotspot,
        /// the natural optimization is a per-agency cached projection invalidated
        /// on <see cref="AgencyState.TechNodes"/> mutation.
        /// </summary>
        private static string SpliceAgencyTechIntoResearchAndDevelopment(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;

            ConfigNode node;
            try
            {
                node = new ConfigNode(scenarioText) { Name = "ResearchAndDevelopment" };
            }
            catch (Exception e)
            {
                // Parse failure: fall back to the (sci-replaced) text unchanged.
                // The player gets the shared tree on scene-load — a regression vs
                // the desired projection but better than failing the whole handshake.
                // [Round-2 review] LOG the fall-through so the operator has a
                // diagnostic — silent regression is exactly the leak this projector
                // exists to prevent. Once-per-send under bug conditions; not flood-
                // worthy.
                LunaLog.Error(
                    $"[fix:per-agency-career] ResearchAndDevelopment projection parse failed for agency {targetAgency.AgencyId:N}; " +
                    $"falling back to shared scenario text (player will see shared tech tree on next scene-load): " +
                    $"{e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Remove ALL existing Tech child nodes. Even if the agency has zero
            // unlocked techs (fresh agency on an upgrade-in-place universe), we
            // strip the shared tree so the player doesn't inherit it as their own.
            // .ToArray() snapshots the enumeration so RemoveNode during iteration
            // doesn't invalidate.
            foreach (var existing in node.GetNodes("Tech").ToArray())
                node.RemoveNode(existing.Value);

            // Snapshot TechNodes under the per-agency lock so a concurrent router
            // invocation (mutates the Dictionary on the same Lidgren receive thread
            // today; cross-thread tomorrow when admin commands land in 5.18d) can't
            // tear our iteration. The lock window is brief (memcpy of the values
            // list) — no I/O, no parse — so it can't deadlock with the router's
            // longer write-then-save window. AgencyState.cs TechNodes XML pins the
            // read-also-needs-lock contract.
            AgencyTechNodeEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                snapshot = targetAgency.TechNodes.Values.ToArray();
            }

            // Add per-agency Tech child nodes. The stored Data field contains the
            // decompressed wire payload (the same brace-stripped ConfigNode-format
            // text the client sent originally); we re-parse + brace-strip via the
            // same ParseClientConfigNode helper the BUG-025 path uses, so any
            // future change to KSP's wire format gets the same treatment in both
            // routing and projection.
            foreach (var entry in snapshot)
            {
                if (entry == null || entry.Data == null || entry.NumBytes <= 0)
                    continue;
                try
                {
                    var techNode = ScenarioDataUpdater.ParseClientConfigNode(entry.Data, entry.NumBytes, "Tech");
                    if (techNode.IsEmpty())
                        continue;
                    node.AddNode(techNode);
                }
                catch (Exception)
                {
                    // Per-entry isolation — drop this tech, keep the rest of the
                    // scenario. An operator-friendly diagnostic would help, but the
                    // projector runs per-send-per-client and a malformed entry
                    // would otherwise spam the log; the entry-level corruption is
                    // already visible via AgencyState.Parse's matching warning.
                }
            }

            return node.ToString();
        }

        /// <summary>
        /// Replaces the FIRST occurrence of a root-level <c>{key} = ...</c> line with the
        /// given new value. "Root-level" means column-0 (no leading whitespace); child-node
        /// keys are tab-indented and therefore not matched. Returns the input unchanged
        /// when the key is not present at root — defensive against malformed or trimmed
        /// scenario text. <see cref="ScenarioFundsDataUpdater"/> and friends already write
        /// the values using <see cref="CultureInfo.InvariantCulture"/>, so we do the same
        /// here for round-trip-stability with the canonical store.
        ///
        /// <para>The <c>count: 1</c> on <see cref="Regex.Replace(string, string, int)"/> is
        /// deliberate: scenarios should have at most ONE root-level instance of any career
        /// scalar (the canonical writer <see cref="ScenarioFundsDataUpdater"/> uses
        /// <c>ConfigNode.UpdateValue</c> which is single-keyed). If a malformed scenario
        /// somehow has two root-level <c>funds</c> lines, replacing only the first leaves
        /// the second as a stale value — preferred over the alternative of mass-replacing
        /// which could over-write into corrupted child-node content.</para>
        /// </summary>
        private static string ReplaceRootValue(string serializedText, Regex pattern, string key, double newValue)
        {
            var replacement = $"{key} = {newValue.ToString(CultureInfo.InvariantCulture)}";
            return pattern.Replace(serializedText, replacement, count: 1);
        }
    }
}
