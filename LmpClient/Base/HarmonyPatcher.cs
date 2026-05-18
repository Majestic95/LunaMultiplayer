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