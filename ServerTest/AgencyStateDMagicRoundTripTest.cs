using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;
using System.Globalization;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// [Mod-compat S4 — DMagic Orbital Science] Round-trip pinning for
    /// <see cref="AgencyState.DMagicAsteroidScience"/> +
    /// <see cref="AgencyState.DMagicAnomalies"/> persistence. Mirrors
    /// <see cref="AgencyStateSCANsatRoundTripTest"/> — every float / double
    /// round-trips under a non-en thread culture without corruption
    /// (Invariant 9 / BUG-013 precedent).
    /// </summary>
    [TestClass]
    public class AgencyStateDMagicRoundTripTest
    {
        private CultureInfo _originalCulture;

        [TestInitialize]
        public void Setup()
        {
            // Comma-decimal culture to expose any forgotten InvariantCulture call.
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        }

        [TestCleanup]
        public void Teardown()
        {
            Thread.CurrentThread.CurrentCulture = _originalCulture;
        }

        [TestMethod]
        public void Asteroid_FullFields_RoundTrip()
        {
            var agency = BuildAgency();
            agency.DMagicAsteroidScience["AsteroidEve"] = new AgencyDMagicAsteroidEntry
            {
                Title = "AsteroidEve",
                BaseValue = 1.5f,
                SciVal = 0.123456f,
                Science = 42.987654f,
                Cap = 100f,
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.DMagicAsteroidScience.TryGetValue("AsteroidEve", out var entry));
            Assert.AreEqual("AsteroidEve", entry.Title);
            Assert.AreEqual(1.5f, entry.BaseValue, 1e-6f);
            Assert.AreEqual(0.123456f, entry.SciVal, 1e-6f);
            Assert.AreEqual(42.987654f, entry.Science, 1e-4f);
            Assert.AreEqual(100f, entry.Cap, 1e-6f);
        }

        [TestMethod]
        public void Anomaly_FullFields_RoundTrip()
        {
            var agency = BuildAgency();
            agency.DMagicAnomalies["5|Monolith"] = new AgencyDMagicAnomalyEntry
            {
                BodyIndex = 5,
                Name = "Monolith",
                Latitude = 12.3456789,
                Longitude = -45.67890,
                Altitude = 9876.54321,
            };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.IsTrue(parsed.DMagicAnomalies.TryGetValue("5|Monolith", out var entry));
            Assert.AreEqual(5, entry.BodyIndex);
            Assert.AreEqual("Monolith", entry.Name);
            Assert.AreEqual(12.3456789, entry.Latitude, 1e-9);
            Assert.AreEqual(-45.67890, entry.Longitude, 1e-9);
            Assert.AreEqual(9876.54321, entry.Altitude, 1e-6);
        }

        [TestMethod]
        public void Anomaly_MultipleBodies_AllRoundTripWithCompositeKeys()
        {
            var agency = BuildAgency();
            agency.DMagicAnomalies["5|Mono1"] = new AgencyDMagicAnomalyEntry { BodyIndex = 5, Name = "Mono1", Latitude = 1, Longitude = 2, Altitude = 3 };
            agency.DMagicAnomalies["5|Mono2"] = new AgencyDMagicAnomalyEntry { BodyIndex = 5, Name = "Mono2", Latitude = 4, Longitude = 5, Altitude = 6 };
            agency.DMagicAnomalies["6|Face"]  = new AgencyDMagicAnomalyEntry { BodyIndex = 6, Name = "Face",  Latitude = 7, Longitude = 8, Altitude = 9 };

            var parsed = AgencyState.Parse(agency.Serialize());

            Assert.AreEqual(3, parsed.DMagicAnomalies.Count);
            Assert.IsTrue(parsed.DMagicAnomalies.ContainsKey("5|Mono1"));
            Assert.IsTrue(parsed.DMagicAnomalies.ContainsKey("5|Mono2"));
            Assert.IsTrue(parsed.DMagicAnomalies.ContainsKey("6|Face"));
            Assert.AreEqual(8.0, parsed.DMagicAnomalies["6|Face"].Longitude, 1e-9);
        }

        [TestMethod]
        public void Parse_MalformedAnomalyBodyIndex_SkipsButKeepsSiblings()
        {
            // Per-entry isolation: an ANOMALY entry with an unparseable
            // BodyIndex skips with a Warning; sibling entries survive.
            var agency = BuildAgency();
            var goodKey = "5|Survives";
            var raw = string.Format(CultureInfo.InvariantCulture,
                "AgencyId = {0}\nOwningPlayerName = {1}\nDisplayName = {2}\nFunds = 0\nScience = 0\nReputation = 0\n" +
                "DMAGIC_ANOMALIES\n{{\n" +
                "\tANOMALY\n\t{{\n\t\tBodyIndex = NOT_AN_INT\n\t\tName = Bad\n\t}}\n" +
                "\tANOMALY\n\t{{\n\t\tBodyIndex = 5\n\t\tName = Survives\n\t\tLatitude = 1\n\t\tLongitude = 2\n\t\tAltitude = 3\n\t}}\n" +
                "}}\n",
                agency.AgencyId.ToString("N", CultureInfo.InvariantCulture),
                agency.OwningPlayerName,
                agency.DisplayName);

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(1, parsed.DMagicAnomalies.Count, "Bad anomaly skipped; sibling survives");
            Assert.IsTrue(parsed.DMagicAnomalies.ContainsKey(goodKey));
        }

        [TestMethod]
        public void Parse_MalformedAsteroidTitleMissing_SkipsButKeepsSiblings()
        {
            var agency = BuildAgency();
            var raw = string.Format(CultureInfo.InvariantCulture,
                "AgencyId = {0}\nOwningPlayerName = {1}\nDisplayName = {2}\nFunds = 0\nScience = 0\nReputation = 0\n" +
                "DMAGIC_ASTEROID_SCIENCE\n{{\n" +
                "\tASTEROID\n\t{{\n\t\tBaseValue = 1\n\t}}\n" +  // missing Title
                "\tASTEROID\n\t{{\n\t\tTitle = Good\n\t\tBaseValue = 2\n\t\tSciVal = 0.5\n\t\tScience = 10\n\t\tCap = 50\n\t}}\n" +
                "}}\n",
                agency.AgencyId.ToString("N", CultureInfo.InvariantCulture),
                agency.OwningPlayerName,
                agency.DisplayName);

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(1, parsed.DMagicAsteroidScience.Count, "Bad asteroid skipped; sibling survives");
            Assert.IsTrue(parsed.DMagicAsteroidScience.ContainsKey("Good"));
        }

        [TestMethod]
        public void Parse_PreS4File_NoDMagicNodes_ProducesEmptyDicts()
        {
            // Forward-compat: pre-S4 agency files have no DMAGIC_ASTEROID_SCIENCE
            // or DMAGIC_ANOMALIES nodes — Parse yields empty dicts without
            // warning or exception.
            var raw = "AgencyId = " + Guid.NewGuid().ToString("N") + "\n" +
                      "OwningPlayerName = Pre-S4 Player\n" +
                      "DisplayName = Pre-S4 Agency\n" +
                      "Funds = 0\nScience = 0\nReputation = 0\n";

            var parsed = AgencyState.Parse(raw);

            Assert.AreEqual(0, parsed.DMagicAsteroidScience.Count);
            Assert.AreEqual(0, parsed.DMagicAnomalies.Count);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyState BuildAgency()
        {
            return new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "DMagicAlice",
                DisplayName = "DMagic Alice Co",
            };
        }
    }
}
