using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    /// [Mod-compat S1] Unit tests for
    /// <see cref="AgencyVesselCoupleReconciler.Reconcile"/>. The reconciler is a
    /// pure helper over <see cref="VesselStoreSystem.CurrentVessels"/> +
    /// <see cref="AgencySystem"/>; no ClientStructure / NetConnection needed,
    /// same internal-helper-pin pattern as the other Stage 5 router tests.
    /// </summary>
    [TestClass]
    public class AgencyVesselCoupleReconcilerTest
    {
        private static readonly string XmlExamplePath = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        private readonly Guid _agencyAlice = Guid.NewGuid();
        private readonly Guid _agencyBob = Guid.NewGuid();
        private readonly Guid _vesselAlice = Guid.NewGuid();
        private readonly Guid _vesselBob = Guid.NewGuid();
        private readonly Guid _vesselUnassigned1 = Guid.NewGuid();
        private readonly Guid _vesselUnassigned2 = Guid.NewGuid();

        [TestInitialize]
        public void Setup()
        {
            VesselStoreSystem.CurrentVessels.Clear();
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;

            AgencySystem.Agencies[_agencyAlice] = new AgencyState
            {
                AgencyId = _agencyAlice,
                OwningPlayerName = "alice",
                DisplayName = "Alice Corp",
            };
            AgencySystem.Agencies[_agencyBob] = new AgencyState
            {
                AgencyId = _agencyBob,
                OwningPlayerName = "bob",
                DisplayName = "Bob Inc",
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
        // Gate-off (dual-mode silence) + race guards
        // -------------------------------------------------------------------

        [TestMethod]
        public void Reconcile_GateOff_NoMutationEvenWhenStampsDiffer()
        {
            // Gate off must short-circuit BEFORE any vessel lookup so the
            // legacy couple path is bit-for-bit identical to pre-S1.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            SeedVessel(_vesselAlice, _agencyAlice);
            SeedVessel(_vesselBob, _agencyBob);

            AgencyVesselCoupleReconciler.Reconcile(_vesselAlice, _vesselBob);

            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselAlice].OwningAgencyId);
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselBob].OwningAgencyId);
        }

        [TestMethod]
        public void Reconcile_SandboxMode_NoMutation()
        {
            // PerAgencyEnabled = PerAgencyCareer && GameMode == Career. Sandbox
            // closes the gate even with PerAgencyCareer=true.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            SeedVessel(_vesselAlice, _agencyAlice);
            SeedVessel(_vesselBob, _agencyBob);

            AgencyVesselCoupleReconciler.Reconcile(_vesselAlice, _vesselBob);

            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselAlice].OwningAgencyId);
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselBob].OwningAgencyId);
        }

        [TestMethod]
        public void Reconcile_KeptNotInStore_NoThrowNoMutation()
        {
            // Race: the dominant vessel disappeared between HandleVesselCouple
            // dispatch and the reconcile (e.g. a concurrent VesselRemove
            // arrived). Must be a silent no-op.
            SeedVessel(_vesselBob, _agencyBob);

            AgencyVesselCoupleReconciler.Reconcile(_vesselAlice, _vesselBob);

            // Merged vessel still has its stamp (caller's RemoveVessel runs
            // unchanged, but we never touched its stamp).
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselBob].OwningAgencyId);
        }

        [TestMethod]
        public void Reconcile_MergedNotInStore_TreatsMergedAsEmpty()
        {
            // Race: the merged vessel was already removed (or never made it
            // into the store). Treat its agency as Empty; kept retains its
            // own stamp; no exception.
            SeedVessel(_vesselAlice, _agencyAlice);

            AgencyVesselCoupleReconciler.Reconcile(_vesselAlice, _vesselBob);

            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselAlice].OwningAgencyId);
        }

        // -------------------------------------------------------------------
        // Same-agency idempotency
        // -------------------------------------------------------------------

        [TestMethod]
        public void Reconcile_SameAgency_NoMutation()
        {
            var second = Guid.NewGuid();
            SeedVessel(_vesselAlice, _agencyAlice);
            SeedVessel(second, _agencyAlice);

            AgencyVesselCoupleReconciler.Reconcile(_vesselAlice, second);

            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselAlice].OwningAgencyId);
            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[second].OwningAgencyId);
        }

        [TestMethod]
        public void Reconcile_BothEmpty_NoMutation()
        {
            // Pre-0.31 universe upgrade case — neither vessel has been stamped
            // yet (operator hasn't run setvesselagency / transferagency). No
            // warning, no mutation.
            SeedVessel(_vesselUnassigned1, Guid.Empty);
            SeedVessel(_vesselUnassigned2, Guid.Empty);

            AgencyVesselCoupleReconciler.Reconcile(_vesselUnassigned1, _vesselUnassigned2);

            Assert.AreEqual(Guid.Empty, VesselStoreSystem.CurrentVessels[_vesselUnassigned1].OwningAgencyId);
            Assert.AreEqual(Guid.Empty, VesselStoreSystem.CurrentVessels[_vesselUnassigned2].OwningAgencyId);
        }

        // -------------------------------------------------------------------
        // Mixed Unassigned-sentinel cases (spec §10 Q3)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Reconcile_KeptUnassigned_MergedTracked_KeptAdoptsMergedStamp()
        {
            // Pre-0.31 vessel couples with a tracked one. Adopt the tracked
            // stamp so agency continuity survives the merge instead of
            // silently losing the only claim.
            SeedVessel(_vesselUnassigned1, Guid.Empty);
            SeedVessel(_vesselBob, _agencyBob);

            AgencyVesselCoupleReconciler.Reconcile(_vesselUnassigned1, _vesselBob);

            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselUnassigned1].OwningAgencyId,
                "Kept must adopt the merged vessel's stamp when kept was Unassigned.");
            // Merged stamp is untouched by the reconciler; the caller's
            // VesselStoreSystem.RemoveVessel will dispose the merged vessel
            // entirely a few lines later.
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselBob].OwningAgencyId);
        }

        [TestMethod]
        public void Reconcile_KeptTracked_MergedUnassigned_KeptUnchanged()
        {
            // Symmetric to the Unassigned-adopt case but with the roles
            // reversed — kept already has the stamp, no mutation needed.
            SeedVessel(_vesselAlice, _agencyAlice);
            SeedVessel(_vesselUnassigned1, Guid.Empty);

            AgencyVesselCoupleReconciler.Reconcile(_vesselAlice, _vesselUnassigned1);

            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselAlice].OwningAgencyId);
            Assert.AreEqual(Guid.Empty, VesselStoreSystem.CurrentVessels[_vesselUnassigned1].OwningAgencyId);
        }

        // -------------------------------------------------------------------
        // True cross-agency couple — kept-wins per KSP determinism
        // -------------------------------------------------------------------

        [TestMethod]
        public void Reconcile_CrossAgency_AKept_KeptStampPreserved()
        {
            SeedVessel(_vesselAlice, _agencyAlice);
            SeedVessel(_vesselBob, _agencyBob);

            AgencyVesselCoupleReconciler.Reconcile(_vesselAlice, _vesselBob);

            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselAlice].OwningAgencyId,
                "Kept side wins on cross-agency couple — KSP's Part.Couple determinism is honored.");
            // Merged vessel still carries its stamp at the moment of
            // reconcile; the caller removes it from CurrentVessels next.
            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselBob].OwningAgencyId);
        }

        [TestMethod]
        public void Reconcile_CrossAgency_BKept_KeptStampPreserved()
        {
            SeedVessel(_vesselAlice, _agencyAlice);
            SeedVessel(_vesselBob, _agencyBob);

            AgencyVesselCoupleReconciler.Reconcile(_vesselBob, _vesselAlice);

            Assert.AreEqual(_agencyBob, VesselStoreSystem.CurrentVessels[_vesselBob].OwningAgencyId);
            Assert.AreEqual(_agencyAlice, VesselStoreSystem.CurrentVessels[_vesselAlice].OwningAgencyId);
        }

        // -------------------------------------------------------------------
        // Setup helpers
        // -------------------------------------------------------------------

        private static void SeedVessel(Guid vesselId, Guid owningAgencyId)
        {
            var vessel = LoadSampleVessel();
            vessel.OwningAgencyId = owningAgencyId;
            Assert.IsTrue(VesselStoreSystem.CurrentVessels.TryAdd(vesselId, vessel),
                "Test setup: vessel must not already be in the store.");
        }

        private static Vessel LoadSampleVessel()
        {
            return new Vessel(File.ReadAllText(Directory.GetFiles(XmlExamplePath).OrderBy(p => p, StringComparer.Ordinal).First()));
        }
    }
}
