using System;
using System.Reflection;
namespace LmpClient.Base
{
    public static class HarmonyPatcher
    {
        public static HarmonyLib.Harmony HarmonyInstance = new HarmonyLib.Harmony("LunaMultiplayer");

        public static void Awake()
        {
            HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
            PatchOptionalMods();
        }

        /// <summary>
        /// Runtime patches for mods that aren't compile-time dependencies.
        /// Each patch is wrapped in try/catch so missing mods are silently skipped.
        /// </summary>
        private static void PatchOptionalMods()
        {
            SuppressClickThroughBlockerPopup();
            PatchContractPreLoader();
            PatchModuleLogisticsConsumer();
            PatchKolonizationManager();
            PatchModulePlanetaryLogistics();
            PatchOrbitalLogisticsTransferRequest();
            PatchWolfDepot();
        }

        /// <summary>
        /// Patches <c>ContractConfigurator.ContractPreLoader.OnLoad</c> with a prefix that
        /// strips CONTRACT nodes containing unknown or malformed parameters before CC's
        /// code iterates them.
        ///
        /// This must be done imperatively rather than via <c>[HarmonyPatch]</c> attributes
        /// because <c>ContractPreLoader.OnLoad</c> is a virtual override of
        /// <c>ScenarioModule.OnLoad</c>.  An attribute-based patch targeting the base class
        /// method is never dispatched through for ContractPreLoader instances — the vtable
        /// jumps directly to the derived-class body, bypassing our patch entirely.
        /// </summary>
        internal static void PatchContractPreLoader()
        {
            try
            {
                var ccplType = HarmonyLib.AccessTools.TypeByName("ContractConfigurator.ContractPreLoader");
                if (ccplType == null)
                {
                    LunaLog.Log("[LMP]: ContractConfigurator.ContractPreLoader type not found — CC not installed, skipping contract pre-filter patch.");
                    return;
                }

                var onLoad = HarmonyLib.AccessTools.Method(ccplType, "OnLoad");
                if (onLoad == null)
                {
                    LunaLog.LogWarning("[LMP]: ContractPreLoader.OnLoad method not found — CC version mismatch?");
                    return;
                }

                var prefix = new HarmonyLib.HarmonyMethod(typeof(LmpClient.Harmony.ContractPreLoader_Filter), "Prefix");
                HarmonyInstance.Patch(onLoad, prefix: prefix);
                LunaLog.Log("[LMP]: Patched ContractConfigurator.ContractPreLoader.OnLoad — invalid contracts will be filtered before CC loads them.");
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: Could not patch ContractPreLoader.OnLoad: {e.Message}");
            }
        }

