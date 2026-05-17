using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Vessel;
using Server.System.Vessel.Classes;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// Regression coverage for BUG-013 (ModuleReactionWheel stateString locale
    /// normalisation). The fixture vessel
    /// XmlExampleFiles/Others/0d463562-b3ab-40db-9605-0f2d11eefbc3.txt ships
    /// with two ModuleReactionWheel entries whose <c>stateString = Running</c>
    /// — exactly the canonical English value. We mutate one to a non-English
    /// value to simulate the bug, run the sanitiser, and assert it's been
    /// rewritten without disturbing anything else on the vessel.
    /// </summary>
    [TestClass]
    public class VesselSanitizerTest
    {
        private static readonly string VesselFixturePath = Path.Combine(
            Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others",
            "0d463562-b3ab-40db-9605-0f2d11eefbc3.txt");

        private static Vessel LoadFixture() => new Vessel(File.ReadAllText(VesselFixturePath));

        private static (Part part, ConfigNode wheel) FirstReactionWheel(Vessel vessel)
        {
            foreach (var part in vessel.Parts.GetAllValues())
            {
                foreach (var module in part.Modules.GetAll())
                {
                    if (module.Key == "ModuleReactionWheel" || module.Key == "ModuleReactionWheelV2")
                        return (part, module.Value);
                }
            }
            return (null, null);
        }

        [TestMethod]
        public void Sanitize_LocalisedStateString_IsRewrittenToRunning()
        {
            var vessel = LoadFixture();
            var (_, wheel) = FirstReactionWheel(vessel);
            Assert.IsNotNull(wheel, "Fixture must contain at least one ModuleReactionWheel.");

            // The Russian "Работает" is the reporter's repro from issue #598.
            wheel.UpdateValue("stateString", "Работает");

            var rewritten = VesselSanitizer.SanitizeReactionWheelStateStrings(vessel);

            Assert.AreEqual(1, rewritten, "Expected exactly one rewrite for one bad wheel.");
            Assert.AreEqual("Running", wheel.GetValue("stateString").Value);
        }

        [TestMethod]
        public void Sanitize_CanonicalStateString_IsLeftAlone()
        {
            var vessel = LoadFixture();
            var (_, wheel) = FirstReactionWheel(vessel);
            Assert.IsNotNull(wheel);

            // Fixture already has stateString = Running on every wheel; baseline run
            // must touch zero fields. Confirms idempotency for clean vessels (most
            // proto-vessel writes — we don't want a busy server to spam the log).
            wheel.UpdateValue("stateString", "Running");

            var rewritten = VesselSanitizer.SanitizeReactionWheelStateStrings(vessel);

            Assert.AreEqual(0, rewritten, "Canonical stateString must not be rewritten.");
            Assert.AreEqual("Running", wheel.GetValue("stateString").Value);
        }

        [TestMethod]
        public void Sanitize_AllWheelStates_PreservedWhenCanonical()
        {
            // Disabled and Broken are the other legitimate stateString values —
            // make sure the whitelist doesn't accidentally collapse them onto Running.
            foreach (var legitimate in new[] { "Running", "Disabled", "Broken" })
            {
                var vessel = LoadFixture();
                foreach (var part in vessel.Parts.GetAllValues())
                {
                    foreach (var module in part.Modules.GetAll())
                    {
                        if (module.Key == "ModuleReactionWheel" || module.Key == "ModuleReactionWheelV2")
                            module.Value.UpdateValue("stateString", legitimate);
                    }
                }

                var rewritten = VesselSanitizer.SanitizeReactionWheelStateStrings(vessel);

                Assert.AreEqual(0, rewritten, $"stateString '{legitimate}' should be whitelisted.");
            }
        }

        [TestMethod]
        public void Sanitize_OnlyTouchesReactionWheelModules()
        {
            // Other modules sit alongside the wheel on the same vessel. Snapshot
            // every non-wheel module's value collection, run the sanitiser, and
            // assert nothing changed. Catches any future regression where the
            // module-name guard accidentally widens.
            var vessel = LoadFixture();

            var beforeByModule = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();
            foreach (var part in vessel.Parts.GetAllValues())
            {
                foreach (var module in part.Modules.GetAll())
                {
                    if (module.Key == "ModuleReactionWheel" || module.Key == "ModuleReactionWheelV2") continue;
                    var key = part.Fields.GetSingle("name").Value + "::" + module.Key;
                    if (!beforeByModule.ContainsKey(key))
                        beforeByModule[key] = module.Value.GetAllValues().Select(v => v.Key + "=" + v.Value).OrderBy(s => s).ToList();
                }
            }

            VesselSanitizer.SanitizeReactionWheelStateStrings(vessel);

            foreach (var part in vessel.Parts.GetAllValues())
            {
                foreach (var module in part.Modules.GetAll())
                {
                    if (module.Key == "ModuleReactionWheel" || module.Key == "ModuleReactionWheelV2") continue;
                    var key = part.Fields.GetSingle("name").Value + "::" + module.Key;
                    var after = module.Value.GetAllValues().Select(v => v.Key + "=" + v.Value).OrderBy(s => s).ToList();
                    CollectionAssert.AreEqual(beforeByModule[key], after,
                        $"Sanitiser modified non-wheel module {key}.");
                }
            }
        }

        [TestMethod]
        public void Sanitize_NullVessel_ReturnsZero_NoThrow()
        {
            Assert.AreEqual(0, VesselSanitizer.SanitizeReactionWheelStateStrings(null));
        }
    }
}
