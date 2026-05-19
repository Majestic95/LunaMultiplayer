using LmpCommon.Enums;
using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Globalization;

namespace ServerTest
{
    /// <summary>
    /// [Mod-compat S4 — DMagic Orbital Science] Unit tests for
    /// <see cref="AgencyDMagicRouter"/>. Same shape as
    /// <see cref="AgencyScanRouterTest"/> — early-return branches via the
    /// public <c>TryRoute</c> with null client; deeper semantics via the
    /// internal <c>UpsertAsteroidScienceEntries</c> + <c>UpsertAnomalyEntries</c>
    /// helpers so we don't need to construct a live ClientStructure (which
    /// requires a NetConnection per the constructor).
    ///
    /// <para>S4 has no cross-agency rejection (no vessel keying) and no
    /// migration helper — simpler test surface than S2. Per-entry isolation
    /// at TWO levels for anomalies (per-DM_Anomaly_List wrapper + per-
    /// DM_Anomaly child) — same shape as S2's multi-Sensor isolation.</para>
    /// </summary>
    [TestClass]
    public class AgencyDMagicRouterTest
    {
        private readonly Guid _agencyAlice = Guid.NewGuid();
        private AgencyState _alice;

        [TestInitialize]
        public void Setup()
        {
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            _alice = new AgencyState
            {
                AgencyId = _agencyAlice,
                OwningPlayerName = "alice",
                DisplayName = "Alice Corp",
            };
            AgencySystem.Agencies[_agencyAlice] = _alice;
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
        }

        [TestCleanup]
        public void Teardown()
        {
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
        }

        // -------------------------------------------------------------------
        // TryRoute early-return branches
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRoute_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var scenario = BuildScenario(asteroidTitles: new[] { "AsteroidA" }, anomalies: new (int, string, double, double, double)[0]);

            var handled = AgencyDMagicRouter.TryRoute(null, scenario);

