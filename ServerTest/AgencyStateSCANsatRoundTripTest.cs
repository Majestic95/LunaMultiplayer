using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Round-trip pinning for the new
    /// <see cref="AgencyState.Coverage"/> + <see cref="AgencyState.Scanners"/>
    /// persistence sections. Mirrors <c>AgencyStateTest.Serialize_UsesInvariantCultureForDoubles</c>
    /// (Invariant 9 / BUG-013 precedent) — every double + float round-trips
    /// under a non-en thread culture without corruption. Adds optional-field
    /// preservation cases (ClampHeight + LandingTarget nullable) + per-entry
    /// isolation cases (malformed Body / malformed Sensor child).
    /// </summary>
    [TestClass]
    public class AgencyStateSCANsatRoundTripTest
    {
        private CultureInfo _originalCulture;

        [TestInitialize]
        public void Setup()
        {
            // Force the test thread to a comma-decimal culture so "R"+invariant
            // emit shows itself if a code path forgets the culture override.
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        }

        [TestCleanup]
        public void Teardown()
        {
            Thread.CurrentThread.CurrentCulture = _originalCulture;
        }

        // -------------------------------------------------------------------
        // Coverage round-trip
        // -------------------------------------------------------------------

        [TestMethod]
        public void Coverage_FullFields_RoundTrip()
        {
            var agency = BuildAgency();
            agency.Coverage["Kerbin"] = new AgencyCoverageBodyEntry
            {
                BodyName = "Kerbin",
                Disabled = false,
                MinHeightRange = 1.23456789f,
                MaxHeightRange = 9876.54321f,
                ClampHeight = 4567.89f,
                PaletteName = "Default",
                PaletteSize = 7,
                PaletteReverse = false,
                PaletteDiscrete = true,
                Map = "abc123-_xyz",
                LandingTarget = "12.3400,-45.6700",
            };

            var serialized = agency.Serialize();
            var parsed = AgencyState.Parse(serialized);

            Assert.IsTrue(parsed.Coverage.TryGetValue("Kerbin", out var entry), "Body round-trip lost");
            Assert.AreEqual("Kerbin", entry.BodyName);
            Assert.AreEqual(1.23456789f, entry.MinHeightRange, 1e-6f);
            Assert.AreEqual(9876.54321f, entry.MaxHeightRange, 1e-3f);
            Assert.IsNotNull(entry.ClampHeight);
            Assert.AreEqual(4567.89f, entry.ClampHeight.Value, 1e-3f);
            Assert.AreEqual(7, entry.PaletteSize);
            Assert.AreEqual("Default", entry.PaletteName);
            Assert.AreEqual("abc123-_xyz", entry.Map);
            Assert.AreEqual("12.3400,-45.6700", entry.LandingTarget);
            Assert.IsTrue(entry.PaletteDiscrete);
            Assert.IsFalse(entry.PaletteReverse);
            Assert.IsFalse(entry.Disabled);
        }

        [TestMethod]
        public void Coverage_OptionalFields_NullPreservedOnRoundTrip()
        {
            // ClampHeight + LandingTarget are SCANsat's conditional emits —
            // emitted only when non-null. Round-trip must preserve null
            // (Serialize omits the field; Parse yields null).
            var agency = BuildAgency();
            agency.Coverage["Mun"] = new AgencyCoverageBodyEntry
            {
                BodyName = "Mun",
                MinHeightRange = 0f,
                MaxHeightRange = 1000f,
                ClampHeight = null,
                PaletteName = "",
                Map = "",
                LandingTarget = null,
            };

            var serialized = agency.Serialize();

            // Direct text check — neither field name should appear when null.
            Assert.IsFalse(serialized.Contains("ClampHeight"),
                "Serialize must omit ClampHeight when null (Decision §8 optional round-trip)");
            Assert.IsFalse(serialized.Contains("LandingTarget"),
                "Serialize must omit LandingTarget when null");

            var parsed = AgencyState.Parse(serialized);
            Assert.IsTrue(parsed.Coverage.TryGetValue("Mun", out var entry));
            Assert.IsNull(entry.ClampHeight, "ClampHeight must round-trip as null");
            Assert.IsNull(entry.LandingTarget, "LandingTarget must round-trip as null");
        }

        [TestMethod]
        public void Coverage_MapBlob_RoundTripByteEqual()
        {
            // SCANsat's Map field is opaque Base64-CLZF2-BinaryFormatter with
            // URL-safe substitution (/ -> -, = -> _). Round-trip must preserve
            // every byte; any Trim/Normalize on the way through would silently
            // corrupt the bitmap on the client side.
            var agency = BuildAgency();
            var rawMap = "ABCDEFG-_xyz1234567890abcdefghijklmnopqrstuvwxyz";
            agency.Coverage["Eve"] = new AgencyCoverageBodyEntry
            {
                BodyName = "Eve",
                Map = rawMap,
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.Coverage.TryGetValue("Eve", out var entry));
            Assert.AreEqual(rawMap, entry.Map, "Opaque Map blob must round-trip byte-equal");
        }

        // -------------------------------------------------------------------
        // Scanners round-trip
        // -------------------------------------------------------------------

        [TestMethod]
        public void Scanners_MultiSensor_RoundTrip()
        {
            // Decision §9 — multi-Sensor-per-Vessel. A single Vessel record
            // carries N nested SensorRecord children; round-trip preserves both
            // the list count + per-Sensor field values.
            var agency = BuildAgency();
            var vesselId = Guid.NewGuid();
            agency.Scanners[vesselId] = new AgencyScannerEntry
            {
                VesselId = vesselId,
                VesselName = "Scout-1",
                Sensors = new List<AgencyScannerSensorRecord>
                {
                    new AgencyScannerSensorRecord
                    {
                        SensorType = 16, // SCANtype.Altimetry
                        Fov = 3.5f,
                        MinAlt = 5000,
                        MaxAlt = 500000,
                        BestAlt = 150000,
                        RequireLight = false,
                    },
                    new AgencyScannerSensorRecord
                    {
                        SensorType = 8, // SCANtype.AnomalyDetail
                        Fov = 1.0f,
                        MinAlt = 0,
                        MaxAlt = 10000,
                        BestAlt = 5000,
                        RequireLight = true,
                    },
                },
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.Scanners.TryGetValue(vesselId, out var entry));
            Assert.AreEqual("Scout-1", entry.VesselName);
            Assert.AreEqual(2, entry.Sensors.Count);
            // Order isn't load-bearing for SCANsat; sort by type for stable comparison.
            entry.Sensors.Sort((a, b) => a.SensorType.CompareTo(b.SensorType));
            Assert.AreEqual(8, entry.Sensors[0].SensorType);
            Assert.IsTrue(entry.Sensors[0].RequireLight);
            Assert.AreEqual(16, entry.Sensors[1].SensorType);
            Assert.AreEqual(3.5f, entry.Sensors[1].Fov, 1e-6f);
            Assert.AreEqual(500000.0, entry.Sensors[1].MaxAlt, 1e-3);
        }

        [TestMethod]
        public void Scanners_ZeroSensors_RoundTrip()
        {
            // Empty Sensors list is a valid SCANsat state (vessel registered
            // but no active sensors yet). Round-trip preserves the empty list.
            var agency = BuildAgency();
            var vesselId = Guid.NewGuid();
            agency.Scanners[vesselId] = new AgencyScannerEntry
            {
                VesselId = vesselId,
                VesselName = "Empty",
                Sensors = new List<AgencyScannerSensorRecord>(),
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.Scanners.TryGetValue(vesselId, out var entry));
            Assert.AreEqual(0, entry.Sensors.Count);
        }

        // -------------------------------------------------------------------
        // Per-entry isolation
        // -------------------------------------------------------------------

        [TestMethod]
        public void Parse_MalformedSensor_KeepsParentVesselAndSiblingSensors()
        {
            // Per-Sensor isolation under Decision §9 nesting: a malformed Sensor
            // child does NOT abort the parent Vessel entry. The router-level
            // try/catch ensures other sensors on the same vessel survive.
            // Synthesize the state via hand-crafted serialized text rather than
            // round-tripping (Serialize always emits valid values; we need a
            // bad value).
            var agency = BuildAgency();
            // Bypass Serialize and craft the input directly:
            var goodVessel = Guid.NewGuid();
            var raw = string.Format(CultureInfo.InvariantCulture,
                "AgencyId = {0}\nOwningPlayerName = {1}\nDisplayName = {2}\nFunds = 0\nScience = 0\nReputation = 0\n" +
                "SCAN_SCANNERS\n{{\n" +
                "\tVESSEL\n\t{{\n" +
                "\t\tVesselId = {3}\n" +
                "\t\tVesselName = MultiSensor\n" +
                "\t\tSENSOR\n\t\t{{\n" +
                "\t\t\tSensorType = 4\n\t\t\tFov = 5\n\t\t\tMinAlt = 100\n\t\t\tMaxAlt = 1000\n\t\t\tBestAlt = 500\n\t\t\tRequireLight = False\n" +
                "\t\t}}\n" +
                "\t\tSENSOR\n\t\t{{\n" +
                "\t\t\tSensorType = NOT_A_NUMBER\n\t\t\tFov = 5\n\t\t\tMinAlt = 100\n\t\t\tMaxAlt = 1000\n\t\t\tBestAlt = 500\n\t\t\tRequireLight = False\n" +
                "\t\t}}\n" +
                "\t}}\n" +
                "}}\n",
                agency.AgencyId.ToString("N", CultureInfo.InvariantCulture),
                agency.OwningPlayerName,
                agency.DisplayName,
                goodVessel.ToString("N", CultureInfo.InvariantCulture));

            var parsed = AgencyState.Parse(raw);

            Assert.IsTrue(parsed.Scanners.TryGetValue(goodVessel, out var entry),
                "Parent VESSEL entry must survive a malformed SENSOR child (per-Sensor isolation Decision §9)");
            Assert.AreEqual(1, entry.Sensors.Count,
                "The well-formed SENSOR child must survive; the malformed sibling is dropped");
            Assert.AreEqual(4, entry.Sensors[0].SensorType);
        }

        [TestMethod]
        public void Parse_MalformedVesselId_SkipsThatVesselKeepsOtherEntries()
        {
            // Per-Vessel isolation: unparseable VesselId skips THAT vessel with
            // a Warning; sibling vessels in the same SCAN_SCANNERS block survive
            // (mirrors KOLONY parse per-entry isolation Invariant 4).
            var agency = BuildAgency();
            var goodVessel = Guid.NewGuid();
            var raw = string.Format(CultureInfo.InvariantCulture,
                "AgencyId = {0}\nOwningPlayerName = {1}\nDisplayName = {2}\nFunds = 0\nScience = 0\nReputation = 0\n" +
                "SCAN_SCANNERS\n{{\n" +
                "\tVESSEL\n\t{{\n\t\tVesselId = NOT-A-GUID\n\t\tVesselName = bad\n\t}}\n" +
                "\tVESSEL\n\t{{\n\t\tVesselId = {3}\n\t\tVesselName = good\n\t}}\n" +
                "}}\n",
                agency.AgencyId.ToString("N", CultureInfo.InvariantCulture),
                agency.OwningPlayerName,
                agency.DisplayName,
                goodVessel.ToString("N", CultureInfo.InvariantCulture));

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(1, parsed.Scanners.Count,
                "Bad vessel skipped; good vessel survives");
            Assert.IsTrue(parsed.Scanners.ContainsKey(goodVessel));
        }

        [TestMethod]
        public void Parse_PreS2File_NoCoverageOrScannersNode_ProducesEmptyDicts()
        {
            // Forward-compat: an agency file written by a pre-S2 server has no
            // SCAN_COVERAGE / SCAN_SCANNERS nodes. Parse yields empty Coverage
            // + Scanners dicts without warning or exception.
            var raw = "AgencyId = " + Guid.NewGuid().ToString("N") + "\n" +
                      "OwningPlayerName = Pre-S2 Player\n" +
                      "DisplayName = Pre-S2 Agency\n" +
                      "Funds = 0\nScience = 0\nReputation = 0\n";

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(0, parsed.Coverage.Count);
            Assert.AreEqual(0, parsed.Scanners.Count);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyState BuildAgency()
        {
            return new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "ScanAlice",
                DisplayName = "Scan Alice Co",
            };
        }
    }
}