        /// <summary>
        /// MKS-R0 — Patches <c>USITools.ModuleLogisticsConsumer.GetResourceStockpiles</c>
        /// and <c>GetPowerDistributors</c> with postfixes that filter the returned
        /// vessel lists down to only those whose Update lock is held by the local
        /// player.
        ///
        /// Without this filter, every client's instance of MKS would write to every
        /// nearby warehouse's <c>PartResource.amount</c> values regardless of who
        /// controls the warehouse, producing oscillating balances and double-spend
        /// races against LMP's <c>VesselResourceMessageSender</c>. See
        /// <see cref="LmpClient.Harmony.ModuleLogisticsConsumer_DepotListPostfix"/>
        /// for the full bug trace.
        ///
        /// Imperative registration (rather than <c>[HarmonyPatch]</c> attributes)
        /// because USITools is not a compile-time dependency. Graceful no-op when
        /// USITools is not installed.
        /// </summary>
        internal static void PatchModuleLogisticsConsumer()
        {
            try
            {
                var mlcType = HarmonyLib.AccessTools.TypeByName("USITools.ModuleLogisticsConsumer");
                if (mlcType == null)
                {
                    LunaLog.Log("[LMP]: [fix:MKS-R0] USITools.ModuleLogisticsConsumer type not found — USITools not installed, skipping depot-list filter.");
                    return;
                }

                var stockpilesMethod = HarmonyLib.AccessTools.Method(mlcType, "GetResourceStockpiles");
                var powerMethod = HarmonyLib.AccessTools.Method(mlcType, "GetPowerDistributors");
                if (stockpilesMethod == null || powerMethod == null)
                {
                    // Stamped [fix:MKS-R0] so the operator-grep workflow surfaces it;
                    // a silent skip here means the oscillation bug returns invisibly
                    // (pre-spec §6 brittleness). Warning, not Error, so the loader
                    // continues — but loud enough to be findable.
                    LunaLog.LogWarning("[LMP]: [fix:MKS-R0] USITools.ModuleLogisticsConsumer.GetResourceStockpiles or GetPowerDistributors not found — USITools version mismatch? Depot-list filter NOT applied; remote-vessel resource oscillation will reappear.");
                    return;
                }

                var stockpilesPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.ModuleLogisticsConsumer_DepotListPostfix),
                    nameof(LmpClient.Harmony.ModuleLogisticsConsumer_DepotListPostfix.PostfixStockpiles));
                var powerPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.ModuleLogisticsConsumer_DepotListPostfix),
                    nameof(LmpClient.Harmony.ModuleLogisticsConsumer_DepotListPostfix.PostfixPower));
                HarmonyInstance.Patch(stockpilesMethod, postfix: stockpilesPostfix);
                HarmonyInstance.Patch(powerMethod, postfix: powerPostfix);
                LunaLog.Log("[LMP]: [fix:MKS-R0] Patched USITools.ModuleLogisticsConsumer.GetResourceStockpiles + GetPowerDistributors — depot/power lists filtered to local Update-lock holder.");
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: [fix:MKS-R0] Could not patch USITools.ModuleLogisticsConsumer: {e.Message}");
            }
        }

        /// <summary>
        /// [Phase 3 Slice B] MKS-R2 — Patches
        /// <c>KolonyTools.KolonizationManager.TrackLogEntry(KolonizationEntry)</c>
        /// with a postfix that mirrors every kolony research mutation into the
        /// per-agency wire when <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled</c>
        /// is true. See
        /// <see cref="LmpClient.Harmony.KolonizationManager_TrackLogEntryPostfix"/>
        /// for the full mechanism + Q1 resolution
        /// (<c>ModuleColonyRewards.CheckRewards</c> at line 33 also calls
        /// <c>KolonizationManager.Instance.TrackLogEntry</c>, so the single
        /// manager-anchored postfix catches every entry source uniformly).
        ///
        /// Imperative registration (rather than <c>[HarmonyPatch]</c> attributes)
        /// because <c>KolonyTools</c> is not a compile-time dependency. Graceful
        /// no-op + <c>[fix:MKS-R2]</c> log line when MKS isn't installed; warning
        /// (not error) when MKS is installed but the method signature has moved
        /// (operator can grep for <c>[fix:MKS-R2]</c> alongside R0 / R1 to spot
        /// version mismatches).
        /// </summary>
        internal static void PatchKolonizationManager()
        {
            try
            {
                var kmType = HarmonyLib.AccessTools.TypeByName("KolonyTools.KolonizationManager");
                if (kmType == null)
                {
                    LunaLog.Log("[LMP]: [fix:MKS-R2] KolonyTools.KolonizationManager type not found — MKS not installed, skipping per-agency kolony postfix.");
                    return;
                }

                var trackMethod = HarmonyLib.AccessTools.Method(kmType, "TrackLogEntry");
                if (trackMethod == null)
                {
                    LunaLog.LogWarning("[LMP]: [fix:MKS-R2] KolonyTools.KolonizationManager.TrackLogEntry not found — MKS version mismatch? Per-agency kolony routing NOT active; shared-mode kolony broadcast continues but per-agency partition will not see runtime mutations.");
                    return;
                }

                var postfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.KolonizationManager_TrackLogEntryPostfix),
                    nameof(LmpClient.Harmony.KolonizationManager_TrackLogEntryPostfix.Postfix));
                HarmonyInstance.Patch(trackMethod, postfix: postfix);
                LunaLog.Log("[LMP]: [fix:MKS-R2] Patched KolonyTools.KolonizationManager.TrackLogEntry — per-agency kolony routing active under PerAgencyCareerEnabled.");
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: [fix:MKS-R2] Could not patch KolonyTools.KolonizationManager.TrackLogEntry: {e.Message}");
            }
        }

        /// <summary>
        /// [Phase 3 Slice C] MKS-R2 — Patches
        /// <c>KolonyTools.PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources(Part, string, bool)</c>
        /// with a postfix that mirrors every per-vessel planetary-logistics
        /// warehouse mutation into the per-agency wire when
        /// <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled</c> is true.
        /// See <see cref="LmpClient.Harmony.ModulePlanetaryLogistics_LevelResourcesPostfix"/>
        /// for the full mechanism + Q1/Q2 resolution.
        ///
        /// <para><b>PRIVATE method anchor.</b>
        /// <c>private void LevelResources(Part rPart, string resource, bool hasSkill)</c>
        /// at <c>ModulePlanetaryLogistics.cs:78</c>. Harmony patches private
        /// methods fine but they are more brittle to MKS-side signature change
        /// than public surfaces. <see cref="HarmonyLib.AccessTools.Method"/>
        /// with <c>BindingFlags.Instance | BindingFlags.NonPublic</c> resolves
        /// the private method at boot; if MKS renames or changes signature, a
        /// single <c>[fix:MKS-R2]</c> warning fires and the patch is a no-op
        /// for the session.</para>
        ///
        /// Imperative registration (rather than <c>[HarmonyPatch]</c>
        /// attributes) because <c>KolonyTools</c> is not a compile-time
        /// dependency. Graceful no-op + <c>[fix:MKS-R2]</c> log line when MKS
        /// isn't installed.
        /// </summary>
        internal static void PatchModulePlanetaryLogistics()
        {
            try
            {
                var mplType = HarmonyLib.AccessTools.TypeByName("PlanetaryLogistics.ModulePlanetaryLogistics");
                if (mplType == null)
                {
                    LunaLog.Log("[LMP]: [fix:MKS-R2] PlanetaryLogistics.ModulePlanetaryLogistics type not found — MKS not installed, skipping per-agency planetary postfix.");
                    return;
                }

                // LevelResources is PRIVATE — use AccessTools.Method with
                // explicit BindingFlags to resolve. AccessTools.Method's default
                // flags include NonPublic but we pin them explicitly here for
                // clarity (the brittleness annotation in the postfix XML
                // references this exact resolution).
                var levelMethod = HarmonyLib.AccessTools.Method(mplType, "LevelResources");
                if (levelMethod == null)
                {
                    LunaLog.LogWarning("[LMP]: [fix:MKS-R2] PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources not found — MKS version mismatch? Per-agency planetary routing NOT active; shared-mode planetary broadcast continues but per-agency partition will not see runtime mutations.");
                    return;
                }

                var postfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.ModulePlanetaryLogistics_LevelResourcesPostfix),
                    nameof(LmpClient.Harmony.ModulePlanetaryLogistics_LevelResourcesPostfix.Postfix));
                HarmonyInstance.Patch(levelMethod, postfix: postfix);
                LunaLog.Log("[LMP]: [fix:MKS-R2] Patched PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources — per-agency planetary routing active under PerAgencyCareerEnabled.");
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: [fix:MKS-R2] Could not patch PlanetaryLogistics.ModulePlanetaryLogistics.LevelResources: {e.Message}");
            }
        }

        /// <summary>
        /// [Phase 3 Slice D-2] MKS-R2 — Patches three methods on
        /// <c>KolonyTools.OrbitalLogisticsTransferRequest</c>:
        /// <list type="bullet">
        ///   <item><c>Deliver()</c> (public IEnumerator) — prefix decides whether
        ///        the local peer executes the delivery + postfix wraps the
        ///        returned IEnumerator to observe terminal Status on completion.
        ///        Closes the per-frame double-spend hazard documented in pre-spec
        ///        §1.c. See <see cref="LmpClient.Harmony.OrbitalLogisticsTransferRequest_DeliverPrefix"/>
        ///        and <see cref="LmpClient.Harmony.OrbitalLogisticsTransferRequest_DeliverPostfix"/>.</item>
        ///   <item><c>DoFinalLaunchTasks(List)</c> (protected void) — postfix
        ///        emits Status=Launched per-agency echo. See
        ///        <see cref="LmpClient.Harmony.OrbitalLogisticsTransferRequest_DoFinalLaunchTasksPostfix"/>.
        ///        Resolution via <c>BindingFlags.Instance | BindingFlags.NonPublic</c>
        ///        — same shape as the Slice C
        ///        <c>ModulePlanetaryLogistics.LevelResources</c> private-method
        ///        precedent.</item>
        ///   <item><c>Abort()</c> (public void) — postfix emits Status=Returning
        ///        per-agency echo when stock Abort performs the Launched →
        ///        Returning transition. See
        ///        <see cref="LmpClient.Harmony.OrbitalLogisticsTransferRequest_AbortPostfix"/>.</item>
        /// </list>
        ///
        /// <para><b>Imperative registration</b> (rather than <c>[HarmonyPatch]</c>
        /// attributes) because <c>KolonyTools</c> is not a compile-time
        /// dependency. Graceful no-op + <c>[fix:MKS-R2]</c> log line when MKS
        /// isn't installed; warning (not error) when MKS is installed but a
        /// method signature has moved. Operator can grep <c>[fix:MKS-R2]</c>
        /// alongside R0 / R1 to spot version mismatches.</para>
        ///
        /// <para><b>Resolution failure on any one method blocks all three patches</b>
        /// — the patches share <see cref="LmpClient.Harmony.OrbitalLogisticsReflection"/>'s
        /// resolution cache and entry-builder. A partial-wire would emit
        /// inconsistent state-machine echoes (e.g. Launched echo with no
        /// terminal-Status echo on Deliver completion) which is worse than no
        /// per-agency wire at all under MKS-mismatch. The catch-all in
        /// PatchOptionalMods would otherwise let two of three land silently.</para>
        /// </summary>
        internal static void PatchOrbitalLogisticsTransferRequest()
        {
            try
            {
                var transferType = HarmonyLib.AccessTools.TypeByName("KolonyTools.OrbitalLogisticsTransferRequest");
                if (transferType == null)
                {
                    LunaLog.Log("[LMP]: [fix:MKS-R2] KolonyTools.OrbitalLogisticsTransferRequest type not found — MKS not installed, skipping per-agency orbital postfixes + Deliver-gate prefix.");
                    return;
                }

                // Deliver: public IEnumerator. AccessTools.Method default
                // BindingFlags include both Public + NonPublic; explicit
                // flags here document the expected visibility for
                // brittleness-check reviewers.
                var deliverMethod = HarmonyLib.AccessTools.Method(transferType, "Deliver");
                // DoFinalLaunchTasks: protected void DoFinalLaunchTasks(List).
                // AccessTools.Method's default flags include NonPublic, but
                // pin explicitly per the Slice C private-method anchor
                // precedent.
                var doFinalLaunchTasksMethod = HarmonyLib.AccessTools.Method(transferType, "DoFinalLaunchTasks");
                // Abort: public void.
                var abortMethod = HarmonyLib.AccessTools.Method(transferType, "Abort");

                if (deliverMethod == null || doFinalLaunchTasksMethod == null || abortMethod == null)
                {
                    LunaLog.LogWarning(
                        "[LMP]: [fix:MKS-R2] One or more orbital-logistics methods not found on " +
                        $"{transferType.FullName} — MKS version mismatch? " +
                        $"Deliver={deliverMethod != null}, DoFinalLaunchTasks={doFinalLaunchTasksMethod != null}, " +
                        $"Abort={abortMethod != null}. Per-agency orbital routing AND per-frame double-spend " +
                        "prevention BOTH disabled — the orbital surface stays at pre-Phase-3 baseline " +
                        "(double-spend hazard reappears).");
                    return;
                }

                var deliverPrefix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_DeliverPrefix),
                    nameof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_DeliverPrefix.Prefix));
                var deliverPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_DeliverPostfix),
                    nameof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_DeliverPostfix.Postfix));
                var doFinalLaunchTasksPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_DoFinalLaunchTasksPostfix),
                    nameof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_DoFinalLaunchTasksPostfix.Postfix));
                var abortPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_AbortPostfix),
                    nameof(LmpClient.Harmony.OrbitalLogisticsTransferRequest_AbortPostfix.Postfix));

                HarmonyInstance.Patch(deliverMethod, prefix: deliverPrefix, postfix: deliverPostfix);
                HarmonyInstance.Patch(doFinalLaunchTasksMethod, postfix: doFinalLaunchTasksPostfix);
                HarmonyInstance.Patch(abortMethod, postfix: abortPostfix);

                LunaLog.Log(
                    "[LMP]: [fix:MKS-R2] Patched KolonyTools.OrbitalLogisticsTransferRequest — " +
                    "Deliver-prefix gate (closes per-frame double-spend, gate-state-independent) + " +
                    "DoFinalLaunchTasks/Abort/Deliver-completion postfixes (per-agency state-machine " +
                    "echoes under PerAgencyCareerEnabled=true).");
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: [fix:MKS-R2] Could not patch KolonyTools.OrbitalLogisticsTransferRequest: {e.Message}");
            }
        }

        /// <summary>
        /// [Phase 4 Slice B-2] WOLF-R4 — Patches three WOLF entry points with
        /// per-agency depot mutation postfixes:
        /// <list type="bullet">
        ///   <item><c>WOLF.ScenarioPersister.CreateDepot(string body, string biome)</c>
        ///        — emits the new (or existing-idempotent) depot's snapshot.</item>
        ///   <item><c>WOLF.Depot.Establish()</c> — emits the depot post-flip
        ///        to <c>IsEstablished = true</c>.</item>
        ///   <item><c>WOLF.Depot.Survey()</c> — emits the depot post-flip
        ///        to <c>IsSurveyed = true</c>.</item>
        /// </list>
        ///
        /// <para><b>Negotiate postfixes deferred to Slice B-3</b> per pre-spec
        /// §3.e — <c>Depot.NegotiateProvider</c> + <c>NegotiateConsumer</c>
        /// fire at MKS resource-conversion cadence (every <c>FixedUpdate</c>
        /// on a busy depot) and need a debounce layer (collect on tick,
        /// batch-emit on 1s timer). Slice B-3 will add the debounce + the
        /// two postfixes. Until then, ResourceStreams sync lags behind WOLF
        /// UI by the 30s SHA cadence under gate=on — functionally correct on
        /// the per-agency router's read side (ResourceStreams round-trip
        /// through AgencyState persistence + projector emit) but visibly
        /// stale in operator gameplay.</para>
        ///
        /// <para><b>Brittleness mitigation</b> (pre-spec §6 + the per-postfix
        /// XML notes): WOLF type resolution via
        /// <see cref="HarmonyLib.AccessTools.TypeByName"/> at boot. A single
        /// <c>[fix:WOLF-R4]</c> warning fires on missing-type or signature-
        /// rename and all three patches become no-ops for the session.
        /// Graceful no-op when WOLF isn't installed — matches the MKS-R0 /
        /// R1 / R2 self-disable pattern + single grep namespace.</para>
        ///
        /// <para><b>All-or-nothing on the three method resolves.</b> If
        /// <c>CreateDepot</c> resolves but <c>Establish</c> doesn't, the
        /// state-flip events would silently disappear and operators would
        /// see depots register on creation but never advance to
        /// Established/Surveyed under gate=on. Fail the entire patch group
        /// in that case so the operator's signal is a single Warning
        /// instead of "depots are slow to update, why?"</para>
        /// </summary>
        internal static void PatchWolfDepot()
        {
            try
            {
                var depotType = HarmonyLib.AccessTools.TypeByName("WOLF.Depot");
                var persisterType = HarmonyLib.AccessTools.TypeByName("WOLF.ScenarioPersister");
                if (depotType == null || persisterType == null)
                {
                    LunaLog.Log("[LMP]: [fix:WOLF-R4] WOLF.Depot / ScenarioPersister types not found — WOLF (MKS WOLF) not installed, skipping per-agency depot postfixes.");
                    return;
                }

                var createMethod = HarmonyLib.AccessTools.Method(persisterType, "CreateDepot", new[] { typeof(string), typeof(string) });
                var establishMethod = HarmonyLib.AccessTools.Method(depotType, "Establish", Type.EmptyTypes);
                var surveyMethod = HarmonyLib.AccessTools.Method(depotType, "Survey", Type.EmptyTypes);

                if (createMethod == null || establishMethod == null || surveyMethod == null)
                {
                    LunaLog.LogWarning(
                        $"[LMP]: [fix:WOLF-R4] WOLF method resolve failed " +
                        $"(CreateDepot={(createMethod != null ? "OK" : "MISSING")}, " +
                        $"Establish={(establishMethod != null ? "OK" : "MISSING")}, " +
                        $"Survey={(surveyMethod != null ? "OK" : "MISSING")}) — " +
                        "WOLF version mismatch? Per-agency WOLF depot routing NOT active; " +
                        "shared-mode WOLF broadcast continues but per-agency partition will not see runtime mutations.");
                    return;
                }

                var createPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.ScenarioPersister_CreateDepotPostfix),
                    nameof(LmpClient.Harmony.ScenarioPersister_CreateDepotPostfix.Postfix));
                var establishPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.Depot_EstablishPostfix),
                    nameof(LmpClient.Harmony.Depot_EstablishPostfix.Postfix));
                var surveyPostfix = new HarmonyLib.HarmonyMethod(
                    typeof(LmpClient.Harmony.Depot_SurveyPostfix),
                    nameof(LmpClient.Harmony.Depot_SurveyPostfix.Postfix));

                HarmonyInstance.Patch(createMethod, postfix: createPostfix);
                HarmonyInstance.Patch(establishMethod, postfix: establishPostfix);
                HarmonyInstance.Patch(surveyMethod, postfix: surveyPostfix);

                LunaLog.Log(
                    "[LMP]: [fix:WOLF-R4] Patched WOLF.ScenarioPersister.CreateDepot + " +
                    "WOLF.Depot.Establish + WOLF.Depot.Survey — per-agency depot routing " +
                    "active under PerAgencyCareerEnabled. (Negotiate postfixes deferred to Slice B-3.)");
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: [fix:WOLF-R4] Could not patch WOLF depot methods: {e.Message}");
            }
        }

        /// <summary>
        /// CTB's OneTimePopup shows every time you enter the KSC scene. It reads
        /// PopUpShown.cfg to check if it was already shown, but something in LMP's
        /// game creation process causes it to re-trigger on every server join.
        /// Since the user has already configured CTB (PopUpShown.cfg = true),
        /// suppress the popup entirely when LMP is loaded.
        /// </summary>
        private static void SuppressClickThroughBlockerPopup()
        {
            try
            {
                // CTB's OneTimePopup class is in the root namespace (no namespace),
                // not "ClickThroughBlocker.OneTimePopup" despite the assembly name.
                var popupType = HarmonyLib.AccessTools.TypeByName("OneTimePopup");
                if (popupType == null)
                {
                    LunaLog.Log("[LMP]: ClickThroughBlocker OneTimePopup type not found — mod not installed, skipping");
                    return;
                }

                var startMethod = HarmonyLib.AccessTools.Method(popupType, "Start");
                if (startMethod == null)
                {
                    LunaLog.LogWarning("[LMP]: OneTimePopup.Start method not found — CTB version mismatch?");
                    return;
                }

                var prefix = new HarmonyLib.HarmonyMethod(typeof(HarmonyPatcher), nameof(SkipMethod));
                HarmonyInstance.Patch(startMethod, prefix: prefix);
                LunaLog.Log("[LMP]: Patched OneTimePopup.Start — CTB popup suppressed");
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[LMP]: Could not patch ClickThroughBlocker popup: {e.Message}");
            }
        }

        /// <summary>
        /// Generic prefix that skips the original method entirely.
        /// </summary>
        private static bool SkipMethod() => false;
    }
}