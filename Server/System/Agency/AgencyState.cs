using LunaConfigNode.CfgNode;
using Server.Context;
using Server.Log;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Server.System.Agency
{
    /// <summary>
    /// Pure data class representing one per-agency career on the server. Persisted as a
    /// ConfigNode-format text file at <c>Universe/Agencies/{AgencyId}.txt</c>. Lifecycle —
    /// registration, in-memory registry, periodic save — lives in <c>AgencySystem</c>
    /// (Stage 5.15a); this class is just the data + ConfigNode round-trip.
    ///
    /// Stage 5.14c scope is scalar fields only (Id, OwningPlayerName, DisplayName, Funds,
    /// Science, Reputation). The richer dictionaries / lists from spec §3 (TechTree,
    /// KerbalRoster, FacilityLevels, Contracts, Strategies, WorldFirsts, Achievements)
    /// land in their respective later Stage 5 steps as the matching wire / projection
    /// surfaces come online — each will append a child ConfigNode to the format defined
    /// here. The format is forward-compatible by design: unknown fields are tolerated
    /// (the parser simply doesn't read them) and missing fields default to their C# zero
    /// values so older files round-trip cleanly through a newer binary.
    ///
    /// Filename is the canonical GUID (no player name in path) per spec Q7 sign-off —
    /// survives player renames, eliminates the legacy-filename migration PlagueNZ
    /// shipped to recover from a player-name-keyed scheme.
    /// </summary>
    public class AgencyState
    {
        public Guid AgencyId { get; set; }
        public string OwningPlayerName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double Funds { get; set; }
        public double Science { get; set; }
        public double Reputation { get; set; }

        /// <summary>
        /// Per-agency contract pool — Active / Completed / Failed / Cancelled /
        /// DeadlineExpired / Withdrawn entries owned by this agency. Offered contracts
        /// are NOT stored here per spec §2 Q6 commitment (a); they live in the shared
        /// scenario pool so Contract Configurator's <c>ContractPreLoader</c> sees the
        /// world it expects. Populated by <see cref="AgencyContractRouter"/>; persisted
        /// as a <c>CONTRACTS</c> child node under the agency's ConfigNode file.
        ///
        /// **Concurrency contract.** Mutations to this list MUST be performed under
        /// <see cref="AgencySystem.GetAgencyLock"/>; the router holds the lock around
        /// the upsert batch, and <see cref="AgencySystem.SaveAgency"/> serialises under
        /// the same lock so a concurrent reader cannot observe a torn list.
        /// </summary>
        public List<AgencyContractEntry> Contracts { get; } = new List<AgencyContractEntry>();

        /// <summary>
        /// Per-agency tech tree — unlocked <c>RDTech</c> nodes owned by this agency.
        /// Keyed by <see cref="AgencyTechNodeEntry.TechId"/> (KSP's <c>RDTech.techID</c>)
        /// for O(1) per-agency BUG-025 dedup. Populated by
        /// <see cref="AgencyTechRouter"/>; persisted as a <c>TECHTREE</c> child node
        /// containing one <c>TECH</c> sub-node per entry (Id + Base64(Data)).
        ///
        /// **Concurrency contract.** Same shape as <see cref="Contracts"/> —
        /// mutations MUST hold <see cref="AgencySystem.GetAgencyLock"/>. The router
        /// does the read-check-then-write under one lock acquisition so a same-
        /// agency double-purchase race resolves deterministically (the second purchase
        /// sees the first's add and rejects). **Reads also need the lock** when
        /// iterating <see cref="Dictionary{TKey,TValue}.Values"/> — Dictionary's
        /// non-concurrent enumerator throws InvalidOperationException OR silently
        /// returns corrupt state on a mid-iteration mutation (worse failure mode
        /// than <see cref="Contracts"/>'s List which only ever throws). The
        /// <see cref="AgencyScenarioProjector"/> Tech splice acquires the lock
        /// implicitly via its read-snapshot pattern; future readers MUST do the same.
        ///
        /// **Why a Dictionary not a List.** Per-agency BUG-025 needs O(1) lookup by
        /// TechId on every Share* TechnologyReceived; a tech tree at endgame has
        /// 100+ entries and the lookup would be the inner loop of every tech
        /// purchase. Contracts uses List because contracts are scanned in batch and
        /// the per-entry dedup is rare (incoming batches replace, not append).
        /// </summary>
        public Dictionary<string, AgencyTechNodeEntry> TechNodes { get; } =
            new Dictionary<string, AgencyTechNodeEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency completed science subjects — KSP <c>ScienceSubject</c>
        /// records (one per experiment outcome the player has transmitted /
        /// recovered). Keyed by subject id for O(1) dedup on incoming
        /// <c>ShareProgressScienceSubjectMsgData</c>. Persisted as SUBJECTS/SUBJECT
        /// child nodes; spliced into outgoing R&amp;D scenarios as
        /// <c>Science { ... }</c> entries by <see cref="AgencyScenarioProjector"/>.
        /// Same concurrency contract as <see cref="TechNodes"/> — mutations AND
        /// reads need <see cref="AgencySystem.GetAgencyLock"/>.
        /// </summary>
        public Dictionary<string, AgencyScienceSubjectEntry> ScienceSubjects { get; } =
            new Dictionary<string, AgencyScienceSubjectEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency purchased parts. Keyed by <c>TechId</c> (the tech node the
        /// part belongs to), value is the set of <c>PartName</c>s the agency has
        /// purchased under that node. Two-level structure mirrors KSP's storage
        /// model: parts live INSIDE Tech nodes in the scenario, not as top-level
        /// entries. The projector reads this dictionary while splicing per-agency
        /// Tech nodes and adds matching <c>part = X</c> values inside each spliced
        /// Tech block. Persisted as PURCHASED_PARTS/TECH/Part hierarchy.
        ///
        /// **Why HashSet not List per tech.** Player can attempt to re-purchase
        /// the same part (KSP doesn't fire ShareProgressPartPurchase if already
        /// owned, but a buggy mod could); HashSet is O(1) dedup and the storage
        /// cost is identical for a few hundred parts.
        /// </summary>
        public Dictionary<string, HashSet<string>> PurchasedParts { get; } =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency experimental parts — KSP <c>ExpParts</c> nodes inside the
        /// R&amp;D scenario. Keyed by <c>PartName</c>, value is the count
        /// (KSP allows multiples of the same experimental part). Count==0 means
        /// "remove" per the shared-scenario writer's behavior; we honour the same
        /// signal here. Persisted as EXPERIMENTAL_PARTS/Part values; spliced as
        /// <c>ExpParts { ... }</c> child node by the projector.
        /// </summary>
        public Dictionary<string, int> ExperimentalParts { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency active strategies (KSP <c>Strategy</c> records — Mission
        /// Control's "Aggressive Negotiations" / "Open-Source Tech" / etc.).
        /// Keyed by strategy name. Same concurrency contract as
        /// <see cref="TechNodes"/>: reads and writes both need
        /// <see cref="AgencySystem.GetAgencyLock"/>.
        /// </summary>
        public Dictionary<string, AgencyStrategyEntry> Strategies { get; } =
            new Dictionary<string, AgencyStrategyEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency achievements / world firsts (KSP <c>ProgressTracking</c>
        /// child nodes — first launch, first orbit, first Mun flyby, etc.).
        /// Keyed by the achievement node's name (e.g. <c>Kerbin/RocketLaunch</c>);
        /// the projector uses that name as the spliced ConfigNode's name when
        /// re-adding under the <c>Progress</c> parent block.
        /// </summary>
        public Dictionary<string, AgencyAchievementEntry> Achievements { get; } =
            new Dictionary<string, AgencyAchievementEntry>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency KSC facility upgrade levels (Launchpad/VAB/SPH/etc.).
        /// Keyed by FacilityId (e.g. <c>SpaceCenter/LaunchPad</c>), value is
        /// the normalised level (0.0-1.0 mapped to KSP's tier slots).
        /// Projector overrides matching facility nodes' <c>lvl</c> values in
        /// the <c>ScenarioUpgradeableFacilities</c> scenario; unmentioned
        /// facilities keep the shared scenario's value (which is the
        /// stock-default baseline for a fresh universe).
        /// </summary>
        public Dictionary<string, float> FacilityLevels { get; } =
            new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>
        /// [Phase 3 Slice A] Per-agency kolonization research entries (MKS
        /// <c>KolonyTools.KolonizationManager.KolonizationInfo</c>). Keyed by
        /// <c>$"{vesselId:N}|{bodyIndex}"</c> — the per-vessel partition lets admin
        /// <c>transferagency</c> migrate kolony research with the vessel
        /// (operator sign-off session 25 Q1). Populated by Phase 3
        /// <c>AgencyKolonyRouter</c> (Slice B) and projected into outgoing
        /// <c>KolonizationScenario</c> blobs by <see cref="AgencyScenarioProjector"/>
        /// (Slice B). Persisted under a <c>KOLONY_ENTRIES</c> child node.
        ///
        /// **Concurrency contract** (same shape as <see cref="TechNodes"/>):
        /// mutations AND reads MUST hold <see cref="AgencySystem.GetAgencyLock"/>.
        /// Dictionary's non-concurrent enumerator throws (or worse) on a mid-
        /// iteration mutation; the per-agency lock is the only safe enumeration
        /// path.
        /// </summary>
        public Dictionary<string, AgencyKolonyEntry> KolonyEntries { get; } =
            new Dictionary<string, AgencyKolonyEntry>(StringComparer.Ordinal);

        /// <summary>
        /// [Phase 3 Slice A] Per-agency planetary-logistics warehouse pool (MKS
        /// <c>PlanetaryLogistics.PlanetaryLogisticsManager.PlanetaryLogisticsInfo</c>).
        /// Keyed by <c>$"{bodyIndex}|{resourceName}"</c> — body-resource-keyed,
        /// NOT vessel-keyed. Per pre-spec §4.e, planetary entries do NOT migrate
        /// on <c>transferagency</c> (the entry represents a body's pool, not a
        /// vessel's contribution). Populated by Phase 3 <c>AgencyPlanetaryRouter</c>
        /// (Slice C); projected into outgoing <c>PlanetaryLogisticsScenario</c>
        /// blobs (Slice C). Persisted under a <c>PLANETARY_ENTRIES</c> child node.
        ///
        /// **Concurrency contract**: same as <see cref="KolonyEntries"/>.
        /// </summary>
        public Dictionary<string, AgencyPlanetaryEntry> PlanetaryEntries { get; } =
            new Dictionary<string, AgencyPlanetaryEntry>(StringComparer.Ordinal);

        /// <summary>
        /// [Phase 3 Slice A] Per-agency orbital-logistics transfer queue (MKS
        /// <c>KolonyTools.ScenarioOrbitalLogistics.PendingTransfers</c> +
        /// <c>ExpiredTransfers</c> projected per-agency). Keyed by
        /// <see cref="AgencyOrbitalTransferEntry.TransferGuid"/>. Populated by
        /// Phase 3 <c>AgencyOrbitalRouter</c> (Slice D) on transfer state-machine
        /// transitions; projected into outgoing <c>ScenarioOrbitalLogistics</c>
        /// blobs (Slice D). Persisted under an <c>ORBITAL_TRANSFERS</c> child node.
        /// Per pre-spec §4.e: on <c>transferagency</c>, transfers where the
        /// moved vessel is the Destination MIGRATE; transfers where the moved
        /// vessel is the Origin stay in the source agency.
        ///
        /// **Concurrency contract**: same as <see cref="KolonyEntries"/>.
        /// </summary>
        public Dictionary<Guid, AgencyOrbitalTransferEntry> OrbitalTransfers { get; } =
            new Dictionary<Guid, AgencyOrbitalTransferEntry>();

        /// <summary>
        /// Universe-relative folder that holds one ConfigNode-format file per agency.
        /// Created at server boot via <see cref="Server.Context.Universe.CheckUniverse"/>
        /// alongside the other Universe child folders, so <see cref="FileHandler.WriteAtomic"/>
        /// can run from the first <see cref="AgencySystem"/> save without a separate
        /// pre-flight directory check.
        /// </summary>
        public static string AgenciesPath => Path.Combine(ServerContext.UniverseDirectory, "Agencies");

        /// <summary>
        /// Canonical file path for this agency's persisted state. The "N" format specifier
        /// gives a 32-char hex string with no hyphens — round-trips through <see cref="Guid.Parse"/>
        /// either way, and keeps filenames operator-friendly (no path-confusing braces).
        /// </summary>
        public string FilePath => Path.Combine(AgenciesPath, AgencyId.ToString("N", CultureInfo.InvariantCulture) + ".txt");

        /// <summary>
        /// Serializes this agency's scalar state to the bare key-value ConfigNode text format
        /// used by <see cref="LunaConfigNode.CfgNode.ConfigNode"/>. No outer <c>{}</c> wrapper
        /// — matches the convention <see cref="ScenarioStoreSystem.GetScenarioInConfigNodeFormat"/>
        /// established (see its summary for why braces are stripped).
        /// </summary>
        public string Serialize() => ToConfigNode().ToString();

        /// <summary>
        /// Builds a ConfigNode for this agency. Exposed (not just internal-to-Serialize) so
        /// future steps can compose AgencyState fragments into larger wire payloads without
        /// reparsing text.
        /// </summary>
        public ConfigNode ToConfigNode()
        {
            var node = new ConfigNode("") { Name = "AGENCY" };
            node.CreateValue(new CfgNodeValue<string, string>("AgencyId", AgencyId.ToString("N", CultureInfo.InvariantCulture)));
            node.CreateValue(new CfgNodeValue<string, string>("OwningPlayerName", OwningPlayerName ?? string.Empty));
            node.CreateValue(new CfgNodeValue<string, string>("DisplayName", DisplayName ?? string.Empty));
            node.CreateValue(new CfgNodeValue<string, string>("Funds", Funds.ToString("R", CultureInfo.InvariantCulture)));
            node.CreateValue(new CfgNodeValue<string, string>("Science", Science.ToString("R", CultureInfo.InvariantCulture)));
            node.CreateValue(new CfgNodeValue<string, string>("Reputation", Reputation.ToString("R", CultureInfo.InvariantCulture)));

            // Contracts persist as a CONTRACTS child node containing one CONTRACT sub-node
            // per entry. Emitted only when non-empty so older / pristine agency files stay
            // visually identical to their 5.14c shape. Bytes are Base64-encoded since
            // ConfigNode values are strings; decompressed form is stored so an operator
            // diffing two AgencyState files can see the actual contract ConfigNode bytes
            // (compression is a wire-only concern handled by ContractInfo at serialize time).
            if (Contracts.Count > 0)
            {
                var contractsRoot = new ConfigNode("") { Name = "CONTRACTS" };
                node.AddNode(contractsRoot);
                foreach (var entry in Contracts)
                {
                    if (entry == null)
                        continue;
                    var contractNode = new ConfigNode("") { Name = "CONTRACT" };
                    contractNode.CreateValue(new CfgNodeValue<string, string>("Guid", entry.ContractGuid.ToString("N", CultureInfo.InvariantCulture)));
                    contractNode.CreateValue(new CfgNodeValue<string, string>("State", entry.State ?? string.Empty));
                    var dataBytes = entry.Data ?? Array.Empty<byte>();
                    var len = Math.Min(entry.NumBytes, dataBytes.Length);
                    var base64 = len > 0 ? Convert.ToBase64String(dataBytes, 0, len) : string.Empty;
                    contractNode.CreateValue(new CfgNodeValue<string, string>("Data", base64));
                    contractsRoot.AddNode(contractNode);
                }
            }

            // [Stage 5.17e-4] Tech tree persists as a TECHTREE child node containing one
            // TECH sub-node per unlocked entry. Same shape as CONTRACTS — emitted only
            // when non-empty (pristine agency files stay visually identical), bytes are
            // Base64-encoded decompressed ConfigNode form so operators can diff readable
            // payloads. Null/empty entries are silently dropped.
            if (TechNodes.Count > 0)
            {
                var techRoot = new ConfigNode("") { Name = "TECHTREE" };
                node.AddNode(techRoot);
                foreach (var entry in TechNodes.Values)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.TechId))
                        continue;
                    var techNode = new ConfigNode("") { Name = "TECH" };
                    techNode.CreateValue(new CfgNodeValue<string, string>("Id", entry.TechId));
                    var dataBytes = entry.Data ?? Array.Empty<byte>();
                    var len = Math.Min(entry.NumBytes, dataBytes.Length);
                    var base64 = len > 0 ? Convert.ToBase64String(dataBytes, 0, len) : string.Empty;
                    techNode.CreateValue(new CfgNodeValue<string, string>("Data", base64));
                    techRoot.AddNode(techNode);
                }
            }

            // [Stage 5.17e-5] Completed science subjects — same shape as TECHTREE.
            // One SUBJECT child node per entry; Id + Base64(Data) values.
            if (ScienceSubjects.Count > 0)
            {
                var subjectsRoot = new ConfigNode("") { Name = "SUBJECTS" };
                node.AddNode(subjectsRoot);
                foreach (var entry in ScienceSubjects.Values)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.SubjectId))
                        continue;
                    var subjectNode = new ConfigNode("") { Name = "SUBJECT" };
                    subjectNode.CreateValue(new CfgNodeValue<string, string>("Id", entry.SubjectId));
                    var dataBytes = entry.Data ?? Array.Empty<byte>();
                    var len = Math.Min(entry.NumBytes, dataBytes.Length);
                    var base64 = len > 0 ? Convert.ToBase64String(dataBytes, 0, len) : string.Empty;
                    subjectNode.CreateValue(new CfgNodeValue<string, string>("Data", base64));
                    subjectsRoot.AddNode(subjectNode);
                }
            }

            // [Stage 5.17e-5] Purchased parts — two-level structure (TECH child
            // nodes, each containing Part values). The projector merges these
            // into per-agency Tech blocks during R&D splice.
            if (PurchasedParts.Count > 0)
            {
                var partsRoot = new ConfigNode("") { Name = "PURCHASED_PARTS" };
                node.AddNode(partsRoot);
                foreach (var kvp in PurchasedParts)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null || kvp.Value.Count == 0)
                        continue;
                    var techNode = new ConfigNode("") { Name = "TECH" };
                    techNode.CreateValue(new CfgNodeValue<string, string>("Id", kvp.Key));
                    foreach (var partName in kvp.Value)
                    {
                        if (string.IsNullOrEmpty(partName))
                            continue;
                        techNode.CreateValue(new CfgNodeValue<string, string>("Part", partName));
                    }
                    partsRoot.AddNode(techNode);
                }
            }

            // [Stage 5.17e-5] Experimental parts — flat key=value pairs under
            // EXPERIMENTAL_PARTS. Count=0 entries are omitted (matches the
            // shared-scenario writer's count==0 → remove semantics; persisting
            // a zero entry would be a forward-compat foot-gun). Build the inner
            // values FIRST and add the parent node only if at least one valid
            // entry made it through — otherwise an all-zero ExperimentalParts
            // would produce an empty EXPERIMENTAL_PARTS block on disk (noise).
            if (ExperimentalParts.Count > 0)
            {
                ConfigNode expRoot = null;
                foreach (var kvp in ExperimentalParts)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || kvp.Value <= 0)
                        continue;
                    if (expRoot == null)
                    {
                        expRoot = new ConfigNode("") { Name = "EXPERIMENTAL_PARTS" };
                        node.AddNode(expRoot);
                    }
                    expRoot.CreateValue(new CfgNodeValue<string, string>(kvp.Key,
                        kvp.Value.ToString(CultureInfo.InvariantCulture)));
                }
            }

            // [Stage 5.17e-6] Strategies — same shape as TECHTREE / SUBJECTS.
            if (Strategies.Count > 0)
            {
                var stratRoot = new ConfigNode("") { Name = "STRATEGIES" };
                node.AddNode(stratRoot);
                foreach (var entry in Strategies.Values)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.StrategyName))
                        continue;
                    var stratNode = new ConfigNode("") { Name = "STRATEGY" };
                    stratNode.CreateValue(new CfgNodeValue<string, string>("Name", entry.StrategyName));
                    var dataBytes = entry.Data ?? Array.Empty<byte>();
                    var len = Math.Min(entry.NumBytes, dataBytes.Length);
                    var base64 = len > 0 ? Convert.ToBase64String(dataBytes, 0, len) : string.Empty;
                    stratNode.CreateValue(new CfgNodeValue<string, string>("Data", base64));
                    stratRoot.AddNode(stratNode);
                }
            }

            // [Stage 5.17e-6] Achievements — same shape, ACHIEVEMENT child nodes.
            if (Achievements.Count > 0)
            {
                var achRoot = new ConfigNode("") { Name = "ACHIEVEMENTS" };
                node.AddNode(achRoot);
                foreach (var entry in Achievements.Values)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Id))
                        continue;
                    var achNode = new ConfigNode("") { Name = "ACHIEVEMENT" };
                    achNode.CreateValue(new CfgNodeValue<string, string>("Id", entry.Id));
                    var dataBytes = entry.Data ?? Array.Empty<byte>();
                    var len = Math.Min(entry.NumBytes, dataBytes.Length);
                    var base64 = len > 0 ? Convert.ToBase64String(dataBytes, 0, len) : string.Empty;
                    achNode.CreateValue(new CfgNodeValue<string, string>("Data", base64));
                    achRoot.AddNode(achNode);
                }
            }

            // [Stage 5.17e-6] Facility levels — flat key=value pairs.
            if (FacilityLevels.Count > 0)
            {
                ConfigNode facRoot = null;
                foreach (var kvp in FacilityLevels)
                {
                    if (string.IsNullOrEmpty(kvp.Key))
                        continue;
                    if (facRoot == null)
                    {
                        facRoot = new ConfigNode("") { Name = "FACILITY_LEVELS" };
                        node.AddNode(facRoot);
                    }
                    facRoot.CreateValue(new CfgNodeValue<string, string>(kvp.Key,
                        kvp.Value.ToString(CultureInfo.InvariantCulture)));
                }
            }

            // [Phase 3 Slice A] Kolony entries — one KOLONY sub-node per entry.
            // Plain numeric values, no Base64 (the entry is not a wire-compressed
            // payload). All doubles use invariant culture per the BUG-013 precedent
            // pinned by AgencyStateTest.Serialize_UsesInvariantCultureForDoubles.
            // Emitted only when non-empty so a pristine 5.14c-era agency file stays
            // visually identical (same convention as CONTRACTS at line 199).
            if (KolonyEntries.Count > 0)
            {
                var kolonyRoot = new ConfigNode("") { Name = "KOLONY_ENTRIES" };
                node.AddNode(kolonyRoot);
                foreach (var entry in KolonyEntries.Values)
                {
                    if (entry == null)
                        continue;
                    var kNode = new ConfigNode("") { Name = "KOLONY" };
                    kNode.CreateValue(new CfgNodeValue<string, string>("VesselId", entry.VesselId ?? string.Empty));
                    kNode.CreateValue(new CfgNodeValue<string, string>("BodyIndex", entry.BodyIndex.ToString(CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("LastUpdate", entry.LastUpdate.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("KolonyDate", entry.KolonyDate.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("GeologyResearch", entry.GeologyResearch.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("BotanyResearch", entry.BotanyResearch.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("KolonizationResearch", entry.KolonizationResearch.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("Science", entry.Science.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("Reputation", entry.Reputation.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("Funds", entry.Funds.ToString("R", CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("RepBoosters", entry.RepBoosters.ToString(CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("FundsBoosters", entry.FundsBoosters.ToString(CultureInfo.InvariantCulture)));
                    kNode.CreateValue(new CfgNodeValue<string, string>("ScienceBoosters", entry.ScienceBoosters.ToString(CultureInfo.InvariantCulture)));
                    kolonyRoot.AddNode(kNode);
                }
            }

            // [Phase 3 Slice A] Planetary entries — one PLANETARY sub-node per
            // entry. OwningVesselId persisted as Guid "N" format for consistency
            // with the agency-file naming convention.
            if (PlanetaryEntries.Count > 0)
            {
                var planetaryRoot = new ConfigNode("") { Name = "PLANETARY_ENTRIES" };
                node.AddNode(planetaryRoot);
                foreach (var entry in PlanetaryEntries.Values)
                {
                    if (entry == null)
                        continue;
                    var pNode = new ConfigNode("") { Name = "PLANETARY" };
                    pNode.CreateValue(new CfgNodeValue<string, string>("OwningVesselId", entry.OwningVesselId.ToString("N", CultureInfo.InvariantCulture)));
                    pNode.CreateValue(new CfgNodeValue<string, string>("BodyIndex", entry.BodyIndex.ToString(CultureInfo.InvariantCulture)));
                    pNode.CreateValue(new CfgNodeValue<string, string>("ResourceName", entry.ResourceName ?? string.Empty));
                    pNode.CreateValue(new CfgNodeValue<string, string>("StoredQuantity", entry.StoredQuantity.ToString("R", CultureInfo.InvariantCulture)));
                    planetaryRoot.AddNode(pNode);
                }
            }

            // [Phase 3 Slice A] Orbital transfers — one TRANSFER sub-node per
            // entry. PayloadBytes is Base64-encoded decompressed form (matches
            // the Stage 5.17d Contracts pattern at line 199 — operators diffing
            // two AgencyState files see readable payloads; compression is a
            // wire-only concern). Status persisted as raw int (opaque to server;
            // client maps to MKS' DeliveryStatus enum at projection time).
            if (OrbitalTransfers.Count > 0)
            {
                var orbitalRoot = new ConfigNode("") { Name = "ORBITAL_TRANSFERS" };
                node.AddNode(orbitalRoot);
                foreach (var entry in OrbitalTransfers.Values)
                {
                    if (entry == null)
                        continue;
                    var oNode = new ConfigNode("") { Name = "TRANSFER" };
                    oNode.CreateValue(new CfgNodeValue<string, string>("TransferGuid", entry.TransferGuid.ToString("N", CultureInfo.InvariantCulture)));
                    oNode.CreateValue(new CfgNodeValue<string, string>("OriginVesselId", entry.OriginVesselId.ToString("N", CultureInfo.InvariantCulture)));
                    oNode.CreateValue(new CfgNodeValue<string, string>("DestinationVesselId", entry.DestinationVesselId.ToString("N", CultureInfo.InvariantCulture)));
                    oNode.CreateValue(new CfgNodeValue<string, string>("Status", entry.Status.ToString(CultureInfo.InvariantCulture)));
                    oNode.CreateValue(new CfgNodeValue<string, string>("StartTime", entry.StartTime.ToString("R", CultureInfo.InvariantCulture)));
                    oNode.CreateValue(new CfgNodeValue<string, string>("Duration", entry.Duration.ToString("R", CultureInfo.InvariantCulture)));
                    var dataBytes = entry.PayloadBytes ?? Array.Empty<byte>();
                    var len = Math.Min(entry.NumBytes, dataBytes.Length);
                    var base64 = len > 0 ? Convert.ToBase64String(dataBytes, 0, len) : string.Empty;
                    oNode.CreateValue(new CfgNodeValue<string, string>("PayloadBytes", base64));
                    orbitalRoot.AddNode(oNode);
                }
            }

            return node;
        }

        /// <summary>
        /// Parses agency state from ConfigNode-format text. Accepts both bare key-value
        /// content (the format <see cref="Serialize"/> produces) and brace-wrapped content
        /// (operators may hand-edit; KSP-side ConfigNode serializers emit braces). Missing
        /// fields default to their C# zero values — forward-compatible for older files
        /// that predate a future field addition.
        /// </summary>
        public static AgencyState Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            var trimmed = text.Trim();
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                trimmed = trimmed.Substring(1, trimmed.Length - 2);

            var node = new ConfigNode(trimmed) { Name = "AGENCY" };
            return FromConfigNode(node);
        }

        /// <summary>
        /// Builds an AgencyState from a pre-parsed ConfigNode. Tolerant of missing fields
        /// (zero/empty defaults) per the forward-compatibility rationale on <see cref="Parse"/>.
        /// Throws only on an unparseable GUID — the AgencyId is the load-bearing identity
        /// and a corrupt one signals a worse-than-default state.
        /// </summary>
        public static AgencyState FromConfigNode(ConfigNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            var state = new AgencyState
            {
                OwningPlayerName = node.GetValue("OwningPlayerName")?.Value ?? string.Empty,
                DisplayName = node.GetValue("DisplayName")?.Value ?? string.Empty,
                Funds = ParseDoubleOrZero(node.GetValue("Funds")?.Value),
                Science = ParseDoubleOrZero(node.GetValue("Science")?.Value),
                Reputation = ParseDoubleOrZero(node.GetValue("Reputation")?.Value),
            };

            var rawId = node.GetValue("AgencyId")?.Value;
            if (string.IsNullOrEmpty(rawId))
                throw new FormatException("AgencyState ConfigNode missing required AgencyId field.");

            state.AgencyId = Guid.Parse(rawId);

            // Forward-compat: CONTRACTS child node is Stage 5.17d addition. Older
            // AgencyState files predate it and load with an empty Contracts list.
            // Per-entry parse failures (malformed Guid / unparseable Base64) are
            // logged via the caller's catch — same per-contract isolation rule the
            // router applies on the wire side.
            var contractsRoot = node.GetNode("CONTRACTS")?.Value;
            if (contractsRoot != null)
            {
                foreach (var contractEntry in contractsRoot.GetNodes("CONTRACT"))
                {
                    var entryNode = contractEntry.Value;
                    var rawGuid = entryNode.GetValue("Guid")?.Value;
                    if (string.IsNullOrEmpty(rawGuid))
                        continue;
                    if (!Guid.TryParse(rawGuid, out var contractGuid))
                        continue;

                    var entry = new AgencyContractEntry
                    {
                        ContractGuid = contractGuid,
                        State = entryNode.GetValue("State")?.Value ?? string.Empty,
                    };

                    var base64 = entryNode.GetValue("Data")?.Value;
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            entry.Data = Convert.FromBase64String(base64);
                            entry.NumBytes = entry.Data.Length;
                        }
                        catch (FormatException)
                        {
                            // Malformed payload: keep entry with empty data so the slot
                            // is observable (operator can see "this contract is broken")
                            // without aborting the parent agency load.
                            entry.Data = Array.Empty<byte>();
                            entry.NumBytes = 0;
                        }
                    }

                    state.Contracts.Add(entry);
                }
            }

            // [Stage 5.17e-4] Forward-compat: TECHTREE child node is Stage 5.17e-4
            // addition. Older AgencyState files predate it and load with an empty
            // TechNodes dict. Per-entry parse failures (missing Id / malformed
            // Base64) are silently skipped — same per-entry isolation rule as
            // contracts. Duplicate TechId entries (operator hand-edited the file)
            // keep the LAST occurrence (Dictionary assignment overwrites).
            var techRoot = node.GetNode("TECHTREE")?.Value;
            if (techRoot != null)
            {
                foreach (var techEntry in techRoot.GetNodes("TECH"))
                {
                    var entryNode = techEntry.Value;
                    var techId = entryNode.GetValue("Id")?.Value;
                    if (string.IsNullOrEmpty(techId))
                        continue;

                    var entry = new AgencyTechNodeEntry { TechId = techId };

                    var base64 = entryNode.GetValue("Data")?.Value;
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            entry.Data = Convert.FromBase64String(base64);
                            entry.NumBytes = entry.Data.Length;
                        }
                        catch (FormatException)
                        {
                            // Malformed payload: keep entry with empty data so the
                            // tech is still recognised as unlocked (BUG-025 dedup
                            // still fires) but the payload is opaque. Operator can
                            // diff the file and spot the broken entry.
                            entry.Data = Array.Empty<byte>();
                            entry.NumBytes = 0;
                        }
                    }

                    state.TechNodes[techId] = entry;
                }
            }

            // [Stage 5.17e-5] Forward-compat for SUBJECTS / PURCHASED_PARTS /
            // EXPERIMENTAL_PARTS — Stage 5.17e-5 additions. Older AgencyState
            // files predate them; missing nodes load as empty collections.
            // Per-entry parse failures are silently skipped, matching the
            // TECHTREE forward-compat contract.
            var subjectsRoot = node.GetNode("SUBJECTS")?.Value;
            if (subjectsRoot != null)
            {
                foreach (var subjectEntry in subjectsRoot.GetNodes("SUBJECT"))
                {
                    var entryNode = subjectEntry.Value;
                    var subjectId = entryNode.GetValue("Id")?.Value;
                    if (string.IsNullOrEmpty(subjectId))
                        continue;
                    var entry = new AgencyScienceSubjectEntry { SubjectId = subjectId };
                    var base64 = entryNode.GetValue("Data")?.Value;
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            entry.Data = Convert.FromBase64String(base64);
                            entry.NumBytes = entry.Data.Length;
                        }
                        catch (FormatException)
                        {
                            entry.Data = Array.Empty<byte>();
                            entry.NumBytes = 0;
                        }
                    }
                    state.ScienceSubjects[subjectId] = entry;
                }
            }

            var partsRoot = node.GetNode("PURCHASED_PARTS")?.Value;
            if (partsRoot != null)
            {
                foreach (var techEntry in partsRoot.GetNodes("TECH"))
                {
                    var entryNode = techEntry.Value;
                    var techId = entryNode.GetValue("Id")?.Value;
                    if (string.IsNullOrEmpty(techId))
                        continue;
                    var partSet = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var partValue in entryNode.GetValues("Part"))
                    {
                        if (!string.IsNullOrEmpty(partValue.Value))
                            partSet.Add(partValue.Value);
                    }
                    if (partSet.Count > 0)
                        state.PurchasedParts[techId] = partSet;
                }
            }

            var expRoot = node.GetNode("EXPERIMENTAL_PARTS")?.Value;
            if (expRoot != null)
            {
                foreach (var partValue in expRoot.GetAllValues())
                {
                    if (string.IsNullOrEmpty(partValue.Key))
                        continue;
                    if (!int.TryParse(partValue.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                        continue;
                    if (count <= 0)
                        continue;
                    state.ExperimentalParts[partValue.Key] = count;
                }
            }

            // [Stage 5.17e-6] Strategies / Achievements / FacilityLevels —
            // Stage 5.17e-6 additions. Same forward-compat shape: missing nodes
            // load as empty collections; per-entry parse failures isolated.
            var strategiesRoot = node.GetNode("STRATEGIES")?.Value;
            if (strategiesRoot != null)
            {
                foreach (var strategyEntry in strategiesRoot.GetNodes("STRATEGY"))
                {
                    var entryNode = strategyEntry.Value;
                    var name = entryNode.GetValue("Name")?.Value;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    var entry = new AgencyStrategyEntry { StrategyName = name };
                    var base64 = entryNode.GetValue("Data")?.Value;
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            entry.Data = Convert.FromBase64String(base64);
                            entry.NumBytes = entry.Data.Length;
                        }
                        catch (FormatException) { entry.Data = Array.Empty<byte>(); entry.NumBytes = 0; }
                    }
                    state.Strategies[name] = entry;
                }
            }

            var achievementsRoot = node.GetNode("ACHIEVEMENTS")?.Value;
            if (achievementsRoot != null)
            {
                foreach (var achEntry in achievementsRoot.GetNodes("ACHIEVEMENT"))
                {
                    var entryNode = achEntry.Value;
                    var id = entryNode.GetValue("Id")?.Value;
                    if (string.IsNullOrEmpty(id))
                        continue;
                    var entry = new AgencyAchievementEntry { Id = id };
                    var base64 = entryNode.GetValue("Data")?.Value;
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            entry.Data = Convert.FromBase64String(base64);
                            entry.NumBytes = entry.Data.Length;
                        }
                        catch (FormatException) { entry.Data = Array.Empty<byte>(); entry.NumBytes = 0; }
                    }
                    state.Achievements[id] = entry;
                }
            }

            var facilityRoot = node.GetNode("FACILITY_LEVELS")?.Value;
            if (facilityRoot != null)
            {
                foreach (var facilityValue in facilityRoot.GetAllValues())
                {
                    if (string.IsNullOrEmpty(facilityValue.Key))
                        continue;
                    if (!float.TryParse(facilityValue.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var level))
                        continue;
                    state.FacilityLevels[facilityValue.Key] = level;
                }
            }

            // [Phase 3 Slice A] Kolony entries parse — KOLONY_ENTRIES is a
            // Phase 3 addition. Older AgencyState files predate it and load
            // with an empty KolonyEntries dict (forward-compat). Per-entry
            // isolation: missing required fields (VesselId / BodyIndex) skip
            // the slot with a [fix:MKS-R2] LunaLog.Warning so operator hand-edits
            // produce a visible trace (review-finding-#3 — silent drop would be
            // an operability gap matching the 5.17d router's per-entry log
            // pattern at AgencyContractRouter.cs:119-122). Malformed numerics
            // default to 0 via ParseDoubleOrZero / ParseIntOrZero. Duplicate
            // dict keys keep the LAST occurrence (Dictionary indexer overwrites
            // — operator hand-edit case). VesselId is normalized to Guid "N"
            // form when parseable so the dict key matches what the future
            // Slice B router emits (pre-spec §2.f promise + review-finding-#1);
            // raw-string fallback preserves the existing key for hand-edited
            // pre-Slice-B test data + handles MKS quirks where VesselId may
            // not be a clean Guid.
            var kolonyRoot = node.GetNode("KOLONY_ENTRIES")?.Value;
            if (kolonyRoot != null)
            {
                foreach (var kEntry in kolonyRoot.GetNodes("KOLONY"))
                {
                    var entryNode = kEntry.Value;
                    var rawVesselId = entryNode.GetValue("VesselId")?.Value;
                    if (string.IsNullOrEmpty(rawVesselId))
                    {
                        LunaLog.Warning("[fix:MKS-R2] KOLONY entry skipped: missing VesselId");
                        continue;
                    }
                    if (!int.TryParse(entryNode.GetValue("BodyIndex")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bodyIndex))
                    {
                        LunaLog.Warning($"[fix:MKS-R2] KOLONY entry skipped: unparseable BodyIndex (VesselId={rawVesselId})");
                        continue;
                    }

                    // Normalize VesselId to Guid "N" form when the raw value is a
                    // valid Guid in any standard format — guarantees Slice B routers
                    // and hand-edited files converge on the same dict key.
                    var vesselId = Guid.TryParse(rawVesselId, out var vesselGuid)
                        ? vesselGuid.ToString("N", CultureInfo.InvariantCulture)
                        : rawVesselId;

                    var entry = new AgencyKolonyEntry
                    {
                        VesselId = vesselId,
                        BodyIndex = bodyIndex,
                        LastUpdate = ParseDoubleOrZero(entryNode.GetValue("LastUpdate")?.Value),
                        KolonyDate = ParseDoubleOrZero(entryNode.GetValue("KolonyDate")?.Value),
                        GeologyResearch = ParseDoubleOrZero(entryNode.GetValue("GeologyResearch")?.Value),
                        BotanyResearch = ParseDoubleOrZero(entryNode.GetValue("BotanyResearch")?.Value),
                        KolonizationResearch = ParseDoubleOrZero(entryNode.GetValue("KolonizationResearch")?.Value),
                        Science = ParseDoubleOrZero(entryNode.GetValue("Science")?.Value),
                        Reputation = ParseDoubleOrZero(entryNode.GetValue("Reputation")?.Value),
                        Funds = ParseDoubleOrZero(entryNode.GetValue("Funds")?.Value),
                        RepBoosters = ParseIntOrZero(entryNode.GetValue("RepBoosters")?.Value),
                        FundsBoosters = ParseIntOrZero(entryNode.GetValue("FundsBoosters")?.Value),
                        ScienceBoosters = ParseIntOrZero(entryNode.GetValue("ScienceBoosters")?.Value),
                    };

                    state.KolonyEntries[$"{vesselId}|{bodyIndex.ToString(CultureInfo.InvariantCulture)}"] = entry;
                }
            }

            // [Phase 3 Slice A] Planetary entries parse — PLANETARY_ENTRIES is
            // a Phase 3 addition. Same forward-compat shape as KOLONY_ENTRIES.
            // Per-entry isolation: missing or unparseable OwningVesselId Guid
            // / BodyIndex / ResourceName skip the slot with a [fix:MKS-R2]
            // LunaLog.Warning per review-finding-#3.
            var planetaryRoot = node.GetNode("PLANETARY_ENTRIES")?.Value;
            if (planetaryRoot != null)
            {
                foreach (var pEntry in planetaryRoot.GetNodes("PLANETARY"))
                {
                    var entryNode = pEntry.Value;
                    var rawOwningVesselId = entryNode.GetValue("OwningVesselId")?.Value;
                    if (!Guid.TryParse(rawOwningVesselId, out var owningVesselId))
                    {
                        LunaLog.Warning($"[fix:MKS-R2] PLANETARY entry skipped: unparseable OwningVesselId ({rawOwningVesselId ?? "<null>"})");
                        continue;
                    }
                    if (!int.TryParse(entryNode.GetValue("BodyIndex")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bodyIndex))
                    {
                        LunaLog.Warning($"[fix:MKS-R2] PLANETARY entry skipped: unparseable BodyIndex (OwningVesselId={owningVesselId:N})");
                        continue;
                    }
                    var resourceName = entryNode.GetValue("ResourceName")?.Value;
                    if (string.IsNullOrEmpty(resourceName))
                    {
                        LunaLog.Warning($"[fix:MKS-R2] PLANETARY entry skipped: missing ResourceName (OwningVesselId={owningVesselId:N})");
                        continue;
                    }

                    var entry = new AgencyPlanetaryEntry
                    {
                        OwningVesselId = owningVesselId,
                        BodyIndex = bodyIndex,
                        ResourceName = resourceName,
                        StoredQuantity = ParseDoubleOrZero(entryNode.GetValue("StoredQuantity")?.Value),
                    };

                    state.PlanetaryEntries[$"{bodyIndex.ToString(CultureInfo.InvariantCulture)}|{resourceName}"] = entry;
                }
            }

            // [Phase 3 Slice A] Orbital transfers parse — ORBITAL_TRANSFERS is
            // a Phase 3 addition. Per-entry isolation: missing TransferGuid
            // skips with [fix:MKS-R2] LunaLog.Warning (review-finding-#3);
            // malformed Base64 PayloadBytes reduces to empty + NumBytes=0
            // (matches the Contracts per-entry rule at line 444-451) so the slot
            // is observable (operator can see "this transfer's payload is broken")
            // without aborting the parent agency load.
            var orbitalRoot = node.GetNode("ORBITAL_TRANSFERS")?.Value;
            if (orbitalRoot != null)
            {
                foreach (var oEntry in orbitalRoot.GetNodes("TRANSFER"))
                {
                    var entryNode = oEntry.Value;
                    var rawTransferGuid = entryNode.GetValue("TransferGuid")?.Value;
                    if (!Guid.TryParse(rawTransferGuid, out var transferGuid))
                    {
                        LunaLog.Warning($"[fix:MKS-R2] TRANSFER entry skipped: unparseable TransferGuid ({rawTransferGuid ?? "<null>"})");
                        continue;
                    }
                    Guid.TryParse(entryNode.GetValue("OriginVesselId")?.Value, out var originVesselId);
                    Guid.TryParse(entryNode.GetValue("DestinationVesselId")?.Value, out var destinationVesselId);

                    var entry = new AgencyOrbitalTransferEntry
                    {
                        TransferGuid = transferGuid,
                        OriginVesselId = originVesselId,
                        DestinationVesselId = destinationVesselId,
                        Status = ParseIntOrZero(entryNode.GetValue("Status")?.Value),
                        StartTime = ParseDoubleOrZero(entryNode.GetValue("StartTime")?.Value),
                        Duration = ParseDoubleOrZero(entryNode.GetValue("Duration")?.Value),
                    };

                    var base64 = entryNode.GetValue("PayloadBytes")?.Value;
                    if (!string.IsNullOrEmpty(base64))
                    {
                        try
                        {
                            entry.PayloadBytes = Convert.FromBase64String(base64);
                            entry.NumBytes = entry.PayloadBytes.Length;
                        }
                        catch (FormatException)
                        {
                            LunaLog.Warning($"[fix:MKS-R2] TRANSFER entry kept with empty PayloadBytes: unparseable Base64 (TransferGuid={transferGuid:N})");
                            entry.PayloadBytes = Array.Empty<byte>();
                            entry.NumBytes = 0;
                        }
                    }

                    state.OrbitalTransfers[transferGuid] = entry;
                }
            }

            return state;
        }

        private static double ParseDoubleOrZero(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return 0d;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0d;
        }

        private static int ParseIntOrZero(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return 0;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
    }
}
