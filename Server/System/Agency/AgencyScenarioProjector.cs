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
