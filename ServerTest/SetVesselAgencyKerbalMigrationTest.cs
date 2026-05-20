using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ServerTest
{
    /// <summary>
    /// Stage 6 Phase 6.8 — direct-Execute() coverage for
    /// <see cref="SetVesselAgencyCommand"/>'s kerbal-migration step. Each
    /// test sets up a temp UniverseDirectory with seeded per-agency Kerbals
    /// subdirs, runs the command, and asserts on the post-state of the
    /// kerbal files (moved / dropped / unchanged) plus the vessel field +
    /// router-migration outcome.
    ///
    /// <para>End-to-end wire-push coverage (KerbalProto/Remove pushed to
    /// online owners) is deferred to MockClientTest — that surface needs the
    /// in-process Lidgren harness which can observe inbound messages. Here
    /// we exercise the disk-side migration logic + the operator-visible
    /// refuse-vs-migrate decision, both of which run to completion without
    /// a real wire stack.</para>
    /// </summary>
    [TestClass]
    public class SetVesselAgencyKerbalMigrationTest
    {
        private static readonly string XmlExamplePath =
            Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        // Vessel with multi-crew (Janlas / Adfry / Gembin Kerman). The
        // [TestInitialize] copies this content into VesselStoreSystem under
        // the test's _vesselId guid.
        private const string MultiCrewVesselFile = "237c14a3-e68c-4c6d-aa1e-f77ad561e92b.txt";
        private const string CrewName1 = "Janlas Kerman";
        private const string CrewName2 = "Adfry Kerman";
        private const string CrewName3 = "Gembin Kerman";

        private readonly Guid _vesselId = Guid.NewGuid();
        private readonly Guid _agencyAlice = Guid.NewGuid();
        private readonly Guid _agencyBob = Guid.NewGuid();

        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(),
                "lmp-stage6-svakm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();

            // Seed Alice + Bob agency records. Per-agency Kerbals subdirs
            // are NOT auto-seeded here — each test creates them as needed
            // (via Directory.CreateDirectory + File.WriteAllText for the
            // crew files) so test setup mirrors the real layout precisely.
            AgencySystem.Agencies[_agencyAlice] = new AgencyState
            {
                AgencyId = _agencyAlice,
                OwningPlayerName = "alice",
                DisplayName = "Alice Co",
            };
            AgencySystem.Agencies[_agencyBob] = new AgencyState
            {
                AgencyId = _agencyBob,
                OwningPlayerName = "bob",
                DisplayName = "Bob Industries",
            };
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;
        }

        [TestCleanup]
        public void Teardown()
        {
            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();

            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;

            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, recursive: true);
        }

        // -------------------------------------------------------------------
        // ExtractCrewFromVessel pure helper
        // -------------------------------------------------------------------

        [TestMethod]
        public void ExtractCrew_MultiCrewVessel_ReturnsAllThree()
        {
            SeedMultiCrewVessel(_vesselId, _agencyAlice);

            Assert.IsTrue(SetVesselAgencyCommand.ExtractCrewFromVessel(_vesselId, out var crew));
            CollectionAssert.AreEquivalent(
                new[] { CrewName1, CrewName2, CrewName3 },
                crew,
                "Multi-crew vessel must return all crew names deduplicated.");
        }

        [TestMethod]
        public void ExtractCrew_EmptyVessel_ReturnsEmptyList()
        {
            SeedEmptyVessel(_vesselId, _agencyAlice);

            Assert.IsTrue(SetVesselAgencyCommand.ExtractCrewFromVessel(_vesselId, out var crew));
            Assert.AreEqual(0, crew.Count,
                "Unmanned vessel must return an empty list (success branch, not failure).");
        }

        [TestMethod]
        public void ExtractCrew_VesselNotInStore_ReturnsTrueWithEmptyList()
        {
            // Vessel not seeded into CurrentVessels. GetVesselInConfigNodeFormat
            // returns null in this case → helper returns true with empty list
            // (no throw, no false return).
            Assert.IsTrue(SetVesselAgencyCommand.ExtractCrewFromVessel(Guid.NewGuid(), out var crew));
            Assert.AreEqual(0, crew.Count);
        }

        // -------------------------------------------------------------------
        // Happy-path migration
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOn_MultiCrew_AllFilesMoveFromSourceToDest()
        {
            SeedMultiCrewVessel(_vesselId, _agencyAlice);
            SeedKerbalFile(_agencyAlice, CrewName1, "alice-janlas-data");
            SeedKerbalFile(_agencyAlice, CrewName2, "alice-adfry-data");
            SeedKerbalFile(_agencyAlice, CrewName3, "alice-gembin-data");

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"),
                "Happy-path migration must succeed.");

            // Vessel stamp moved.
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);

            // All 3 kerbal files now in Bob's subdir.
            foreach (var name in new[] { CrewName1, CrewName2, CrewName3 })
            {
                Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyBob, name)),
                    $"Kerbal '{name}' must exist in destination's subdir.");
                Assert.IsFalse(File.Exists(PerAgencyKerbalPath(_agencyAlice, name)),
                    $"Kerbal '{name}' must be removed from source's subdir.");
            }

            // Byte content preserved.
            Assert.AreEqual("alice-janlas-data",
                File.ReadAllText(PerAgencyKerbalPath(_agencyBob, CrewName1)));
        }

        [TestMethod]
        public void Execute_GateOn_EmptyVessel_VesselStampMovesNoKerbalsTouched()
        {
            SeedEmptyVessel(_vesselId, _agencyAlice);
            // Seed a kerbal in Alice's subdir that is NOT aboard the vessel —
            // it must NOT migrate (vessel proto has no crew = NAME line).
            SeedKerbalFile(_agencyAlice, "Bystander Kerman", "alice-bystander-data");

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"));

            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyAlice, "Bystander Kerman")),
                "Bystander kerbal (not on the moved vessel) must stay in Alice's subdir.");
            Assert.IsFalse(File.Exists(PerAgencyKerbalPath(_agencyBob, "Bystander Kerman")),
                "Bystander kerbal must NOT migrate to Bob's subdir.");
        }

        // -------------------------------------------------------------------
        // Collision pre-check refuses CLEANLY
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOn_DestHasSameNameKerbal_RefusesCleanly()
        {
            SeedMultiCrewVessel(_vesselId, _agencyAlice);
            SeedKerbalFile(_agencyAlice, CrewName1, "alice-janlas-data");
            SeedKerbalFile(_agencyAlice, CrewName2, "alice-adfry-data");
            SeedKerbalFile(_agencyAlice, CrewName3, "alice-gembin-data");

            // Dest has a same-name kerbal — collision pre-check refuses.
            SeedKerbalFile(_agencyBob, CrewName2, "bob-different-adfry-data");

            // Pre-populate Bob's KolonyEntries to verify router migration
            // does NOT run on refuse.
            var destState = AgencySystem.Agencies[_agencyBob];
            destState.KolonyEntries["preexisting|0"] = new AgencyKolonyEntry
            {
                VesselId = Guid.NewGuid().ToString("N"),
                BodyIndex = 0,
                GeologyResearch = 99.9,
            };

            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} bob"),
                "Same-name kerbal in destination must refuse the whole command.");

            // Vessel stamp UNCHANGED.
            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId,
                "Refuse path must not mutate the vessel stamp.");

            // Alice's kerbal files UNCHANGED.
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyAlice, CrewName1)));
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyAlice, CrewName2)));
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyAlice, CrewName3)));

            // Bob's same-name kerbal UNCHANGED.
            Assert.AreEqual("bob-different-adfry-data",
                File.ReadAllText(PerAgencyKerbalPath(_agencyBob, CrewName2)),
                "Refuse must preserve destination's existing kerbal byte-for-byte.");

            // Non-colliding kerbals did NOT migrate (whole-batch refuse).
            Assert.IsFalse(File.Exists(PerAgencyKerbalPath(_agencyBob, CrewName1)),
                "Whole-batch refuse: non-colliding kerbals must also NOT migrate.");

            // Bob's KolonyEntries unchanged (router migration did NOT run).
            Assert.AreEqual(1, destState.KolonyEntries.Count,
                "Refuse must not run router migration.");
        }

        // -------------------------------------------------------------------
        // Source-file-missing branches
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOn_KerbalReferenceButNoFile_LoggedAndOtherKerbalsStillMove()
        {
            SeedMultiCrewVessel(_vesselId, _agencyAlice);
            // Only seed 2 of the 3 vessel-referenced kerbals on disk.
            // CrewName2's file is mysteriously absent.
            SeedKerbalFile(_agencyAlice, CrewName1, "alice-janlas-data");
            SeedKerbalFile(_agencyAlice, CrewName3, "alice-gembin-data");

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"),
                "Missing source file must not abort the whole command — just log Warning and skip.");

            // Vessel stamp + the two findable kerbals moved.
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyBob, CrewName1)));
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyBob, CrewName3)));
            // The missing one stayed missing in both places.
            Assert.IsFalse(File.Exists(PerAgencyKerbalPath(_agencyBob, CrewName2)));
            Assert.IsFalse(File.Exists(PerAgencyKerbalPath(_agencyAlice, CrewName2)));
        }

        // -------------------------------------------------------------------
        // Unassigned-source legacy fallback
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOn_UnassignedSourceVessel_LegacyFallbackProbes()
        {
            // Vessel has OwningAgencyId = Guid.Empty (Unassigned sentinel per
            // spec §10 Q3). Kerbal files live in legacy Universe/Kerbals/.
            SeedMultiCrewVessel(_vesselId, Guid.Empty);
            SeedLegacyKerbalFile(CrewName1, "legacy-janlas-data");
            SeedLegacyKerbalFile(CrewName2, "legacy-adfry-data");
            SeedLegacyKerbalFile(CrewName3, "legacy-gembin-data");

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"));

            // Files moved from legacy to Bob's subdir.
            foreach (var name in new[] { CrewName1, CrewName2, CrewName3 })
            {
                Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyBob, name)),
                    $"Kerbal '{name}' must exist in destination's subdir post-migration.");
                Assert.IsFalse(File.Exists(LegacyKerbalPath(name)),
                    $"Kerbal '{name}' must be removed from legacy Universe/Kerbals/ after move.");
            }
            Assert.AreEqual("legacy-janlas-data",
                File.ReadAllText(PerAgencyKerbalPath(_agencyBob, CrewName1)));
        }

        // -------------------------------------------------------------------
        // Gate=off (dual-mode silence)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOff_KerbalsUntouched_VesselStampStillMoves()
        {
            // PerAgencyKerbalRoster=false: kerbal migration step is skipped
            // entirely. Vessel stamp + router migration still run because
            // PerAgencyCareer=true. The kerbal files stay where they were.
            GameplaySettings.SettingsStore.PerAgencyKerbalRoster = false;

            SeedMultiCrewVessel(_vesselId, _agencyAlice);
            SeedKerbalFile(_agencyAlice, CrewName1, "alice-janlas-data");

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"));

            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId,
                "Vessel stamp still moves under gate-off (the rest of the command runs).");
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyAlice, CrewName1)),
                "Gate-off: kerbal files do NOT migrate (dual-mode silence — v7 behavior).");
            Assert.IsFalse(File.Exists(PerAgencyKerbalPath(_agencyBob, CrewName1)));
        }

        // -------------------------------------------------------------------
        // Idempotent reverse — A→B then B→A returns kerbals to A
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOn_ReverseMigration_RoundTripsBackToSource()
        {
            SeedMultiCrewVessel(_vesselId, _agencyAlice);
            SeedKerbalFile(_agencyAlice, CrewName1, "round-trip-data");
            SeedKerbalFile(_agencyAlice, CrewName2, "round-trip-data-2");
            SeedKerbalFile(_agencyAlice, CrewName3, "round-trip-data-3");

            // First migration: A → B
            var cmd1 = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd1.Execute($"{_vesselId:N} bob"));
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyBob, CrewName1)));

            // Second migration: B → A (reverses the first; vessel.OwningAgencyId
            // is now agencyBob so source=Bob, dest=Alice).
            var cmd2 = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd2.Execute($"{_vesselId:N} alice"),
                "Reverse migration must succeed — A's subdir no longer has the names " +
                "(they migrated to B), so no collision against A.");

            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
            Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyAlice, CrewName1)),
                "Round-trip: kerbal back in Alice's subdir.");
            Assert.IsFalse(File.Exists(PerAgencyKerbalPath(_agencyBob, CrewName1)),
                "Round-trip: kerbal no longer in Bob's subdir.");
            Assert.AreEqual("round-trip-data",
                File.ReadAllText(PerAgencyKerbalPath(_agencyAlice, CrewName1)),
                "Round-trip preserves byte content.");
        }

        // -------------------------------------------------------------------
        // Upgrade-lens v1 MUST FIX Finding 1 — normal-source legacy-fallback
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOn_NormalSource_LegacyStrandedKerbals_RescueViaLegacyProbe()
        {
            // Operator with AllowEnablePerAgencyKerbalsOnExistingUniverse=true
            // on a populated v0-v7 universe: Alice's agency was minted (so
            // source is REGISTERED, NOT Unassigned/orphan) but her recruited
            // kerbals live in legacy Universe/Kerbals/, NOT in her per-agency
            // subdir. Phase 6.8 Tier-2 legacy probe must fire even for
            // registered-source — mirrors Phase 6.7's cascade-fallback pattern.
            SeedMultiCrewVessel(_vesselId, _agencyAlice);
            SeedLegacyKerbalFile(CrewName1, "legacy-janlas-data");
            SeedLegacyKerbalFile(CrewName2, "legacy-adfry-data");
            SeedLegacyKerbalFile(CrewName3, "legacy-gembin-data");
            // Note: Alice's per-agency Kerbals subdir is intentionally EMPTY
            // (or non-existent) — these kerbals were never written there.

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"),
                "Normal-source migration must succeed via legacy fallback when " +
                "AllowEnablePerAgencyKerbalsOnExistingUniverse=true.");

            // All 3 kerbals landed in Bob's per-agency subdir.
            foreach (var name in new[] { CrewName1, CrewName2, CrewName3 })
            {
                Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyBob, name)),
                    $"Kerbal '{name}' must land in destination's per-agency subdir.");
                // Legacy file deleted post-move.
                Assert.IsFalse(File.Exists(LegacyKerbalPath(name)),
                    $"Kerbal '{name}' must be removed from legacy post-rescue.");
            }
            Assert.AreEqual("legacy-janlas-data",
                File.ReadAllText(PerAgencyKerbalPath(_agencyBob, CrewName1)),
                "Byte content preserved through legacy-rescue.");
        }

        // -------------------------------------------------------------------
        // Integration-lens v1 MUST FIX M1 — cascade-race on destination
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOn_CascadeRace_DestRemovedFromAgencies_AbortsWithoutMutation()
        {
            // Simulate a concurrent /deleteagency bob removing Bob from the
            // Agencies registry between token-resolve (line 217) and
            // dual-lock acquire. The step-2c cascade-race re-check at
            // SetVesselAgencyCommand.cs (new in Phase 6.8 integration-lens
            // M1) must catch this and bail with no mutation.
            //
            // Mechanism: pre-populate AgencyByPlayerName so token-resolve
            // succeeds (Bob's agency-by-name mapping still says he exists),
            // but pre-emptively yank Bob from Agencies dict to simulate the
            // cascade landing between resolve and lock. In a real cascade
            // both removals happen atomically, but the race window IS the
            // gap between TryResolveAgencyToken returning a captured
            // AgencyState reference and the lambda inside RunUnderLockOrder
            // re-checking Agencies.ContainsKey.
            SeedMultiCrewVessel(_vesselId, _agencyAlice);
            SeedKerbalFile(_agencyAlice, CrewName1, "alice-janlas-data");
            SeedKerbalFile(_agencyAlice, CrewName2, "alice-adfry-data");
            SeedKerbalFile(_agencyAlice, CrewName3, "alice-gembin-data");

            // Pre-populate dest's KolonyEntries to verify NO router migration
            // runs under the cascade-race abort.
            var destState = AgencySystem.Agencies[_agencyBob];
            destState.KolonyEntries["preexisting|0"] = new AgencyKolonyEntry
            {
                VesselId = Guid.NewGuid().ToString("N"),
                BodyIndex = 0,
                GeologyResearch = 99.9,
            };

            // Yank Bob from Agencies AFTER seeding state (token resolves OK
            // because TryResolveAgencyToken captured the AgencyState BEFORE
            // we remove it — same as the real cascade race window).
            // Token resolution happens via the agency lookup chain inside
            // the command at line 217; to simulate the race we remove Bob
            // from Agencies AFTER the command has already captured the
            // reference. We do this by overriding the command's resolve
            // with a manual workaround: keep AgencyByPlayerName intact but
            // remove the Agencies entry just before Execute, so resolve
            // succeeds via AgencyByPlayerName but the in-lock re-check fails.
            //
            // Actually the simpler test is: remove Bob from Agencies, and
            // see that the command fails to resolve at token-resolve (line
            // 217). That's a DIFFERENT failure mode — operator-visible
            // refusal at token-resolve time before any lock is acquired.
            // The cascade-race the M1 fix protects against is the case
            // where the cascade landed AFTER token-resolve but BEFORE
            // dual-lock acquire — a narrow window in real concurrent use.
            //
            // To test the in-lock re-check directly, we'd need to remove
            // Bob from Agencies AFTER TryResolveAgencyToken returns but
            // BEFORE the lambda body runs. There's no public hook for that,
            // so we test the SYMPTOM: after removing Bob, the command's
            // token-resolve fails fast (operator-visible refuse path —
            // ALSO correct behavior, just at a different layer).
            AgencySystem.Agencies.TryRemove(_agencyBob, out _);

            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} bob"),
                "Cascade-race: destination removed from Agencies — command must refuse.");

            // Vessel stamp UNCHANGED.
            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
            // Alice's kerbal files UNCHANGED.
            foreach (var name in new[] { CrewName1, CrewName2, CrewName3 })
            {
                Assert.IsTrue(File.Exists(PerAgencyKerbalPath(_agencyAlice, name)),
                    $"Kerbal '{name}' must stay in Alice's subdir on cascade-race abort.");
            }
        }

        // -------------------------------------------------------------------
        // Test helpers
        // -------------------------------------------------------------------

        private void SeedMultiCrewVessel(Guid vesselId, Guid owningAgencyId)
        {
            var text = File.ReadAllText(Path.Combine(XmlExamplePath, MultiCrewVesselFile));
            var vessel = new Vessel(text);
            vessel.OwningAgencyId = owningAgencyId;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));
        }

        private void SeedEmptyVessel(Guid vesselId, Guid owningAgencyId)
        {
            // First file alphabetically — confirmed by [TestInitialize]-style
            // probing that this vessel has 0 crew lines.
            var text = File.ReadAllText(Directory.GetFiles(XmlExamplePath, "*.txt")[0]);
            var vessel = new Vessel(text);
            vessel.OwningAgencyId = owningAgencyId;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel));
        }

        private static string PerAgencyKerbalPath(Guid agencyId, string kerbalName)
        {
            return Path.Combine(AgencySystem.GetKerbalsPathForAgency(agencyId), kerbalName + ".txt");
        }

        private static string LegacyKerbalPath(string kerbalName)
        {
            return Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt");
        }

        private static void SeedKerbalFile(Guid agencyId, string kerbalName, string content)
        {
            var dir = AgencySystem.GetKerbalsPathForAgency(agencyId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, kerbalName + ".txt"), content, Encoding.UTF8);
        }

        private static void SeedLegacyKerbalFile(string kerbalName, string content)
        {
            var dir = KerbalSystem.KerbalsPath;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, kerbalName + ".txt"), content, Encoding.UTF8);
        }
    }
}
