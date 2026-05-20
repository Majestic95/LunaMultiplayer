using LmpCommon.ModFile.Structure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace LmpCommonTest
{
    /// <summary>
    /// Drift sentinel for <see cref="ModControlStructure.SetDefaultAllowedParts"/>.
    ///
    /// History: the v6 soak on 2026-05-20 hit a "Banned Parts" KSP dialog on every joiner
    /// because the hand-curated <c>AllowedParts</c> list had aged out of step with KSP's
    /// actual stock part set since the list was last refreshed around KSP 1.10. The
    /// structural fix moved <c>GeneralSettings.ModControl</c> default to <c>false</c>
    /// and added wildcard semantics (empty <c>AllowedParts</c> = no restriction) so the
    /// failure mode is no longer reachable on the default config — see
    /// <c>GeneralSettingsDefinition.ModControl</c> and <c>LmpClient.Systems.Mod.ModSystem.IsPartAllowed</c>
    /// for the rationale. This sentinel protects operators who explicitly opt back in to
    /// <c>ModControl=true</c> with the curated list (the only path that still exercises
    /// this code).
    ///
    /// The contract is narrow: each entry below must remain in the default list. New
    /// "drift hit my server" parts should be appended here AND added to
    /// <c>SetDefaultAllowedParts</c> when discovered, so future regressions of the same
    /// removal surface fail CI before reaching cohort. The list is intentionally small;
    /// the runtime wildcard is the structural fix, not this sentinel.
    /// </summary>
    [TestClass]
    public class ModControlDefaultsTest
    {
        /// <summary>
        /// Stock beginner parts that have been in KSP since well before 1.10. If any
        /// of these regress out of the default list, the "every joiner sees banned parts"
        /// failure mode comes back for operators with <c>ModControl=true</c>.
        /// </summary>
        private static readonly string[] StockBeginnerPartsAnchor =
        {
            "mk1pod",
            "parachuteSingle",
            "basicFin",
            "liquidEngine",
            "fuelTankSmall",
            "stackDecoupler",
            "launchClamp1",
            "GooExperiment",
            "longAntenna",
            "solarPanels1",
            "kerbalEVA",
            "kerbalEVAfemale",
            "flag",
        };

        [TestMethod]
        public void SetDefaultAllowedParts_ContainsStockBeginnerAnchor()
        {
            var modCtrl = new ModControlStructure();
            modCtrl.SetDefaultAllowedParts();

            var missing = StockBeginnerPartsAnchor.Where(p => !modCtrl.AllowedParts.Contains(p)).ToArray();

            Assert.IsFalse(missing.Any(),
                $"SetDefaultAllowedParts is missing stock beginner parts that joiners' first vessel will trip on: " +
                $"{string.Join(", ", missing)}. Either (a) restore the missing entries to LmpCommon/ModFile/Structure/ModControlStructure.cs " +
                $"or (b) accept the loss and remove the entries from ModControlDefaultsTest.StockBeginnerPartsAnchor (which signals " +
                $"a deliberate scope reduction). Note: the structural fix on this fork defaults ModControl=false so the curated " +
                $"list is no longer the hot path; this sentinel only guards operators who explicitly opt in to ModControl=true.");
        }

        [TestMethod]
        public void SetDefaultAllowedResources_ContainsStockResources()
        {
            // Sibling sentinel for AllowedResources. The resource list has historically been
            // less prone to drift (10 stock resources, none added since KSP 1.0), so this
            // anchor is more of a regression net than a drift catcher.
            var modCtrl = new ModControlStructure();
            modCtrl.SetDefaultAllowedResources();

            string[] stockAnchor =
            {
                "LiquidFuel",
                "Oxidizer",
                "SolidFuel",
                "MonoPropellant",
                "ElectricCharge",
                "Ore",
                "Ablator",
            };

            var missing = stockAnchor.Where(r => !modCtrl.AllowedResources.Contains(r)).ToArray();

            Assert.IsFalse(missing.Any(),
                $"SetDefaultAllowedResources is missing stock resources: {string.Join(", ", missing)}.");
        }
    }
}
