using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.IO;

namespace ServerTest
{
    /// <summary>
    /// Phase 4 Slice F — unit tests for the deterministic core of
    /// <see cref="AgencyWolfMigration.CascadeOnDelete"/>: the per-FlightStatus
    /// restoration-scope decision + the on-disk kerbal-file rewrite +
    /// per-kerbal failure isolation. The full <c>/deleteagency</c> command
    /// happy-path (parser + gate + TryDeleteAgency + visibility broadcast +
    /// lock release + cascade composition) gets coverage via the existing
    /// <c>DeleteAgencyCommand</c> + <c>DeleteAgencyCommandParserTest</c>
    /// surface; this file pins the new Slice F helper directly so the
    /// kerbal-restoration logic + the FlightStatus filter + the depth-aware
    /// line rewrite can regress independently.
    ///
    /// <para>Test pattern mirrors <see cref="AgencySystemTest"/> — fresh
    /// per-test temp <see cref="ServerContext.UniverseDirectory"/> +
    /// <see cref="KerbalSystem.KerbalsPath"/> subdirectory; gate flipped on;
    /// AgencySystem reset between tests; cleanup deletes the temp dir.</para>
    /// </summary>
    [TestClass]
    public class DeleteAgencyCommandWolfCascadeTest
    {
        private AgencyState _agency;

        [TestInitialize]
        public void Setup()
        {
            // Per-test temp universe — mirrors AgencySystemTest.Setup. KerbalsPath
            // resolves lazily from UniverseDirectory so we must set the directory
            // BEFORE any helper that consults KerbalSystem.KerbalsPath.
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(),
                "lmp-wolfcascade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(KerbalSystem.KerbalsPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();

            _agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "WolfCascadeOwner",
                DisplayName = "Wolf Cascade Co",
            };
            AgencySystem.Agencies[_agency.AgencyId] = _agency;
            AgencySystem.AgencyByPlayerName[_agency.OwningPlayerName] = _agency.AgencyId;
        }

