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
            // [Stage 5.17e-6]
            "StrategySystem",
            "ProgressTracking",
            "ScenarioUpgradeableFacilities",
            // [Stage 5.18d slice (j)]
            "ContractSystem",
        };

        /// <summary>
        /// Contract states that stay in the SHARED <c>CONTRACTS</c> pool when
        /// projecting — the rest are spliced per-agency. Mirrors
        /// <see cref="AgencyContractRouter"/>'s <c>SharedScenarioStates</c>
        /// (Q6 commitment a: CC's <c>ContractPreLoader</c> draws from the
        /// shared pool's Offered + Generated entries; per-agency clients see
        /// them as available pre-Accept).
        /// </summary>
        private static readonly HashSet<string> SharedContractStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Offered",
            "Generated",
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
                // [Stage 5.17e-6] Strategy / Achievement / Facility scenarios.
                case "StrategySystem":
                    return SpliceAgencyStrategiesIntoScenario(serializedText, targetAgency);
                case "ProgressTracking":
                    return SpliceAgencyAchievementsIntoScenario(serializedText, targetAgency);
                case "ScenarioUpgradeableFacilities":
                    return OverrideFacilityLevelsInScenario(serializedText, targetAgency);
                // [Stage 5.18d slice (j)] Per-agency ContractSystem projection.
                // Keeps Offered/Generated in the shared CONTRACTS pool (CC's
                // ContractPreLoader source); strips other shared-pool entries
                // (pre-5.17d leftover or pre-0.31 upgrade-in-place data) and
                // splices the requesting agency's AgencyState.Contracts.
                // CONTRACTS_FINISHED is stripped (no per-agency Finished
                // surface today; pre-0.31 entries would otherwise bleed).
                case "ContractSystem":
                    return SpliceAgencyContractsIntoScenario(serializedText, targetAgency);
                default:
                    return serializedText;
            }
        }

        /// <summary>
        /// [Stage 5.17e-6] Strip shared STRATEGIES → STRATEGY child nodes and
        /// splice in per-agency Strategies. Shared StrategySystem scenario shape:
        /// <c>StrategySystem { STRATEGIES { STRATEGY { name = X, ... } } }</c>.
        /// Per-entry isolation on parse failures. Same parse-fallback pattern
        /// as <see cref="SpliceAgencyTechIntoResearchAndDevelopment"/>: on
        /// whole-scenario parse failure, log + return input unchanged so the
        /// player gets the shared blob rather than a hung handshake.
        /// </summary>
        private static string SpliceAgencyStrategiesIntoScenario(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "StrategySystem" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:per-agency-career] StrategySystem projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Find or create STRATEGIES container, strip its children, splice per-agency.
            var stratsContainer = node.GetNode("STRATEGIES")?.Value;
            if (stratsContainer == null)
            {
                stratsContainer = new ConfigNode("") { Name = "STRATEGIES" };
                node.AddNode(stratsContainer);
            }
            else
            {
                foreach (var existing in stratsContainer.GetNodes("STRATEGY").ToArray())
                    stratsContainer.RemoveNode(existing.Value);
            }

            AgencyStrategyEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
                snapshot = targetAgency.Strategies.Values.ToArray();

            foreach (var entry in snapshot)
            {
                if (entry == null || entry.Data == null || entry.NumBytes <= 0)
                    continue;
                try
                {
                    var strategyNode = ScenarioDataUpdater.ParseClientConfigNode(entry.Data, entry.NumBytes, "STRATEGY");
                    if (strategyNode.IsEmpty())
                        continue;
                    stratsContainer.AddNode(strategyNode);
                }
                catch (Exception)
                {
                    // Per-entry isolation — drop this strategy, keep others.
                }
            }

            return node.ToString();
        }

        /// <summary>
        /// [Stage 5.17e-6] Strip shared Progress → {achievement-named-node}
        /// children and splice in per-agency Achievements. ProgressTracking
        /// scenario shape: <c>ProgressTracking { Progress { Kerbin { RocketLaunch
        /// { ... } } FirstLaunch { ... } } }</c>. KSP names progress nodes
        /// dynamically by the achievement path; the per-agency entry's Id
        /// becomes the spliced ConfigNode's Name.
        /// </summary>
        private static string SpliceAgencyAchievementsIntoScenario(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "ProgressTracking" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:per-agency-career] ProgressTracking projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Find or create Progress container, strip ALL existing children, then
            // splice in the per-agency Achievements. Strict-isolation matches the
            // Strategy splice (SpliceAgencyStrategiesIntoScenario) and the spec §10
            // Q-BootRefusal sign-off — the Warn helpers tell the operator "the
            // projector strips ... so per-agency clients start with ... no world
            // firsts," and the AllowEnablePerAgencyOnExistingUniverse=true override
            // is "I accept the strip on first per-agency connect." Pre-review-
            // finding-A.2 (session 19) the projector used upsert semantics that left
            // unmatched shared Progress children intact, contradicting both the
            // sibling splice and the documented operator contract.
            //
            // The historical "no enumerate-all" rationale on the in-code comment
            // was incorrect: LunaConfigNode.CfgNode.ConfigNode.GetAllNodes() (verified
            // v1.9.1) enumerates children regardless of name — exactly what the
            // dynamic-named Progress children need. Strip-then-splice is the same
            // shape as the STRATEGY strip-and-splice, just with GetAllNodes()
            // substituted for GetNodes("STRATEGY"). The boot refusal (5.17e-9 +
            // session-19 fix to RefuseStartupIfUpgradeHazardWithoutOverride) is the
            // operator safety net for upgrade-in-place universes; the projector
            // honours the override-flag contract once the operator opts in.
            var progressContainer = node.GetNode("Progress")?.Value;
            if (progressContainer == null)
            {
                progressContainer = new ConfigNode("") { Name = "Progress" };
                node.AddNode(progressContainer);
            }
            else
            {
                // .ToArray() snapshots the enumeration so RemoveNode during iteration
                // doesn't invalidate the cursor (same pattern as the Tech/Science/
                // ExpParts strip in SpliceAgencyResearchIntoScenario). Note:
                // GetAllNodes() returns List<ConfigNode> directly, not the
                // CfgNodeValue<string, ConfigNode> wrappers that the named overload
                // GetNodes(name) returns — so we pass `existing` straight to
                // RemoveNode(ConfigNode), no `.Value` indirection.
                foreach (var existing in progressContainer.GetAllNodes().ToArray())
                    progressContainer.RemoveNode(existing);
            }

            AgencyAchievementEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
                snapshot = targetAgency.Achievements.Values.ToArray();

            foreach (var entry in snapshot)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Id) || entry.Data == null || entry.NumBytes <= 0)
                    continue;
                try
                {
                    var achNode = ScenarioDataUpdater.ParseClientConfigNode(entry.Data, entry.NumBytes, entry.Id);
                    if (achNode.IsEmpty())
                        continue;
                    progressContainer.AddNode(achNode);
                }
                catch (Exception)
                {
                    // Per-entry isolation — drop this achievement, keep others.
                }
            }

            return node.ToString();
        }

        /// <summary>
        /// [Stage 5.17e-6] Override <c>lvl</c> values for facilities the
        /// agency has upgraded. ScenarioUpgradeableFacilities shape:
        /// <c>ScenarioUpgradeableFacilities { SpaceCenter/LaunchPad { lvl = X }
        /// SpaceCenter/VehicleAssemblyBuilding { lvl = Y } ... }</c>. Each
        /// facility is a top-level named child node. We don't strip-and-resplice
        /// here — facilities NOT in the per-agency dict keep the shared scenario
        /// default (which is the stock baseline for a fresh universe; an
        /// upgrade-in-place universe's tier values for unmentioned facilities
        /// already get the WarnAboutSharedFacilityOnUpgrade diagnostic).
        /// </summary>
        private static string OverrideFacilityLevelsInScenario(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "ScenarioUpgradeableFacilities" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:per-agency-career] ScenarioUpgradeableFacilities projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            KeyValuePair<string, float>[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
                snapshot = targetAgency.FacilityLevels.ToArray();

            foreach (var kvp in snapshot)
            {
                if (string.IsNullOrEmpty(kvp.Key))
                    continue;
                var facilityNode = node.GetNode(kvp.Key)?.Value;
                if (facilityNode == null)
                {
                    // Facility not in shared scenario — add a fresh node so
                    // KSP-side ScenarioUpgradeableFacilities reads the per-agency
                    // tier correctly. Otherwise the player sees the unupgraded
                    // default for this facility.
                    facilityNode = new ConfigNode("") { Name = kvp.Key };
                    facilityNode.CreateValue(new CfgNodeValue<string, string>("lvl",
                        kvp.Value.ToString(CultureInfo.InvariantCulture)));
                    node.AddNode(facilityNode);
                }
                else
                {
                    facilityNode.UpdateValue("lvl", kvp.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            return node.ToString();
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
        /// <summary>
        /// [Stage 5.18d slice (j)] Per-agency <c>ContractSystem</c> scenario
        /// projection. The closing splice in the Stage 5 projector family;
        /// makes the Stage 5.17d <c>AgencyContractRouter</c>'s per-agency
        /// persistence observable via the scene-load scenario wire (in
        /// addition to the existing owner-only <c>AgencyContractMsgData</c>
        /// echo).
        ///
        /// <para><b>Splice rules.</b> The shared <c>ContractSystem</c>
        /// scenario carries a <c>CONTRACTS</c> child node containing one
        /// <c>CONTRACT</c> sub-node per active or pending contract, plus a
        /// <c>CONTRACTS_FINISHED</c> sibling that KSP archives terminal
        /// (Completed/Failed/etc.) entries into. Under gate=on:
        /// <list type="bullet">
        ///   <item><b>Keep</b> shared-pool <c>CONTRACTS</c> entries whose
        ///         <c>state</c> is <c>Offered</c> or <c>Generated</c> —
        ///         Contract Configurator's <c>ContractPreLoader</c> draws
        ///         from this pool; every agency sees the same Offered/
        ///         Generated slots and may Accept them, at which point
        ///         <see cref="AgencyContractRouter.ApplyPerAgencyBatch"/>
        ///         removes the slot via <c>RemoveContractFromSharedOfferedPool</c>
        ///         + persists the post-Accept entry into
        ///         <see cref="AgencyState.Contracts"/> instead. Q6 commitment a.</item>
        ///   <item><b>Strip</b> shared-pool entries with any other state. In
        ///         steady-state 5.17d these shouldn't exist (the router
        ///         removes them on Accept); but pre-5.17d snapshots or
        ///         upgrade-in-place universes carry stale Active/Completed/
        ///         etc. entries that would otherwise leak to every per-
        ///         agency client as if they belonged to that client's
        ///         agency. Matches the operator-warned strip-on-projection
        ///         contract of <see cref="AgencySystem.WarnAboutSharedContractsOnUpgrade"/>.</item>
        ///   <item><b>Strip</b> ALL existing <c>CONTRACTS_FINISHED</c>
        ///         children (the operator-warned removal of pre-0.31 shared
        ///         Finished entries). Container stays present but empty so
        ///         KSP-side <c>ScenarioModule.OnLoad</c> schema expectations
        ///         remain stable; a subsequent agency-Finished splice
        ///         repopulates it.</item>
        ///   <item><b>Splice per-agency entries partitioned by state</b>
        ///         (consumer-lens v1 MUST FIX). <see cref="AgencyState.Contracts"/>
        ///         carries the full router-persisted set (Active +
        ///         Completed / Failed / Cancelled / DeadlineExpired /
        ///         Withdrawn). KSP's <c>ContractSystem.OnLoad</c> treats
        ///         <c>CONTRACTS</c> entries as LIVE (they appear in Mission
        ///         Control's Active tab); terminal entries belong in
        ///         <c>CONTRACTS_FINISHED</c>. The router doesn't tag the
        ///         destination — the projector partitions on read: state
        ///         == "Active" → <c>CONTRACTS</c>; anything else (terminal,
        ///         unknown, empty) → <c>CONTRACTS_FINISHED</c>. Each
        ///         entry's bytes parse via
        ///         <see cref="ScenarioDataUpdater.ParseClientConfigNode"/>;
        ///         per-entry try/catch isolation so one malformed entry
        ///         doesn't drop the batch.</item>
        /// </list></para>
        ///
        /// <para><b>Why the CONTRACTS / CONTRACTS_FINISHED partition.</b>
        /// Pre-MUST-FIX the splice placed every entry into <c>CONTRACTS</c>.
        /// KSP's <c>ContractSystem</c> instantiates each <c>CONTRACTS</c>
        /// entry as a live contract — Completed entries would remain in
        /// Mission Control's Active tab forever (checked-but-undismissable).
        /// The PlagueNZ-bloat doc-comment that originally argued for
        /// strip-only behaviour referred to PERSISTENCE volume (the router
        /// already persists terminal contracts in <see cref="AgencyState.Contracts"/>);
        /// the strip-from-wire-CONTRACTS_FINISHED was the WRONG mitigation
        /// for that concern.</para>
        ///
        /// <para><b>CONTRACTS_FINISHED container creation asymmetry.</b> The
        /// projector unconditionally creates a fresh empty <c>CONTRACTS</c>
        /// container when absent (CC's pre-loader iterates children; empty
        /// container is benign). For <c>CONTRACTS_FINISHED</c> the container
        /// is created if absent only when the agency actually has terminal
        /// contracts to splice — otherwise we don't surface a new container
        /// shape KSP didn't already have. Both branches honour KSP's
        /// <c>ScenarioModule.OnLoad</c> tolerance for missing/empty containers.</para>
        ///
        /// <para><b>Concurrency.</b> Snapshots <see cref="AgencyState.Contracts"/>
        /// under <see cref="AgencySystem.GetAgencyLock"/> so a concurrent
        /// <see cref="AgencyContractRouter.Upsert"/> can't tear the
        /// projection mid-walk. Matches the snapshot-inside-lock pattern in
        /// <see cref="SpliceAgencyStrategiesIntoScenario"/>.</para>
        ///
        /// <para><b>Two-agency simultaneous Accept race (upgrade-lens v1
        /// known limitation).</b> If Bob and Alice both Accept Offered slot
        /// X within the same router tick, both <see cref="AgencyContractRouter.ApplyPerAgencyBatch"/>
        /// calls may classify+upsert X into their respective
        /// <see cref="AgencyState.Contracts"/> before either's
        /// <c>RemoveContractFromSharedOfferedPool</c> commits. Each agency
        /// then holds X; the projection ships each their own copy + the
        /// slot is gone from the shared pool. Completing X in either agency
        /// then credits funds via that agency's
        /// <see cref="Server.System.Agency.AgencyCurrencyRouter"/> path —
        /// silent dual-rewards. Slice (j) is the surface that makes this
        /// observable; the proper fix is a router-side lock around
        /// classify + upsert + pool-remove. Tracked for a future band-2
        /// router follow-up.</para>
        ///
        /// <para><b>Per-entry isolation on parse failure.</b> Each agency
        /// contract's bytes pass through
        /// <see cref="ScenarioDataUpdater.ParseClientConfigNode"/> in a
        /// per-entry try/catch. One malformed payload drops that single
        /// contract and continues the batch — matches the router's
        /// per-contract isolation contract (Q6 commitment b). The drop is
        /// logged so operators triaging "Alice's mirror knows about a
        /// contract that disappeared from her UI" can grep for the
        /// scenario-projection drop (consumer-lens v1 catch-up-vs-scenario
        /// divergence observability).</para>
        ///
        /// <para><b>Whole-scenario parse failure.</b> On ConfigNode parse
        /// failure of the outer scenario text, logs and returns the input
        /// unchanged (player gets the shared blob rather than a hung
        /// handshake). Matches the Strategy / Achievement / Tech splice
        /// fallback contracts.</para>
        /// </summary>
        private static string SpliceAgencyContractsIntoScenario(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;

            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "ContractSystem" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:per-agency-career] ContractSystem projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Find or create CONTRACTS container, partition existing children
            // (Offered/Generated stay; others strip), then splice per-agency
            // Active entries.
            var contractsContainer = node.GetNode("CONTRACTS")?.Value;
            if (contractsContainer == null)
            {
                contractsContainer = new ConfigNode("") { Name = "CONTRACTS" };
                node.AddNode(contractsContainer);
            }
            else
            {
                foreach (var existing in contractsContainer.GetNodes("CONTRACT").ToArray())
                {
                    var state = existing.Value.GetValue("state")?.Value ?? string.Empty;
                    if (!SharedContractStates.Contains(state))
                        contractsContainer.RemoveNode(existing.Value);
                }
            }

            // Strip existing CONTRACTS_FINISHED children up front (pre-0.31 /
            // upgrade-in-place shared Finished pool would otherwise leak). The
            // per-agency terminal splice below repopulates the container with
            // this client's actual archived contracts.
            var finishedContainer = node.GetNode("CONTRACTS_FINISHED")?.Value;
            if (finishedContainer != null)
            {
                foreach (var existing in finishedContainer.GetAllNodes().ToArray())
                    finishedContainer.RemoveNode(existing);
            }

            // Snapshot agency contracts under the per-agency lock so a
            // concurrent router upsert can't tear the projection.
            AgencyContractEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
                snapshot = targetAgency.Contracts.ToArray();

            foreach (var entry in snapshot)
            {
                if (entry == null || entry.Data == null || entry.NumBytes <= 0)
                    continue;

                ConfigNode contractNode;
                try
                {
                    contractNode = ScenarioDataUpdater.ParseClientConfigNode(entry.Data, entry.NumBytes, "CONTRACT");
                }
                catch (Exception ex)
                {
                    // Per-entry isolation. Log the drop so the catch-up-vs-
                    // scene-load divergence (5.18a mirror has the contract;
                    // KSP UI doesn't because the projection couldn't parse it)
                    // is greppable in operator triage.
                    LunaLog.Warning($"[fix:per-agency-career] ContractSystem projection dropped contract {entry.ContractGuid:N} from agency {targetAgency.AgencyId:N} on parse: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (contractNode.IsEmpty())
                    continue;

                // Partition: live state ("Active") goes to CONTRACTS; everything
                // else (Completed/Failed/Cancelled/DeadlineExpired/Withdrawn +
                // any unknown or empty state) goes to CONTRACTS_FINISHED. The
                // router persists the state field on AgencyState.Contracts[i].
                // State entries from KSP have empty state for malformed
                // entries; treat empty as terminal (Mission Control already
                // hides a contract without a state).
                var contractState = entry.State ?? string.Empty;
                if (contractState == "Active")
                {
                    contractsContainer.AddNode(contractNode);
                }
                else
                {
                    if (finishedContainer == null)
                    {
                        finishedContainer = new ConfigNode("") { Name = "CONTRACTS_FINISHED" };
                        node.AddNode(finishedContainer);
                    }
                    finishedContainer.AddNode(contractNode);
                }
            }

            return node.ToString();
        }

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

            // [Stage 5.17e-5] Strip pre-existing Science child nodes too. Same
            // upgrade-leak hazard as Tech — an upgrade-in-place universe with
            // accumulated shared subjects would bleed into every fresh per-agency
            // client otherwise.
            foreach (var existing in node.GetNodes("Science").ToArray())
                node.RemoveNode(existing.Value);

            // [Stage 5.17e-5] Strip pre-existing ExpParts child node — same
            // hazard. We re-add a per-agency-scoped one further below if the
            // agency has any experimental parts.
            foreach (var existing in node.GetNodes("ExpParts").ToArray())
                node.RemoveNode(existing.Value);

            // Snapshot all per-agency R&D collections under the per-agency lock
            // so a concurrent router invocation can't tear our iterations. The
            // lock window is brief (memcpy of values + part-set sizes) — no I/O,
            // no parse. AgencyState.cs XML pins the read-also-needs-lock contract.
            AgencyTechNodeEntry[] techSnapshot;
            AgencyScienceSubjectEntry[] subjectSnapshot;
            Dictionary<string, string[]> partsSnapshot;
            KeyValuePair<string, int>[] expPartsSnapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                techSnapshot = targetAgency.TechNodes.Values.ToArray();
                subjectSnapshot = targetAgency.ScienceSubjects.Values.ToArray();
                partsSnapshot = new Dictionary<string, string[]>(targetAgency.PurchasedParts.Count, StringComparer.Ordinal);
                foreach (var kvp in targetAgency.PurchasedParts)
                    partsSnapshot[kvp.Key] = kvp.Value.ToArray();
                expPartsSnapshot = targetAgency.ExperimentalParts.ToArray();
            }

            // Add per-agency Tech child nodes. The stored Data field contains the
            // decompressed wire payload (the same brace-stripped ConfigNode-format
            // text the client sent originally); we re-parse + brace-strip via the
            // same ParseClientConfigNode helper the BUG-025 path uses, so any
            // future change to KSP's wire format gets the same treatment in both
            // routing and projection.
            //
            // [Stage 5.17e-5] As each per-agency Tech node is spliced in, we ALSO
            // merge in matching purchased parts from partsSnapshot — KSP stores
            // parts as `part = X` values INSIDE Tech blocks, not as top-level
            // entries, so the part-merge has to happen at Tech-splice time.
            foreach (var entry in techSnapshot)
            {
                if (entry == null || entry.Data == null || entry.NumBytes <= 0)
                    continue;
                try
                {
                    var techNode = ScenarioDataUpdater.ParseClientConfigNode(entry.Data, entry.NumBytes, "Tech");
                    if (techNode.IsEmpty())
                        continue;

                    // [Stage 5.17e-5] Merge per-agency PurchasedParts into this Tech.
                    // The stored Tech bytes contain a snapshot of parts at the time of
                    // first unlock; subsequent part purchases (via ShareProgressPartPurchase)
                    // accumulate in AgencyState.PurchasedParts. Merge them so the projected
                    // Tech node reflects the agency's current purchase set.
                    var techId = techNode.GetValue("id")?.Value;
                    if (!string.IsNullOrEmpty(techId) && partsSnapshot.TryGetValue(techId, out var parts))
                    {
                        // Dedup against the snapshot's existing `part = X` values
                        // (some part_iD-style records ship inside the original Tech bytes).
                        var existingParts = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var v in techNode.GetValues("part"))
                            if (!string.IsNullOrEmpty(v.Value))
                                existingParts.Add(v.Value);
                        foreach (var partName in parts)
                        {
                            if (string.IsNullOrEmpty(partName) || existingParts.Contains(partName))
                                continue;
                            techNode.CreateValue(new CfgNodeValue<string, string>("part", partName));
                        }
                    }

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

            // [Stage 5.17e-5] Splice per-agency Science subject child nodes.
            foreach (var entry in subjectSnapshot)
            {
                if (entry == null || entry.Data == null || entry.NumBytes <= 0)
                    continue;
                try
                {
                    var subjectNode = ScenarioDataUpdater.ParseClientConfigNode(entry.Data, entry.NumBytes, "Science");
                    if (subjectNode.IsEmpty())
                        continue;
                    node.AddNode(subjectNode);
                }
                catch (Exception)
                {
                    // Per-entry isolation — same rationale as Tech.
                }
            }

            // [Stage 5.17e-5] Splice per-agency ExpParts as a single child node
            // (matches KSP's scenario shape: one ExpParts block containing
            // `partname = count` value pairs).
            if (expPartsSnapshot.Length > 0)
            {
                var expPartsNode = new ConfigNode("") { Name = "ExpParts" };
                foreach (var kvp in expPartsSnapshot)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || kvp.Value <= 0)
                        continue;
                    expPartsNode.CreateValue(new CfgNodeValue<string, string>(kvp.Key,
                        kvp.Value.ToString(CultureInfo.InvariantCulture)));
                }
                // Only add if at least one valid value made it through (otherwise the
                // shared-scenario writer's "dummyPart" defensive shim could trigger
                // an empty-ExpParts re-create cycle).
                if (expPartsNode.GetAllValues().Count > 0)
                    node.AddNode(expPartsNode);
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
