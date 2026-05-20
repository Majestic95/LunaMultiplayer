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
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false; // [Phase 6.7] tests set this true; reset for sibling-class isolation
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
            return ReadKerbalFieldAtPath(path, field);
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — gate=on routing helpers
        // -------------------------------------------------------------------

        private static string ReadKerbalFieldFromAgencySubdir(Guid agencyId, string kerbalName, string field)
        {
            var path = Path.Combine(AgencySystem.GetKerbalsPathForAgency(agencyId), kerbalName + ".txt");
            return ReadKerbalFieldAtPath(path, field);
        }

        private static string ReadKerbalFieldAtPath(string path, string field)
        {
            if (!File.Exists(path)) return null;
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

        private static void WriteKerbalFileToAgencySubdir(Guid agencyId, string kerbalName, string state, string tod)
        {
            var subdir = AgencySystem.GetKerbalsPathForAgency(agencyId);
            Directory.CreateDirectory(subdir);
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
            File.WriteAllText(Path.Combine(subdir, kerbalName + ".txt"), text);
        }

        private static AgencyState NewAgencyInRegistry(string ownerName)
        {
            var agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = ownerName,
                DisplayName = ownerName + " Co",
            };
            AgencySystem.Agencies[agency.AgencyId] = agency;
            AgencySystem.AgencyByPlayerName[agency.OwningPlayerName] = agency.AgencyId;
            return agency;
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — CountInFlightPassengersForRefusalCheck
        // -------------------------------------------------------------------

        [TestMethod]
        public void CountInFlight_NoRoutes_Zero()
        {
            // Empty WolfCrewRoutes — pre-cascade refusal-check should NOT
            // require the operator to specify --restore-to / --restore-to-none.
            Assert.AreEqual(0, AgencyWolfMigration.CountInFlightPassengersForRefusalCheck(_agency));
        }

        [TestMethod]
        public void CountInFlight_BoardingOnly_Zero()
        {
            // Boarding-status routes are out of restoration scope per the
            // {Enroute, Arrived}-only contract. Operator shouldn't be forced
            // to pick a destination for a route whose passengers don't need
            // rescue.
            SeedCrewRoute(_agency, "boarding-only", "Boarding", new[] { "Some Kerman" });
            Assert.AreEqual(0, AgencyWolfMigration.CountInFlightPassengersForRefusalCheck(_agency));
        }

        [TestMethod]
        public void CountInFlight_MixedStates_CountsOnlyEnrouteAndArrived()
        {
            SeedCrewRoute(_agency, "boarding", "Boarding", new[] { "Skip-Me Kerman" });
            SeedCrewRoute(_agency, "enroute", "Enroute", new[] { "Count-Me-1 Kerman", "Count-Me-2 Kerman" });
            SeedCrewRoute(_agency, "arrived", "Arrived", new[] { "Count-Me-3 Kerman" });
            Assert.AreEqual(3, AgencyWolfMigration.CountInFlightPassengersForRefusalCheck(_agency));
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — gate=on + --restore-to <dest> (happy path)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_GateOn_WithDestination_WritesToDestinationSubdir()
        {
            // Two agencies; Alice is being deleted, Bob receives the rescued
            // kerbals. Alice's per-agency subdir has the in-flight passenger's
            // Missing/MaxValue file. Bob's subdir is empty before the cascade.
            // After cascade: Bob's subdir has the restored Available/ToD=0
            // file; Alice's subdir is left intact (TryDeleteAgency's recursive
            // delete handles source-side cleanup).
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency; // re-use the Setup-minted agency as source
            var bob = NewAgencyInRegistry("Bob");

            SeedCrewRoute(alice, "alice-route", "Enroute", new[] { "Pebbles Kerman" });
            WriteKerbalFileToAgencySubdir(alice.AgencyId, "Pebbles Kerman", state: "Missing", tod: "1.79769313486232E+308");

            var result = AgencyWolfMigration.CascadeOnDelete(alice, bob);

            Assert.AreEqual(1, result.InFlightRoutesScanned);
            Assert.AreEqual(1, result.RestoredKerbalCount);
            Assert.AreEqual(0, result.FailedKerbalNames.Count);
            Assert.AreEqual(0, result.CollidedKerbalNames.Count);
            Assert.AreEqual(bob.AgencyId, result.DestinationAgencyId,
                "Destination agency id must be reflected in the audit summary for the operator log.");
            Assert.IsTrue(result.WroteToDestinationSubdir,
                "Happy-path restoration must mark WroteToDestinationSubdir so the command summary reads correctly.");

            Assert.AreEqual("Available", ReadKerbalFieldFromAgencySubdir(bob.AgencyId, "Pebbles Kerman", "state"));
            Assert.AreEqual("0", ReadKerbalFieldFromAgencySubdir(bob.AgencyId, "Pebbles Kerman", "ToD"));
            Assert.AreEqual("Missing", ReadKerbalFieldFromAgencySubdir(alice.AgencyId, "Pebbles Kerman", "state"),
                "Source-side file must NOT be mutated by the cascade — TryDeleteAgency's recursive subdir delete " +
                "is the source-side cleanup. Mutating in-place would race against operator hand-recovery workflows.");
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — gate=on + --restore-to-none (operator-accepted loss)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_GateOn_NoDestination_AuditWithoutDiskWrite()
        {
            // Operator explicitly chose --restore-to-none. Cascade still walks
            // the routes (so the summary audit is accurate) but writes nothing
            // anywhere. Bob's subdir stays empty.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency;
            var bob = NewAgencyInRegistry("Bob");
            SeedCrewRoute(alice, "doomed-route", "Enroute", new[] { "Lost Kerman" });
            WriteKerbalFileToAgencySubdir(alice.AgencyId, "Lost Kerman", state: "Missing", tod: "1.79769313486232E+308");

            var result = AgencyWolfMigration.CascadeOnDelete(alice, destination: null);

            Assert.AreEqual(1, result.InFlightRoutesScanned, "Routes still counted even when no restoration writes.");
            Assert.AreEqual(0, result.RestoredKerbalCount,
                "--restore-to-none means no successful restorations.");
            Assert.AreEqual(0, result.FailedKerbalNames.Count);
            Assert.AreEqual(0, result.CollidedKerbalNames.Count);
            Assert.AreEqual(Guid.Empty, result.DestinationAgencyId,
                "DestinationAgencyId stays Empty when operator chose --restore-to-none.");
            Assert.IsFalse(result.WroteToDestinationSubdir);

            // Source file unchanged (cascade only read it). No file in Bob's subdir.
            Assert.AreEqual("Missing", ReadKerbalFieldFromAgencySubdir(alice.AgencyId, "Lost Kerman", "state"));
            var bobPath = Path.Combine(AgencySystem.GetKerbalsPathForAgency(bob.AgencyId), "Lost Kerman.txt");
            Assert.IsFalse(File.Exists(bobPath),
                "No file should land in Bob's subdir under --restore-to-none.");
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — gate=on + destination collision (skip with Warning)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_GateOn_DestinationHasSameNamedKerbal_CollidesAndPreserves()
        {
            // Alice's "Jebediah Kerman" is in-flight. Bob already has his own
            // Jebediah Kerman.txt (per Q-Seed, each agency has its own stock 4
            // with the same name). The cascade must NOT overwrite Bob's
            // existing file — that would silently destroy his agency's Jeb.
            // Collision counts toward CollidedKerbalNames (separate from
            // FailedKerbalNames so the operator can distinguish "I picked
            // the wrong destination" from "file was malformed").
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency;
            var bob = NewAgencyInRegistry("Bob");
            SeedCrewRoute(alice, "collision-route", "Enroute", new[] { "Jebediah Kerman" });
            WriteKerbalFileToAgencySubdir(alice.AgencyId, "Jebediah Kerman", state: "Missing", tod: "1.79769313486232E+308");
            // Bob already has HIS OWN Jeb (different career — XP=2, Available).
            WriteKerbalFileToAgencySubdir(bob.AgencyId, "Jebediah Kerman", state: "Available", tod: "0");
            var bobPath = Path.Combine(AgencySystem.GetKerbalsPathForAgency(bob.AgencyId), "Jebediah Kerman.txt");
            var bobOriginalText = File.ReadAllText(bobPath);

            var result = AgencyWolfMigration.CascadeOnDelete(alice, bob);

            Assert.AreEqual(1, result.InFlightRoutesScanned);
            Assert.AreEqual(0, result.RestoredKerbalCount);
            Assert.AreEqual(0, result.FailedKerbalNames.Count,
                "Collision is a separate category from malformed/missing failure.");
            Assert.AreEqual(1, result.CollidedKerbalNames.Count);
            Assert.AreEqual("Jebediah Kerman", result.CollidedKerbalNames[0]);
            Assert.IsFalse(result.WroteToDestinationSubdir,
                "Collision skips the write; no successful destination write occurred.");

            // Bob's file is byte-identical to the seed — no partial overwrite.
            var bobAfter = File.ReadAllText(bobPath);
            Assert.AreEqual(bobOriginalText, bobAfter,
                "Destination's existing kerbal file must be PRESERVED byte-for-byte under collision.");
        }

        [TestMethod]
        public void Cascade_GateOn_PartialCollision_SiblingsStillRestored()
        {
            // Multi-passenger CrewRoute. One passenger collides with destination;
            // the other is restored normally. Per-kerbal isolation contract.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency;
            var bob = NewAgencyInRegistry("Bob");
            SeedCrewRoute(alice, "mixed-route", "Enroute", new[] { "Jebediah Kerman", "Pebbles Kerman" });
            WriteKerbalFileToAgencySubdir(alice.AgencyId, "Jebediah Kerman", state: "Missing", tod: "1.79769313486232E+308");
            WriteKerbalFileToAgencySubdir(alice.AgencyId, "Pebbles Kerman", state: "Missing", tod: "1.79769313486232E+308");
            // Bob has only Jeb pre-existing.
            WriteKerbalFileToAgencySubdir(bob.AgencyId, "Jebediah Kerman", state: "Available", tod: "0");

            var result = AgencyWolfMigration.CascadeOnDelete(alice, bob);

            Assert.AreEqual(1, result.RestoredKerbalCount);
            Assert.AreEqual(1, result.CollidedKerbalNames.Count);
            CollectionAssert.Contains(result.RestoredKerbalNames, "Pebbles Kerman");
            CollectionAssert.Contains(result.CollidedKerbalNames, "Jebediah Kerman");

            // Sibling restored to Bob's subdir.
            Assert.AreEqual("Available", ReadKerbalFieldFromAgencySubdir(bob.AgencyId, "Pebbles Kerman", "state"));
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — destination cascade-race guard
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_GateOn_DestinationRemovedFromRegistry_DropsWriteWithoutThrow()
        {
            // Race window: a concurrent /deleteagency on the destination removed
            // it from Agencies between the command's TryResolveAgencyToken and
            // the cascade's per-kerbal write step. The cascade must NOT throw
            // (would orphan the source agency); it must DROP the write with
            // Warning + mark the kerbal as Failed. Same posture as Phase 6.5
            // TryWriteKerbalProtoPerAgency.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency;
            var bob = NewAgencyInRegistry("Bob");
            SeedCrewRoute(alice, "raced-route", "Enroute", new[] { "Doomed Kerman" });
            WriteKerbalFileToAgencySubdir(alice.AgencyId, "Doomed Kerman", state: "Missing", tod: "1.79769313486232E+308");

            // Simulate destination removed mid-cascade by ripping Bob out of
            // Agencies BEFORE invoking the cascade — the per-kerbal write
            // step's ContainsKey re-check should catch it.
            AgencySystem.Agencies.TryRemove(bob.AgencyId, out _);

            var result = AgencyWolfMigration.CascadeOnDelete(alice, bob);

            Assert.AreEqual(0, result.RestoredKerbalCount);
            Assert.AreEqual(1, result.FailedKerbalNames.Count,
                "Cascade-race against the destination registers as Failed (the kerbal couldn't be saved anywhere).");
            CollectionAssert.Contains(result.FailedKerbalNames, "Doomed Kerman");
            Assert.IsFalse(result.WroteToDestinationSubdir);
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — same-agency destination (defensive)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_GateOn_DestinationIsSourceAgency_TreatedAsNoDestination()
        {
            // Caller is supposed to refuse same-source-and-destination upstream,
            // but cascade is defensive: if destination.AgencyId == source.AgencyId
            // we fall back to the --restore-to-none disposition rather than write
            // into the cascade-doomed source subdir.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency;
            SeedCrewRoute(alice, "self-route", "Enroute", new[] { "Self Kerman" });
            WriteKerbalFileToAgencySubdir(alice.AgencyId, "Self Kerman", state: "Missing", tod: "1.79769313486232E+308");

            var result = AgencyWolfMigration.CascadeOnDelete(alice, destination: alice);

            Assert.AreEqual(0, result.RestoredKerbalCount,
                "Self-destination must not be written to (subdir is cascade-doomed).");
            Assert.AreEqual(Guid.Empty, result.DestinationAgencyId);
            // The Self Kerman file in alice's subdir remains Missing — TryDeleteAgency's
            // recursive delete will remove it. Operator-equivalent of --restore-to-none.
            Assert.AreEqual("Missing", ReadKerbalFieldFromAgencySubdir(alice.AgencyId, "Self Kerman", "state"));
        }

        // -------------------------------------------------------------------
        // Phase 6.7 — gate=off path is unchanged (Phase 6.5 regression guard)
        // -------------------------------------------------------------------

        // -------------------------------------------------------------------
        // Phase 6.7 — pre-Phase-6.5 / AllowEnable... upgrade fallback
        // -------------------------------------------------------------------

        [TestMethod]
        public void Cascade_GateOn_LegacyStrandedSourceFile_FallsBackAndRestoresToDestination()
        {
            // Upgrade hazard: an operator who ran a pre-Phase-6.5 binary with
            // PerAgencyKerbalRoster=true, OR who used the
            // AllowEnablePerAgencyKerbalsOnExistingUniverse=true override on a
            // populated v0-v7 universe. The deleted agency's kerbal file
            // exists at LEGACY Universe/Kerbals/{name}.txt rather than at
            // Universe/Agencies/{aliceGuid:N}/Kerbals/{name}.txt. The cascade
            // must (a) detect the legacy-stranded file, (b) read from it, (c)
            // write to the destination subdir, (d) emit a Warning describing
            // the upgrade hazard so operators see what happened. Without the
            // fallback the cascade would mis-diagnose as "newly-recruited
            // kerbal — file never made it to disk" which is wrong.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency;
            var bob = NewAgencyInRegistry("Bob");
            SeedCrewRoute(alice, "legacy-stranded-route", "Enroute", new[] { "Legacy Stuart Kerman" });
            // File exists at LEGACY path, not at alice's per-agency subdir.
            WriteKerbalFile("Legacy Stuart Kerman", state: "Missing", tod: "1.79769313486232E+308");

            var result = AgencyWolfMigration.CascadeOnDelete(alice, bob);

            Assert.AreEqual(1, result.RestoredKerbalCount,
                "Cascade must rescue the legacy-stranded file via the Phase 6.7 upgrade-fallback probe.");
            Assert.AreEqual(0, result.FailedKerbalNames.Count);
            Assert.IsTrue(result.WroteToDestinationSubdir);

            // Destination subdir receives the restored file with Available + ToD=0.
            Assert.AreEqual("Available", ReadKerbalFieldFromAgencySubdir(bob.AgencyId, "Legacy Stuart Kerman", "state"));
            Assert.AreEqual("0", ReadKerbalFieldFromAgencySubdir(bob.AgencyId, "Legacy Stuart Kerman", "ToD"));
            // Legacy file at Universe/Kerbals/ is NOT removed by the cascade —
            // operator hand-cleanup territory (documented in Warning).
            Assert.AreEqual("Missing", ReadKerbalField("Legacy Stuart Kerman", "state"),
                "Cascade does NOT delete the legacy-stranded source; operator decides whether to hand-remove.");
        }

        [TestMethod]
        public void Cascade_GateOn_NoLegacyNoPerAgencyFile_FailsWithEnhancedReason()
        {
            // Under gate=on, when neither the per-agency path NOR the legacy
            // path has the kerbal file, the failure Warning must mention BOTH
            // paths were checked. Without this an operator on an upgrade
            // universe would mis-attribute the failure to a per-agency lookup
            // bug.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;

            var alice = _agency;
            var bob = NewAgencyInRegistry("Bob");
            SeedCrewRoute(alice, "no-file-route", "Enroute", new[] { "Vanished Kerman" });
            // No file written anywhere.

            var result = AgencyWolfMigration.CascadeOnDelete(alice, bob);

            Assert.AreEqual(0, result.RestoredKerbalCount);
            Assert.AreEqual(1, result.FailedKerbalNames.Count);
            CollectionAssert.Contains(result.FailedKerbalNames, "Vanished Kerman");
            // The Warning content (legacy probe acknowledged) is operator-
            // observable but we don't pin the exact text — pinning at this
            // level locks the format and future-tweaks-of-language-for-
            // clarity would break the test for no semantic reason.
        }

        [TestMethod]
        public void Cascade_GateOff_LegacyPathUnchanged_IgnoresDestination()
        {
            // Under PerAgencyKerbalRoster=false the cascade writes to legacy
            // Universe/Kerbals/ regardless of whether a destination was passed
            // (the parser rejects --restore-to under gate=off, but the cascade
            // is defensive against a misuse — destination arg is silently
            // ignored, behaviour matches Phase 6.5 baseline).
            //
            // PerAgencyKerbalRoster stays false (Setup doesn't set it true).
            var alice = _agency;
            var bob = NewAgencyInRegistry("Bob");
            SeedCrewRoute(alice, "legacy-route", "Enroute", new[] { "Legacy Kerman" });
            WriteKerbalFile("Legacy Kerman", state: "Missing", tod: "1.79769313486232E+308");

            // Pass destination=bob — should be silently ignored under gate=off.
            var result = AgencyWolfMigration.CascadeOnDelete(alice, bob);

            Assert.AreEqual(1, result.RestoredKerbalCount);
            Assert.AreEqual(Guid.Empty, result.DestinationAgencyId,
                "Gate=off ignores the destination argument; DestinationAgencyId stays Empty.");
            Assert.IsFalse(result.WroteToDestinationSubdir);
            Assert.AreEqual("Available", ReadKerbalField("Legacy Kerman", "state"),
                "Legacy in-place rewrite at Universe/Kerbals/{name}.txt under gate=off.");
        }
    }
}