            Assert.IsFalse(handled, "Gate off must return false (legacy SHA pass runs unchanged)");
            Assert.AreEqual(0, _alice.DMagicAsteroidScience.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalse()
        {
            var scenario = BuildScenario(asteroidTitles: new[] { "AsteroidA" }, anomalies: new (int, string, double, double, double)[0]);
            Assert.IsFalse(AgencyDMagicRouter.TryRoute(null, scenario));
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalse()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var scenario = BuildScenario(asteroidTitles: new[] { "AsteroidA" }, anomalies: new (int, string, double, double, double)[0]);
            Assert.IsFalse(AgencyDMagicRouter.TryRoute(null, scenario));
        }

        // -------------------------------------------------------------------
        // Asteroid upsert
        // -------------------------------------------------------------------

        [TestMethod]
        public void UpsertAsteroid_TwoAsteroids_UpsertsBothByTitle()
        {
            var scenario = BuildScenario(
                asteroidTitles: new[] { "Asteroid Sample Eve", "Asteroid Sample Duna" },
                anomalies: new (int, string, double, double, double)[0]);

            var any = AgencyDMagicRouter.UpsertAsteroidScienceEntries(scenario, _alice);

            Assert.IsTrue(any);
            Assert.AreEqual(2, _alice.DMagicAsteroidScience.Count);
            Assert.IsTrue(_alice.DMagicAsteroidScience.ContainsKey("Asteroid Sample Eve"));
            Assert.IsTrue(_alice.DMagicAsteroidScience.ContainsKey("Asteroid Sample Duna"));
        }

        [TestMethod]
        public void UpsertAsteroid_MissingTitle_DropsEntryKeepsOthers()
        {
            var scenario = new ConfigNode("") { Name = "DMScienceScenario" };
            var astContainer = new ConfigNode("") { Name = "Asteroid_Science" };
            scenario.AddNode(astContainer);
            // Bad entry — no title
            var bad = new ConfigNode("") { Name = "DM_Science" };
            bad.CreateValue(new CfgNodeValue<string, string>("bsv", "1"));
            astContainer.AddNode(bad);
            // Good entry
            var good = new ConfigNode("") { Name = "DM_Science" };
            good.CreateValue(new CfgNodeValue<string, string>("title", "GoodAsteroid"));
            good.CreateValue(new CfgNodeValue<string, string>("bsv", "1"));
            good.CreateValue(new CfgNodeValue<string, string>("sci", "5"));
            astContainer.AddNode(good);

            AgencyDMagicRouter.UpsertAsteroidScienceEntries(scenario, _alice);

            Assert.AreEqual(1, _alice.DMagicAsteroidScience.Count,
                "Malformed entry dropped, sibling survives (Invariant 4)");
            Assert.IsTrue(_alice.DMagicAsteroidScience.ContainsKey("GoodAsteroid"));
        }

        [TestMethod]
        public void UpsertAsteroid_NoAsteroidScienceContainer_ReturnsFalse()
        {
            var scenario = new ConfigNode("") { Name = "DMScienceScenario" };
            var any = AgencyDMagicRouter.UpsertAsteroidScienceEntries(scenario, _alice);
            Assert.IsFalse(any);
            Assert.AreEqual(0, _alice.DMagicAsteroidScience.Count);
        }

        // -------------------------------------------------------------------
        // Anomaly upsert (2-level nested per Decision §B)
        // -------------------------------------------------------------------

        [TestMethod]
        public void UpsertAnomaly_MultipleBodies_UpsertsAllAsFlatCompositeKeys()
        {
            // 2 anomalies on Eve (body 5) + 1 on Duna (body 6).
            var scenario = BuildScenario(
                asteroidTitles: new string[0],
                anomalies: new (int, string, double, double, double)[]
                {
                    (5, "Monolith01", 1.0, 2.0, 100.0),
                    (5, "Monolith02", 3.0, 4.0, 200.0),
                    (6, "Pyramid",    5.0, 6.0, 300.0),
                });

            AgencyDMagicRouter.UpsertAnomalyEntries(scenario, _alice, _agencyAlice);

            Assert.AreEqual(3, _alice.DMagicAnomalies.Count);
            Assert.IsTrue(_alice.DMagicAnomalies.ContainsKey("5|Monolith01"));
            Assert.IsTrue(_alice.DMagicAnomalies.ContainsKey("5|Monolith02"));
            Assert.IsTrue(_alice.DMagicAnomalies.ContainsKey("6|Pyramid"));
            // Spot check field values
            Assert.AreEqual(3.0, _alice.DMagicAnomalies["5|Monolith02"].Latitude, 1e-9);
            Assert.AreEqual(5, _alice.DMagicAnomalies["5|Monolith02"].BodyIndex);
        }

        [TestMethod]
        public void UpsertAnomaly_MalformedBodyValue_DropsWrapperKeepsSiblings()
        {
            // Outer per-Wrapper isolation per Decision §B.
            var scenario = new ConfigNode("") { Name = "DMScienceScenario" };
            var anomContainer = new ConfigNode("") { Name = "Anomaly_Records" };
            scenario.AddNode(anomContainer);
            // Bad wrapper — Body field is "NOT_AN_INT"
            var badList = new ConfigNode("") { Name = "DM_Anomaly_List" };
            badList.CreateValue(new CfgNodeValue<string, string>("Body", "NOT_AN_INT"));
            var inBad = new ConfigNode("") { Name = "DM_Anomaly" };
            inBad.CreateValue(new CfgNodeValue<string, string>("Name", "ShouldBeDropped"));
            badList.AddNode(inBad);
            anomContainer.AddNode(badList);
            // Good wrapper
            var goodList = new ConfigNode("") { Name = "DM_Anomaly_List" };
            goodList.CreateValue(new CfgNodeValue<string, string>("Body", "5"));
            var inGood = new ConfigNode("") { Name = "DM_Anomaly" };
            inGood.CreateValue(new CfgNodeValue<string, string>("Name", "Survives"));
            inGood.CreateValue(new CfgNodeValue<string, string>("Lat", "1"));
            inGood.CreateValue(new CfgNodeValue<string, string>("Lon", "2"));
            inGood.CreateValue(new CfgNodeValue<string, string>("Alt", "3"));
            goodList.AddNode(inGood);
            anomContainer.AddNode(goodList);

            AgencyDMagicRouter.UpsertAnomalyEntries(scenario, _alice, _agencyAlice);

            Assert.AreEqual(1, _alice.DMagicAnomalies.Count);
            Assert.IsTrue(_alice.DMagicAnomalies.ContainsKey("5|Survives"));
        }

        [TestMethod]
        public void UpsertAnomaly_MissingNameOnChild_DropsChildKeepsSiblingsInSameBody()
        {
            // Inner per-DM_Anomaly isolation — a single bad anomaly does NOT
            // drop the whole per-body wrapper.
            var scenario = new ConfigNode("") { Name = "DMScienceScenario" };
            var anomContainer = new ConfigNode("") { Name = "Anomaly_Records" };
            scenario.AddNode(anomContainer);
            var list = new ConfigNode("") { Name = "DM_Anomaly_List" };
            list.CreateValue(new CfgNodeValue<string, string>("Body", "5"));
            // Bad anomaly — no Name
            var bad = new ConfigNode("") { Name = "DM_Anomaly" };
            bad.CreateValue(new CfgNodeValue<string, string>("Lat", "0"));
            list.AddNode(bad);
            // Good anomaly
            var good = new ConfigNode("") { Name = "DM_Anomaly" };
            good.CreateValue(new CfgNodeValue<string, string>("Name", "Survives"));
            good.CreateValue(new CfgNodeValue<string, string>("Lat", "1"));
            good.CreateValue(new CfgNodeValue<string, string>("Lon", "2"));
            good.CreateValue(new CfgNodeValue<string, string>("Alt", "3"));
            list.AddNode(good);
            anomContainer.AddNode(list);

            AgencyDMagicRouter.UpsertAnomalyEntries(scenario, _alice, _agencyAlice);

            Assert.AreEqual(1, _alice.DMagicAnomalies.Count);
            Assert.IsTrue(_alice.DMagicAnomalies.ContainsKey("5|Survives"));
        }

        [TestMethod]
        public void UpsertAnomaly_NoAnomalyContainer_ReturnsFalse()
        {
            var scenario = new ConfigNode("") { Name = "DMScienceScenario" };
            var any = AgencyDMagicRouter.UpsertAnomalyEntries(scenario, _alice, _agencyAlice);
            Assert.IsFalse(any);
            Assert.AreEqual(0, _alice.DMagicAnomalies.Count);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static ConfigNode BuildScenario(string[] asteroidTitles,
            (int bodyIndex, string name, double lat, double lon, double alt)[] anomalies)
        {
            var scenario = new ConfigNode("") { Name = "DMScienceScenario" };
            if (asteroidTitles != null && asteroidTitles.Length > 0)
            {
                var astContainer = new ConfigNode("") { Name = "Asteroid_Science" };
                scenario.AddNode(astContainer);
                foreach (var title in asteroidTitles)
                {
                    var aNode = new ConfigNode("") { Name = "DM_Science" };
                    aNode.CreateValue(new CfgNodeValue<string, string>("title", title));
                    aNode.CreateValue(new CfgNodeValue<string, string>("bsv", "1"));
                    aNode.CreateValue(new CfgNodeValue<string, string>("scv", "0.5"));
                    aNode.CreateValue(new CfgNodeValue<string, string>("sci", "5"));
                    aNode.CreateValue(new CfgNodeValue<string, string>("cap", "10"));
                    astContainer.AddNode(aNode);
                }
            }
            if (anomalies != null && anomalies.Length > 0)
            {
                var anomContainer = new ConfigNode("") { Name = "Anomaly_Records" };
                scenario.AddNode(anomContainer);
                // Group by body index for the wire shape.
                var byBody = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(string name, double lat, double lon, double alt)>>();
                foreach (var a in anomalies)
                {
                    if (!byBody.TryGetValue(a.bodyIndex, out var list))
                    {
                        list = new System.Collections.Generic.List<(string, double, double, double)>();
                        byBody[a.bodyIndex] = list;
                    }
                    list.Add((a.name, a.lat, a.lon, a.alt));
                }
                foreach (var kv in byBody)
                {
                    var listNode = new ConfigNode("") { Name = "DM_Anomaly_List" };
                    listNode.CreateValue(new CfgNodeValue<string, string>("Body", kv.Key.ToString(CultureInfo.InvariantCulture)));
                    foreach (var a in kv.Value)
                    {
                        var anomNode = new ConfigNode("") { Name = "DM_Anomaly" };
                        anomNode.CreateValue(new CfgNodeValue<string, string>("Name", a.name));
                        anomNode.CreateValue(new CfgNodeValue<string, string>("Lat", a.lat.ToString("R", CultureInfo.InvariantCulture)));
                        anomNode.CreateValue(new CfgNodeValue<string, string>("Lon", a.lon.ToString("R", CultureInfo.InvariantCulture)));
                        anomNode.CreateValue(new CfgNodeValue<string, string>("Alt", a.alt.ToString("R", CultureInfo.InvariantCulture)));
                        listNode.AddNode(anomNode);
                    }
                    anomContainer.AddNode(listNode);
                }
            }
            return scenario;
        }
    }
}
