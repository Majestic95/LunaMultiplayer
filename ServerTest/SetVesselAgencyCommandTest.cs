using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using Server.System.Vessel.Classes;
using System;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// Phase 3 Slice E-2 — direct-Execute() integration coverage for the
    /// <see cref="SetVesselAgencyCommand"/>'s gate / resolve / short-circuit
    /// branches. Each test verifies behavior on a code path that returns
    /// BEFORE any disk write, wire emit, or migration-helper call — so the
    /// test can drive the command via <c>Execute()</c> without needing the
    /// full <see cref="Server.Server.MessageQueuer"/> +
    /// <see cref="Server.System.BackupSystem"/> harness.
    ///
    /// <para><b>End-to-end coverage</b> (happy A→B with kolony+orbital+
    /// planetary state, cross-router echoes, lock release, visibility
    /// broadcast) lives in <c>MockClientTest/CrossRouterVesselTransferTest</c>
    /// — that surface needs the full Lidgren wire + in-process server harness
    /// because the Command's terminal side-effects all hit the message bus or
    /// the canonical Universe directory.</para>
    ///
    /// <para>Test pattern mirrors <see cref="LockSystemAgencyTest"/> —
    /// seed AgencySystem + VesselStoreSystem state per [TestInitialize],
    /// drive the command via <c>new SetVesselAgencyCommand().Execute(args)</c>,
    /// assert on the return value + persisted state (or absence of mutation
    /// for the early-return branches).</para>
    /// </summary>
    [TestClass]
    public class SetVesselAgencyCommandTest
    {
        private static readonly string XmlExamplePath =
            Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        private readonly Guid _vesselId = Guid.NewGuid();
        private readonly Guid _agencyAlice = Guid.NewGuid();
        private readonly Guid _agencyBob = Guid.NewGuid();

        [TestInitialize]
        public void Setup()
        {
            // Wipe per-test state — same shape as LockSystemAgencyTest /
            // AgencyKolonyRouterTest. No locks to clear (this command doesn't
            // touch the lock store on its early-return paths).
            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            // Seed two agencies (Alice, Bob) so token-resolve + happy-path
            // setup is consistent across tests. Tests that exercise the
            // empty-registry branch override this in-method.
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
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
        }

        // -------------------------------------------------------------------
        // Parser-failure path — returns false before any AgencySystem touch
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_NoArgs_ReturnsFalse()
        {
            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute(string.Empty),
                "Empty args must fail parse and return false.");
        }

        [TestMethod]
        public void Execute_OneArg_ReturnsFalse()
        {
            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute(_vesselId.ToString("N")),
                "One-token input must fail parse and return false.");
        }

        // -------------------------------------------------------------------
        // Gate refusal — dual-mode silence; never touches the registry
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_GateOff_PerAgencyCareerFalse_ReturnsFalse()
        {
            // PerAgencyCareer=false: vessels carry no agency stamp — there is
            // nothing to reassign. Refuse loudly so an operator running with
            // gate=off doesn't think a no-op succeeded.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            SeedVessel(_vesselId, _agencyAlice);

            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} bob"),
                "Gate off must refuse with a clear error.");
            // No mutation happened — confirm the vessel stamp is unchanged.
            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
        }

        [TestMethod]
        public void Execute_GateOff_NonCareerGameMode_ReturnsFalse()
        {
            // PerAgencyCareer=true but GameMode=Sandbox: the AgencySystem
            // combined gate (PerAgencyEnabled) is off. Mirrors
            // /transferagency + /deleteagency's combined-gate refusal.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            SeedVessel(_vesselId, _agencyAlice);

            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} bob"));
            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
        }

        // -------------------------------------------------------------------
        // Vessel-token / vessel-store resolve failures
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_UnparseableVesselGuid_ReturnsFalse()
        {
            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute("not-a-guid bob"),
                "Unparseable vessel-token must fail with a clear error.");
        }

        [TestMethod]
        public void Execute_EmptyVesselGuid_ReturnsFalse()
        {
            // Guid.Empty is the Unassigned-vessel sentinel (spec §10 Q3) — not
            // a real vessel identity. Refusing prevents an operator from
            // mass-stamping the sentinel itself.
            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{Guid.Empty:N} bob"),
                "Guid.Empty vessel-token must be refused.");
        }

        [TestMethod]
        public void Execute_VesselNotInStore_ReturnsFalse()
        {
            // No seed — vessel store is empty.
            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} bob"),
                "Vessel not in CurrentVessels must be refused.");
        }

        // -------------------------------------------------------------------
        // Destination agency token resolve failures
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_EmptyRegistry_ReturnsFalseWithDistinctError()
        {
            // Empty registry path: the agency token can't possibly resolve.
            // Operator-friendly framing ("no agencies registered yet") so
            // pre-first-connect upgrades see the right hint.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.Reset();

            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} bob"),
                "Empty agency registry must refuse the token-resolve.");
        }

        [TestMethod]
        public void Execute_UnknownAgencyToken_ReturnsFalse()
        {
            // Non-empty registry, unknown token (not a registered owner name,
            // not a registered agency id).
            SeedVessel(_vesselId, _agencyAlice);

            var cmd = new SetVesselAgencyCommand();
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} not-a-registered-owner"),
                "Unknown agency token must be refused.");
            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
        }

        // -------------------------------------------------------------------
        // Step 1 — same-stamp short-circuit
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_SameStamp_ReturnsTrueWithoutMutating()
        {
            // Idempotent re-issue: vessel already belongs to dest agency.
            // Step 1 short-circuits BEFORE the lock acquire / migration /
            // SaveAgency / BackupSystem.RunBackup / wire emit. Return true
            // (success-no-op) — operator scripts that idempotently re-run on
            // a session don't need to re-check vessel.OwningAgencyId first.
            SeedVessel(_vesselId, _agencyBob);
            // Pre-populate Bob's kolony partition so we can assert that the
            // short-circuit didn't accidentally call MigrateForVesselTransfer
            // (which would have removed the entry).
            AgencySystem.Agencies[_agencyBob].KolonyEntries[$"{_vesselId:N}|0"] =
                new LmpCommon.Message.Data.Agency.AgencyKolonyEntry
                {
                    VesselId = _vesselId.ToString("N"),
                    BodyIndex = 0,
                };

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"),
                "Same-stamp re-issue must return true (idempotent success-no-op).");

            // Vessel stamp unchanged.
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId);
            // Bob's kolony entry survives — no migration ran.
            Assert.AreEqual(1, AgencySystem.Agencies[_agencyBob].KolonyEntries.Count,
                "Same-stamp short-circuit must not invoke MigrateForVesselTransfer.");
        }

        // -------------------------------------------------------------------
        // Orphaned-source (Round-1 upgrade-lens CONSIDER C2)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Execute_OrphanedSource_ProceedsWithoutMigrationAndStampsDestination()
        {
            // Orphaned source: vessel.OwningAgencyId points at a Guid that's
            // not in AgencySystem.Agencies (the AgencyState file was deleted,
            // boot warning fired, operator continues anyway). The command
            // must (a) succeed, (b) stamp the vessel to dest, (c) NOT call
            // any migration helper (no per-router partitions to migrate FROM),
            // (d) NOT mutate dest's per-router dicts as a side effect.
            var orphanedSourceId = Guid.NewGuid();
            // Deliberately NOT added to AgencySystem.Agencies — orphaned.
            SeedVessel(_vesselId, orphanedSourceId);

            // Pre-populate dest's KolonyEntries with something that would
            // get accidentally clobbered if migration ran in error.
            var destState = AgencySystem.Agencies[_agencyBob];
            destState.KolonyEntries["preexisting|0"] = new AgencyKolonyEntry
            {
                VesselId = Guid.NewGuid().ToString("N"),
                BodyIndex = 0,
                GeologyResearch = 99.9,
            };

            var cmd = new SetVesselAgencyCommand();
            Assert.IsTrue(cmd.Execute($"{_vesselId:N} bob"),
                "Orphaned-source command must succeed (orphan ≠ refusal).");

            // Vessel stamp mutated.
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselId].OwningAgencyId,
                "Orphaned-source must still stamp the vessel to destination.");

            // Dest's KolonyEntries unchanged (no migration ran).
            Assert.AreEqual(1, destState.KolonyEntries.Count,
                "Dest's KolonyEntries must not be mutated when source is orphaned.");
            Assert.IsTrue(destState.KolonyEntries.ContainsKey("preexisting|0"),
                "Dest's pre-existing entry must survive.");
        }

        private void SeedVessel(Guid vesselId, Guid owningAgencyId)
        {
            var vessel = LoadSampleVessel();
            vessel.OwningAgencyId = owningAgencyId;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel),
                "Test setup: vessel must not already be in the store.");
        }

        private static Vessel LoadSampleVessel()
        {
            return new Vessel(File.ReadAllText(
                Directory.GetFiles(XmlExamplePath).OrderBy(p => p, StringComparer.Ordinal).First()));
        }

        [TestMethod]
        public void Execute_SameStamp_WithUnassignedVessel_AndUnassignedDest_ImpossibleByDesign()
        {
            // Defensive: a same-stamp short-circuit on (Empty, Empty) would
            // fire — but TryResolveAgencyToken refuses Guid.Empty (it's not in
            // Agencies). So this path is unreachable through legitimate
            // operator input. The branch in code is defense-in-depth; we
            // confirm here that the operator path can't reach it.
            SeedVessel(_vesselId, Guid.Empty);

            var cmd = new SetVesselAgencyCommand();
            // Operator can't pass Empty as the destination token because no
            // registered agency has that id. Token resolve fails first.
            Assert.IsFalse(cmd.Execute($"{_vesselId:N} 00000000000000000000000000000000"),
                "Guid.Empty as a destination token must fail to resolve.");
        }
    }
}
