using LmpCommon.Message.Data.Agency;
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
            // [Phase 3 Slice B] MKS kolonization research projection. Strip-then-splice
            // pattern same as STRATEGIES — each agency's projected scenario carries ONLY
            // their own KOLONY_ENTRY records under the KOLONIZATION container.
            "KolonizationScenario",
            // [Phase 3 Slice C] MKS planetary-logistics warehouse projection.
            // Same strip-then-splice — each agency's projected scenario carries
            // ONLY their own LOGISTICS_ENTRY records under the PLANETARY_LOGISTICS
            // container. Distinct partition shape from kolony (body-and-resource
            // vs vessel-and-body) but same projector contract.
            "PlanetaryLogisticsScenario",
            // [Phase 3 Slice D] MKS orbital-logistics transfer-queue projection.
            // Strip-then-splice — each agency's projected scenario carries ONLY
            // their own TRANSFER records (parsed from per-entry PayloadBytes).
            // Distinct partition shape from kolony / planetary — TransferGuid-
            // keyed at the dict level, opaque PayloadBytes (the MKS-side
            // ConfigNode Save() output) at the wire level. Closes the per-frame
            // double-spend (pre-spec §1.c) from the read side; the companion
            // client-side Deliver-prefix closes it from the write side gate-
            // state-independent.
            "ScenarioOrbitalLogistics",
            // [Mod-compat S2] SCANsat per-agency coverage projection. Strip-
            // then-splice for Progress → Body (per-body coverage + UI prefs)
            // and Scanners → Vessel → Sensor (multi-Sensor nested per Decision
            // §9). SCANResources and ~30 root-level KSPField UI scalars pass
            // through unchanged (Decisions §6 + §7 — shared, frozen at
            // operator seed under gate=on). Each agency's projected scenario
            // carries ONLY their own Body + Vessel records. Cross-agency
            // isolation is enforced at INGRESS (AgencyScanRouter rejects
            // cross-agency Vessel claims, see UpsertScannerEntries cross-
            // agency check); the splice emits AgencyState.Scanners verbatim
            // because the router has already filtered. No splice-time
            // ownership re-check.
            "SCANcontroller",
            // [Mod-compat S4] DMagic Orbital Science per-agency projection.
            // Strip-then-splice for Asteroid_Science → DM_Science (flat per-
            // asteroid entries) and Anomaly_Records → DM_Anomaly_List →
            // DM_Anomaly (2-level nested per Decision §B — group agency
            // entries by BodyIndex on emit, one wrapper per body). No cross-
            // agency rejection (no vessel keying); no transferagency
            // migration. Wire field names lowercase (title/bsv/scv/sci/cap
            // on asteroid; Body on wrapper; Name/Lat/Lon/Alt on anomaly per
            // DMScienceScenario.OnLoad parse contract).
            "DMScienceScenario",
            // [Phase 4 Slice B-2] WOLF per-agency depot projection. Strip-then-
            // splice for DEPOTS child of WOLF_ScenarioModule. Slices C-E add
            // ROUTES / HOPPERS / TERMINALS / CREWROUTES emit alongside. The
            // emit ORDER is DEPOTS FIRST (pre-spec §2.c) because WOLF's
            // ScenarioPersister.OnLoad at line 288 has the comment
            // "Depots need to be loaded first!" and Routes/CrewRoutes call
            // _registry.GetDepot during OnLoad — if the projector emitted a
            // Route or CrewRoute referencing a depot not in the agency's
            // pool, WOLF would throw DepotDoesNotExistException + the scene
            // load would hang. In Slice B-2 only DEPOTS are emitted; Slices
            // C-E add the foreign-key integrity sweep when they bring the
            // Route/Hopper/CrewRoute splices online.
            "WOLF_ScenarioModule",
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
                // [Phase 3 Slice B] MKS kolonization scenario splice. Strip all
                // shared KOLONY_ENTRY children under KOLONIZATION; splice in
                // per-agency entries from AgencyState.KolonyEntries. Each agency
                // sees ONLY their own kolony research — spec §10 Q1.
                case "KolonizationScenario":
                    return SpliceAgencyKolonyEntries(serializedText, targetAgency);
                // [Phase 3 Slice C] MKS planetary-logistics scenario splice.
                // Strip all shared LOGISTICS_ENTRY children under
                // PLANETARY_LOGISTICS; splice in per-agency entries from
                // AgencyState.PlanetaryEntries. Each agency sees ONLY their own
                // planetary balances — spec §10 Q1.
                case "PlanetaryLogisticsScenario":
                    return SpliceAgencyPlanetaryEntries(serializedText, targetAgency);
                // [Phase 3 Slice D] MKS orbital-logistics scenario splice. Strip
                // all shared TRANSFER children at the root; splice in per-agency
                // entries from AgencyState.OrbitalTransfers (parsed from each
                // entry's opaque PayloadBytes). Each agency sees ONLY their own
                // pending + recently-completed transfers — spec §10 Q1.
                case "ScenarioOrbitalLogistics":
                    return SpliceAgencyOrbitalTransfers(serializedText, targetAgency);
                // [Mod-compat S2] SCANsat scenario splice. Strip shared Progress
                // → Body children + Scanners → Vessel children; splice in per-
                // agency entries from AgencyState.Coverage + AgencyState.Scanners.
                // SCANResources + root-level UI scalars are NOT touched (Decisions
                // §6 + §7 — shared, frozen at operator seed under gate=on).
                case "SCANcontroller":
                    return SpliceSCANsatCoverageIntoScenario(serializedText, targetAgency);
                // [Mod-compat S4] DMagic Orbital Science scenario splice. Strip
                // shared Asteroid_Science → DM_Science children + Anomaly_Records
                // → DM_Anomaly_List wrappers; splice in per-agency entries from
                // AgencyState.DMagicAsteroidScience + AgencyState.DMagicAnomalies.
                // Anomaly emit is 2-level nested per Decision §B — group entries
                // by BodyIndex into per-body DM_Anomaly_List wrappers.
                case "DMScienceScenario":
                    return SpliceDMagicScienceIntoScenario(serializedText, targetAgency);
                // [Phase 4 Slice B-2] WOLF scenario splice. Strip shared
                // DEPOTS / ROUTES / HOPPERS / TERMINALS / CREWROUTES and
                // splice in per-agency entries from AgencyState.WolfDepots
                // (Slice B-2 emits DEPOTS only; Slices C-E extend
                // SpliceAgencyWolfState to also handle ROUTES / HOPPERS /
                // TERMINALS / CREWROUTES, each with its own foreign-key
                // integrity sweep against the per-agency depot pool per
                // pre-spec §2.c).
                case "WOLF_ScenarioModule":
                    return SpliceAgencyWolfState(serializedText, targetAgency);
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
        /// [Phase 3 Slice B] Strip shared <c>KOLONIZATION → KOLONY_ENTRY</c> child
        /// nodes and splice in per-agency entries. KolonizationScenario shape (verified
        /// against MKS <c>KolonyTools/Kolonization/KolonizationPersistance.cs</c> at SHA
        /// <c>ed0f6aa6</c>):
        /// <code>
        /// KolonizationScenario {
        ///     KOLONIZATION {
        ///         KOLONY_ENTRY { BodyIndex=... VesselId=... LastUpdate=... ... }
        ///         KOLONY_ENTRY { ... }
        ///     }
        /// }
        /// </code>
        ///
        /// <para><b>Strip-then-splice</b> matches the <see cref="SpliceAgencyStrategiesIntoScenario"/>
        /// pattern (line 173-220). Each agency's projected scenario contains ONLY
        /// their own <c>KOLONY_ENTRY</c> records — the shared scenario's
        /// pre-existing entries are stripped out so an upgrade-in-place universe
        /// doesn't bleed its accumulated shared kolony research into every fresh
        /// per-agency client. The boot-time
        /// <see cref="AgencySystem.WarnAboutSharedKolonyOnUpgrade"/> + the
        /// <see cref="AgencySystem.RefuseStartupIfUpgradeHazardWithoutOverride"/>
        /// hazard-gate (Phase 3 Slice B item 11) protect operators from silent
        /// data loss; the override flag is the documented opt-in.</para>
        ///
        /// <para><b>Field-name mapping.</b> The per-agency
        /// <see cref="AgencyKolonyEntry"/> uses <c>Reputation</c> (matching LMP
        /// naming conventions); MKS' on-disk <c>KOLONY_ENTRY</c> uses <c>Rep</c>.
        /// The splice emits the MKS-side names (<c>Rep</c>, not <c>Reputation</c>)
        /// so KSP-side <c>KolonizationPersistance.ImportStatusNodeList</c> /
        /// <c>ResourceUtilities.LoadNodeProperties&lt;KolonizationEntry&gt;</c>
        /// reads the values into the right fields. The other 12 fields map 1:1.</para>
        ///
        /// <para><b>Locale</b>: all doubles emit via
        /// <c>CultureInfo.InvariantCulture</c> + <c>"R"</c> round-trip specifier so
        /// a comma-decimal server locale doesn't corrupt the on-wire format.
        /// Mirrors the per-agency <see cref="AgencyState"/> persistence convention
        /// (AgencyState.cs:433-443).</para>
        ///
        /// <para><b>Per-entry isolation</b>: a malformed entry's failure is
        /// logged + skipped, siblings continue. Whole-scenario parse failure
        /// falls back to the input unchanged + logs at Error level (same
        /// pattern as <see cref="SpliceAgencyTechIntoResearchAndDevelopment"/>
        /// line 393-407) so a hung handshake never blocks the player.</para>
        /// </summary>
        private static string SpliceAgencyKolonyEntries(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "KolonizationScenario" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:MKS-R2] KolonizationScenario projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Find or create KOLONIZATION container, strip its KOLONY_ENTRY children,
            // splice in per-agency entries. Same shape as STRATEGIES strip-and-splice.
            var kolonyContainer = node.GetNode("KOLONIZATION")?.Value;
            if (kolonyContainer == null)
            {
                kolonyContainer = new ConfigNode("") { Name = "KOLONIZATION" };
                node.AddNode(kolonyContainer);
            }
            else
            {
                // .ToArray() snapshots the enumeration so RemoveNode during iteration
                // doesn't invalidate the cursor (same pattern as STRATEGY strip at
                // line 194-195).
                foreach (var existing in kolonyContainer.GetNodes("KOLONY_ENTRY").ToArray())
                    kolonyContainer.RemoveNode(existing.Value);
            }

            AgencyKolonyEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                snapshot = targetAgency.KolonyEntries.Values.ToArray();
            }

            foreach (var entry in snapshot)
            {
                if (entry == null)
                    continue;
                try
                {
                    var kNode = new ConfigNode("") { Name = "KOLONY_ENTRY" };
                    // Field order matches MKS' KolonizationPersistance.Save (line 62-76).
                    // Field NAME mapping: Reputation→Rep; all other names match 1:1.
                    kNode.CreateValue(new CfgNodeValue<string, string>("BodyIndex", entry.BodyIndex.ToString(CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("VesselId", entry.VesselId ?? string.Empty));
                    kNode.CreateValue(new CfgNodeValue<string, string>("LastUpdate", entry.LastUpdate.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("KolonyDate", entry.KolonyDate.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("GeologyResearch", entry.GeologyResearch.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("BotanyResearch", entry.BotanyResearch.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("KolonizationResearch", entry.KolonizationResearch.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("FundsBoosters", entry.FundsBoosters.ToString(CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("ScienceBoosters", entry.ScienceBoosters.ToString(CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("RepBoosters", entry.RepBoosters.ToString(CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("Science", entry.Science.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("Rep", entry.Reputation.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("Funds", entry.Funds.ToString("R", CultureInfo.InvariantCulture)));
                    kolonyContainer.AddNode(kNode);
                }
                catch (Exception)
                {
                    // Per-entry isolation — drop this entry, keep others.
                }
            }

            return node.ToString();
        }

        /// <summary>
        /// [Phase 3 Slice C] Strip shared <c>PLANETARY_LOGISTICS → LOGISTICS_ENTRY</c>
        /// child nodes and splice in per-agency entries. PlanetaryLogisticsScenario
        /// shape (verified against MKS
        /// <c>KolonyTools/PlanetaryLogistics/PlanetaryLogisticsPersistance.cs</c>
        /// at SHA <c>ed0f6aa6</c>, Load lines 18-31 + Save lines 49-71):
        /// <code>
        /// PlanetaryLogisticsScenario {
        ///     PLANETARY_LOGISTICS {
        ///         LOGISTICS_ENTRY { BodyIndex=... ResourceName=... StoredQuantity=... }
        ///         LOGISTICS_ENTRY { ... }
        ///     }
        /// }
        /// </code>
        ///
        /// <para><b>Strip-then-splice</b> matches the
        /// <see cref="SpliceAgencyKolonyEntries"/> pattern. Each agency's
        /// projected scenario contains ONLY their own <c>LOGISTICS_ENTRY</c>
        /// records — the shared scenario's pre-existing entries are stripped
        /// out so an upgrade-in-place universe doesn't bleed its accumulated
        /// shared planetary balances into every fresh per-agency client. The
        /// boot-time
        /// <see cref="AgencySystem.WarnAboutSharedPlanetaryOnUpgrade"/> + the
        /// <see cref="AgencySystem.RefuseStartupIfUpgradeHazardWithoutOverride"/>
        /// hazard-gate (Phase 3 Slice C) protect operators from silent data
        /// loss; the override flag is the documented opt-in.</para>
        ///
        /// <para><b>Field-name mapping.</b> Unlike Slice B kolony (where the LMP
        /// field <c>Reputation</c> maps to MKS' on-disk <c>Rep</c>), all four
        /// planetary fields map 1:1 to MKS' field names: <c>OwningVesselId</c>
        /// is fork-only (LMP-side addition not present in MKS' on-disk shape —
        /// emit excluded from the projected scenario since KSP-side
        /// <c>PlanetaryLogisticsEntry</c> has no such field;
        /// <see cref="ResourceUtilities.LoadNodeProperties&lt;PlanetaryLogisticsEntry&gt;"/>
        /// would silently ignore the extra key but emitting it is wire bloat
        /// for no reader). <c>BodyIndex</c> / <c>ResourceName</c> /
        /// <c>StoredQuantity</c> match MKS names exactly per
        /// <c>PlanetaryLogisticsPersistance.Save</c> lines 63-65.</para>
        ///
        /// <para><b>Locale</b>: <c>StoredQuantity</c> emits via
        /// <c>CultureInfo.InvariantCulture</c> + <c>"R"</c> round-trip
        /// specifier so a comma-decimal server locale doesn't corrupt the
        /// on-wire format. Mirrors the per-agency <see cref="AgencyState"/>
        /// persistence convention.</para>
        ///
        /// <para><b>Per-entry isolation</b>: a malformed entry's failure is
        /// logged + skipped, siblings continue. Whole-scenario parse failure
        /// falls back to the input unchanged + logs at Error level (same
        /// pattern as <see cref="SpliceAgencyKolonyEntries"/>) so a hung
        /// handshake never blocks the player.</para>
        /// </summary>
        private static string SpliceAgencyPlanetaryEntries(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "PlanetaryLogisticsScenario" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:MKS-R2] PlanetaryLogisticsScenario projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Find or create PLANETARY_LOGISTICS container, strip its
            // LOGISTICS_ENTRY children, splice in per-agency entries. Same
            // shape as KOLONIZATION strip-and-splice.
            var planetaryContainer = node.GetNode("PLANETARY_LOGISTICS")?.Value;
            if (planetaryContainer == null)
            {
                planetaryContainer = new ConfigNode("") { Name = "PLANETARY_LOGISTICS" };
                node.AddNode(planetaryContainer);
            }
            else
            {
                // .ToArray() snapshots the enumeration so RemoveNode during
                // iteration doesn't invalidate the cursor (same pattern as
                // KOLONY_ENTRY strip).
                foreach (var existing in planetaryContainer.GetNodes("LOGISTICS_ENTRY").ToArray())
                    planetaryContainer.RemoveNode(existing.Value);
            }

            AgencyPlanetaryEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                snapshot = targetAgency.PlanetaryEntries.Values.ToArray();
            }

            foreach (var entry in snapshot)
            {
                if (entry == null)
                    continue;
                if (string.IsNullOrEmpty(entry.ResourceName))
                    continue; // Empty-resource defensive — router gate should have caught this.
                try
                {
                    var pNode = new ConfigNode("") { Name = "LOGISTICS_ENTRY" };
                    // Field order matches MKS' PlanetaryLogisticsPersistance.Save (lines 63-65).
                    // OwningVesselId is the LMP-side fork addition (not in MKS'
                    // on-disk shape) — deliberately NOT emitted to the projected
                    // scenario per the field-name mapping note in the XML above.
                    pNode.CreateValue(new CfgNodeValue<string, string>("BodyIndex", entry.BodyIndex.ToString(CultureInfo.InvariantCulture)));
                    pNode.CreateValue(new CfgNodeValue<string, string>("ResourceName", entry.ResourceName));
                    pNode.CreateValue(new CfgNodeValue<string, string>("StoredQuantity", entry.StoredQuantity.ToString("R", CultureInfo.InvariantCulture)));
                    planetaryContainer.AddNode(pNode);
                }
                catch (Exception)
                {
                    // Per-entry isolation — drop this entry, keep others.
                }
            }

            return node.ToString();
        }

        /// <summary>
        /// [Phase 3 Slice D] Strip shared root-level <c>TRANSFER</c> child nodes
        /// and splice in per-agency transfers (parsed from per-entry opaque
        /// <see cref="AgencyOrbitalTransferEntry.PayloadBytes"/>).
        /// ScenarioOrbitalLogistics shape (verified against MKS
        /// <c>KolonyTools/OrbitalLogistics/ScenarioOrbitalLogistics.cs</c> at
        /// SHA <c>ed0f6aa6</c>, OnSave lines 79-100):
        /// <code>
        /// ScenarioOrbitalLogistics {
        ///     TRANSFER {
        ///         status = Launched
        ///         startTime = 12345.6
        ///         duration = 6789.0
        ///         DestinationVesselId = 123
        ///         OriginVesselId = 456
        ///         RESOURCE { ... }
        ///     }
        ///     TRANSFER { ... }
        /// }
        /// </code>
        /// TRANSFER nodes are direct children of the scenario (NOT nested under
        /// a container like KOLONIZATION or PLANETARY_LOGISTICS — different from
        /// Slice B/C). The strip pattern is <c>GetNodes("TRANSFER")</c> at the
        /// scenario root.
        ///
        /// <para><b>Opaque-payload passthrough</b> (pre-spec §3.c, contracts
        /// router precedent at AgencyContractMsgData / ContractInfo). Each
        /// per-agency entry's PayloadBytes is the verbatim UTF-8 ConfigNode
        /// bytes the client emitted from MKS'
        /// <c>OrbitalLogisticsTransferRequest.Save</c> at state-machine-postfix
        /// time. We parse the bytes back into a <see cref="ConfigNode"/> and
        /// add as a child of the projected scenario. KSP-side
        /// <c>ScenarioOrbitalLogistics.OnLoad</c> reads the bytes back via
        /// <c>OrbitalLogisticsTransferRequest.Load</c> using the symmetric
        /// shape. LMP never inspects the inner field set — Status / StartTime /
        /// Duration on the entry are for server-side routing decisions only.
        /// </para>
        ///
        /// <para><b>Per-entry isolation</b>: a malformed PayloadBytes (parse
        /// failure, empty bytes, missing transfer-name) is logged + skipped,
        /// siblings continue. Whole-scenario parse failure falls back to the
        /// input unchanged + logs at Error level (same pattern as
        /// <see cref="SpliceAgencyKolonyEntries"/> +
        /// <see cref="SpliceAgencyPlanetaryEntries"/>) so a hung handshake
        /// never blocks the player.</para>
        ///
        /// <para><b>Strip-then-splice contract</b> matches Slice B/C: an
        /// agency with zero per-agency transfers gets an outgoing scenario
        /// with zero TRANSFER children (the shared scenario's pre-existing
        /// transfers are stripped). Pre-0.31 upgrade-in-place universes with
        /// shared <c>ScenarioOrbitalLogistics</c> transfers are flagged by
        /// <see cref="AgencySystem.WarnAboutSharedOrbitalOnUpgrade"/> +
        /// hazard-gate refusal so operators have opt-in time before this
        /// strip silently fires on first per-agency connect.</para>
        /// </summary>
        private static string SpliceAgencyOrbitalTransfers(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "ScenarioOrbitalLogistics" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:MKS-R2] ScenarioOrbitalLogistics projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Strip ALL shared TRANSFER children at the scenario root. TRANSFER
            // is the only KSP/MKS-emitted child node type here (verified
            // against ScenarioOrbitalLogistics.OnSave lines 86-99 emitting
            // node.AddNode(transferNode) where transferNode.Name = "TRANSFER"
            // via OrbitalLogisticsTransferRequest.Save lines 660-663).
            // .ToArray() snapshots the enumeration so RemoveNode during
            // iteration doesn't invalidate the cursor (same pattern as
            // KOLONY_ENTRY / LOGISTICS_ENTRY strip).
            foreach (var existing in node.GetNodes("TRANSFER").ToArray())
                node.RemoveNode(existing.Value);

            AgencyOrbitalTransferEntry[] snapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                snapshot = targetAgency.OrbitalTransfers.Values.ToArray();
            }

            foreach (var entry in snapshot)
            {
                if (entry == null)
                    continue;
                // Skip entries with no payload — operator hand-edited / Slice
                // E migration that hasn't yet supplied PayloadBytes. The dict
                // value is preserved (we still hold it under the lock) so a
                // future router update will round-trip; today's projection
                // just doesn't include it.
                var payloadLen = Math.Max(0, Math.Min(entry.NumBytes, entry.PayloadBytes?.Length ?? 0));
                if (payloadLen == 0)
                    continue;

                try
                {
                    // Parse PayloadBytes back into a ConfigNode. The bytes are
                    // UTF-8 ConfigNode-format text produced by MKS'
                    // OrbitalLogisticsTransferRequest.Save (which calls
                    // ConfigNode.CreateConfigFromObject then sets node.name =
                    // "TRANSFER" + adds RESOURCE child nodes). We reverse the
                    // operation here: convert bytes to string, build ConfigNode,
                    // re-set Name to "TRANSFER" (the LunaConfigNode parser
                    // assigns the root the calling-side name).
                    var payloadText = global::System.Text.Encoding.UTF8.GetString(entry.PayloadBytes, 0, payloadLen);
                    if (string.IsNullOrWhiteSpace(payloadText))
                        continue;
                    var transferNode = new ConfigNode(payloadText) { Name = "TRANSFER" };
                    if (transferNode.IsEmpty())
                        continue;
                    node.AddNode(transferNode);
                }
                catch (Exception e)
                {
                    // [General-lens SF4] Per-entry isolation — drop this
                    // transfer, keep others. Log at Warning so operator
                    // grep gets visibility into an opaque-payload parse
                    // failure (slice D's payload is bytes, not typed
                    // fields — schema drift risk is highest here of the
                    // three Phase 3 splices). Once per call, not rate-
                    // limited; expected in normal operation only on
                    // operator hand-edit / MKS schema-drift cases.
                    LunaLog.Warning($"[fix:MKS-R2] orbital splice: dropped TRANSFER entry for agency {targetAgency.AgencyId:N} transfer {entry.TransferGuid:N}: {e.GetType().Name}: {e.Message}");
                }
            }

            return node.ToString();
        }

        /// <summary>
        /// [Mod-compat S2] Strip shared <c>Progress → Body</c> children + shared
        /// <c>Scanners → Vessel</c> children, splice in per-agency entries from
        /// <see cref="AgencyState.Coverage"/> + <see cref="AgencyState.Scanners"/>.
        /// Both containers honor the find-or-create + strip-then-splice
        /// canonical pattern (see <see cref="SpliceAgencyKolonyEntries"/>).
        ///
        /// <para>SCANcontroller shape (verified against
        /// <c>F:/tmp/mks-external/SCANsat</c> SHA <c>0d67371</c>,
        /// <c>SCANcontroller.cs:783-865</c>):</para>
        /// <code>
        /// SCANcontroller {
        ///     [~30 root-level KSPField UI scalars — left alone per Decision §7]
        ///     Scanners { Vessel { guid, name, Sensor { type, fov, min_alt, ... } ...N } ...M }
        ///     Progress { Body { Name, Map, Disabled, MinHeightRange, ... } ...K }
        ///     SCANResources { ResourceType { Resource, MinColor, MinMaxValues, ... } ... }  -- left alone per Decision §6
        /// }
        /// </code>
        ///
        /// <para><b>Decision §6 — `SCANResources` stays SHARED.</b> The
        /// `MinMaxValues` field is body-resource display ranges (operator /
        /// world config), not per-agency player-discovered amounts. Per
        /// Color / Transparency are visualization. This splice does NOT touch
        /// the SCANResources container — passes through unchanged.</para>
        ///
        /// <para><b>Decision §7 — root UI scalars stay SHARED.</b> The ~30
        /// KSPField scalars (`mainMapVisible`, `bigMapColor`, etc.) persist at
        /// the ScenarioModule root. Path B suppresses the shared-store WRITE
        /// under gate=on, so each client's runtime UI tweaks accumulate
        /// locally but the server's projection serves the operator-seeded
        /// baseline. This splice does NOT touch root-level scalars — they
        /// pass through unchanged from the inbound blob.</para>
        ///
        /// <para><b>Decision §3 — per-vessel scanner filter.</b> Under
        /// uniform gate=on the suppressed-shared-write model means the
        /// inbound <c>Scanners</c> children are typically EMPTY (operator-seed
        /// baseline has no Vessel children). The per-agency state may contain
        /// only the requesting agency's owned vessels (router rejects cross-
        /// agency claims). However, the operator-seed baseline COULD contain
        /// stale Vessel entries (pre-0.31 upgrade case); the splice always
        /// strips them before splicing per-agency entries so each agency
        /// sees ONLY their own vessels.</para>
        ///
        /// <para><b>Multi-Sensor nested round-trip (Decision §9).</b> Each
        /// per-agency <see cref="AgencyScannerEntry"/> carries a nested
        /// <see cref="AgencyScannerEntry.Sensors"/> list. The splice emits
        /// one <c>Sensor</c> child per record inside the Vessel parent — order
        /// is not load-bearing per SCANsat's set-semantics.</para>
        ///
        /// <para><b>Empty-container retention (M9)</b>. Strips shared children
        /// then emits empty <c>Progress { }</c> / <c>Scanners { }</c> containers
        /// when the agency has no entries — SCANsat's <c>OnLoad</c> guards on
        /// container presence (<c>node.GetNode("Progress") != null</c>) rather
        /// than child count, so empty containers are fine and missing
        /// containers cause OnLoad to skip the whole load branch (which would
        /// silently revert per-agency state to client-local SCANsat defaults).</para>
        ///
        /// <para><b>Whole-scenario parse fallback per Invariant 5.</b></para>
        /// </summary>
        private static string SpliceSCANsatCoverageIntoScenario(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "SCANcontroller" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:S2-SCANsat] SCANcontroller projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Find or create Progress container, strip its Body children, splice
            // in per-agency entries. Same shape as KOLONIZATION strip-and-splice
            // (see SpliceAgencyKolonyEntries).
            var progressContainer = node.GetNode("Progress")?.Value;
            if (progressContainer == null)
            {
                progressContainer = new ConfigNode("") { Name = "Progress" };
                node.AddNode(progressContainer);
            }
            else
            {
                foreach (var existing in progressContainer.GetNodes("Body").ToArray())
                    progressContainer.RemoveNode(existing.Value);
            }

            // Find or create Scanners container, strip its Vessel children.
            var scannersContainer = node.GetNode("Scanners")?.Value;
            if (scannersContainer == null)
            {
                scannersContainer = new ConfigNode("") { Name = "Scanners" };
                node.AddNode(scannersContainer);
            }
            else
            {
                foreach (var existing in scannersContainer.GetNodes("Vessel").ToArray())
                    scannersContainer.RemoveNode(existing.Value);
            }

            // Snapshot both collections inside the per-agency lock — same
            // pattern as kolony at SpliceAgencyKolonyEntries. Iteration of the
            // snapshot happens outside the lock so slow per-entry ConfigNode
            // construction doesn't extend the writer-blocking window.
            AgencyCoverageBodyEntry[] coverageSnapshot;
            AgencyScannerEntry[] scannersSnapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                coverageSnapshot = targetAgency.Coverage.Values.ToArray();
                scannersSnapshot = targetAgency.Scanners.Values.ToArray();
            }

            // Splice Body children. Field names match SCANsat OnLoad parse
            // (SCANcontroller.cs:619-700) — Name / Disabled / Map /
            // MinHeightRange / MaxHeightRange / ClampHeight (optional) /
            // palette / LandingTarget (optional).
            foreach (var entry in coverageSnapshot)
            {
                if (entry == null || string.IsNullOrEmpty(entry.BodyName))
                    continue;
                try
                {
                    var bNode = new ConfigNode("") { Name = "Body" };
                    bNode.CreateValue(new CfgNodeValue<string, string>("Name", entry.BodyName));
                    bNode.CreateValue(new CfgNodeValue<string, string>("Disabled", entry.Disabled.ToString(CultureInfo.InvariantCulture)));
                    bNode.CreateValue(new CfgNodeValue<string, string>("MinHeightRange", entry.MinHeightRange.ToString("R", CultureInfo.InvariantCulture)));
                    bNode.CreateValue(new CfgNodeValue<string, string>("MaxHeightRange", entry.MaxHeightRange.ToString("R", CultureInfo.InvariantCulture)));
                    if (entry.ClampHeight.HasValue)
                        bNode.CreateValue(new CfgNodeValue<string, string>("ClampHeight", entry.ClampHeight.Value.ToString("R", CultureInfo.InvariantCulture)));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteName", entry.PaletteName ?? string.Empty));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteSize", entry.PaletteSize.ToString(CultureInfo.InvariantCulture)));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteReverse", entry.PaletteReverse.ToString(CultureInfo.InvariantCulture)));
                    bNode.CreateValue(new CfgNodeValue<string, string>("PaletteDiscrete", entry.PaletteDiscrete.ToString(CultureInfo.InvariantCulture)));
                    bNode.CreateValue(new CfgNodeValue<string, string>("Map", entry.Map ?? string.Empty));
                    if (!string.IsNullOrEmpty(entry.LandingTarget))
                        bNode.CreateValue(new CfgNodeValue<string, string>("LandingTarget", entry.LandingTarget));
                    progressContainer.AddNode(bNode);
                }
                catch (Exception e)
                {
                    // Per-entry isolation per Invariant 4 — drop this body,
                    // keep others. Logged at Warning to match sibling splice
                    // precedent (general-lens SHOULD-FIX: silent drops are
                    // invisible to operators trying to diagnose missing per-
                    // agency Body entries; the round-trip-from-typed-state
                    // path should never fail, so a failure here is a real
                    // signal worth surfacing).
                    LunaLog.Warning(
                        $"[fix:S2-SCANsat] Body splice skipped for agency {targetAgency.AgencyId:N} " +
                        $"body='{entry.BodyName}': {e.GetType().Name}: {e.Message}");
                }
            }

            // Splice Vessel children with nested Sensor children per Decision §9.
            // Field names match SCANsat OnLoad parse (SCANcontroller.cs:610-630)
            // — lowercase guid + name on Vessel; lowercase-underscore on Sensor
            // (type / fov / min_alt / max_alt / best_alt / require_light).
            foreach (var entry in scannersSnapshot)
            {
                if (entry == null)
                    continue;
                try
                {
                    var vNode = new ConfigNode("") { Name = "Vessel" };
                    vNode.CreateValue(new CfgNodeValue<string, string>("guid", entry.VesselId.ToString("D", CultureInfo.InvariantCulture)));
                    if (!string.IsNullOrEmpty(entry.VesselName))
                        vNode.CreateValue(new CfgNodeValue<string, string>("name", entry.VesselName));
                    if (entry.Sensors != null)
                    {
                        foreach (var sensor in entry.Sensors)
                        {
                            if (sensor == null)
                                continue;
                            var sNode = new ConfigNode("") { Name = "Sensor" };
                            sNode.CreateValue(new CfgNodeValue<string, string>("type", sensor.SensorType.ToString(CultureInfo.InvariantCulture)));
                            sNode.CreateValue(new CfgNodeValue<string, string>("fov", sensor.Fov.ToString("R", CultureInfo.InvariantCulture)));
                            sNode.CreateValue(new CfgNodeValue<string, string>("min_alt", sensor.MinAlt.ToString("R", CultureInfo.InvariantCulture)));
                            sNode.CreateValue(new CfgNodeValue<string, string>("max_alt", sensor.MaxAlt.ToString("R", CultureInfo.InvariantCulture)));
                            sNode.CreateValue(new CfgNodeValue<string, string>("best_alt", sensor.BestAlt.ToString("R", CultureInfo.InvariantCulture)));
                            sNode.CreateValue(new CfgNodeValue<string, string>("require_light", sensor.RequireLight.ToString(CultureInfo.InvariantCulture)));
                            vNode.AddNode(sNode);
                        }
                    }
                    scannersContainer.AddNode(vNode);
                }
                catch (Exception e)
                {
                    // Per-entry isolation per Invariant 4 — drop this vessel,
                    // keep others. Logged at Warning to match sibling splice
                    // precedent (general-lens SHOULD-FIX).
                    LunaLog.Warning(
                        $"[fix:S2-SCANsat] Vessel splice skipped for agency {targetAgency.AgencyId:N} " +
                        $"VesselId={entry.VesselId:N}: {e.GetType().Name}: {e.Message}");
                }
            }

            return node.ToString();
        }

        /// <summary>
        /// [Mod-compat S4] Strip shared <c>Asteroid_Science → DM_Science</c>
        /// children and <c>Anomaly_Records → DM_Anomaly_List</c> wrappers,
        /// splice in per-agency entries from
        /// <see cref="AgencyState.DMagicAsteroidScience"/> +
        /// <see cref="AgencyState.DMagicAnomalies"/>. Mirrors
        /// <see cref="SpliceSCANsatCoverageIntoScenario"/> shape with one
        /// extra twist — anomalies emit 2-level nested per Decision §B by
        /// grouping agency entries on <see cref="AgencyDMagicAnomalyEntry.BodyIndex"/>.
        ///
        /// <para>DMScienceScenario shape (verified against
        /// <c>F:/tmp/mks-external/DMagicOrbitalScience</c> SHA <c>a4e805b9</c>,
        /// <c>Source/Scenario/DMScienceScenario.cs:68-182</c>):</para>
        /// <code>
        /// DMScienceScenario {
        ///     Asteroid_Science { DM_Science { title=..., bsv=..., scv=..., sci=..., cap=... } ... }
        ///     Anomaly_Records {
        ///         DM_Anomaly_List { Body=N
        ///             DM_Anomaly { Name=..., Lat=..., Lon=..., Alt=... } ...
        ///         } ...
        ///     }
        /// }
        /// </code>
        ///
        /// <para><b>Field names lowercase</b> — DMagic's OnLoad uses
        /// <c>parse("title", ...)</c> / <c>parse("bsv", ...)</c> /
        /// <c>parse("Name", ...)</c> / <c>parse("Lat", ...)</c> etc. The
        /// fork-side disk format uses PascalCase (Title / BaseValue / Name /
        /// Latitude) per the S2 disk-vs-wire convention; this splice converts
        /// on emit.</para>
        ///
        /// <para><b>Numeric formats:</b> asteroid fields are <c>float</c>
        /// (Decision §A), Lat/Lon/Alt are <c>double</c>. Both round-trip via
        /// <c>"R"</c> + <see cref="CultureInfo.InvariantCulture"/> per
        /// Invariant 9 (BUG-013). Stock DMagic emits anomaly coordinates
        /// as <c>"N5"</c> (culture-sensitive); the fork's stricter "R"
        /// output is BUG-013 defense + accepted by DMagic's parse on load.</para>
        ///
        /// <para><b>Anomaly group-by-BodyIndex emit (Decision §B).</b> Storage
        /// is flat; wire is per-body nested. We build a Dictionary&lt;int,
        /// List&lt;AgencyDMagicAnomalyEntry&gt;&gt; grouping then emit one
        /// DM_Anomaly_List wrapper per body with that body's anomalies as
        /// DM_Anomaly children. Empty agency → empty Anomaly_Records
        /// container (M9 retention — DMagic.OnLoad iterates GetNodes returning
        /// empty, harmless).</para>
        ///
        /// <para><b>Per-entry isolation</b> per Invariant 4 — both per-asteroid
        /// + per-anomaly try/catch. <b>Whole-scenario parse failure</b> per
        /// Invariant 5 — return input unchanged + log Error.</para>
        /// </summary>
        private static string SpliceDMagicScienceIntoScenario(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "DMScienceScenario" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:S4-DMagic] DMScienceScenario projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Find or create Asteroid_Science container, strip its DM_Science
            // children. Same shape as KOLONIZATION strip-and-splice.
            var asteroidContainer = node.GetNode("Asteroid_Science")?.Value;
            if (asteroidContainer == null)
            {
                asteroidContainer = new ConfigNode("") { Name = "Asteroid_Science" };
                node.AddNode(asteroidContainer);
            }
            else
            {
                foreach (var existing in asteroidContainer.GetNodes("DM_Science").ToArray())
                    asteroidContainer.RemoveNode(existing.Value);
            }

            // Find or create Anomaly_Records container, strip its
            // DM_Anomaly_List wrappers (which carry per-body groups —
            // stripping a wrapper strips all its DM_Anomaly children too).
            var anomaliesContainer = node.GetNode("Anomaly_Records")?.Value;
            if (anomaliesContainer == null)
            {
                anomaliesContainer = new ConfigNode("") { Name = "Anomaly_Records" };
                node.AddNode(anomaliesContainer);
            }
            else
            {
                foreach (var existing in anomaliesContainer.GetNodes("DM_Anomaly_List").ToArray())
                    anomaliesContainer.RemoveNode(existing.Value);
            }

            // [Review SHOULD-FIX general#3] Snapshot under per-agency lock.
            // `.ToArray()` copies references not values; the read-out loop
            // below iterates outside the lock for performance (DMagic state
            // can be large under heavy science play). This is safe ONLY because
            // AgencyDMagicRouter.Upsert*Entries REPLACES dict slots whole-cloth
            // (`agency.DMagicAsteroidScience[title] = new ...`) rather than
            // mutating an existing entry's fields in place. A future router
            // refactor that mutates instead of replaces would race against this
            // snapshot — document the invariant on the router class XML if you
            // ever consider that refactor. Same invariant applies to
            // SpliceSCANsatCoverageIntoScenario (S2 precedent) and the kolony/
            // planetary/orbital splices.
            AgencyDMagicAsteroidEntry[] asteroidSnapshot;
            AgencyDMagicAnomalyEntry[] anomalySnapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                asteroidSnapshot = targetAgency.DMagicAsteroidScience.Values.ToArray();
                anomalySnapshot = targetAgency.DMagicAnomalies.Values.ToArray();
            }

            // Splice DM_Science children (flat per-asteroid). Field names
            // lowercase per DMagic OnLoad parse contract.
            foreach (var entry in asteroidSnapshot)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Title))
                    continue;
                try
                {
                    var aNode = new ConfigNode("") { Name = "DM_Science" };
                    aNode.CreateValue(new CfgNodeValue<string, string>("title", entry.Title));
                    aNode.CreateValue(new CfgNodeValue<string, string>("bsv", entry.BaseValue.ToString("R", CultureInfo.InvariantCulture)));
                    aNode.CreateValue(new CfgNodeValue<string, string>("scv", entry.SciVal.ToString("R", CultureInfo.InvariantCulture)));
                    aNode.CreateValue(new CfgNodeValue<string, string>("sci", entry.Science.ToString("R", CultureInfo.InvariantCulture)));
                    aNode.CreateValue(new CfgNodeValue<string, string>("cap", entry.Cap.ToString("R", CultureInfo.InvariantCulture)));
                    asteroidContainer.AddNode(aNode);
                }
                catch (Exception e)
                {
                    LunaLog.Warning(
                        $"[fix:S4-DMagic] DM_Science splice skipped for agency {targetAgency.AgencyId:N} " +
                        $"title='{entry.Title}': {e.GetType().Name}: {e.Message}");
                }
            }

            // Group anomalies by BodyIndex (Decision §B nested wire shape).
            // Manual grouping rather than LINQ GroupBy keeps the deterministic
            // order + lets per-entry isolation cover the group-building step.
            var byBody = new Dictionary<int, List<AgencyDMagicAnomalyEntry>>();
            foreach (var entry in anomalySnapshot)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name))
                    continue;
                if (!byBody.TryGetValue(entry.BodyIndex, out var list))
                {
                    list = new List<AgencyDMagicAnomalyEntry>();
                    byBody[entry.BodyIndex] = list;
                }
                list.Add(entry);
            }

            // Emit one DM_Anomaly_List wrapper per body.
            foreach (var kv in byBody)
            {
                try
                {
                    var listNode = new ConfigNode("") { Name = "DM_Anomaly_List" };
                    listNode.CreateValue(new CfgNodeValue<string, string>("Body", kv.Key.ToString(CultureInfo.InvariantCulture)));
                    foreach (var entry in kv.Value)
                    {
                        try
                        {
                            var anomNode = new ConfigNode("") { Name = "DM_Anomaly" };
                            anomNode.CreateValue(new CfgNodeValue<string, string>("Name", entry.Name));
                            anomNode.CreateValue(new CfgNodeValue<string, string>("Lat", entry.Latitude.ToString("R", CultureInfo.InvariantCulture)));
                            anomNode.CreateValue(new CfgNodeValue<string, string>("Lon", entry.Longitude.ToString("R", CultureInfo.InvariantCulture)));
                            anomNode.CreateValue(new CfgNodeValue<string, string>("Alt", entry.Altitude.ToString("R", CultureInfo.InvariantCulture)));
                            listNode.AddNode(anomNode);
                        }
                        catch (Exception e)
                        {
                            LunaLog.Warning(
                                $"[fix:S4-DMagic] DM_Anomaly splice skipped for agency {targetAgency.AgencyId:N} " +
                                $"body={kv.Key} Name='{entry.Name}': {e.GetType().Name}: {e.Message}");
                        }
                    }
                    anomaliesContainer.AddNode(listNode);
                }
                catch (Exception e)
                {
                    LunaLog.Warning(
                        $"[fix:S4-DMagic] DM_Anomaly_List wrapper splice skipped for agency {targetAgency.AgencyId:N} " +
                        $"body={kv.Key}: {e.GetType().Name}: {e.Message}");
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

        /// <summary>
        /// [Phase 4 Slice B-2 — WOLF] Strip shared <c>WOLF_ScenarioModule</c>
        /// child node families and splice in per-agency entries. WOLF's
        /// on-disk shape per <c>ScenarioPersister.OnSave</c> at MKS SHA
        /// <c>ed0f6aa6</c>:
        /// <code>
        /// WOLF_ScenarioModule {
        ///     CREWROUTES { ROUTE { ... } ROUTE { ... } }
        ///     DEPOTS { DEPOT { ... } DEPOT { ... } }
        ///     HOPPERS { HOPPER { ... } HOPPER { ... } }
        ///     ROUTES { ROUTE { ... } ROUTE { ... } }
        ///     TERMINALS { TERMINAL { ... } TERMINAL { ... } }
        /// }
        /// </code>
        ///
        /// <para><b>Strip ALL 5 child node families first</b> (clean the
        /// slate). The 5 emit blocks for the 5 families are all in
        /// place: Slice B-2 added DEPOTS; Slice C added ROUTES (with FK
        /// sweep — see below); Slice D added HOPPERS (FK-swept) +
        /// TERMINALS (no FK); Slice E added CREWROUTES (origin+dest
        /// FK-swept). WOLF's <c>ScenarioPersister.OnLoad</c>'s
        /// <c>HasNode</c> guards at lines 289/303/314/332/343 tolerate a
        /// missing family — emit only when the per-agency snapshot has
        /// non-empty entries (lazy-allocate container).</para>
        ///
        /// <para><b>Emit ORDER: DEPOTS FIRST</b> (pre-spec §2.c) because
        /// WOLF's <c>ScenarioPersister.OnLoad</c> at line 288-302 loads
        /// depots first and other entity types call
        /// <c>_registry.GetDepot</c> during their <c>OnLoad</c>. Slice C
        /// ROUTES + Slice D HOPPERS + Slice E CREWROUTES all emit AFTER
        /// the depot emit + run the foreign-key integrity sweep against
        /// the just-emitted depot pool (pre-spec §2.c) to drop entries
        /// that reference depots not in the agency's pool — otherwise
        /// WOLF's OnLoad throws <c>DepotDoesNotExistException</c> on
        /// Routes/CrewRoutes or silently drops Hoppers, and the scene
        /// load misbehaves. The shared depotKeySet HashSet is built
        /// lazily by the <c>EnsureDepotKeySet</c> local function at
        /// method scope so all three FK consumers share one build.</para>
        ///
        /// <para><b>Per-entry isolation</b>: a malformed entry's failure
        /// is logged + skipped, siblings continue. Whole-scenario parse
        /// failure falls back to the input unchanged + logs at Error
        /// level (same pattern as <see cref="SpliceAgencyKolonyEntries"/>)
        /// so a hung handshake never blocks the player.</para>
        ///
        /// <para><b>ConfigNode value-name mapping.</b> The disk persistence
        /// format in <see cref="AgencyState"/> uses PascalCase
        /// (<c>Body</c> / <c>Biome</c> / <c>IsEstablished</c> /
        /// <c>IsSurveyed</c>); the wire/scenario format expected by
        /// WOLF's <c>Depot.OnLoad</c> at <c>Depot.cs:219-243</c> matches
        /// (Body / Biome / IsEstablished / IsSurveyed). 1:1 — no field-
        /// name re-mapping needed. ResourceStream sub-nodes are named
        /// <c>RESOURCE</c> on the wire (per <c>Depot.cs:256-263</c>)
        /// with values <c>ResourceName</c> / <c>Incoming</c> /
        /// <c>Outgoing</c>.</para>
        /// </summary>
        private static string SpliceAgencyWolfState(string scenarioText, AgencyState targetAgency)
        {
            if (string.IsNullOrEmpty(scenarioText))
                return scenarioText;
            ConfigNode node;
            try { node = new ConfigNode(scenarioText) { Name = "WOLF_ScenarioModule" }; }
            catch (Exception e)
            {
                LunaLog.Error($"[fix:WOLF-R4] WOLF_ScenarioModule projection parse failed for agency {targetAgency.AgencyId:N}: {e.GetType().Name}: {e.Message}");
                return scenarioText;
            }

            // Strip ALL 5 pre-existing child node families. Removing
            // top-level nodes via .ToArray() so the enumerator isn't
            // invalidated by RemoveNode during iteration. WOLF's OnLoad
            // tolerates missing nodes per ScenarioPersister.cs:289-343.
            foreach (var name in new[] { "CREWROUTES", "DEPOTS", "HOPPERS", "ROUTES", "TERMINALS" })
            {
                foreach (var existing in node.GetNodes(name).ToArray())
                    node.RemoveNode(existing.Value);
            }

            // Snapshot agency WOLF state under a SINGLE per-agency lock
            // acquisition so a concurrent router upsert across any of the
            // four families can't tear our iteration AND so the cross-family
            // view is internally consistent (route + hopper FK sweeps rely
            // on the depot snapshot being from the same moment in time).
            AgencyWolfDepotEntry[] depotSnapshot;
            AgencyWolfRouteEntry[] routeSnapshot;
            AgencyWolfHopperEntry[] hopperSnapshot;
            AgencyWolfTerminalEntry[] terminalSnapshot;
            AgencyWolfCrewRouteEntry[] crewRouteSnapshot;
            lock (AgencySystem.GetAgencyLock(targetAgency.AgencyId))
            {
                depotSnapshot = new AgencyWolfDepotEntry[targetAgency.WolfDepots.Count];
                var di = 0;
                foreach (var kvp in targetAgency.WolfDepots)
                {
                    if (kvp.Value == null) continue;
                    depotSnapshot[di++] = kvp.Value;
                }
                if (di < depotSnapshot.Length)
                    Array.Resize(ref depotSnapshot, di);

                routeSnapshot = new AgencyWolfRouteEntry[targetAgency.WolfRoutes.Count];
                var ri = 0;
                foreach (var kvp in targetAgency.WolfRoutes)
                {
                    if (kvp.Value == null) continue;
                    routeSnapshot[ri++] = kvp.Value;
                }
                if (ri < routeSnapshot.Length)
                    Array.Resize(ref routeSnapshot, ri);

                hopperSnapshot = new AgencyWolfHopperEntry[targetAgency.WolfHoppers.Count];
                var hi = 0;
                foreach (var kvp in targetAgency.WolfHoppers)
                {
                    if (kvp.Value == null) continue;
                    hopperSnapshot[hi++] = kvp.Value;
                }
                if (hi < hopperSnapshot.Length)
                    Array.Resize(ref hopperSnapshot, hi);

                terminalSnapshot = new AgencyWolfTerminalEntry[targetAgency.WolfTerminals.Count];
                var ti = 0;
                foreach (var kvp in targetAgency.WolfTerminals)
                {
                    if (kvp.Value == null) continue;
                    terminalSnapshot[ti++] = kvp.Value;
                }
                if (ti < terminalSnapshot.Length)
                    Array.Resize(ref terminalSnapshot, ti);

                crewRouteSnapshot = new AgencyWolfCrewRouteEntry[targetAgency.WolfCrewRoutes.Count];
                var ci = 0;
                foreach (var kvp in targetAgency.WolfCrewRoutes)
                {
                    if (kvp.Value == null) continue;
                    crewRouteSnapshot[ci++] = kvp.Value;
                }
                if (ci < crewRouteSnapshot.Length)
                    Array.Resize(ref crewRouteSnapshot, ci);
            }

            // Emit DEPOTS first (WOLF OnLoad ordering invariant). Slices
            // C-E will append HOPPERS / ROUTES / TERMINALS / CREWROUTES
            // emit blocks AFTER this one (with FK integrity sweeps for
            // the depot-referencing types).
            if (depotSnapshot.Length > 0)
            {
                var depotsContainer = new ConfigNode("") { Name = "DEPOTS" };
                node.AddNode(depotsContainer);
                foreach (var entry in depotSnapshot)
                {
                    if (entry == null)
                        continue;
                    try
                    {
                        var dNode = new ConfigNode("") { Name = "DEPOT" };
                        dNode.CreateValue(new CfgNodeValue<string, string>("Body", entry.Body ?? string.Empty));
                        dNode.CreateValue(new CfgNodeValue<string, string>("Biome", entry.Biome ?? string.Empty));
                        dNode.CreateValue(new CfgNodeValue<string, string>("IsEstablished", entry.IsEstablished.ToString(CultureInfo.InvariantCulture)));
                        dNode.CreateValue(new CfgNodeValue<string, string>("IsSurveyed", entry.IsSurveyed.ToString(CultureInfo.InvariantCulture)));

                        if (entry.ResourceStreams != null)
                        {
                            foreach (var stream in entry.ResourceStreams)
                            {
                                if (stream == null || string.IsNullOrEmpty(stream.ResourceName))
                                    continue;
                                // Per WOLF Depot.cs:257-262, streams are nested
                                // as RESOURCE child nodes (NOT WOLF_RESOURCE_STREAM
                                // — that's our disk-side name). Wire format must
                                // match WOLF's OnLoad parse contract.
                                var sNode = new ConfigNode("") { Name = "RESOURCE" };
                                sNode.CreateValue(new CfgNodeValue<string, string>("ResourceName", stream.ResourceName));
                                sNode.CreateValue(new CfgNodeValue<string, string>("Incoming", stream.Incoming.ToString(CultureInfo.InvariantCulture)));
                                sNode.CreateValue(new CfgNodeValue<string, string>("Outgoing", stream.Outgoing.ToString(CultureInfo.InvariantCulture)));
                                dNode.AddNode(sNode);
                            }
                        }
                        depotsContainer.AddNode(dNode);
                    }
                    catch (Exception)
                    {
                        // Per-entry isolation — drop this depot, keep others.
                    }
                }
            }

            // [Phase 4 Slice C] WOLF_ROUTES emit with FK integrity sweep
            // against the just-emitted depot pool. WOLF's Route.OnLoad at
            // Route.cs:172-173 calls _depotRegistry.GetDepot(OriginBody,
            // OriginBiome) — which throws DepotDoesNotExistException when
            // the depot is missing. That throw would kill OnLoad for the
            // whole scenario, so any route whose origin OR destination
            // composite key isn't in the depotSnapshot MUST be dropped from
            // the outgoing blob. This decouples the disk-side persistence
            // (router accepts routes regardless of depot presence — message
            // arrival ordering is not guaranteed) from the wire-side
            // projection (strict referential integrity for WOLF's parse
            // contract).
            //
            // FK sweep semantics: "depot EXISTENCE" — NOT stream-consistency.
            // The Slice B-3 Depot Negotiate path is debounced through
            // WolfDepotDebouncer (~1s flush); a Route.AddResource fires
            // internal Depot.Negotiate*, so a route message can arrive
            // before the debounced depot stream update lands. This is
            // tolerable because WOLF's Route.OnLoad does NOT read depot
            // stream state from route nodes — only depot existence. The
            // FK gate enforces that, and only that.
            //
            // Recovery-window scope: when arrival ordering produces a
            // transient FK miss (route arrives before its depot), the
            // route drops from THIS projection tick only and reappears on
            // the next tick once the depot lands. The owner's LOCAL
            // KSP-side WOLF already has the route (CreateRoute ran fully
            // local before the postfix fired); the window only affects
            // (a) the OWNER's reconnect / catchup if disconnect lands
            // mid-sequence, and (b) post-server-restart scenario load
            // before the next tick. Self-healing across ticks.
            //
            // FK key set is built from the just-emitted depot pool using
            // the per-depot composite "Body|Biome" — matches WOLF's
            // GetDepot lookup at ScenarioPersister.cs:177-189. Slices D
            // (Hoppers — single-depot FK) and E (CrewRoutes — origin/dest
            // FK like routes) MUST reuse this same depotKeySet from the
            // current scope; build-once-per-projection is intentional. The
            // HashSet is declared at the WOLF-emit-method scope (here, not
            // inside the routes block) and built lazily — null until first
            // FK consumer; subsequent consumers (Hopper FK in Slice D,
            // CrewRoute FK in Slice E) reuse the populated instance.
            HashSet<string> depotKeySet = null;

            if (routeSnapshot.Length > 0)
            {
                EnsureDepotKeySet();

                ConfigNode routesContainer = null;
                foreach (var entry in routeSnapshot)
                {
                    if (entry == null)
                        continue;
                    try
                    {
                        if (string.IsNullOrEmpty(entry.OriginBody) || string.IsNullOrEmpty(entry.OriginBiome)
                            || string.IsNullOrEmpty(entry.DestinationBody) || string.IsNullOrEmpty(entry.DestinationBiome))
                        {
                            // Malformed entry — drop, keep siblings.
                            continue;
                        }

                        // FK sweep: both origin AND destination depot must
                        // be in the emitted pool. The router accepts routes
                        // out-of-order; the projector enforces the lookup
                        // contract WOLF expects.
                        var originKey = $"{entry.OriginBody}|{entry.OriginBiome}";
                        var destKey = $"{entry.DestinationBody}|{entry.DestinationBiome}";
                        if (depotKeySet == null
                            || !depotKeySet.Contains(originKey)
                            || !depotKeySet.Contains(destKey))
                        {
                            LunaLog.Debug($"[fix:WOLF-R4] Route {originKey}→{destKey} dropped from projection (agency {targetAgency.AgencyId:N}): origin or destination depot missing from per-agency pool — will reappear once both depots' postfixes have routed.");
                            continue;
                        }

                        // Lazy-allocate the ROUTES container so an FK-sweep-
                        // empty result emits no container node at all.
                        // Slices D/E will use the same lazy-allocate pattern
                        // for their FK-swept families.
                        if (routesContainer == null)
                        {
                            routesContainer = new ConfigNode("") { Name = "ROUTES" };
                            node.AddNode(routesContainer);
                        }

                        var rNode = new ConfigNode("") { Name = "ROUTE" };
                        rNode.CreateValue(new CfgNodeValue<string, string>("OriginBody", entry.OriginBody));
                        rNode.CreateValue(new CfgNodeValue<string, string>("OriginBiome", entry.OriginBiome));
                        rNode.CreateValue(new CfgNodeValue<string, string>("DestinationBody", entry.DestinationBody));
                        rNode.CreateValue(new CfgNodeValue<string, string>("DestinationBiome", entry.DestinationBiome));
                        rNode.CreateValue(new CfgNodeValue<string, string>("Payload", entry.Payload.ToString(CultureInfo.InvariantCulture)));

                        if (entry.Resources != null)
                        {
                            foreach (var resource in entry.Resources)
                            {
                                if (resource == null || string.IsNullOrEmpty(resource.ResourceName))
                                    continue;
                                // Per WOLF Route.cs:188-205, route resources
                                // persist as RESOURCE child nodes (NOT
                                // WOLF_ROUTE_RESOURCE — that's our disk-side
                                // name). Wire format MUST match WOLF's OnLoad
                                // parse contract at Route.cs:175-185.
                                var resNode = new ConfigNode("") { Name = "RESOURCE" };
                                resNode.CreateValue(new CfgNodeValue<string, string>("ResourceName", resource.ResourceName));
                                resNode.CreateValue(new CfgNodeValue<string, string>("Quantity", resource.Quantity.ToString(CultureInfo.InvariantCulture)));
                                rNode.AddNode(resNode);
                            }
                        }
                        routesContainer.AddNode(rNode);
                    }
                    catch (Exception)
                    {
                        // Per-entry isolation — drop this route, keep others.
                    }
                }
            }

            // [Phase 4 Slice D] WOLF_HOPPERS emit with FK integrity sweep
            // against the per-agency depot pool. WOLF's
            // ScenarioPersister.OnLoad at ScenarioPersister.cs:320-329 looks
            // up each hopper's depot by Body+Biome via
            // Depots.FirstOrDefault(...) and SILENTLY DROPS hoppers whose
            // depot isn't present. Unlike Routes (which throw
            // DepotDoesNotExistException), a missing-depot hopper does not
            // crash OnLoad — but the hopper is lost. The projector enforces
            // the same FK contract proactively so the emitted blob doesn't
            // carry hoppers whose owner client (or any peer client viewing
            // the projected scenario) would silently lose them on first
            // OnLoad parse. depotKeySet is the shared HashSet declared at
            // method scope for Slices D / E reuse (built lazily by the
            // routes block; build it here if the routes block was empty).
            if (hopperSnapshot.Length > 0)
            {
                EnsureDepotKeySet();

                ConfigNode hoppersContainer = null;
                foreach (var entry in hopperSnapshot)
                {
                    if (entry == null)
                        continue;
                    try
                    {
                        if (string.IsNullOrEmpty(entry.Id)
                            || string.IsNullOrEmpty(entry.Body)
                            || string.IsNullOrEmpty(entry.Biome))
                        {
                            // Malformed entry — drop, keep siblings.
                            continue;
                        }

                        // FK sweep: hopper's Body+Biome must be in the
                        // emitted depot pool (single-depot FK, unlike Routes'
                        // two-endpoint FK). The router accepts hoppers out-
                        // of-order; the projector enforces the lookup
                        // contract WOLF expects.
                        var depotKey = $"{entry.Body}|{entry.Biome}";
                        if (depotKeySet == null || !depotKeySet.Contains(depotKey))
                        {
                            LunaLog.Debug($"[fix:WOLF-R4] Hopper '{entry.Id}' at {depotKey} dropped from projection (agency {targetAgency.AgencyId:N}): depot missing from per-agency pool — will reappear once the parent depot's postfix has routed.");
                            continue;
                        }

                        // Lazy-allocate the HOPPERS container so an FK-sweep-
                        // empty result emits no container node at all.
                        if (hoppersContainer == null)
                        {
                            hoppersContainer = new ConfigNode("") { Name = "HOPPERS" };
                            node.AddNode(hoppersContainer);
                        }

                        // Per WOLF HopperMetadata.OnSave at HopperMetadata.cs:37-49,
                        // hoppers persist as HOPPER child nodes (NOT
                        // WOLF_HOPPER — that's our disk-side name). Wire
                        // format MUST match WOLF's OnLoad parse contract.
                        var hNode = new ConfigNode("") { Name = "HOPPER" };
                        hNode.CreateValue(new CfgNodeValue<string, string>("Id", entry.Id));
                        hNode.CreateValue(new CfgNodeValue<string, string>("Body", entry.Body));
                        hNode.CreateValue(new CfgNodeValue<string, string>("Biome", entry.Biome));
                        hNode.CreateValue(new CfgNodeValue<string, string>("Recipe", entry.Recipe ?? string.Empty));
                        hoppersContainer.AddNode(hNode);
                    }
                    catch (Exception)
                    {
                        // Per-entry isolation — drop this hopper, keep others.
                    }
                }
            }

            // [Phase 4 Slice D] WOLF_TERMINALS emit. NO FK sweep — WOLF's
            // ScenarioPersister.OnLoad at ScenarioPersister.cs:343-353 loads
            // terminals via TerminalMetadata.OnLoad without a depot lookup
            // (TerminalMetadata carries its own Body+Biome per
            // TerminalMetadata.cs:9-29). A terminal can persist independent
            // of depot existence; emitting unconditionally is the source-
            // contract-correct shape. Per-entry try/catch + Id/Body/Biome
            // validation mirrors the Hoppers block.
            if (terminalSnapshot.Length > 0)
            {
                ConfigNode terminalsContainer = null;
                foreach (var entry in terminalSnapshot)
                {
                    if (entry == null)
                        continue;
                    try
                    {
                        if (string.IsNullOrEmpty(entry.Id)
                            || string.IsNullOrEmpty(entry.Body)
                            || string.IsNullOrEmpty(entry.Biome))
                        {
                            // Malformed entry — drop, keep siblings.
                            continue;
                        }

                        if (terminalsContainer == null)
                        {
                            terminalsContainer = new ConfigNode("") { Name = "TERMINALS" };
                            node.AddNode(terminalsContainer);
                        }

                        // Per WOLF TerminalMetadata.OnSave at
                        // TerminalMetadata.cs:31-37, terminals persist as
                        // TERMINAL child nodes (NOT WOLF_TERMINAL — that's
                        // our disk-side name).
                        var tNode = new ConfigNode("") { Name = "TERMINAL" };
                        tNode.CreateValue(new CfgNodeValue<string, string>("Id", entry.Id));
                        tNode.CreateValue(new CfgNodeValue<string, string>("Body", entry.Body));
                        tNode.CreateValue(new CfgNodeValue<string, string>("Biome", entry.Biome));
                        terminalsContainer.AddNode(tNode);
                    }
                    catch (Exception)
                    {
                        // Per-entry isolation — drop this terminal, keep others.
                    }
                }
            }

            // [Phase 4 Slice E] WOLF_CREWROUTES emit with FK integrity
            // sweep against the per-agency depot pool. WOLF's
            // CrewRoute.OnLoad at CrewRoute.cs:249-250 calls
            // _registry.GetDepot(OriginBody, OriginBiome) AND
            // _registry.GetDepot(DestinationBody, DestinationBiome) — both
            // throw DepotDoesNotExistException on FK miss, killing OnLoad
            // for the whole WOLF scenario. Same strict-FK behaviour as
            // Slice C Routes; emit must enforce the contract.
            //
            // The cross-agency kerbal authority gate (pre-spec §8) does
            // NOT fire in the projector — it's authoritatively enforced
            // upstream in AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger
            // (cross-agency passengers never reach AgencyState.WolfCrewRoutes
            // in the first place). The projector trusts the persisted
            // snapshot.
            if (crewRouteSnapshot.Length > 0)
            {
                // Third consumer of the lazy depotKeySet. Routes block (~line
                // 1864) + Hoppers block (~line 1963) are the prior consumers;
                // the marker comment they share documented this extraction
                // for Slice E. The local function keeps the lazy semantics
                // (single build per projection, only when there are entries
                // that consume it) while collapsing the duplicated 7-line
                // build idiom to a single call site. Pure pure: no captured
                // closures except the read-only depotSnapshot.
                EnsureDepotKeySet();

                ConfigNode crewRoutesContainer = null;
                foreach (var entry in crewRouteSnapshot)
                {
                    if (entry == null)
                        continue;
                    try
                    {
                        if (string.IsNullOrEmpty(entry.UniqueId)
                            || string.IsNullOrEmpty(entry.OriginBody) || string.IsNullOrEmpty(entry.OriginBiome)
                            || string.IsNullOrEmpty(entry.DestinationBody) || string.IsNullOrEmpty(entry.DestinationBiome))
                        {
                            // Malformed entry — drop, keep siblings.
                            continue;
                        }

                        // FK sweep: both origin AND destination depot must
                        // be in the emitted pool — mirrors the Slice C
                        // Routes block. CrewRoute.OnLoad is strict-FK like
                        // Route.OnLoad (throws DepotDoesNotExistException);
                        // emitting an FK-miss entry would crash WOLF's
                        // entire scenario load.
                        var originKey = $"{entry.OriginBody}|{entry.OriginBiome}";
                        var destKey = $"{entry.DestinationBody}|{entry.DestinationBiome}";
                        if (depotKeySet == null
                            || !depotKeySet.Contains(originKey)
                            || !depotKeySet.Contains(destKey))
                        {
                            LunaLog.Debug($"[fix:WOLF-R4] CrewRoute '{entry.UniqueId}' {originKey}→{destKey} dropped from projection (agency {targetAgency.AgencyId:N}): origin or destination depot missing from per-agency pool — will reappear once both depots' postfixes have routed.");
                            continue;
                        }

                        // Lazy-allocate the CREWROUTES container so an
                        // FK-sweep-empty result emits no container node at
                        // all. Matches the Slice C Routes + Slice D Hoppers
                        // lazy-allocate pattern.
                        if (crewRoutesContainer == null)
                        {
                            crewRoutesContainer = new ConfigNode("") { Name = "CREWROUTES" };
                            node.AddNode(crewRoutesContainer);
                        }

                        // Per WOLF CrewRoute.OnSave at CrewRoute.cs:253-276,
                        // each crew route persists as a "ROUTE" child node
                        // (NOT "CREWROUTE" — the per-entry name is "ROUTE",
                        // matching WOLF's naming choice; cf. the
                        // PASSENGER_NODE_NAME + ROUTE_NODE_NAME consts at
                        // CrewRoute.cs:44-45). Field set + ordering mirrors
                        // WOLF source exactly so OnLoad's value-by-name
                        // lookup at CrewRoute.cs:200-223 succeeds. Doubles
                        // round-trip via "R" + invariant culture (Invariant 9
                        // / BUG-013 precedent — CrewRoute.OnLoad uses
                        // double.Parse which is culture-sensitive in older
                        // KSP; invariant string keeps load-side parse stable).
                        var crNode = new ConfigNode("") { Name = "ROUTE" };
                        crNode.CreateValue(new CfgNodeValue<string, string>("ArrivalTime", entry.ArrivalTime.ToString("R", CultureInfo.InvariantCulture)));
                        crNode.CreateValue(new CfgNodeValue<string, string>("DestinationBiome", entry.DestinationBiome));
                        crNode.CreateValue(new CfgNodeValue<string, string>("DestinationBody", entry.DestinationBody));
                        crNode.CreateValue(new CfgNodeValue<string, string>("Duration", entry.Duration.ToString("R", CultureInfo.InvariantCulture)));
                        crNode.CreateValue(new CfgNodeValue<string, string>("EconomyBerths", entry.EconomyBerths.ToString(CultureInfo.InvariantCulture)));
                        crNode.CreateValue(new CfgNodeValue<string, string>("FlightNumber", entry.FlightNumber ?? string.Empty));
                        // FlightStatus emitted as the enum-NAME string per
                        // CrewRoute.OnSave at CrewRoute.cs:262 (WOLF writes
                        // routeNode.AddValue(nameof(FlightStatus),
                        // FlightStatus) which stringifies the
                        // WOLFUI.FlightStatus enum). Forward-compat against
                        // enum reordering: the string form survives;
                        // the int form would not. Empty string maps to
                        // FlightStatus.Unknown at CrewRoute.cs:240-246 (then
                        // recovered to Boarding + Passengers cleared) —
                        // safe forward-compat for a malformed wire payload.
                        crNode.CreateValue(new CfgNodeValue<string, string>("FlightStatus", entry.FlightStatus ?? string.Empty));
                        crNode.CreateValue(new CfgNodeValue<string, string>("LuxuryBerths", entry.LuxuryBerths.ToString(CultureInfo.InvariantCulture)));
                        crNode.CreateValue(new CfgNodeValue<string, string>("OriginBiome", entry.OriginBiome));
                        crNode.CreateValue(new CfgNodeValue<string, string>("OriginBody", entry.OriginBody));
                        crNode.CreateValue(new CfgNodeValue<string, string>("UniqueId", entry.UniqueId));

                        // Nested PASSENGERS → PASSENGER × N per
                        // Passenger.OnSave at Passenger.cs:68-76. Container
                        // name is PASSENGERS (WOLF const at CrewRoute.cs:44
                        // PASSENGERS_NODE_NAME) — emitted only when there's
                        // at least one valid passenger so an empty list
                        // doesn't bloat the wire / disk. WOLF tolerates
                        // missing PASSENGERS at OnLoad CrewRoute.cs:225-236.
                        if (entry.Passengers != null && entry.Passengers.Count > 0)
                        {
                            ConfigNode passengersContainer = null;
                            foreach (var passenger in entry.Passengers)
                            {
                                if (passenger == null || string.IsNullOrEmpty(passenger.Name))
                                    continue;
                                if (passengersContainer == null)
                                {
                                    passengersContainer = new ConfigNode("") { Name = "PASSENGERS" };
                                    crNode.AddNode(passengersContainer);
                                }
                                var pNode = new ConfigNode("") { Name = "PASSENGER" };
                                pNode.CreateValue(new CfgNodeValue<string, string>("Name", passenger.Name));
                                pNode.CreateValue(new CfgNodeValue<string, string>("DisplayName", passenger.DisplayName ?? string.Empty));
                                pNode.CreateValue(new CfgNodeValue<string, string>("IsTourist", passenger.IsTourist.ToString(CultureInfo.InvariantCulture)));
                                pNode.CreateValue(new CfgNodeValue<string, string>("Occupation", passenger.Occupation ?? string.Empty));
                                pNode.CreateValue(new CfgNodeValue<string, string>("Stars", passenger.Stars.ToString(CultureInfo.InvariantCulture)));
                                passengersContainer.AddNode(pNode);
                            }
                        }

                        crewRoutesContainer.AddNode(crNode);
                    }
                    catch (Exception)
                    {
                        // Per-entry isolation — drop this crew route, keep others.
                    }
                }
            }

            return node.ToString();

            // Local function: lazy-build the per-WOLF-emit depotKeySet
            // shared by the Routes (~line 1864) + Hoppers (~line 1963) +
            // CrewRoutes (above) FK consumers. Captures depotKeySet
            // (out-var of the enclosing scope) and depotSnapshot. Build
            // only once per projection — depotKeySet starts null and
            // stays null until at least one FK consumer has entries.
            // Extracted in Slice E as the third consumer per the prior
            // Slices' insertion-point marker; cosmetic but collapses 7
            // duplicated lines to a single call site.
            void EnsureDepotKeySet()
            {
                if (depotKeySet != null || depotSnapshot.Length == 0)
                    return;
                depotKeySet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in depotSnapshot)
                {
                    if (d == null) continue;
                    depotKeySet.Add($"{d.Body ?? string.Empty}|{d.Biome ?? string.Empty}");
                }
            }
        }
    }
}
