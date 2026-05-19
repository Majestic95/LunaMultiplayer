using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Definition;
using Server.Settings;
using System;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// Pins the BUG-039 contract: operator-added custom XML elements in
    /// gameplaysettings.xml survive Load() then Save() rather than being silently dropped
    /// by the default XmlSerializer-then-rewrite cycle.
    ///
    /// Mechanism: <see cref="GameplaySettingsDefinition.CustomElements"/> has
    /// <c>[XmlAnyElement]</c>, so XmlSerializer parks unrecognised child elements there
    /// during deserialisation and emits them back on serialisation.
    ///
    /// Out-of-scope (intentionally not pinned by these tests): preservation of original
    /// sibling order between known settings and custom elements. XmlSerializer's standard
    /// behaviour moves <c>[XmlAnyElement]</c>-captured children to the end of the parent
    /// — acceptable trade-off vs dropping the data entirely.
    /// </summary>
    [TestClass]
    public class GameplaySettingsCustomElementsTest
    {
        [TestMethod]
        public void Deserialize_PopulatesCustomElements_WhenUnknownChildrenPresent()
        {
            var xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<GameplaySettingsDefinition>\n" +
                "  <CanRevert>true</CanRevert>\n" +
                "  <CustomOperatorTag>my-custom-value</CustomOperatorTag>\n" +
                "  <PerAgencyCareer>false</PerAgencyCareer>\n" +
                "  <SomeModSetting>42</SomeModSetting>\n" +
                "</GameplaySettingsDefinition>\n";

            var loaded = LunaXmlSerializer.ReadXmlFromString<GameplaySettingsDefinition>(xml);

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.CanRevert);
            Assert.IsFalse(loaded.PerAgencyCareer);
            Assert.IsNotNull(loaded.CustomElements);
            Assert.AreEqual(2, loaded.CustomElements.Length,
                "Both <CustomOperatorTag> and <SomeModSetting> should be captured as custom elements.");

            var names = loaded.CustomElements.Select(e => e.Name).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { "CustomOperatorTag", "SomeModSetting" }, names);
        }

        [TestMethod]
        public void Deserialize_LeavesCustomElementsNull_WhenNoUnknownChildren()
        {
            var xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<GameplaySettingsDefinition>\n" +
                "  <CanRevert>true</CanRevert>\n" +
                "</GameplaySettingsDefinition>\n";

            var loaded = LunaXmlSerializer.ReadXmlFromString<GameplaySettingsDefinition>(xml);

            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded.CanRevert);
            // XmlSerializer leaves [XmlAnyElement] arrays as null (not empty) when no unknowns
            // are present. This is fine; it just means there is nothing to serialize back.
            Assert.IsNull(loaded.CustomElements,
                "Default state for [XmlAnyElement] is null when no unknown children are parsed.");
        }

        [TestMethod]
        public void RoundTrip_PreservesCustomElements_ThroughLoadAndSave()
        {
            var path = Path.Combine(Path.GetTempPath(), $"bug_039_test_{Guid.NewGuid():N}.xml");
            try
            {
                // Stage 1: operator writes the file by hand with a custom element.
                var initial =
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<GameplaySettingsDefinition>\n" +
                    "  <CanRevert>true</CanRevert>\n" +
                    "  <OperatorAnnotation>set-by-hand-2026-05-19</OperatorAnnotation>\n" +
                    "</GameplaySettingsDefinition>\n";
                File.WriteAllText(path, initial);

                // Stage 2: server boots, loads, saves (the Load-then-Save pattern in
                // SettingsBase that previously dropped unknown elements).
                var loaded = LunaXmlSerializer.ReadXmlFromPath<GameplaySettingsDefinition>(path);
                Assert.IsNotNull(loaded);
                LunaXmlSerializer.WriteToXmlFile(loaded, path);

                // Stage 3: file on disk still contains the operator's annotation.
                var rewritten = File.ReadAllText(path);
                Assert.IsTrue(rewritten.Contains("<OperatorAnnotation>set-by-hand-2026-05-19</OperatorAnnotation>"),
                    $"Operator-added custom element should be preserved through Load+Save. Got: {rewritten}");

                // Stage 4: re-reading the post-save file still produces the custom element.
                var reloaded = LunaXmlSerializer.ReadXmlFromPath<GameplaySettingsDefinition>(path);
                Assert.IsNotNull(reloaded.CustomElements);
                Assert.AreEqual(1, reloaded.CustomElements.Length);
                Assert.AreEqual("OperatorAnnotation", reloaded.CustomElements[0].Name);
                Assert.AreEqual("set-by-hand-2026-05-19", reloaded.CustomElements[0].InnerText);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void HasDifferencesAgainstGivenSetting_IgnoresCustomElements_PublicFieldNotProperty()
        {
            // Regression guard: SettingsHandler.HasDifferencesAgainstGivenSetting uses
            // typeof(GameplaySettingsDefinition).GetProperties() which only returns public
            // properties, NOT public fields. CustomElements is a public field deliberately
            // (the XML doc explains why) — so the preset-comparison reflection does not see
            // it. If a future refactor turns CustomElements into an auto-property, the
            // reflection would start hitting it and NRE on the empty-array .ToString() vs
            // null-default .ToString() check.
            var customElementsMember = typeof(GameplaySettingsDefinition)
                .GetMember(nameof(GameplaySettingsDefinition.CustomElements))
                .FirstOrDefault();

            Assert.IsNotNull(customElementsMember);
            Assert.AreEqual(System.Reflection.MemberTypes.Field, customElementsMember.MemberType,
                "CustomElements MUST remain a public field (not a property) so SettingsHandler's preset-reflection doesn't see it. See [fix:BUG-039] doc on the member.");

            // Sanity: the reflection sweep used by SettingsHandler should not include the field.
            var propertyNames = typeof(GameplaySettingsDefinition).GetProperties()
                .Select(p => p.Name)
                .ToArray();
            CollectionAssert.DoesNotContain(propertyNames, nameof(GameplaySettingsDefinition.CustomElements));
        }
    }
}