        [TestCleanup]
        public void Teardown()
        {
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, recursive: true);
        }

        // -------------------------------------------------------------------
        // No-work happy path — empty + Boarding-only routes restore nothing
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_NoCrewRoutes_RestoresZero()
        {
            // Empty WolfCrewRoutes — cascade sees no in-flight routes,
            // restores nothing. The summary line in DeleteAgencyCommand emits
            // in-flight-routes=0 restored-kerbals=0 deterministically so a
            // GUI launcher's "cascade ran?" check has a present signal.
            var result = AgencyWolfMigration.CascadeOnDelete(_agency);

            Assert.AreEqual(0, result.InFlightRoutesScanned);
            Assert.AreEqual(0, result.RestoredKerbalCount);
            Assert.AreEqual(0, result.RestoredKerbalNames.Count);
            Assert.AreEqual(0, result.FailedKerbalNames.Count);
        }

        [TestMethod]
        public void Cascade_BoardingOnly_RestoresZero()
        {
            // Boarding routes have passengers in the route's list but the
            // kerbal rosterStatus stays Assigned (WOLF's Embark mutates only
            // the passenger list, NOT rosterStatus — verified
            // CrewRoute.cs:141-184 / WOLF_CrewTransferScenario.cs:586-590
            // which shows the Missing mutation happens only in Launch).
            // Cascade must skip Boarding to avoid gratuitous stomping of
            // Assigned->Available on kerbals that don't need it.
            SeedCrewRoute(_agency, "boarding-route-1", "Boarding", new[] { "Bill Kerman" });
            WriteKerbalFile("Bill Kerman", state: "Assigned", tod: "0");

            var result = AgencyWolfMigration.CascadeOnDelete(_agency);

            Assert.AreEqual(0, result.InFlightRoutesScanned);
            Assert.AreEqual(0, result.RestoredKerbalCount);
            Assert.AreEqual("Assigned", ReadKerbalField("Bill Kerman", "state"),
                "Boarding passenger's kerbal file must be left untouched.");
        }

        // -------------------------------------------------------------------
        // Restoration scope — Enroute AND Arrived both restore (per WOLF's
        // CheckArrived contract that flips FlightStatus but not rosterStatus)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_EnrouteWithPassengers_RestoresAll()
        {
            SeedCrewRoute(_agency, "enroute-route-1", "Enroute", new[]
            {
                "Jebediah Kerman", "Valentina Kerman", "Bob Kerman",
            });
            WriteKerbalFile("Jebediah Kerman", state: "Missing", tod: "1.79769313486232E+308");
            WriteKerbalFile("Valentina Kerman", state: "Missing", tod: "1.79769313486232E+308");
            WriteKerbalFile("Bob Kerman", state: "Missing", tod: "1.79769313486232E+308");

            var result = AgencyWolfMigration.CascadeOnDelete(_agency);

            Assert.AreEqual(1, result.InFlightRoutesScanned);
            Assert.AreEqual(3, result.RestoredKerbalCount);
            Assert.AreEqual(0, result.FailedKerbalNames.Count);

            Assert.AreEqual("Available", ReadKerbalField("Jebediah Kerman", "state"));
            Assert.AreEqual("0", ReadKerbalField("Jebediah Kerman", "ToD"));
            Assert.AreEqual("Available", ReadKerbalField("Valentina Kerman", "state"));
            Assert.AreEqual("0", ReadKerbalField("Valentina Kerman", "ToD"));
            Assert.AreEqual("Available", ReadKerbalField("Bob Kerman", "state"));
            Assert.AreEqual("0", ReadKerbalField("Bob Kerman", "ToD"));
        }

        [TestMethod]
        public void Cascade_ArrivedWithPassengers_RestoresAll()
        {
            // CrewRoute.CheckArrived only mutates FlightStatus Enroute->Arrived;
            // it does NOT touch the kerbal rosterStatus. Disembark (which DOES
            // flip rosterStatus to Available) requires operator click. So an
            // Arrived route with passengers is one that arrived at destination
            // but the operator hasn't disembarked yet — passengers are STILL
            // in Missing on disk. Cascade must restore them.
            SeedCrewRoute(_agency, "arrived-route-1", "Arrived", new[] { "Stranded Stuart Kerman" });
            WriteKerbalFile("Stranded Stuart Kerman", state: "Missing", tod: "1.79769313486232E+308");

            var result = AgencyWolfMigration.CascadeOnDelete(_agency);

            Assert.AreEqual(1, result.InFlightRoutesScanned);
            Assert.AreEqual(1, result.RestoredKerbalCount);
            Assert.AreEqual("Available", ReadKerbalField("Stranded Stuart Kerman", "state"));
            Assert.AreEqual("0", ReadKerbalField("Stranded Stuart Kerman", "ToD"));
        }

        // -------------------------------------------------------------------
        // Multi-route + dedup — same kerbal name in two routes restores once
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_MultiplePassengersAcrossRoutes_DedupesByName()
        {
            // Defensive: WOLF's wire contract says a kerbal appears in at most
            // one in-flight CrewRoute, but a malformed wire upsert could put
            // the same name in two routes. The HashSet<string> dedup in the
            // snapshot phase ensures restoration runs once per distinct name —
            // FileHandler.WriteToFile's ContentChecker dedup would also no-op
            // the second write, but we don't want to rely on that for the
            // CascadeResult.RestoredKerbalCount audit accuracy.
            SeedCrewRoute(_agency, "duplicate-route-A", "Enroute", new[] { "Dup Kerman" });
            SeedCrewRoute(_agency, "duplicate-route-B", "Enroute", new[] { "Dup Kerman" });
            WriteKerbalFile("Dup Kerman", state: "Missing", tod: "1.79769313486232E+308");

            var result = AgencyWolfMigration.CascadeOnDelete(_agency);

            Assert.AreEqual(2, result.InFlightRoutesScanned,
                "Both Enroute routes count toward the in-flight scan.");
            Assert.AreEqual(1, result.RestoredKerbalCount,
                "Distinct kerbal restoration count is HashSet-deduped to 1.");
            Assert.AreEqual(1, result.RestoredKerbalNames.Count);
            Assert.AreEqual("Dup Kerman", result.RestoredKerbalNames[0]);
        }

        // -------------------------------------------------------------------
        // Per-kerbal isolation — failures don't abort siblings
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_MissingKerbalFile_IsolatedAsFailure()
        {
            // Two passengers; one's kerbal file is missing on disk (operator
            // hand-deleted, or kerbal proto never made it from client to
            // server before /deleteagency). Cascade marks the missing one as
            // Failed but successfully restores the sibling.
            SeedCrewRoute(_agency, "mixed-route", "Enroute", new[] { "Present Kerman", "Missing Kerman" });
            WriteKerbalFile("Present Kerman", state: "Missing", tod: "1.79769313486232E+308");
            // No file written for "Missing Kerman" — FileHandler.FileExists returns false.

            var result = AgencyWolfMigration.CascadeOnDelete(_agency);

            Assert.AreEqual(1, result.RestoredKerbalCount);
            Assert.AreEqual(1, result.FailedKerbalNames.Count);
            Assert.AreEqual("Missing Kerman", result.FailedKerbalNames[0]);
            Assert.AreEqual("Available", ReadKerbalField("Present Kerman", "state"),
                "Sibling restoration must NOT be aborted by the missing-file failure.");
        }

        [TestMethod]
        public void Cascade_MalformedKerbalFile_IsolatedAsFailure()
        {
            // Two passengers; one's kerbal file is missing the 'state =' line.
            // The rewriter detects this via the stateSeen/todSeen flags and
            // refuses to write back a partial restoration; cascade marks as
            // Failed and continues.
            SeedCrewRoute(_agency, "malformed-route", "Enroute", new[] { "Healthy Kerman", "Broken Kerman" });
            WriteKerbalFile("Healthy Kerman", state: "Missing", tod: "1.79769313486232E+308");
            // Broken Kerman: file exists but is missing top-level state/ToD lines.
            var brokenPath = Path.Combine(KerbalSystem.KerbalsPath, "Broken Kerman.txt");
            var brokenOriginal =
                "name = Broken Kerman\n" +
                "type = Crew\n" +
                "trait = Pilot\n" +
                "CAREER_LOG\n" +
                "{\n" +
                "\tflight = 0\n" +
                "}\n";
            File.WriteAllText(brokenPath, brokenOriginal);

            var result = AgencyWolfMigration.CascadeOnDelete(_agency);

            Assert.AreEqual(1, result.RestoredKerbalCount);
            Assert.AreEqual(1, result.FailedKerbalNames.Count);
            Assert.AreEqual("Broken Kerman", result.FailedKerbalNames[0]);
            Assert.AreEqual("Available", ReadKerbalField("Healthy Kerman", "state"));
            // Broken file must be byte-identical to the seed on partial-rewrite
            // refusal — a Contains-based negation would also pass if the
            // rewrite silently truncated the file or otherwise corrupted it.
            // (Slice F general-lens CONSIDER #4 — pin the actual "do not
            // corrupt" contract, not an artifact of it.)
            var brokenContents = File.ReadAllText(brokenPath);
            Assert.AreEqual(brokenOriginal, brokenContents,
                "Malformed file must be byte-identical to the seed (no partial rewrite, no truncation).");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static void SeedCrewRoute(AgencyState agency, string uniqueId, string flightStatus, IEnumerable<string> passengerNames)
        {
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = uniqueId,
                FlightStatus = flightStatus,
                OriginBody = "Kerbin",
                OriginBiome = "KSC",
                DestinationBody = "Mun",
                DestinationBiome = "Lowlands",
                ArrivalTime = 0,
                Duration = 1000,
                EconomyBerths = 4,
                LuxuryBerths = 0,
                FlightNumber = "TST-001",
                Passengers = new List<AgencyWolfPassengerEntry>(),
            };
            foreach (var name in passengerNames)
            {
                entry.Passengers.Add(new AgencyWolfPassengerEntry
                {
                    Name = name,
                    DisplayName = name,
                    IsTourist = false,
                    Occupation = "Pilot",
                    Stars = 1,
                });
            }
            agency.WolfCrewRoutes[uniqueId] = entry;
        }

        private static void WriteKerbalFile(string kerbalName, string state, string tod)
        {
            // Minimal but realistic kerbal file shape — fields ordered to match
            // the canonical resource template at Server/Resources/Kerbals/.
            // CAREER_LOG / FLIGHT_LOG blocks present so the depth-aware
            // rewriter must walk them correctly without trying to rewrite
            // their nested 'flight =' lines.
            var text = "name = " + kerbalName + "\n" +
                       "gender = Male\n" +
                       "type = Crew\n" +
                       "trait = Pilot\n" +
                       "brave = 0.5\n" +
                       "dumb = 0.5\n" +
                       "badS = False\n" +
                       "veteran = False\n" +
                       "tour = False\n" +
                       "state = " + state + "\n" +
                       "inactive = False\n" +
                       "inactiveTimeEnd = 0\n" +
                       "gExperienced = 0\n" +
                       "outDueToG = False\n" +
                       "ToD = " + tod + "\n" +
                       "idx = -1\n" +
                       "extraXP = 0\n" +
                       "CAREER_LOG\n" +
                       "{\n" +
                       "\tflight = 0\n" +
                       "}\n" +
                       "FLIGHT_LOG\n" +
                       "{\n" +
                       "\tflight = 0\n" +
                       "}\n";
            File.WriteAllText(Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt"), text);
        }

        private static string ReadKerbalField(string kerbalName, string field)
        {
            var path = Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt");
            var text = File.ReadAllText(path);
            var lines = text.Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (!line.StartsWith(field, StringComparison.Ordinal))
                    continue;
                var rest = line.Substring(field.Length).TrimStart();
                if (rest.Length == 0 || rest[0] != '=')
                    continue;
                return rest.Substring(1).Trim();
            }
            return null;
        }
    }
}
