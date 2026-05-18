using Server.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
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
                    return ReplaceRootValue(serializedText, SciRegex, "sci", targetAgency.Science);
                case "Reputation":
                    return ReplaceRootValue(serializedText, RepRegex, "rep", targetAgency.Reputation);
                default:
                    return serializedText;
            }
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
