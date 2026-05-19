using LmpCommon.Enums;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Server.System.Agency
{
    /// <summary>
    /// Server-side registry + persistence lifecycle for per-agency career state. Holds the
    /// canonical <see cref="AgencyState"/> for every player who has connected to this server.
    /// Hooked into <see cref="HandshakeSystem"/> on player auth (register-or-load) and into
    /// <see cref="MainServer"/> boot (load every persisted agency).
    ///
    /// **Gated on <see cref="PerAgencyEnabled"/>** (the combined check
    /// <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>=true AND
    /// <see cref="LmpCommon.Enums.GameMode"/>=Career). Every public entry point
    /// early-returns when the gate is closed. The dual-mode guarantee (Stage 5 acceptance
    /// criteria, spec §11) requires that with the gate closed, this system has zero
    /// observable effect — no disk reads, no disk writes, no registry entries. The
    /// shared-agency code path (<see cref="Share*System"/> family) is the authority in
    /// that mode and AgencySystem is invisible.
    ///
    /// **Career-only (Stage 5.17e-1, signed off session 15).** Per spec §10 product
    /// decision Q-Mode, per-agency career is supported in <see cref="GameMode.Career"/>
    /// only. <see cref="GameMode.Science"/> and <see cref="GameMode.Sandbox"/> close the
    /// gate even when <see cref="GameplaySettingsDefinition.PerAgencyCareer"/> is true —
    /// per-agency mid-session writes (Stage 5.17e-3 onwards) would NRE in Science mode
    /// where <c>Funding.Instance</c> doesn't exist, and Sandbox has no career scalars to
    /// project at all. The boot-time <see cref="LoadExistingAgencies"/> path logs a one-
    /// time operator warning when the operator misconfigured the combination so the
    /// silent no-op isn't a head-scratcher.
    ///
    /// Stage 5.15a scope is lifecycle only — register, load, save, boot-load. Wire-protocol
    /// broadcasting of agency state to clients lands in 5.15b/5.15c. Per-agency routing of
    /// the <c>Share*</c> mutations lands in 5.17b. Until then, the registry is populated but
    /// nothing consumes it on the wire.
    /// </summary>
    public static class AgencySystem
    {
        /// <summary>
        /// The combined dual-mode gate. <c>true</c> iff
        /// <see cref="GameplaySettingsDefinition.PerAgencyCareer"/> is set AND
        /// <see cref="LmpCommon.Enums.GameMode"/> is <see cref="GameMode.Career"/>. Every
        /// per-agency code path (registration, projection, routing, wire sender, lock
        /// rejection, vessel stamping, admin-command refusal) reads this property — not
        /// the raw <c>PerAgencyCareer</c> setting — so the Career-only product decision
        /// (Stage 5.17e-1, spec §10) is enforced consistently across the codebase. If
        /// you find yourself reaching for <c>GameplaySettings.SettingsStore.PerAgencyCareer</c>
        /// directly outside <see cref="LoadExistingAgencies"/> and the boot-warning path,
        /// you're almost certainly bypassing the gate.
        /// </summary>
        public static bool PerAgencyEnabled =>
            GameplaySettings.SettingsStore.PerAgencyCareer &&
            GeneralSettings.SettingsStore.GameMode == GameMode.Career;

        /// <summary>
        /// Authoritative registry. Key is the canonical <see cref="AgencyState.AgencyId"/> Guid
        /// per Q7 sign-off — survives player renames and is the on-disk filename.
        /// </summary>
        public static ConcurrentDictionary<Guid, AgencyState> Agencies { get; } =
            new ConcurrentDictionary<Guid, AgencyState>();

        /// <summary>
        /// Convenience index for the <c>player → agency</c> lookup. Updated in lockstep with
        /// <see cref="Agencies"/>. Player renames are admin-driven (Stage 5.18d
        /// <c>transferagency</c> command); until then the OwningPlayerName field on
        /// AgencyState is treated as stable.
        ///
        /// **Mutation ordering rule (Stage 5.18d caller contract).** Any future writer that
        /// reassigns a player → agency mapping (transferagency, admin overrides) MUST:
        ///   1. acquire <see cref="PlayerNameLocks"/> for the moving player name,
        ///   2. update <see cref="Agencies"/> entries (per-agency lock for each side),
        ///   3. persist via <see cref="SaveAgency"/>,
        ///   4. ONLY THEN flip the <see cref="AgencyByPlayerName"/> entry.
        /// The per-vessel proto path in <see cref="Server.Message.VesselMsgReader.HandleVesselProto"/>
        /// snapshots this index on the receive thread — a torn read where the index points
        /// at an agency whose state hasn't been persisted yet would stamp a vessel with an
        /// id that fails the next disk round-trip.
        /// </summary>
        public static ConcurrentDictionary<string, Guid> AgencyByPlayerName { get; } =
            new ConcurrentDictionary<string, Guid>(StringComparer.Ordinal);

        /// <summary>
        /// Per-agency lock anchor. Mutations to a single AgencyState (RegisterAgency creating
        /// + initial save, SaveAgency overwriting) acquire this object so Serialize doesn't
        /// race a concurrent field write. Follows the BUG-033 precedent from
        /// <see cref="Server.System.Scenario.ScenarioDataUpdater.GetSemaphore"/> — per-key
        /// locks via <see cref="ConcurrentDictionary.GetOrAdd"/>.
        ///
        /// Exposed as <see cref="GetAgencyLock"/> (internal) so future Stage 5.17b
        /// <c>Share*</c> writers that mutate <see cref="AgencyState"/> fields can hold the
        /// same lock around their mutation. Without this, a concurrent <see cref="SaveAgency"/>
        /// would <see cref="AgencyState.Serialize"/> a non-atomic multi-field snapshot.
        /// </summary>
        private static readonly ConcurrentDictionary<Guid, object> AgencyLocks =
            new ConcurrentDictionary<Guid, object>();

        /// <summary>
        /// Per-player-name lock anchor for the <see cref="RegisterAgency"/> lookup-or-create
        /// path. Without it, two concurrent <see cref="OnPlayerAuthenticated"/> calls for the
        /// same name (e.g. a fast reconnect racing the in-flight auth of a stale connection,
        /// arriving on different Lidgren receive threads) can both miss the registry, both
        /// mint distinct <c>Guid.NewGuid()</c> AgencyIds, both <see cref="ConcurrentDictionary.TryAdd"/>
        /// succeed, and leave one agency orphaned on disk + a non-deterministic last-writer-wins
        /// on the player-name index. The lock serializes per name so the second call sees the
        /// first's commit and returns the existing agency.
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> PlayerNameLocks =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        /// <summary>
        /// Boot-time loader. Enumerates <see cref="AgencyState.AgenciesPath"/>, parses every
        /// <c>{guid}.txt</c>, and populates <see cref="Agencies"/> + <see cref="AgencyByPlayerName"/>.
        /// Per-file failures are logged and skipped — one corrupt agency file must not block
        /// the rest of the server from booting (spec §3 isolation principle, same shape as
        /// per-contract isolation in Stage 5.17b).
        ///
        /// **Operator visibility design (Stage 5.17e-1 round-1 upgrade-lens review).** The
        /// per-agency disk state is independent of the current runtime gate. We always
        /// scan + load the registry whenever <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>
        /// is true, regardless of <see cref="LmpCommon.Enums.GameMode"/> — so the operator-
        /// facing upgrade diagnostics (<see cref="WarnAboutOrphanedVessels"/>,
        /// <see cref="WarnAboutSavingsLossOnUpgrade"/>,
        /// <see cref="WarnAboutSharedContractsOnUpgrade"/>) can compare disk state against
        /// the current vessel + scenario stores accurately. Runtime mutations (Register /
        /// Save / routing / wire sends) remain gated on <see cref="PerAgencyEnabled"/>, so
        /// loading the registry under a non-Career game mode is observably silent except
        /// for the diagnostics. When <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>
        /// is false we don't load (the operator opted out entirely), but we DO scan for
        /// stranded <c>lmpOwningAgency</c> stamps on vessels so the operator is warned if
        /// they disabled the gate while pre-stamped vessels still exist (re-enabling later
        /// without restoring the corresponding agency files would lock the owning players
        /// out of those vessels via the cross-agency check).
        /// </summary>
        public static void LoadExistingAgencies()
        {
            var perAgencyConfigured = GameplaySettings.SettingsStore.PerAgencyCareer;

            // Gate off entirely → still surface a diagnostic if vessels carry stamps from
            // a prior per-agency-on session. Without this, an operator who flipped the
            // gate off (or never had it on but inherited stamped vessels from a peer)
            // would silently lose the ability to re-enable per-agency without the
            // affected players being locked out.
            if (!perAgencyConfigured)
            {
                WarnAboutStrandedAgencyStampsIfGateOff();
                return;
            }

            // [Stage 5.17e-1] Career-only product decision (spec §10, session 15 sign-off).
            // The operator has set the gate on but selected a non-Career game mode. Per-
            // agency runtime is disabled (via PerAgencyEnabled) but we STILL load the disk
            // state and run upgrade diagnostics so the operator sees what's accumulated
            // and can recover deliberately. Without the load, WarnAboutOrphanedVessels
            // would mark every stamped vessel as orphan; with it, the diagnostics produce
            // accurate information for the operator's recovery decision.
            if (GeneralSettings.SettingsStore.GameMode != GameMode.Career)
            {
                LunaLog.Warning(
                    $"[fix:per-agency-career] PerAgencyCareer=true but GameMode={GeneralSettings.SettingsStore.GameMode}. " +
                    "Per-agency career is supported in Career mode only (spec §10, session 15 sign-off — Science " +
                    "mode has no Funding/Reputation singletons so per-agency mid-session writes would NRE; Sandbox " +
                    "has no career scalars to project). Agency registration, projection, and per-agency routing " +
                    "are disabled for this server, but the disk-side registry will still be loaded so the upgrade " +
                    "diagnostics below report accurately. Either set GameMode=Career in Settings/GeneralSettings.xml " +
                    "or set PerAgencyCareer=false in Settings/GameplaySettings.xml. Note: changing PerAgencyCareer " +
                    "may flip GameDifficulty to Custom on next save — see CLAUDE.md Settings caveat.");
            }

            var folder = AgencyState.AgenciesPath;
            if (!FileHandler.FolderExists(folder))
            {
                // Empty/missing folder → still run the upgrade diagnostics + the
                // boot-refusal hazard gate. This is the canonical first-flip
                // operator trajectory: an operator who enables PerAgencyCareer
                // for the first time on a pre-0.31 universe has never minted
                // an agency, so Universe/Agencies/ has never been created. The
                // savings-loss / shared-contracts / shared-tech / shared-
                // research / shared-progress-facility / shared-kolony / shared-
                // planetary checks all fire on the (Agencies.Count==0 + vessels
                // exist + non-zero shared state) predicate which is EXACTLY
                // this case. Without the full warning + refusal sequence here,
                // first per-agency connect silently strips all accumulated
                // career/research/progress/MKS state with no operator
                // opportunity to intervene. [Phase 3 Slice C / upgrade-lens
                // MUST FIX MF1 — Slice B inherited the same gap; fixed
                // uniformly here for kolony + planetary + the existing 5.17
                // hazards.]
                WarnAboutOrphanedVessels();
                WarnAboutSavingsLossOnUpgrade();
                WarnAboutSharedContractsOnUpgrade();
                WarnAboutSharedTechOnUpgrade();
                WarnAboutSharedResearchOnUpgrade();
                WarnAboutSharedProgressFacilityOnUpgrade();
                WarnAboutSharedKolonyOnUpgrade();
                WarnAboutSharedPlanetaryOnUpgrade();
                WarnAboutSharedOrbitalOnUpgrade();
                RefuseStartupIfUpgradeHazardWithoutOverride();
                return;
            }

            var loadedCount = 0;
            foreach (var filePath in FileHandler.GetFilesInPath(folder))
            {
                if (Path.GetExtension(filePath) != ".txt")
                    continue;

                try
                {
                    var state = LoadAgencyFromFile(filePath);
                    if (state == null)
                        continue;

                    if (Agencies.TryAdd(state.AgencyId, state))
                    {
                        if (!string.IsNullOrEmpty(state.OwningPlayerName))
                            AgencyByPlayerName[state.OwningPlayerName] = state.AgencyId;
                        loadedCount++;
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[fix:per-agency-career] Failed to load agency file {Path.GetFileName(filePath)}: {e}");
                }
            }

            LunaLog.Normal($"[fix:per-agency-career] Loaded {loadedCount} per-agency career state(s) from disk");

            // [Stage 5.17a round-1 upgrade-lens review] Per-orphan-agency vessel warning.
            // If a vessel's lmpOwningAgency points at an agency-id that didn't load (file
            // corrupted + per-file isolation skip; manual delete from Universe/Agencies/;
            // .bak rotation lost; etc.), the owning player on reconnect will mint a NEW
            // agency under a DIFFERENT id — and Stage 5.17a's cross-agency rejection will
            // lock them out of their own vessels. Loud diagnostic at boot lets the operator
            // recover (restore .bak, transferagency, or accept the loss before it bites).
            //
            // Walk the already-loaded vessel set (LoadExistingVessels ran first at boot per
            // MainServer ordering). Deduplicate per orphan agency id and report the vessel
            // count. Quiet when no orphans found.
            WarnAboutOrphanedVessels();

            // [Stage 5.17c round-1 upgrade-lens review] Pre-0.31 universe savings-loss
            // warning. If gate=on AND zero agencies loaded AND there are pre-existing
            // vessels (signal: this is an in-place upgrade, not a fresh universe) AND
            // CurrentScenarios holds non-zero career scalars, the next player to connect
            // will mint a fresh agency seeded from GameplaySettings.Starting* — and the
            // projector will then overwrite the accumulated funds/sci/rep in the
            // ScenarioModule payload with the fresh-start values. Silent data loss from
            // the operator's perspective: KSP shows StartingFunds instead of accumulated
            // career savings.
            //
            // Spec §10 migration sign-off is fresh-start-only (archive + start fresh), so
            // an operator who follows the guidance never hits this. This warning catches
            // the operator who didn't read release notes — they see the message and can
            // stop the server before the first connect, archive Universe/, and start
            // over without losing data. Stage 5.18d's /transferagency + /setagency
            // are the proper recovery path for an already-mid-loss universe.
            WarnAboutSavingsLossOnUpgrade();

            // [Stage 5.17d upgrade-lens review] Pre-existing shared-agency contract
            // diagnostic. Without the per-agency ContractSystem scenario projection
            // (deferred to 5.18a per AgencyScenarioProjector.cs), the shared `CONTRACTS`
            // node still ships at SendScenarioModules time to every connecting player.
            // On an upgrade-in-place universe with accumulated shared-agency Active
            // contracts, every per-0.31 player will see those contracts as their own
            // agency's Active list and could "complete" them — producing duplicate
            // rewards via the existing Share*Funds path. Operator-readable warning so
            // they can archive + restart before the first connect.
            WarnAboutSharedContractsOnUpgrade();

            // [Stage 5.17e-4 upgrade-lens review] Same shape as the contracts warning
            // above. The 5.17e-4 projector extension strips ALL shared Tech nodes from
            // the outgoing R&D scenario before splicing in per-agency entries — so a
            // fresh-mint agency on an upgrade-in-place universe with accumulated
            // shared-agency tech unlocks would see an EMPTY tech tree at first
            // handshake. The 50+ hours the operator spent unlocking the shared tree
            // are not migrated. Same fresh-start-only spec §10 sign-off applies;
            // operator workflow is archive + restart with PerAgencyCareer enabled.
            WarnAboutSharedTechOnUpgrade();

            // [Stage 5.17e-5 upgrade-lens review] Sibling diagnostic for the three
            // new R&D-side surfaces (Science subjects + ExpParts; PurchasedParts are
            // already covered by the Tech warning since parts are inlined inside
            // Tech blocks). Same triggering conditions and recovery workflow.
            WarnAboutSharedResearchOnUpgrade();

            // [Stage 5.17e-6] Sibling diagnostic for Strategy / Progress /
            // Facility scenarios — same shape and recovery workflow.
            WarnAboutSharedProgressFacilityOnUpgrade();

            // [Phase 3 Slice B / MKS-R2] Sibling diagnostic for MKS' shared
            // KolonizationScenario. The projector splice strips ALL pre-
            // existing KOLONY_ENTRY children from the projected scenario per
            // requesting agency; an upgrade-in-place universe with accumulated
            // shared kolony research would silently vanish from every per-
            // agency client's view on first scene-load. Same fresh-start-only
            // operator workflow as the existing 5.17e diagnostics.
            WarnAboutSharedKolonyOnUpgrade();

            // [Phase 3 Slice C / MKS-R2] Sibling diagnostic for MKS' shared
            // PlanetaryLogisticsScenario. Same shape as the kolony warning —
            // the projector strips all shared LOGISTICS_ENTRY children on send
            // so accumulated cross-vessel planetary balances vanish from every
            // per-agency client's view on first scene-load. Note that per
            // pre-spec §4.b, gate=off retains the pre-existing MKS-multiplayer
            // (BodyIndex, ResourceName) same-key collision hazard — that is a
            // KNOWN LIMITATION not fixed by Phase 3; operators wanting strict
            // planetary-warehouse correctness across multiple players need
            // per-agency mode.
            WarnAboutSharedPlanetaryOnUpgrade();

            // [Phase 3 Slice D-1 / MKS-R2] Sibling diagnostic for MKS' shared
            // ScenarioOrbitalLogistics. The Slice D projector splice strips
            // all pre-existing TRANSFER children at the scenario root and
            // replaces them with per-agency (empty for fresh-mint agencies)
            // transfers — every in-flight transfer in the shared queue
            // becomes invisible to per-agency clients on first scene-load.
            // The shared-queue authority resolves per pre-spec §4.d to
            // "first agency to proto-resend the destination vessel" via the
            // §2.a vessel stamp; the warning text spells this out + offers
            // the cancel-before-upgrade operator workflow.
            WarnAboutSharedOrbitalOnUpgrade();

            // [Stage 5.17e-9] Hard refusal: if any of the above upgrade-hazard
            // warnings fired AND the operator hasn't explicitly opted into the
            // accepted-loss path, refuse to keep running. The accumulated shared
            // state can't be safely projected per-agency (the WarnAbout* warnings
            // describe exactly what the projector would silently strip on first
            // connect); fail-closed prevents the operator missing the warnings
            // and continuing into silent data-loss territory.
            RefuseStartupIfUpgradeHazardWithoutOverride();
        }

        /// <summary>
        /// [Stage 5.17e-9 boot-refusal hardening] Spec §10 Q-BootRefusal sign-off.
        /// Detects the upgrade-in-place hazard (gate on + no agencies loaded yet +
        /// non-pristine universe + ANY accumulated shared career/research/progress/
        /// facility state). When detected AND
        /// <see cref="GameplaySettingsDefinition.AllowEnablePerAgencyOnExistingUniverse"/>
        /// is false, logs a Fatal message and flips
        /// <see cref="ServerContext.ServerRunning"/> to false — the main loop
        /// observes the flag and exits cleanly on its next tick.
        ///
        /// The operator's recovery paths are: (a) follow the spec §10 fresh-start
        /// workflow (archive Universe/, restart) and the refusal disappears
        /// because vessels go empty; (b) set
        /// <c>AllowEnablePerAgencyOnExistingUniverse=true</c> to explicitly
        /// acknowledge the projector will strip the accumulated state on first
        /// connect. The fail-closed default protects operators from misconfiguring
        /// into silent career-data loss.
        /// </summary>
        private static void RefuseStartupIfUpgradeHazardWithoutOverride()
        {
            if (GameplaySettings.SettingsStore.AllowEnablePerAgencyOnExistingUniverse)
                return; // Operator opted in; respect the override.

            // Re-evaluate the hazard conditions — duplicates a few lines from the
            // WarnAbout* helpers but keeps the refusal decision self-contained and
            // independent of how those helpers structure their early-returns.
            if (Agencies.Count > 0)
                return; // At least one agency loaded; no upgrade hazard.
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return; // Pristine universe; no upgrade hazard.

            bool hasHazard = false;

            // Career scalars (5.17c).
            if (TryReadScenarioRootDouble("Funding", "funds", out var f) && f != 0d) hasHazard = true;
            else if (TryReadScenarioRootDouble("ResearchAndDevelopment", "sci", out var s) && s != 0d) hasHazard = true;
            else if (TryReadScenarioRootDouble("Reputation", "rep", out var r) && r != 0d) hasHazard = true;

            // Contracts (5.17d), tech / research (5.17e-4/5).
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("ContractSystem", out var contractScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("ContractSystem"))
                {
                    var c = contractScn.GetNode("CONTRACTS")?.Value;
                    if (c != null)
                    {
                        foreach (var entry in c.GetNodes("CONTRACT"))
                        {
                            var st = entry.Value.GetValue("state")?.Value ?? string.Empty;
                            if (st != "Offered" && st != "Generated" && st != string.Empty)
                            {
                                hasHazard = true;
                                break;
                            }
                        }
                    }
                }
            }
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("ResearchAndDevelopment", out var rndScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("ResearchAndDevelopment"))
                {
                    if (rndScn.GetNodes("Tech").Any()) hasHazard = true;
                    else if (rndScn.GetNodes("Science").Any()) hasHazard = true;
                    else if ((rndScn.GetNode("ExpParts")?.Value.GetAllValues().Count ?? 0) > 0) hasHazard = true;
                }
            }
            // Progress/facility (5.17e-6) — strategies OR shared world-firsts OR upgraded
            // facility tiers. Pre-review-finding-A.1 (session 19): only the strategy
            // branch fired here despite WarnAboutSharedProgressFacilityOnUpgrade
            // counting all three. That asymmetry left ProgressTracking-only and
            // facility-only upgrade universes booting (with a warning) into silent
            // strip-on-first-connect — exactly the failure mode the refusal exists
            // to prevent. Keep the three sub-checks aligned with the Warn helper.
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("StrategySystem", out var stratScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("StrategySystem"))
                {
                    var sc = stratScn.GetNode("STRATEGIES")?.Value;
                    if (sc != null && sc.GetNodes("STRATEGY").Any()) hasHazard = true;
                }
            }
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("ProgressTracking", out var progScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("ProgressTracking"))
                {
                    // Same dynamic-naming constraint as the Warn helper — any child
                    // under Progress signals accumulated world-firsts. GetAllNodes()
                    // enumerates regardless of name, so we don't need a hard-coded
                    // sentinel list here.
                    var progContainer = progScn.GetNode("Progress")?.Value;
                    if (progContainer != null && progContainer.GetAllNodes().Any()) hasHazard = true;
                }
            }
            // [Phase 3 Slice B / MKS-R2] Kolony hazard predicate. Mirrors the
            // WarnAboutSharedKolonyOnUpgrade helper — any shared KOLONY_ENTRY
            // child under KOLONIZATION means the projector will strip on first
            // per-agency connect, deleting operator-visible kolony research.
            // Same fail-closed posture as the other hazards in this method.
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("KolonizationScenario", out var kolonyScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("KolonizationScenario"))
                {
                    var kolonyContainer = kolonyScn.GetNode("KOLONIZATION")?.Value;
                    if (kolonyContainer != null && kolonyContainer.GetNodes("KOLONY_ENTRY").Any()) hasHazard = true;
                }
            }
            // [Phase 3 Slice C / MKS-R2] Planetary hazard predicate. Mirrors
            // the WarnAboutSharedPlanetaryOnUpgrade helper — any shared
            // LOGISTICS_ENTRY child under PLANETARY_LOGISTICS with non-zero
            // StoredQuantity means the projector will strip on first per-agency
            // connect, deleting operator-visible planetary balances. Same
            // fail-closed posture. Note: the Warn helper specifies "non-zero
            // StoredQuantity" because MKS' Persistance.Save persists empty
            // (StoredQuantity=0) entries too; flagging those would false-fire
            // on an upgrade-in-place universe where MKS' first-load created
            // skeleton entries via FetchLogEntry's "create if not exists" path
            // (PlanetaryLogisticsManager.cs:61-75).
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("PlanetaryLogisticsScenario", out var planetaryScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("PlanetaryLogisticsScenario"))
                {
                    var planetaryContainer = planetaryScn.GetNode("PLANETARY_LOGISTICS")?.Value;
                    if (planetaryContainer != null)
                    {
                        foreach (var entry in planetaryContainer.GetNodes("LOGISTICS_ENTRY"))
                        {
                            var qty = entry.Value.GetValue("StoredQuantity")?.Value;
                            if (string.IsNullOrEmpty(qty)) continue;
                            if (double.TryParse(qty, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                                && parsed != 0d)
                            {
                                hasHazard = true;
                                break;
                            }
                        }
                    }
                }
            }
            // [Phase 3 Slice D-1 / MKS-R2] Orbital hazard predicate. Mirrors
            // the WarnAboutSharedOrbitalOnUpgrade helper — any shared
            // TRANSFER child at the ScenarioOrbitalLogistics root means the
            // projector will strip on first per-agency connect, deleting
            // operator-visible in-flight + recently-completed transfers.
            // Unlike planetary's "non-zero StoredQuantity" filter, ALL
            // TRANSFER children count as hazardous: a Status=Launched
            // transfer represents resources already deducted from Origin;
            // a Status=Returning is cancelled and refunding mid-flight; a
            // Status=Delivered/Partial/Failed is a recently-completed
            // record the operator may want to inspect post-upgrade. No
            // "skeleton zero-balance" false-fire risk — MKS only persists
            // transfers it actually launched. Same fail-closed posture.
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("ScenarioOrbitalLogistics", out var orbitalScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("ScenarioOrbitalLogistics"))
                {
                    if (orbitalScn.GetNodes("TRANSFER").Any()) hasHazard = true;
                }
            }
            if (!hasHazard && ScenarioStoreSystem.CurrentScenarios.TryGetValue("ScenarioUpgradeableFacilities", out var facScn))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("ScenarioUpgradeableFacilities"))
                {
                    // Mirror the Warn helper's known-facility-keys sweep: any KSC
                    // facility with lvl > 0 means the operator has accumulated tier
                    // upgrades that the projector overrides to stock defaults.
                    var knownFacilityKeys = new[]
                    {
                        "SpaceCenter/LaunchPad", "SpaceCenter/VehicleAssemblyBuilding",
                        "SpaceCenter/Runway", "SpaceCenter/SpaceplaneHangar",
                        "SpaceCenter/TrackingStation", "SpaceCenter/AstronautComplex",
                        "SpaceCenter/MissionControl", "SpaceCenter/Administration",
                        "SpaceCenter/ResearchAndDevelopment",
                    };
                    foreach (var k in knownFacilityKeys)
                    {
                        var fac = facScn.GetNode(k)?.Value;
                        if (fac == null) continue;
                        var lvl = fac.GetValue("lvl")?.Value;
                        if (string.IsNullOrEmpty(lvl)) continue;
                        if (float.TryParse(lvl, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0f)
                        {
                            hasHazard = true;
                            break;
                        }
                    }
                }
            }

            if (!hasHazard)
                return;

            LunaLog.Fatal(
                "[fix:per-agency-career] BOOT REFUSED: PerAgencyCareer=true on a non-pristine universe " +
                "with accumulated shared-agency career/research/progress state (see WarnAbout* warnings " +
                "above). Per spec §10 Q-BootRefusal (session 15 sign-off), the server fails closed by " +
                "default — the projector strips accumulated shared state on first per-agency client " +
                "connect, which would silently delete operator-visible career progress. " +
                "Resolve by either: (a) follow spec §10 fresh-start workflow — stop server, archive " +
                "Universe/, restart with empty universe; OR (b) set " +
                "AllowEnablePerAgencyOnExistingUniverse=true in Settings/GameplaySettings.xml to " +
                "explicitly accept the projection-strip and continue. The server will now shut down.");
            ServerContext.ServerRunning = false;
        }

        /// <summary>
        /// [Stage 5.17e-6 upgrade-lens diagnostic] Sibling of the Tech / Research
        /// warnings for the three new non-R&amp;D scenarios touched by 5.17e-6:
        /// shared Strategies, ProgressTracking achievements, and facility upgrade
        /// tiers. The projector strips/overrides these per-agency on send, so
        /// accumulated shared values vanish on first per-agency client connect.
        /// Combined into one warning to avoid log spam — the operator sees a
        /// consolidated picture of what's at risk.
        /// </summary>
        private static void WarnAboutSharedProgressFacilityOnUpgrade()
        {
            if (Agencies.Count > 0)
                return;
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return;

            int strategies = 0, achievements = 0, facilities = 0;
            if (ScenarioStoreSystem.CurrentScenarios.TryGetValue("StrategySystem", out var stratScenario))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("StrategySystem"))
                {
                    var stratsContainer = stratScenario.GetNode("STRATEGIES")?.Value;
                    if (stratsContainer != null)
                        strategies = stratsContainer.GetNodes("STRATEGY").Count();
                }
            }
            if (ScenarioStoreSystem.CurrentScenarios.TryGetValue("ProgressTracking", out var progScenario))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("ProgressTracking"))
                {
                    var progContainer = progScenario.GetNode("Progress")?.Value;
                    if (progContainer != null)
                    {
                        // ProgressTracking children have dynamic names; we don't
                        // enumerate-all here (LunaConfigNode API constraint —
                        // see 5.17e-6 projector comment). Treat any non-null
                        // Progress container with at least one child as a flag.
                        var anySharedChild = progContainer.GetNode("Kerbin") != null
                            || progContainer.GetNode("FirstLaunch") != null
                            || progContainer.GetNode("FirstCrewToSurvive") != null;
                        achievements = anySharedChild ? 1 : 0; // sentinel >0 indicates "has shared progress"
                    }
                }
            }
            if (ScenarioStoreSystem.CurrentScenarios.TryGetValue("ScenarioUpgradeableFacilities", out var facScenario))
            {
                lock (Scenario.ScenarioDataUpdater.GetSemaphore("ScenarioUpgradeableFacilities"))
                {
                    // Same dynamic-naming constraint; count known KSC facility ids
                    // as a heuristic for "has accumulated facility upgrades."
                    var knownFacilityKeys = new[]
                    {
                        "SpaceCenter/LaunchPad", "SpaceCenter/VehicleAssemblyBuilding",
                        "SpaceCenter/Runway", "SpaceCenter/SpaceplaneHangar",
                        "SpaceCenter/TrackingStation", "SpaceCenter/AstronautComplex",
                        "SpaceCenter/MissionControl", "SpaceCenter/Administration",
                        "SpaceCenter/ResearchAndDevelopment",
                    };
                    foreach (var k in knownFacilityKeys)
                    {
                        var fac = facScenario.GetNode(k)?.Value;
                        if (fac == null) continue;
                        var lvl = fac.GetValue("lvl")?.Value;
                        if (string.IsNullOrEmpty(lvl)) continue;
                        if (float.TryParse(lvl, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0f)
                            facilities++;
                    }
                }
            }

            if (strategies == 0 && achievements == 0 && facilities == 0)
                return;

            LunaLog.Warning(
                "[fix:per-agency-career] PerAgencyCareer=true on an upgrade universe carries " +
                $"shared-agency progression state: {strategies} active strategy/strategies, " +
                $"{(achievements > 0 ? "some" : "0")} ProgressTracking entries, " +
                $"{facilities} upgraded facility/facilities. The Stage 5.17e-6 projector strips or " +
                "overrides these on send so per-agency clients start with empty strategy lists, no " +
                "world firsts, and stock-default facility tiers. Operator workflow remains fresh-start-" +
                "only per spec §10: stop the server, archive Universe/ before any player connects.");
        }

        /// <summary>
        /// [Stage 5.17e-5 upgrade-lens diagnostic] Sibling of
        /// <see cref="WarnAboutSharedTechOnUpgrade"/> for the two other R&amp;D-side
        /// surfaces stripped by the projector splice: shared <c>Science</c> child
        /// nodes (completed experiment archive) and the shared <c>ExpParts</c> node
        /// (experimental parts inventory). Same triggering conditions: gate-on,
        /// zero agencies loaded, non-pristine universe. Operator workflow remains
        /// fresh-start-only.
        /// </summary>
        private static void WarnAboutSharedResearchOnUpgrade()
        {
            if (Agencies.Count > 0)
                return;
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return;
            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ResearchAndDevelopment", out var scenario))
                return;

            int subjectCount;
            int expPartCount;
            lock (Scenario.ScenarioDataUpdater.GetSemaphore("ResearchAndDevelopment"))
            {
                subjectCount = scenario.GetNodes("Science").Count();
                var expNode = scenario.GetNode("ExpParts")?.Value;
                expPartCount = expNode?.GetAllValues().Count ?? 0;
            }

            if (subjectCount == 0 && expPartCount == 0)
                return;

            LunaLog.Warning(
                "[fix:per-agency-career] PerAgencyCareer=true on an upgrade universe carries " +
                $"{subjectCount} shared-agency science-subject record(s) and {expPartCount} experimental-part " +
                "entry/entries in the R&D scenario. The Stage 5.17e-5 projector strips these on send so per-agency " +
                "clients start with empty experiment archives + zero experimental parts — the accumulated shared " +
                "progress is NOT migrated. Spec §10 migration is fresh-start-only: stop the server, archive " +
                "Universe/ before any player connects, and start fresh. Operators wishing to preserve specific " +
                "subjects / experimental parts can stop the server and hand-edit individual SUBJECT / " +
                "EXPERIMENTAL_PARTS entries into Universe/Agencies/{guid}.txt files once agencies are minted, " +
                "but bulk migration is not supported.");
        }

        /// <summary>
        /// [Stage 5.17e-4 upgrade-lens diagnostic] Mirror of
        /// <see cref="WarnAboutSharedContractsOnUpgrade"/> for the
        /// <c>ResearchAndDevelopment</c> scenario's accumulated <c>Tech</c> child nodes.
        /// Fires when (a) gate is on, (b) zero agencies loaded (fresh-mint upcoming),
        /// (c) the universe is non-pristine (vessels exist — signals in-place upgrade),
        /// (d) the shared R&amp;D scenario has at least one Tech node. Under those
        /// conditions, the 5.17e-4 projector extension will STRIP those Tech entries
        /// from the outgoing scenario blob and replace them with the (empty) per-agency
        /// tree — the operator's accumulated tech progress is invisible on first
        /// connect. Spec §10 fresh-start-only sign-off applies; operator workflow is
        /// archive + restart.
        /// </summary>
        private static void WarnAboutSharedTechOnUpgrade()
        {
            if (Agencies.Count > 0)
                return; // Fresh-mint upcoming check; only fire when no agencies are loaded.
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return; // Pristine universe — no upgrade hazard.

            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ResearchAndDevelopment", out var scenario))
                return;

            int techCount;
            lock (Scenario.ScenarioDataUpdater.GetSemaphore("ResearchAndDevelopment"))
            {
                techCount = scenario.GetNodes("Tech").Count();
            }

            if (techCount == 0)
                return;

            LunaLog.Warning(
                "[fix:per-agency-career] PerAgencyCareer=true on an upgrade universe carries " +
                $"{techCount} shared-agency Tech unlock(s) in the R&D scenario. The Stage 5.17e-4 " +
                "projector strips these on send so per-agency clients start with empty tech trees " +
                "— the accumulated shared-tree progress is NOT migrated. Spec §10 migration is " +
                "fresh-start-only: stop the server, archive Universe/ before any player connects, " +
                "and start fresh. Operators wishing to preserve specific unlocks can stop the " +
                "server and hand-edit individual TECH entries into Universe/Agencies/{guid}.txt " +
                "files once agencies are minted, but bulk migration is not supported.");
        }

        /// <summary>
        /// [Phase 3 Slice B / MKS-R2] Upgrade-lens diagnostic for MKS' shared
        /// <c>KolonizationScenario</c>. Fires when (a) gate=on, (b) zero
        /// agencies loaded (fresh-mint upcoming), (c) the universe is non-
        /// pristine (vessels exist — signals in-place upgrade), (d) the shared
        /// scenario has at least one <c>KOLONY_ENTRY</c> child under its
        /// <c>KOLONIZATION</c> container. Under those conditions, the Slice B
        /// projector strips ALL shared <c>KOLONY_ENTRY</c> children from the
        /// projected scenario before splicing in per-agency entries — so an
        /// upgrade-in-place universe with accumulated MKS research would
        /// silently lose every entry on first per-agency scene-load. Spec §10
        /// fresh-start-only sign-off applies; operator workflow is archive +
        /// restart, or stamp the destination vessels' <c>lmpOwningAgency</c>
        /// before first connect (Slice E ships <c>setvesselagency</c> as a
        /// thin wrapper for that recovery path).
        ///
        /// <para>Counted via <see cref="ScenarioStoreSystem.CurrentScenarios"/>
        /// + the per-scenario lock the projector uses to read. The
        /// <c>KolonizationScenario</c>'s on-disk shape is
        /// <c>KOLONIZATION { KOLONY_ENTRY { ... } ... }</c> per MKS'
        /// <c>KolonizationPersistance.Save</c> at SHA <c>ed0f6aa6</c>.</para>
        /// </summary>
        private static void WarnAboutSharedKolonyOnUpgrade()
        {
            if (Agencies.Count > 0)
                return; // Fresh-mint agency on a new universe is fine.
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return; // Pristine universe — no upgrade hazard.

            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("KolonizationScenario", out var scenario))
                return;

            int entryCount;
            lock (Scenario.ScenarioDataUpdater.GetSemaphore("KolonizationScenario"))
            {
                var kolonyContainer = scenario.GetNode("KOLONIZATION")?.Value;
                entryCount = kolonyContainer?.GetNodes("KOLONY_ENTRY").Count() ?? 0;
            }

            if (entryCount == 0)
                return;

            LunaLog.Warning(
                "[fix:MKS-R2] PerAgencyCareer=true on an upgrade universe carries " +
                $"{entryCount} shared-agency KOLONY_ENTRY record(s) in the MKS KolonizationScenario. " +
                "The Phase 3 projector strips these on send so per-agency clients start with empty " +
                "kolony research — the accumulated shared research is NOT migrated. Spec §10 migration " +
                "is fresh-start-only: stop the server, archive Universe/ before any player connects, " +
                "and start fresh. " +
                "RECOVERY OPTIONS at this moment (no agencies minted yet, so per-agency vessel-stamping " +
                "is NOT yet actionable — that requires Slice E's setvesselagency admin command, which " +
                "runs AFTER each player has minted their agency by connecting once): " +
                "(1) Accept the loss + set AllowEnablePerAgencyOnExistingUniverse=true in " +
                "Settings/GameplaySettings.xml. Server boots, projector strips on first connect, " +
                "players start kolony research from zero. (2) After agencies are minted (each player " +
                "has connected at least once), run Slice E's `setvesselagency {vesselGuid} {agencyGuid}` " +
                "for each kolony-bearing base — the next post-attribution kolony mutation routes the " +
                "entry into the right agency. Slice E is not yet shipped; until then the only recovery " +
                "is option (1) or the spec §10 fresh-start (archive Universe/, restart). (3) Stay on " +
                "shared-agency mode (PerAgencyCareer=false) — no kolony data loss; kolony continues as " +
                "shared accumulation under the Phase 3 gate=off path.");
        }

        /// <summary>
        /// [Phase 3 Slice C / MKS-R2 upgrade-lens diagnostic] Sibling of
        /// <see cref="WarnAboutSharedKolonyOnUpgrade"/> for the MKS planetary-
        /// logistics scenario. Fires when (a) gate is on, (b) zero agencies
        /// loaded (fresh-mint upcoming), (c) the universe is non-pristine
        /// (vessels exist — signals in-place upgrade), (d) the shared
        /// <c>PlanetaryLogisticsScenario</c> has at least one
        /// <c>LOGISTICS_ENTRY</c> child node with non-zero
        /// <c>StoredQuantity</c>. Under those conditions, the Slice C
        /// projector will STRIP those entries from the outgoing scenario blob
        /// and replace them with the (empty) per-agency pool — operator's
        /// accumulated planetary balances are invisible on first connect.
        ///
        /// <para>Differs from kolony in TWO important respects per pre-spec
        /// §4.b / §4.e:</para>
        /// <list type="bullet">
        ///   <item><b>Pre-existing MKS-multiplayer hazard.</b> The gate=off
        ///        baseline already has a known hazard: two players pumping the
        ///        same resource on the same body collide on the
        ///        <c>(BodyIndex, ResourceName)</c> key under MKS'
        ///        <c>PlanetaryLogisticsPersistance.SaveLogEntryNode</c>
        ///        (last-write-wins). Phase 3 does NOT pretend to fix this
        ///        under shared mode — it's an MKS-design hazard that exists
        ///        pre-Phase-3 on master and would require a wider scope to
        ///        address. Operators wanting strict planetary correctness
        ///        across multiple players need per-agency mode (gate=on).</item>
        ///   <item><b>NO transferagency migration.</b> Per pre-spec §4.e:
        ///        planetary entries do NOT migrate when a vessel changes
        ///        agency via <c>transferagency</c> — the entry represents a
        ///        body's logistics pool, not a vessel's contribution. So the
        ///        kolony warning's "option 2: stamp the vessel" recovery
        ///        does NOT apply for planetary. The only recoveries are
        ///        accept-the-strip (set the override flag) or stay-on-
        ///        shared-mode.</item>
        /// </list>
        ///
        /// <para>Counted via <see cref="ScenarioStoreSystem.CurrentScenarios"/>
        /// + the per-scenario lock the projector uses to read. The
        /// <c>PlanetaryLogisticsScenario</c>'s on-disk shape is
        /// <c>PLANETARY_LOGISTICS { LOGISTICS_ENTRY { BodyIndex= ResourceName=
        /// StoredQuantity= } ... }</c> per MKS'
        /// <c>PlanetaryLogisticsPersistance.Save</c> at SHA <c>ed0f6aa6</c>.
        /// Non-zero <c>StoredQuantity</c> guards against false-firing on the
        /// skeleton-entries-with-zero-balance case (MKS' FetchLogEntry
        /// "create if not exists" path persists empty entries too — those
        /// represent no operator-visible data loss).</para>
        /// </summary>
        private static void WarnAboutSharedPlanetaryOnUpgrade()
        {
            if (Agencies.Count > 0)
                return; // Fresh-mint agency on a new universe is fine.
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return; // Pristine universe — no upgrade hazard.

            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("PlanetaryLogisticsScenario", out var scenario))
                return;

            int nonZeroCount;
            double totalQuantity;
            lock (Scenario.ScenarioDataUpdater.GetSemaphore("PlanetaryLogisticsScenario"))
            {
                var container = scenario.GetNode("PLANETARY_LOGISTICS")?.Value;
                nonZeroCount = 0;
                totalQuantity = 0d;
                if (container != null)
                {
                    foreach (var entry in container.GetNodes("LOGISTICS_ENTRY"))
                    {
                        var qty = entry.Value.GetValue("StoredQuantity")?.Value;
                        if (string.IsNullOrEmpty(qty)) continue;
                        if (double.TryParse(qty, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                            && parsed != 0d)
                        {
                            nonZeroCount++;
                            totalQuantity += parsed;
                        }
                    }
                }
            }

            if (nonZeroCount == 0)
                return;

            LunaLog.Warning(
                "[fix:MKS-R2] PerAgencyCareer=true on an upgrade universe carries " +
                $"{nonZeroCount} non-zero shared-agency LOGISTICS_ENTRY record(s) in the MKS " +
                $"PlanetaryLogisticsScenario (total quantity across body/resource pools: " +
                $"{totalQuantity.ToString(CultureInfo.InvariantCulture)}). The Phase 3 projector " +
                "strips these on send so per-agency clients start with empty planetary-logistics pools — " +
                "the accumulated shared balances are NOT migrated. " +
                "IMPORTANT — DIFFERS FROM KOLONY: per pre-spec §4.e, planetary entries do NOT migrate " +
                "on transferagency (the entry represents a body's pool, not a vessel's contribution), " +
                "so the kolony recovery option of 'stamp the vessel via setvesselagency' is NOT " +
                "actionable here — even post-mint, planetary balances stay where they were when last " +
                "written. The pre-mint recovery shape is therefore the only option once gate=on. " +
                "RECOVERY OPTIONS at this moment (no agencies minted yet): " +
                "(1) Accept the loss + set AllowEnablePerAgencyOnExistingUniverse=true in " +
                "Settings/GameplaySettings.xml. Server boots, projector strips on first connect, " +
                "players start planetary pools from zero. (2) Pre-flip drain workflow: stop the " +
                "server now, flip PerAgencyCareer=false, restart, drain the warehouses to a single " +
                "vessel via in-game MKS UI, stop the server again, archive Universe/ (spec §10 fresh-" +
                "start workflow), then restart with PerAgencyCareer=true. The drained quantities are " +
                "lost but the warehouses + vessels survive. (3) Stay on shared-agency mode " +
                "(PerAgencyCareer=false) — no planetary data loss. " +
                "KNOWN LIMITATION under gate=off (pre-spec §4.b): the pre-existing MKS-multiplayer " +
                "(BodyIndex, ResourceName) same-key collision hazard remains in shared mode — two " +
                "players pumping the same resource on the same body collide last-write-wins on MKS' " +
                "own PlanetaryLogisticsPersistance.SaveLogEntryNode. This hazard exists on master " +
                "pre-Phase-3 and is NOT fixed by Phase 3 under shared mode; only the gate=on " +
                "projection + router fix it. Operators wanting strict planetary correctness across " +
                "multiple players need per-agency mode.");
        }

        /// <summary>
        /// [Phase 3 Slice D-1 / MKS-R2 upgrade-lens diagnostic] Sibling of
        /// <see cref="WarnAboutSharedKolonyOnUpgrade"/> +
        /// <see cref="WarnAboutSharedPlanetaryOnUpgrade"/> for the MKS
        /// orbital-logistics scenario. Fires when (a) gate is on, (b) zero
        /// agencies loaded (fresh-mint upcoming), (c) the universe is
        /// non-pristine (vessels exist — signals in-place upgrade), (d) the
        /// shared <c>ScenarioOrbitalLogistics</c> has at least one
        /// <c>TRANSFER</c> child at the scenario root. Under those
        /// conditions, the Slice D projector splice will STRIP those
        /// transfers from the outgoing scenario blob and replace them with
        /// the (empty) per-agency queue — every operator-visible pending +
        /// recently-completed transfer disappears on first connect.
        ///
        /// <para>Differs from kolony / planetary in TWO important respects
        /// per pre-spec §4.d:</para>
        /// <list type="bullet">
        ///   <item><b>Resource cost of inaction is asymmetric.</b> A
        ///        Status=Launched transfer has ALREADY deducted resources
        ///        from Origin's tanks (per MKS' <c>DoLaunchTasks</c> at
        ///        OrbitalLogisticsTransferRequest.cs:687-691). Stripping the
        ///        transfer mid-flight loses BOTH the in-flight payload AND
        ///        the launch-cost fuel — the operator's vessel didn't
        ///        receive the delivery but DID pay for it. The kolony /
        ///        planetary strips lose accumulated state but no in-flight
        ///        resources are mid-spend.</item>
        ///   <item><b>Pre-Phase-3 destination-resolution outcome.</b> If
        ///        operator declines the override and the universe upgrades,
        ///        the §2.a vessel stamp's first-proto-wins rule attributes
        ///        each in-flight transfer to whichever agency proto-resends
        ///        the destination vessel FIRST. The operator's intent at
        ///        Launch time may have been "deliver to my own base" but
        ///        a peer who connects first could end up the agency of
        ///        record. Spec §10 first-stamp-wins applies uniformly; the
        ///        warning text spells out the recovery (cancel-before-
        ///        upgrade via in-game MKS UI; or stamp destination vessel
        ///        pre-flip via Slice E's setvesselagency).</item>
        /// </list>
        ///
        /// <para>Counted via <see cref="ScenarioStoreSystem.CurrentScenarios"/>
        /// + the per-scenario lock the projector uses to read. The
        /// <c>ScenarioOrbitalLogistics</c> on-disk shape is
        /// <c>ScenarioOrbitalLogistics { TRANSFER { ... } TRANSFER { ... } }</c>
        /// per MKS' <c>ScenarioOrbitalLogistics.OnSave</c> at SHA
        /// <c>ed0f6aa6</c> — TRANSFER nodes are direct children of the
        /// scenario, NOT nested under a container (different from kolony's
        /// KOLONIZATION container + planetary's PLANETARY_LOGISTICS
        /// container).</para>
        ///
        /// <para><b>The Slice D Deliver-prefix is gate-state-independent.</b>
        /// Once Slice D-2 ships the Deliver-prefix
        /// (OrbitalLogisticsTransferRequest_DeliverPrefix), the per-frame
        /// double-spend hazard is closed under BOTH gate=on and gate=off.
        /// Under gate=off, in-flight shared-queue transfers continue
        /// delivering normally — they just deliver exactly ONCE rather than
        /// once-per-peer. Slice D-1 does NOT ship the prefix; this warning
        /// applies regardless of when D-2 lands.</para>
        /// </summary>
        private static void WarnAboutSharedOrbitalOnUpgrade()
        {
            if (Agencies.Count > 0)
                return; // Fresh-mint agency on a new universe is fine.
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return; // Pristine universe — no upgrade hazard.

            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ScenarioOrbitalLogistics", out var scenario))
                return;

            // ScenarioOrbitalLogistics's OnLoad sorts transfers into
            // PendingTransfers (Status==Launched|Returning) vs ExpiredTransfers
            // (everything else — Delivered / Partial / Failed / Cancelled).
            // We don't fully parse here; just count TRANSFER children and
            // categorize by the `status` value-pair on each (the same
            // ConfigNode field MKS' OrbitalLogisticsTransferRequest persists
            // via the [Persistent] attribute on the Status enum field). The
            // count gives the operator visibility into both "in-flight that
            // will lose resources" and "recently-completed records they may
            // want to inspect."
            int pendingCount = 0;
            int expiredCount = 0;
            lock (Scenario.ScenarioDataUpdater.GetSemaphore("ScenarioOrbitalLogistics"))
            {
                foreach (var entry in scenario.GetNodes("TRANSFER"))
                {
                    var statusStr = entry.Value.GetValue("status")?.Value ?? string.Empty;
                    if (statusStr == "Launched" || statusStr == "Returning")
                        pendingCount++;
                    else
                        expiredCount++;
                }
            }

            if (pendingCount == 0 && expiredCount == 0)
                return;

            LunaLog.Warning(
                "[fix:MKS-R2] PerAgencyCareer=true on an upgrade universe carries " +
                $"shared-agency orbital-logistics transfer state: {pendingCount} pending (Launched/Returning) + " +
                $"{expiredCount} recently-completed transfer(s) in the MKS ScenarioOrbitalLogistics. The Phase " +
                "3 projector strips ALL TRANSFER children on send so per-agency clients start with empty " +
                "transfer queues — pending transfers vanish from the MKS UI and the in-flight resources " +
                "previously deducted from Origin tanks at Launch time are LOST WITHOUT BEING DELIVERED. " +
                "Recently-completed transfers (delivery records) also disappear from the UI. " +
                "RESOURCE-COST WARNING: a Status=Launched transfer has ALREADY paid the launch-cost fuel + " +
                "deducted the resource payload from Origin (per MKS' DoLaunchTasks at " +
                "OrbitalLogisticsTransferRequest.cs:687-691). Stripping it post-upgrade means the player " +
                "paid for a delivery that never arrives. " +
                "RECOVERY OPTIONS at this moment (no agencies minted yet): " +
                "(1) Accept the loss + set AllowEnablePerAgencyOnExistingUniverse=true in " +
                "Settings/GameplaySettings.xml. Server boots, projector strips on first connect, " +
                "in-flight transfer payloads are gone. (2) Pre-flip cancel workflow (recommended for " +
                "high-value transfers): stop the server now, flip PerAgencyCareer=false, restart, cancel " +
                "every pending transfer via the in-game MKS UI (the Abort path returns the payload to " +
                "Origin), then stop again, archive Universe/ (spec §10 fresh-start workflow), and " +
                "restart with PerAgencyCareer=true. (3) Pre-flip destination-stamp workflow (after " +
                "agencies exist; requires Slice E's setvesselagency, NOT yet shipped): stamp the " +
                "destination vessel of each pending transfer to its intended owning agency before " +
                "first per-agency connect. The §2.a stamp-then-route flow then attributes each transfer " +
                "to the right agency on its first projection; the persisted state migrates intact via the " +
                "Slice E transferagency MKS extension. (4) Stay on shared-agency mode " +
                "(PerAgencyCareer=false) — no transfer data loss. Note: even under shared mode, the Slice " +
                "D-2 Deliver-prefix (when shipped) closes the per-frame double-spend hazard that has " +
                "existed pre-Phase-3 on master — strict improvement regardless of gate.");
        }

        /// <summary>
        /// Stage 5.17d upgrade-lens diagnostic. Pre-existing CONTRACTS / CONTRACTS_FINISHED
        /// entries in the shared <c>ContractSystem</c> scenario under gate=on means the
        /// scenario will ship to every connecting player at handshake; without the per-
        /// agency <c>ContractSystem</c> projection (deferred to Stage 5.18a) every player
        /// inherits those contracts as their own. Operator sees the warning and can
        /// archive Universe/ before any player connects (spec §10 fresh-start-only).
        /// </summary>
        private static void WarnAboutSharedContractsOnUpgrade()
        {
            if (Agencies.Count > 0)
                return; // Fresh-mint agency on a new universe is fine.
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return; // Pristine universe — no upgrade hazard.

            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ContractSystem", out var scenario))
                return;

            int active = 0, finished = 0;
            // Same per-scenario lock the projector uses to read; under boot there are
            // no concurrent writers but the guard is symmetric.
            lock (Scenario.ScenarioDataUpdater.GetSemaphore("ContractSystem"))
            {
                var activeNode = scenario.GetNode("CONTRACTS")?.Value;
                if (activeNode != null)
                {
                    foreach (var c in activeNode.GetNodes("CONTRACT"))
                    {
                        var st = c.Value.GetValue("state")?.Value ?? string.Empty;
                        // Pre-existing Offered entries are expected (the shared pool is
                        // designed to carry them); only flag in-progress / completed.
                        if (st != "Offered" && st != "Generated" && st != string.Empty)
                            active++;
                    }
                }
                var finishedNode = scenario.GetNode("CONTRACTS_FINISHED")?.Value;
                if (finishedNode != null)
                    finished = finishedNode.GetNodes("CONTRACT").Count();
            }

            if (active == 0 && finished == 0)
                return;

            LunaLog.Warning(
                "[fix:per-agency-career] PerAgencyCareer=true on an upgrade universe carries " +
                $"shared-agency contract state: {active} non-Offered entry/entries in CONTRACTS + " +
                $"{finished} entry/entries in CONTRACTS_FINISHED. Per-agency clients (Stage 5.18a) " +
                "do NOT yet receive a projected ContractSystem scenario, so each connecting player " +
                "would inherit these contracts as their own agency's work — completing them would " +
                "credit duplicate rewards through the Share*Funds path. Spec §10 migration is " +
                "fresh-start-only: stop the server, archive Universe/ before any player connects, " +
                "and start fresh. If accepted as-is, the duplication risk persists until 5.18a's " +
                "ContractSystem projection lands.");
        }

        /// <summary>
        /// Operator-facing boot diagnostic: scan <see cref="VesselStoreSystem.CurrentVessels"/>
        /// for any vessel stamped with an <c>OwningAgencyId</c> that is not present in the
        /// <see cref="Agencies"/> registry. Each orphan id produces one warning line listing
        /// the affected vessel count. Stage 5.17a hardening: without this, a corrupted agency
        /// file (per-file isolation skip in <see cref="LoadExistingAgencies"/>) silently locks
        /// the affected player out of their own vessels at next reconnect.
        /// </summary>
        /// <summary>
        /// Operator-facing boot diagnostic for the pre-0.31-upgrade savings-loss scenario.
        /// Fires when (a) per-agency career is on, (b) no agencies loaded at boot,
        /// (c) vessels exist in the store (signalling an in-place upgrade, not a fresh
        /// universe), and (d) any of the three career scenarios has a non-zero scalar.
        /// The next player to register mints a fresh agency seeded from
        /// <see cref="GameplaySettingsDefinition.StartingFunds"/> et al; the projector
        /// then overwrites the accumulated value in the wire payload. Operator sees the
        /// warning, decides whether to stop and archive or accept the loss.
        /// </summary>
        private static void WarnAboutSavingsLossOnUpgrade()
        {
            if (Agencies.Count > 0)
                return;
            if (VesselStoreSystem.CurrentVessels.IsEmpty)
                return; // Fresh universe; no upgrade scenario to warn about.

            var scalarsPresent = new List<string>();
            if (TryReadScenarioRootDouble("Funding", "funds", out var funds) && funds != 0d)
                scalarsPresent.Add($"funds={funds.ToString(CultureInfo.InvariantCulture)}");
            if (TryReadScenarioRootDouble("ResearchAndDevelopment", "sci", out var sci) && sci != 0d)
                scalarsPresent.Add($"sci={sci.ToString(CultureInfo.InvariantCulture)}");
            if (TryReadScenarioRootDouble("Reputation", "rep", out var rep) && rep != 0d)
                scalarsPresent.Add($"rep={rep.ToString(CultureInfo.InvariantCulture)}");

            if (scalarsPresent.Count == 0)
                return;

            LunaLog.Warning(
                "[fix:per-agency-career] PerAgencyCareer=true on an upgrade universe " +
                $"({VesselStoreSystem.CurrentVessels.Count} pre-existing vessel(s), zero agencies loaded). " +
                $"Career scalars accumulated under shared-agency play: [{string.Join(", ", scalarsPresent)}]. " +
                "The next player to connect will mint a fresh agency seeded from GameplaySettings.Starting* " +
                "and the scenario projector will overwrite these scalars with the fresh-start values on every send — " +
                "the accumulated values are NOT inherited. Spec §10 migration is fresh-start-only: stop the server, " +
                "archive Universe/ before any player connects, and start fresh. If you accept the loss, this warning " +
                "is informational. Stage 5.18d's /transferagency + /setagency are the recovery path for an " +
                "already-mid-loss universe (run /listagencies after the first player connects to see the minted " +
                "agency's id/owner, then /setagency funds|science|reputation <id-or-owner> <amount>).");
        }

        private static bool TryReadScenarioRootDouble(string scenarioName, string key, out double value)
        {
            value = 0d;
            if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue(scenarioName, out var scenario))
                return false;
            // Use the per-scenario writer lock for the same BUG-033 reason BackupScenarios
            // does — ConfigNode.GetValue iterates the values collection, which would race
            // an in-flight AddNode/RemoveNode on the same instance from a parallel writer.
            // At boot there is no concurrent writer (no clients have authenticated yet),
            // so this is belt-and-braces.
            string raw;
            lock (Scenario.ScenarioDataUpdater.GetSemaphore(scenarioName))
            {
                raw = scenario.GetValue(key)?.Value;
            }
            if (string.IsNullOrEmpty(raw))
                return false;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// [Stage 5.17e-1 round-1 upgrade-lens review] Fires only when
        /// <see cref="GameplaySettingsDefinition.PerAgencyCareer"/> is false at boot.
        /// Scans <see cref="VesselStoreSystem.CurrentVessels"/> for any vessel carrying a
        /// non-Empty <c>OwningAgencyId</c>. If any are found, warns the operator that
        /// these stamps will lock the owning players out of those vessels if they later
        /// re-enable PerAgencyCareer (since the new sessions would mint fresh GUIDs and
        /// the cross-agency check would refuse acquires). Quiet when no stranded stamps
        /// exist — the common fresh-universe-with-gate-off boot stays silent.
        /// </summary>
        private static void WarnAboutStrandedAgencyStampsIfGateOff()
        {
            var strandedCount = 0;
            foreach (var vessel in VesselStoreSystem.CurrentVessels.Values)
            {
                if (vessel.OwningAgencyId != Guid.Empty)
                    strandedCount++;
            }

            if (strandedCount == 0)
                return;

            LunaLog.Warning(
                $"[fix:per-agency-career] PerAgencyCareer=false at boot, but {strandedCount} vessel(s) " +
                "carry an lmpOwningAgency stamp from a prior per-agency session. The agency registry is not " +
                "loaded under gate=off, but the stamps persist on disk. If you re-enable PerAgencyCareer later " +
                "WITHOUT restoring the matching Universe/Agencies/{guid}.txt files (e.g. you deleted them), the " +
                "owning players will mint NEW agency GUIDs on reconnect and be locked out of these vessels by " +
                "the cross-agency lock check. Stage 5.18d's transferagency admin command will be the recovery " +
                "path. To preserve full re-enable: keep Universe/Agencies/ intact, or accept the loss and use " +
                "transferagency to re-own the affected vessels under the new agencies.");

            // [Phase 3 Slice B / MKS-R2 upgrade-lens finding MF1 + Slice C/D
            // extension] Per-agency MKS state (KOLONY_ENTRIES +
            // PLANETARY_ENTRIES + ORBITAL_TRANSFERS) is frozen on disk
            // under gate=off — invisible to the shared-mode 30s SHA pass,
            // stale relative to the now-diverging shared
            // KolonizationScenario / PlanetaryLogisticsScenario /
            // ScenarioOrbitalLogistics. An operator who re-enables
            // PerAgencyCareer will see the frozen entries re-materialise,
            // producing a stale snapshot of the world the operator was
            // actually playing in shared-mode. Cheap text scan of
            // Universe/Agencies/*.txt; don't load the full AgencyState
            // file. The three scans share one file-walk for efficiency.
            try
            {
                if (FileHandler.FolderExists(AgencyState.AgenciesPath))
                {
                    var frozenKolonyCount = 0;
                    var frozenPlanetaryCount = 0;
                    var frozenOrbitalCount = 0;
                    foreach (var filePath in FileHandler.GetFilesInPath(AgencyState.AgenciesPath))
                    {
                        if (Path.GetExtension(filePath) != ".txt") continue;
                        try
                        {
                            // Substring match is sufficient — KOLONY_ENTRIES /
                            // PLANETARY_ENTRIES / ORBITAL_TRANSFERS are the
                            // only top-level child nodes containing those
                            // tokens and a legitimate agency file is
                            // operator-friendly text.
                            var text = File.ReadAllText(filePath);
                            if (text.Contains("KOLONY_ENTRIES")) frozenKolonyCount++;
                            if (text.Contains("PLANETARY_ENTRIES")) frozenPlanetaryCount++;
                            if (text.Contains("ORBITAL_TRANSFERS")) frozenOrbitalCount++;
                        }
                        catch (Exception) { /* per-file isolation — skip and keep scanning */ }
                    }

                    if (frozenKolonyCount > 0)
                    {
                        LunaLog.Warning(
                            $"[fix:MKS-R2] Additionally, {frozenKolonyCount} agency file(s) under " +
                            $"{AgencyState.AgenciesPath} carry frozen per-agency KOLONY_ENTRIES from a prior " +
                            "gate=on session. Shared-mode kolony play continues from the shared scenario only; " +
                            "the per-agency entries on disk are NOT updated by the legacy 30s SHA pass and will " +
                            "reappear stale if you re-enable PerAgencyCareer (the projected state will reflect " +
                            "a snapshot from before the gate=off session). Consider clearing KOLONY_ENTRIES " +
                            "child blocks from Universe/Agencies/*.txt before re-enabling, or accepting the " +
                            "stale projection.");
                    }

                    if (frozenPlanetaryCount > 0)
                    {
                        LunaLog.Warning(
                            $"[fix:MKS-R2] Additionally, {frozenPlanetaryCount} agency file(s) under " +
                            $"{AgencyState.AgenciesPath} carry frozen per-agency PLANETARY_ENTRIES from a prior " +
                            "gate=on session. Same staleness profile as KOLONY_ENTRIES — re-enabling " +
                            "PerAgencyCareer would project a pre-flip snapshot, NOT the current shared-mode " +
                            "state. Consider clearing PLANETARY_ENTRIES child blocks from " +
                            "Universe/Agencies/*.txt before re-enabling, or accepting the stale projection.");
                    }

                    if (frozenOrbitalCount > 0)
                    {
                        // Orbital staleness is the MOST hazardous of the three —
                        // a frozen ORBITAL_TRANSFERS dict carries Status=Launched
                        // transfers from a prior session whose Origin/Destination
                        // vessels may have been recovered / unloaded / changed
                        // owning-agency under shared-mode play. Re-enabling
                        // gate=on would project these stale transfers back into
                        // the per-agency client view; if the destination vessel
                        // still exists, the client's MKS UI surfaces a "pending
                        // arrival" with a UT in the past, and the Slice D-2
                        // Deliver-prefix's destination-resolution would attempt
                        // resource exchange against the stale state. Operator
                        // advice: prefer clearing ORBITAL_TRANSFERS over the
                        // other two when re-enabling.
                        LunaLog.Warning(
                            $"[fix:MKS-R2] Additionally, {frozenOrbitalCount} agency file(s) under " +
                            $"{AgencyState.AgenciesPath} carry frozen per-agency ORBITAL_TRANSFERS from a prior " +
                            "gate=on session. MOST HAZARDOUS of the three frozen MKS surfaces — frozen " +
                            "transfers carry Status values + StartTime/Duration from the pre-flip world; " +
                            "the Origin/Destination vessels may have been recovered / unloaded / re-stamped " +
                            "during the gate=off session, leaving the transfer pointing at stale vessel ids. " +
                            "Re-enabling PerAgencyCareer would surface these stale entries in MKS UIs with " +
                            "GetArrivalTime() values in the past, and the Slice D-2 Deliver-prefix " +
                            "(when shipped) would attempt resource exchange against missing vessels. " +
                            "STRONGLY recommended to " +
                            "clear ORBITAL_TRANSFERS child blocks from Universe/Agencies/*.txt before " +
                            "re-enabling, or accept the stale projection + manually cancel each surfaced " +
                            "transfer via the in-game MKS UI on first per-agency reconnect.");
                    }
                }
            }
            catch (Exception e)
            {
                // Diagnostic failure must not block boot — log and continue. Same
                // posture as the per-file isolation inside the loop.
                LunaLog.Warning($"[fix:MKS-R2] Frozen-per-agency-state scan failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void WarnAboutOrphanedVessels()
        {
            var orphanCounts = new Dictionary<Guid, int>();
            foreach (var vessel in VesselStoreSystem.CurrentVessels.Values)
            {
                var ownerId = vessel.OwningAgencyId;
                if (ownerId == Guid.Empty)
                    continue; // Unassigned-sentinel per spec §10 Q3 — not orphan.
                if (Agencies.ContainsKey(ownerId))
                    continue;
                orphanCounts.TryGetValue(ownerId, out var count);
                orphanCounts[ownerId] = count + 1;
            }

            foreach (var kvp in orphanCounts)
            {
                LunaLog.Warning($"[fix:per-agency-career] {kvp.Value} vessel(s) reference agency {kvp.Key:N} which did not load. The owning player will mint a new agency on reconnect and (a) be refused vessel-scoped lock acquires by the 5.17a cross-agency lock check, AND (b) have their position/flightstate/update broadcasts silently dropped by the 5.17a write-path counterpart (session 19 soak Finding 2). Restore Universe/Agencies/{kvp.Key:N}.txt(.bak) or admin transferagency to recover.");
            }
        }

        /// <summary>
        /// Registers a new agency for the given player, or returns the player's existing
        /// agency if one is already in the registry. Idempotent — calling twice for the
        /// same player returns the same <see cref="AgencyState"/> both times. The starting
        /// scalars (Funds, Science, Reputation) seed from <see cref="GameplaySettingsDefinition"/>
        /// — same source the shared-agency path uses for first-universe initialization,
        /// so the per-agency mode inherits the operator's chosen career economy.
        ///
        /// Persists immediately via <see cref="SaveAgency"/> so a crash before the next
        /// periodic save doesn't lose the registration.
        ///
        /// No-op (returns null) when <see cref="PerAgencyEnabled"/> is false (gate off OR
        /// non-Career game mode).
        /// </summary>
        public static AgencyState RegisterAgency(string playerName)
        {
            if (!PerAgencyEnabled)
                return null;
            if (string.IsNullOrEmpty(playerName))
                throw new ArgumentException("Player name must be non-empty", nameof(playerName));

            // Per-name lock closes the same-name double-register race: two concurrent
            // OnPlayerAuthenticated for "Alice" arriving on separate receive threads
            // serialize here; the second call sees the first's commit and returns its
            // agency instead of minting a parallel one.
            lock (PlayerNameLocks.GetOrAdd(playerName, _ => new object()))
            {
                if (AgencyByPlayerName.TryGetValue(playerName, out var existingId))
                {
                    if (Agencies.TryGetValue(existingId, out var existing))
                        return existing;

                    // Stale-index defensive heal (round-2 review): the player-name index
                    // points at a Guid that's missing from the Agencies registry. Try the
                    // disk fallback (covers the .bak-recovery path); if that also misses,
                    // unbind the stale name so the mint path below runs cleanly instead of
                    // silently shadowing an orphan that lives forever in vessel stamps.
                    var healed = LoadAgency(existingId);
                    if (healed != null)
                    {
                        AgencyByPlayerName[playerName] = healed.AgencyId;
                        return healed;
                    }
                    AgencyByPlayerName.TryRemove(playerName, out _);
                    LunaLog.Warning($"[fix:per-agency-career] Healed stale AgencyByPlayerName entry for {playerName} (pointed at missing agency {existingId:N})");
                }

                var state = new AgencyState
                {
                    AgencyId = Guid.NewGuid(),
                    OwningPlayerName = playerName,
                    DisplayName = $"{playerName} Space Agency",
                    Funds = GameplaySettings.SettingsStore.StartingFunds,
                    Science = GameplaySettings.SettingsStore.StartingScience,
                    Reputation = GameplaySettings.SettingsStore.StartingReputation,
                };

                if (!Agencies.TryAdd(state.AgencyId, state))
                {
                    // Guid.NewGuid collision is astronomically unlikely; if it ever fires it
                    // indicates a deeper problem (deterministic GUID generator, test harness
                    // reuse, etc.) — fail loudly rather than silently overwrite an existing
                    // agency.
                    throw new InvalidOperationException(
                        $"GUID collision on AgencyId {state.AgencyId:N} while registering player {playerName}");
                }

                // Persistence-before-index ordering (round-2 review). The XML doc on
                // AgencyByPlayerName guarantees: persist via SaveAgency BEFORE flipping the
                // index. Prior order (flip-then-save) created a crash window between the
                // index write and the disk write where a server crash left the index pointing
                // at a GUID with no on-disk file. After restart, LoadExistingAgencies couldn't
                // find it; the player reconnected and minted a new agency; any vessel stamped
                // with the orphan GUID was disconnected from any active agency. By calling
                // SaveAgency first, a crash before the index flip simply means the player
                // reconnects, mints a new agency, and the orphan-on-disk is unreferenced and
                // safe to operator-delete. The vessel proto path (HandleVesselProto) reads
                // AgencyByPlayerName, so a crash here leaves vessels unstamped (Guid.Empty)
                // until next proto — also safe.
                SaveAgency(state.AgencyId);

                AgencyByPlayerName[playerName] = state.AgencyId;

                LunaLog.Normal($"[fix:per-agency-career] Registered new agency '{state.DisplayName}' ({state.AgencyId:N}) for player {playerName}");
                return state;
            }
        }

        /// <summary>
        /// Returns the agency for the given id. Registry-first; falls back to disk and
        /// caches in the registry on a hit. Returns null when the agency is unknown to
        /// both the registry and disk. Useful for admin tooling (Stage 5.18d) and for
        /// the future wire path where a client references an agency by id.
        ///
        /// No-op (returns null) when <see cref="PerAgencyEnabled"/> is false (gate off OR
        /// non-Career game mode).
        /// </summary>
        public static AgencyState LoadAgency(Guid agencyId)
        {
            if (!PerAgencyEnabled)
                return null;

            if (Agencies.TryGetValue(agencyId, out var existing))
                return existing;

            var filePath = FilePathFor(agencyId);
            var state = LoadAgencyFromFile(filePath);
            if (state == null)
                return null;

            if (Agencies.TryAdd(state.AgencyId, state) && !string.IsNullOrEmpty(state.OwningPlayerName))
                AgencyByPlayerName[state.OwningPlayerName] = state.AgencyId;

            return state;
        }

        /// <summary>
        /// Resolves an admin-command argument token to an <see cref="AgencyState"/>.
        /// Stage 5.18d shared helper for the <c>setagency*</c> / <c>transferagency</c> /
        /// <c>deleteagency</c> commands so each one accepts the same set of operator-
        /// friendly forms.
        ///
        /// <para><b>Accepted tokens:</b>
        /// <list type="number">
        ///   <item><b>Guid in any .NET format</b> — "N" (32-hex no dashes, the form
        ///         <c>/listagencies</c> emits), "D" (hyphenated, the
        ///         <c>Guid.ToString()</c> default), "B" / "P" braced. Tried first;
        ///         a successful parse <i>commits the lookup to the Guid path</i> —
        ///         the resolver does NOT fall through to the name path on a parse-
        ///         succeeds-but-registry-misses, because a hex-shaped LMP handle
        ///         could otherwise silently shadow an operator's typo'd agency id.
        ///         If you typed a Guid, you meant an id.</item>
        ///   <item><b>OwningPlayerName (the LMP player handle)</b> — case-sensitive
        ///         match against <see cref="AgencyByPlayerName"/>. The handle is the
        ///         join key; an operator typing "Alice" gets Alice's agency
        ///         regardless of its display name. Reached only when the token
        ///         did NOT parse as a Guid.</item>
        /// </list></para>
        ///
        /// <para><b>What is NOT accepted:</b>
        /// <list type="bullet">
        ///   <item>The agency's DisplayName — free-form, may contain spaces /
        ///         quotes / unicode that would be ambiguous on a command line.
        ///         Operators reading <c>/listagencies</c> output should use the
        ///         <c>id=</c> token or the <c>owner=</c> token, NOT
        ///         <c>display=</c>.</item>
        ///   <item>An <b>orphaned agency id</b> — a Guid that vessels in
        ///         <see cref="VesselStoreSystem.CurrentVessels"/> reference but
        ///         <see cref="LoadExistingAgencies"/> couldn't load (per-file
        ///         isolation skip on corruption). Recovery is the Stage 5.18d
        ///         <c>/transferagency</c> command (slice e), NOT <c>/setagency</c>
        ///         (there's no <c>AgencyState</c> to mutate).</item>
        ///   <item>A <b>renamed-player handle</b> — <see cref="AgencyByPlayerName"/>
        ///         indexes by REGISTRATION-time name. If a player reconnects under
        ///         a different LMP handle, this lookup misses. Operator workaround:
        ///         use the agency id (still in the registry) or <c>/transferagency</c>
        ///         (slice e) to rebind to the new name.</item>
        /// </list></para>
        ///
        /// <para>Returns false when the gate is closed (gate-off OR non-Career
        /// mode) — admin commands MUST refuse loudly in that case, not silently
        /// look up against a stale registry. Returns false also when the token
        /// matches neither path; caller produces the appropriate "agency not
        /// found" / "no agencies registered" error.</para>
        /// </summary>
        public static bool TryResolveAgencyToken(string token, out AgencyState state)
        {
            state = null;
            if (!PerAgencyEnabled) return false;
            if (string.IsNullOrEmpty(token)) return false;

            // Guid-first commitment (server-systems-review v1 SS-1). A successful
            // Guid.TryParse routes the lookup through the registry-by-id path; a
            // miss returns false IMMEDIATELY rather than falling through to the
            // name path. Without this commitment, a player with a hex-string LMP
            // handle could silently shadow an operator's mistyped agency id.
            if (Guid.TryParse(token, out var parsedId))
            {
                if (Agencies.TryGetValue(parsedId, out var byId))
                {
                    state = byId;
                    return true;
                }
                return false;
            }

            if (AgencyByPlayerName.TryGetValue(token, out var idByName) &&
                Agencies.TryGetValue(idByName, out var byName))
            {
                state = byName;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Stage 5.18d slice (g) <c>/deleteagency</c>. Removes an
        /// <see cref="AgencyState"/> entirely — in-memory registry entry,
        /// <see cref="AgencyByPlayerName"/> index, and on-disk file (canonical
        /// + .bak). Walks <see cref="VesselStoreSystem.CurrentVessels"/>
        /// demoting any vessel stamped with the deleted agency's id to
        /// <see cref="Guid.Empty"/> (the Unassigned-sentinel per spec §10 Q3),
        /// returning the set of demoted vessel ids so the caller can emit the
        /// matching <c>AgencyVisibilityMsgData</c> broadcast and release locks.
        ///
        /// <para><b>Lock discipline.</b> Holds <see cref="PlayerNameLocks"/> for
        /// the agency's prior owner + <see cref="GetAgencyLock"/> for the agency
        /// id. Single-name-lock (not dual) because deletion only mutates one
        /// player-name mapping (remove). The vessel walk reads
        /// <see cref="VesselStoreSystem.CurrentVessels"/> values which is a
        /// <see cref="ConcurrentDictionary{TKey, TValue}"/> — its
        /// <see cref="ConcurrentDictionary{TKey, TValue}.Values"/> enumerator
        /// is moment-in-time-safe; a vessel proto landing mid-walk that
        /// stamps a NEW vessel with this AgencyId would not be in our snapshot
        /// and would survive the delete as a fresh orphan (boot
        /// <see cref="WarnAboutOrphanedVessels"/> would surface it). Acceptable
        /// transient — the new-stamp race is narrow and the orphan diagnostic
        /// catches it.</para>
        ///
        /// <para><b>Persistence-before-broadcast contract for callers.</b> The
        /// caller MUST emit the <c>AgencyVisibilityMsgData</c> broadcast AFTER
        /// this method returns and AFTER the canonical store mutation lands —
        /// matches the slice (a) <see cref="AgencySystemSender.BroadcastVisibilityChange"/>
        /// XML's mutation-ordering contract. This method handles steps 1-4 of
        /// the cascade (vessel demote + registry remove + index remove + disk
        /// delete) inside the lock; the broadcast is step 5, outside.</para>
        ///
        /// <para><b>Crash-window note.</b> Between the in-memory vessel
        /// <see cref="Server.System.Vessel.Classes.Vessel.OwningAgencyId"/>
        /// mutations and the next periodic <see cref="BackupSystem.RunBackup"/>
        /// flush of vessel state to disk, a server crash leaves vessels on disk
        /// still carrying the deleted agency's stamp — they would re-load as
        /// orphans on next boot. The boot helper
        /// <see cref="WarnAboutOrphanedVessels"/> surfaces them. Operator
        /// recovery in that case: restore <c>Universe/Agencies/{guid}.txt(.bak)</c>
        /// if still on disk (the .bak deletion below makes this unrecoverable
        /// — same destructive contract documented on the command surface), OR
        /// accept that the orphan-stamped vessels become operator-fixable only
        /// via a future re-stamp (no admin command supports that today).</para>
        ///
        /// <para><b>Failure modes</b> (caller surfaces <paramref name="failureReason"/>):
        /// <list type="bullet">
        ///   <item>Gate is closed — returns false.</item>
        ///   <item>Source state is null — returns false (defensive).</item>
        /// </list>
        /// No "agency not found in registry" branch because the caller has
        /// already resolved the source via <see cref="TryResolveAgencyToken"/>;
        /// a successful resolve guarantees the agency is in
        /// <see cref="Agencies"/>.</para>
        ///
        /// <para><b>Out-of-scope concerns the CALLER handles:</b>
        /// <list type="bullet">
        ///   <item>Broadcast <c>AgencyVisibilityMsgData</c> with the demoted
        ///         vessel ids.</item>
        ///   <item>Release the prior owner's vessel-scoped locks on the demoted
        ///         vessels (the lock subsystem is not coupled to AgencySystem).</item>
        ///   <item>Operator-visible logging.</item>
        /// </list></para>
        /// </summary>
        public static bool TryDeleteAgency(
            AgencyState source,
            out List<Guid> demotedVesselIds,
            out string failureReason)
        {
            // [Phase 3 Slice C / consumer-lens MUST FIX #2 — Slice E migration
            // policy anchor.] When Slice E (Phase 3 MKS-aware extension to
            // transferagency + deleteagency) lands, the per-router migration
            // policy on the THREE per-agency MKS dicts is:
            //   - Kolony (AgencyState.KolonyEntries, vessel-and-body keyed):
            //         on deleteagency, the agency file is removed wholesale
            //         (per AgencyState.AgenciesPath unlink below) — kolony
            //         entries vanish with the agency. No separate echo needed
            //         (client mirror forgets the agency id and its state).
            //         On transferagency, kolony entries migrate A→B keyed by
            //         vessel-prefix scan (entries whose key starts with
            //         {movedVesselId:N}|...).
            //   - Planetary (AgencyState.PlanetaryEntries, body-and-resource
            //         keyed): on deleteagency, entries vanish with the agency
            //         (same wholesale-file-removal mechanism). On
            //         transferagency, planetary entries DO NOT MIGRATE
            //         — the entry represents a body's logistics pool, not a
            //         vessel's contribution (pre-spec §4.e). Slice E's
            //         transferagency MKS extension must explicitly SKIP this
            //         dict; the documented operator recovery for "I want my
            //         planetary balances to move with the vessel" is to
            //         hand-edit Universe/Agencies/{guid}.txt.
            //   - Orbital (AgencyState.OrbitalTransfers, transfer-Guid keyed):
            //         on deleteagency, entries vanish wholesale. On
            //         transferagency, value-field-scan against
            //         OriginVesselId + DestinationVesselId; the entries where
            //         Destination is the moved vessel migrate (delivery
            //         authority follows the destination's owner); entries
            //         where Origin is the moved vessel STAY in the source
            //         agency by default (the launch obligation was incurred
            //         in source's frame; product decision).
            // Slice E author: see the three router class XMLs +
            // AgencyKolonyRouter.Upsert + AgencyPlanetaryRouter.Upsert +
            // AgencyOrbitalRouter.Upsert "Migration scan strategy" paragraphs
            // for the per-router scan implementation guidance.
            demotedVesselIds = new List<Guid>();
            failureReason = string.Empty;

            if (!PerAgencyEnabled)
            {
                failureReason = "Per-agency career is not active (gate-off or non-Career mode).";
                return false;
            }
            if (source == null)
            {
                failureReason = "Source agency is null.";
                return false;
            }

            var oldOwnerName = source.OwningPlayerName ?? string.Empty;
            var agencyId = source.AgencyId;

            // Single name-lock (the OWNER's name). PlayerNameLocks.GetOrAdd is
            // idempotent — a fresh entry for an empty-string name is harmless
            // and ConcurrentDictionary handles the GetOrAdd race cleanly.
            //
            // Empty-name branch (server-systems-review v1 SS-4): an AgencyState
            // with empty OwningPlayerName shouldn't legitimately exist post-
            // RegisterAgency (which throws on IsNullOrEmpty), but hand-edited
            // AgencyState files can parse with empty OwningPlayerName per
            // AgencyState.Parse's permissive zero-defaults. Use a per-call
            // private object rather than polluting PlayerNameLocks with a
            // shared "" anchor that any future empty-string-special-case path
            // would race on.
            var nameLock = string.IsNullOrEmpty(oldOwnerName)
                ? new object()
                : PlayerNameLocks.GetOrAdd(oldOwnerName, _ => new object());

            lock (nameLock)
            {
                lock (GetAgencyLock(agencyId))
                {
                    // Demote vessels in-place. The mutation is in-memory; vessel
                    // disk persistence happens on the next BackupSystem.RunBackup
                    // (crash-window note above). Snapshot ids during the walk so
                    // the caller's broadcast / lock-release passes have a stable
                    // list independent of further VesselStoreSystem mutations.
                    foreach (var kvp in VesselStoreSystem.CurrentVessels)
                    {
                        if (kvp.Value.OwningAgencyId != agencyId) continue;
                        kvp.Value.OwningAgencyId = Guid.Empty;
                        demotedVesselIds.Add(kvp.Key);
                    }

                    // Remove the in-memory registry entry first, then the index.
                    // ConcurrentDictionary atomic mutations; a parallel reader
                    // landing between TryRemove(Agencies) and TryRemove(AgencyByPlayerName)
                    // sees an AgencyByPlayerName mapping that points at a missing
                    // agency — TryResolveAgencyToken's name path then misses cleanly
                    // (Agencies.TryGetValue returns false, falls back to its existing
                    // not-found return). Reverse order would leave a stale Agencies
                    // entry with no name mapping — harder to reason about.
                    Agencies.TryRemove(agencyId, out _);
                    if (!string.IsNullOrEmpty(oldOwnerName))
                        AgencyByPlayerName.TryRemove(oldOwnerName, out _);

                    // Delete the canonical file + .bak. FileHandler.FileDelete is
                    // existence-checked + per-path-locked; safe to call on missing
                    // files (no-op). Operators who want to preserve the file for
                    // forensic recovery should rename it elsewhere BEFORE running
                    // /deleteagency (the command's --confirm flag is the
                    // destructive opt-in).
                    var canonicalPath = source.FilePath;
                    FileHandler.FileDelete(canonicalPath);
                    FileHandler.FileDelete(canonicalPath + ".bak");

                    // GC the per-agency lock anchor. After the registry remove,
                    // no legitimate caller resolves this AgencyId; a stale path
                    // that does will receive a fresh uncontended lock from
                    // GetAgencyLock on next call, which is safe.
                    AgencyLocks.TryRemove(agencyId, out _);
                }
            }

            // PlayerNameLocks GC was deliberately removed (server-systems-review
            // v1 SS-1). The prior owner reconnects with the SAME name — that's
            // the documented "fresh agency on next reconnect" UX — so
            // RegisterAgency hits PlayerNameLocks.GetOrAdd(oldOwnerName, ...).
            // If we'd GC'd the entry here, two concurrent reconnects for the
            // same name would observe DIFFERENT lock anchors (one sees the
            // remembered one, the other gets a freshly minted one between the
            // TryRemove and the next GetOrAdd) — defeating the same-name
            // double-register serialisation contract at RegisterAgency. The
            // memory cost of retaining the anchor is one heap-object per
            // ever-registered player name (bounded by the cardinality of
            // distinct LMP handles a server has ever served) — negligible.
            // AgencyLocks GC above is safe by contrast: no legitimate caller
            // resolves to a deleted AgencyId, so a fresh uncontended object on
            // a hypothetical stale call is the right behavior.

            return true;
        }

        /// <summary>
        /// Stage 5.18d slice (e) <c>/transferagency</c>. Renames the owner of an
        /// existing agency to a different LMP player handle. Vessels keep their
        /// <c>OwningAgencyId</c> stamp — the agency's identity is preserved; only
        /// the player handle attached to it changes.
        ///
        /// <para><b>Lock discipline.</b> Holds the per-name locks for BOTH the
        /// old and new owner names (alphabetically ordered to prevent ABBA
        /// deadlocks against a concurrent <see cref="OnPlayerAuthenticated"/> for
        /// either name) and the per-agency lock for the source <see cref="AgencyState"/>.
        /// The two name-locks are required because <see cref="AgencyByPlayerName"/>
        /// is mutated for both keys; the agency-lock is required for the
        /// <see cref="AgencyState.OwningPlayerName"/> field write +
        /// <see cref="SaveAgency"/> serialisation. The name-lock acquire order
        /// matches <see cref="RegisterAgency"/>'s contract so a concurrent register
        /// for either name sees the post-mutation index state cleanly.</para>
        ///
        /// <para><b>Failure modes</b> (caller surfaces <paramref name="failureReason"/>):
        /// <list type="bullet">
        ///   <item>Gate is closed — returns false immediately.</item>
        ///   <item>Source state is null — returns false (defensive).</item>
        ///   <item>New name is null / whitespace — returns false. Length-cap and
        ///         character-class validation live at the caller (the command
        ///         layer applies <c>MaxUsernameLength</c> and any future character
        ///         constraints).</item>
        ///   <item>New name collides with an existing agency's <c>OwningPlayerName</c>
        ///         — returns false. Operator must transfer the colliding agency
        ///         first, or use <c>/deleteagency</c> on it.</item>
        ///   <item>New name equals current name — returns true as a no-op (idempotent
        ///         rename to same value is safe).</item>
        /// </list></para>
        ///
        /// <para><b>Out-of-scope concerns the CALLER handles:</b>
        /// <list type="bullet">
        ///   <item>Releasing the old owner's vessel-scoped locks for vessels of
        ///         this agency. The lock subsystem is not coupled to AgencySystem;
        ///         the caller walks <c>LockQuery.GetAllPlayerLocks</c> directly.</item>
        ///   <item>Echoing <see cref="LmpCommon.Message.Data.Agency.AgencyStateMsgData"/>
        ///         to the new owner if online.</item>
        ///   <item>Operator-visible logging.</item>
        /// </list></para>
        /// </summary>
        public static bool TryRenameAgencyOwner(AgencyState source, string newOwnerName, out string failureReason)
        {
            // [Phase 3 Slice C / consumer-lens MUST FIX #2 — Slice E author
            // note.] This method renames the OWNER of an existing agency;
            // the agency's Guid identity is preserved. Vessels keep their
            // OwningAgencyId stamp unchanged. NO per-router MKS migration is
            // needed here for kolony / planetary / orbital state — the
            // entries stay under the same agency id (just with a different
            // player handle attached). The per-router migration policy
            // documented at TryDeleteAgency applies to a FUTURE Slice E
            // setvesselagency command (which mutates vessel.OwningAgencyId
            // directly), NOT this method.
            failureReason = string.Empty;
            if (!PerAgencyEnabled)
            {
                failureReason = "Per-agency career is not active (gate-off or non-Career mode).";
                return false;
            }
            if (source == null)
            {
                failureReason = "Source agency is null.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(newOwnerName))
            {
                failureReason = "New player name must be non-empty.";
                return false;
            }

            var oldOwnerName = source.OwningPlayerName ?? string.Empty;
            if (string.Equals(oldOwnerName, newOwnerName, StringComparison.Ordinal))
            {
                // Idempotent no-op. Operator may run the same command twice from
                // a script or GUI; the second call returns success without churning
                // the index / disk write / lock-release subsystem.
                return true;
            }

            // Lock both name-locks in alphabetical order. We acquire BOTH because
            // we mutate AgencyByPlayerName entries keyed on both names (remove old,
            // add new); a concurrent OnPlayerAuthenticated for either name could
            // otherwise race the index mutation.
            string first, second;
            if (string.CompareOrdinal(oldOwnerName, newOwnerName) < 0)
            {
                first = oldOwnerName; second = newOwnerName;
            }
            else
            {
                first = newOwnerName; second = oldOwnerName;
            }

            var firstLock = PlayerNameLocks.GetOrAdd(first, _ => new object());
            var secondLock = PlayerNameLocks.GetOrAdd(second, _ => new object());

            lock (firstLock)
            {
                lock (secondLock)
                {
                    // Re-check collision under lock. A concurrent OnPlayerAuthenticated
                    // for newOwnerName that landed between our unlocked precondition
                    // check (if any) and this acquire could have minted a fresh agency
                    // for newOwnerName. The lock pair would have serialised that
                    // register against this rename; whichever landed first wins, and
                    // the re-check here catches the case where the register landed
                    // first under a different lock-order observation.
                    if (AgencyByPlayerName.ContainsKey(newOwnerName))
                    {
                        failureReason =
                            $"Player '{newOwnerName}' already owns another agency. " +
                            "Transfer or delete the other agency first, then retry.";
                        return false;
                    }

                    lock (GetAgencyLock(source.AgencyId))
                    {
                        // Persistence-before-index ordering — same rule as RegisterAgency.
                        // Mutate the state field first + persist, then flip the index.
                        // A crash between SaveAgency and the index swap leaves disk
                        // showing the new owner but the in-memory index pointing at
                        // the old name; LoadExistingAgencies on next boot reads the
                        // disk file's OwningPlayerName and rebuilds the index from
                        // it, so the disk-truth wins.
                        source.OwningPlayerName = newOwnerName;
                        SaveAgency(source.AgencyId);

                        // Swap the index in ADD-THEN-REMOVE order. AgencyByPlayerName
                        // uses ConcurrentDictionary so individual mutations are atomic;
                        // the brief window between Add and Remove leaves BOTH names
                        // mapped to the same id, rather than NEITHER (server-systems-
                        // review v1 SS-3). An unlocked reader (e.g. a parallel
                        // /setagency on another admin thread) that lands in this
                        // window through TryResolveAgencyToken's name path resolves
                        // to the correct agency under either name — both paths
                        // succeed and return the same state. The remove-then-add
                        // alternative would briefly fail BOTH lookups.
                        AgencyByPlayerName[newOwnerName] = source.AgencyId;
                        if (!string.IsNullOrEmpty(oldOwnerName))
                            AgencyByPlayerName.TryRemove(oldOwnerName, out _);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Flushes one agency's state to disk via <see cref="FileHandler.WriteAtomic"/>.
        /// Caller is typically <see cref="RegisterAgency"/> (initial save) or — in future
        /// Stage 5 steps — a Share*-routed mutation. Holds the per-agency lock so
        /// <see cref="AgencyState.Serialize"/> doesn't race a concurrent field write.
        ///
        /// **Locking contract for callers that mutate <see cref="AgencyState"/> fields**
        /// (Stage 5.17b <c>Share*</c> writers): hold <see cref="GetAgencyLock"/> around
        /// the field write itself. <see cref="SaveAgency"/> serialises under the same
        /// lock, so the snapshot it persists is internally consistent. Without that,
        /// a multi-field mutation (e.g. paying for a tech node = debit Science + flip
        /// TechNodeState) can produce a torn intermediate snapshot on disk.
        ///
        /// No-op when <see cref="PerAgencyEnabled"/> is false (gate off OR non-Career game mode).
        /// </summary>
        public static void SaveAgency(Guid agencyId)
        {
            if (!PerAgencyEnabled)
                return;
            if (!Agencies.TryGetValue(agencyId, out var state))
                return;

            lock (GetAgencyLock(agencyId))
            {
                FileHandler.WriteAtomic(FilePathFor(agencyId), state.Serialize());
            }
        }

        /// <summary>
        /// Called from <see cref="HandshakeSystem"/> when a client clears handshake.
        /// First-connect creates a fresh agency; subsequent connects return the existing
        /// one (idempotent — <see cref="RegisterAgency"/> handles both paths).
        ///
        /// No-op when <see cref="PerAgencyEnabled"/> is false (gate off OR non-Career game mode).
        /// </summary>
        public static void OnPlayerAuthenticated(string playerName)
        {
            if (!PerAgencyEnabled)
                return;

            RegisterAgency(playerName);
        }

        /// <summary>
        /// Test-only helper. Clears the in-memory registries + lock anchors so successive
        /// tests don't carry state across. Called from <c>ServerTest</c> (<c>AgencySystemTest</c>)
        /// and <c>MockClientTest</c> (<c>ServerHarness.ResetPerTestState</c>); both assemblies
        /// have <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/> on
        /// the Server assembly. Never call from production code.
        /// </summary>
        internal static void Reset()
        {
            Agencies.Clear();
            AgencyByPlayerName.Clear();
            AgencyLocks.Clear();
            PlayerNameLocks.Clear();
        }

        /// <summary>
        /// Returns the per-agency lock object that <see cref="SaveAgency"/> acquires.
        /// Stage 5.17b <c>Share*</c> writers MUST hold this around field mutations on
        /// <see cref="AgencyState"/> so a concurrent SaveAgency does not serialise a
        /// torn multi-field snapshot. Same pattern as
        /// <see cref="Server.System.Scenario.ScenarioDataUpdater.GetSemaphore"/>.
        ///
        /// <para><b>Stage 5.18d MKS-aware <c>transferagency</c> extension</b>
        /// (consumer-lens Lens-3 SF1): the migration path acquires this lock
        /// TWICE (source agency + destination agency) in ordinal Guid-comparison
        /// order to avoid AB-BA deadlock against concurrent
        /// <c>transferagency</c> commands moving vessels in the opposite
        /// direction. Same lock-ordering rule as the BUG-033 precedent in
        /// <c>ScenarioDataUpdater.GetSemaphore</c>. See pre-spec §4.e
        /// (<c>mks-lmp-compatibility-phase-3-prespec.md</c>) for the full
        /// migration contract — atomic dict-remove + dict-add under the dual
        /// lock + persist both agencies + wire-echo (removal-echo to source,
        /// added-entries echo to destination — Slice E protocol-additive on
        /// <see cref="LmpCommon.Message.Data.Agency.AgencyKolonyStateMsgData"/>).</para>
        /// </summary>
        internal static object GetAgencyLock(Guid agencyId) =>
            AgencyLocks.GetOrAdd(agencyId, _ => new object());

        /// <summary>
        /// Reads + parses one agency file, healing the canonical path back from <c>.bak</c>
        /// if <see cref="FileHandler.ReadAtomic"/> had to fall back. The heal closes the
        /// 5.14c deferred CONSIDER where ReadAtomic would log the recovery warning on every
        /// subsequent read until something rewrote the canonical path.
        ///
        /// Returns null when the file (and its <c>.bak</c>) are absent — caller decides
        /// whether that means "skip" (LoadExistingAgencies) or "no such agency" (LoadAgency).
        ///
        /// **Locking (round-3 persistence review).** The heal <see cref="FileHandler.WriteAtomic"/>
        /// is gated on the per-agency lock when the parsed state has a real AgencyId, so a
        /// concurrent <see cref="SaveAgency"/> for the same agency cannot have its fresh
        /// content silently overwritten by stale <c>.bak</c> content during runtime
        /// <see cref="LoadAgency"/> calls. <see cref="LoadExistingAgencies"/> is boot-time
        /// (before the server accepts connections) so the lock is uncontested there but the
        /// guard is symmetric across both callers.
        /// </summary>
        private static AgencyState LoadAgencyFromFile(string filePath)
        {
            var canonicalExisted = FileHandler.FileExists(filePath);
            var bakExisted = FileHandler.FileExists(filePath + ".bak");

            var content = FileHandler.ReadAtomic(filePath);
            if (string.IsNullOrEmpty(content))
                return null;

            var state = AgencyState.Parse(content);

            if (!canonicalExisted && bakExisted)
            {
                // .bak supplied the content — write it back to the canonical path so
                // ReadAtomic stops surfacing the warning on every read after this one.
                // Hold the per-agency lock so a concurrent SaveAgency cannot land between
                // our content snapshot and the heal write and have its fresh state
                // silently rolled back to stale .bak. Stage 5.17b Share* writers will
                // need this guarantee — see GetAgencyLock locking contract above.
                lock (GetAgencyLock(state.AgencyId))
                {
                    FileHandler.WriteAtomic(filePath, content);
                }
                LunaLog.Normal($"[fix:per-agency-career] Healed canonical path after .bak recovery: {Path.GetFileName(filePath)}");
            }

            return state;
        }

        private static string FilePathFor(Guid agencyId) =>
            Path.Combine(AgencyState.AgenciesPath, agencyId.ToString("N") + ".txt");
    }
}
