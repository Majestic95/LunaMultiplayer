using LmpCommon.Enums;
using LmpCommon.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
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
    /// Stage 5.17a — cross-agency lock rejection in <see cref="LockSystem.AcquireLock"/>.
    /// Under <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>=true, a vessel-scoped
    /// lock acquire is refused when the requesting player's agency differs from the vessel's
    /// <see cref="Vessel.OwningAgencyId"/>. The implicit <see cref="Guid.Empty"/> Unassigned
    /// sentinel (spec §10 Q3) allows any agency to interact, preserving the
    /// "operator transfers via admin command" path for pre-0.31 vessels.
    ///
    /// The tests below pin each branch of the new rejection guard. End-to-end wire
    /// coverage is in <c>MockClientTest/CrossAgencyLockRejectionTest</c>.
    /// </summary>
    [TestClass]
    public class LockSystemAgencyTest
    {
        private static readonly string XmlExamplePath = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        private readonly Guid _vesselId = Guid.NewGuid();
        private readonly Guid _agencyAlice = Guid.NewGuid();
        private readonly Guid _agencyBob = Guid.NewGuid();

        [TestInitialize]
        public void Setup()
        {
            // Wipe lock + vessel + agency state so adjacent tests don't bleed.
            foreach (var l in LockSystem.LockQuery.GetAllLocks().ToList())
                LockSystem.ReleaseLock(l);
            VesselStoreSystem.CurrentVessels.Clear();
            WarpContext.Subspaces.Clear();
            AgencySystem.Reset();
            // [Stage 5.17e-1] Combined gate requires both PerAgencyCareer AND GameMode=Career
            // (spec §10 Q-Mode Career-only). Tests below exercise the on-path of LockSystem's
            // cross-agency guard and the off-path bypasses; the gate-off test flips
            // PerAgencyCareer to false locally.
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
        }

        [TestCleanup]
        public void Teardown()
        {
            VesselStoreSystem.CurrentVessels.Clear();
            WarpContext.Subspaces.Clear();
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox; // restore default for adjacent test classes
        }

        [TestMethod]
        public void AcquireLock_SameAgencyOwnsVessel_Succeeds()
        {
            // Baseline: Alice's agency owns the vessel; Alice's player acquires Control. OK.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;

            var lockDef = new LockDefinition(LockType.Control, "alice", _vesselId);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Same-agency acquire must succeed.");
        }

        [TestMethod]
        public void AcquireLock_DifferentAgencyOwnsVessel_IsRejected()
        {
            // Alice's vessel; Bob (different agency) tries to take Control. Server refuses.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsFalse(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Cross-agency acquire must be rejected.");
            Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.Control, _vesselId, null),
                "Rejected lock must not be stored.");
        }

        [TestMethod]
        public void AcquireLock_VesselOwnedByEmptySentinel_AnyAgencyMaySucceed()
        {
            // Spec §10 Q3: pre-0.31 vessels (lmpOwningAgency absent → OwningAgencyId == Empty)
            // are Unassigned. Any agency may interact until operator transferagency assigns
            // ownership (Stage 5.18d).
            SeedVessel(_vesselId, Guid.Empty);
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Unassigned-sentinel vessel must allow lock acquire from any agency.");
        }

        [TestMethod]
        public void AcquireLock_GateOff_CrossAgencyAllowed()
        {
            // PerAgencyCareer=false: agency surface is invisible; cross-agency rejection
            // never fires. Even with the registry populated (Alice owns the vessel), Bob's
            // acquire proceeds because the gate is off.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Gate-off must bypass cross-agency rejection (spec §11 dual-mode silence).");
        }

        [TestMethod]
        public void AcquireLock_NonVesselScopedLock_IsNotRejected()
        {
            // Spectator/AsteroidComet/Contract/Kerbal locks have no vessel dimension; the
            // cross-agency guard does not apply. Bob can take a Spectator lock even though
            // Alice owns the vessel.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Spectator, "bob");
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Non-vessel-scoped lock must not trigger cross-agency rejection.");
        }

        [TestMethod]
        public void AcquireLock_VesselNotInStore_UnderGateOn_IsRejected()
        {
            // Round-1 consumer-lens MUST FIX: close the ingest-vs-acquire race. The vessel
            // proto stamp runs in a fire-and-forget Task.Run (VesselDataUpdater), but the
            // relay broadcast is synchronous on the receive thread. A racing LockAcquire
            // arriving before the stamp Task completes would, under the old bypass-on-not-
            // in-store rule, sneak past the cross-agency check and let any peer claim a
            // brand-new vessel as it's being ingested. Defensive reject: legitimate KSP
            // clients delay LockAcquire until after VesselSync; racing peers get refused.
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsFalse(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Under gate=on, an acquire for a vessel not yet in CurrentVessels must reject (ingest-vs-acquire race).");
            Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.Control, _vesselId, null),
                "Rejected lock must not be stored.");
        }

        [TestMethod]
        public void AcquireLock_VesselNotInStore_UnderGateOff_IsNotRejected()
        {
            // Dual-mode silence (spec §11): with the gate off, the agency-guard block is
            // skipped entirely — including the vessel-not-in-store reject. Legacy callers
            // and pre-Stage-5 tests must continue to work.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Gate-off: unknown vesselId must fall through to the normal acquire path.");
        }

        [TestMethod]
        public void AcquireLock_RequesterHasNoAgencyMapping_IsNotRejected()
        {
            // Defensive bypass: a player without a populated AgencyByPlayerName entry can't
            // be cross-agency-rejected — we can't compute the comparison. Should not happen
            // on the production path (MessageReceiver gates on Authenticated which is set
            // after OnPlayerAuthenticated), but tests that drive AcquireLock directly with
            // force:true (e.g. Bug010PinnedBroadcastTest) need this to work.
            SeedVessel(_vesselId, _agencyAlice);
            // No AgencyByPlayerName entry for "stray-player".

            var lockDef = new LockDefinition(LockType.Control, "stray-player", _vesselId);
            Assert.IsTrue(LockSystem.AcquireLock(lockDef, force: false, out _),
                "Player without an agency mapping must fall through to the normal acquire path.");
        }

        [TestMethod]
        public void AcquireLock_ForceTrue_DoesNotBypassCrossAgencyRejection()
        {
            // Important invariant: force:true allows overriding an existing lock holder, but
            // it does NOT bypass the cross-agency authority check. Same precedent as
            // BUG-005/006 cross-subspace: force overrides ownership conflict, not authority.
            // Without this, an admin command using force:true could silently grant a player
            // a lock on another agency's vessel.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsFalse(LockSystem.AcquireLock(lockDef, force: true, out _),
                "force:true must NOT bypass cross-agency rejection.");
        }

        [TestMethod]
        public void AcquireLock_AllVesselScopedLockTypes_AreGated()
        {
            // The cross-agency check applies to Control, Update, and UnloadedUpdate (the
            // three vessel-scoped lock types per IsVesselScopedLockType). Pin each.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            foreach (var type in new[] { LockType.Control, LockType.Update, LockType.UnloadedUpdate })
            {
                var lockDef = new LockDefinition(type, "bob", _vesselId);
                Assert.IsFalse(LockSystem.AcquireLock(lockDef, force: false, out _),
                    $"Cross-agency {type} acquire must be rejected.");
            }
        }

        [TestMethod]
        public void AcquireLock_CrossSubspaceAndCrossAgency_BothRejectionPathsRefuse()
        {
            // Both BUG-005/006 cross-subspace and Stage 5.17a cross-agency guards fire on
            // overlapping inputs. Which one fires first is implementation detail; the
            // outcome (rejection) is the invariant.
            WarpContext.Subspaces.TryAdd(1, new Subspace(1, 10d, "test"));
            WarpContext.Subspaces.TryAdd(2, new Subspace(2, 100d, "test"));

            var vessel = LoadSampleVessel();
            vessel.AuthoritativeSubspaceId = 2;
            vessel.OwningAgencyId = _agencyAlice;
            VesselStoreSystem.CurrentVessels.TryAdd(_vesselId, vessel);
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.UnloadedUpdate, "bob", _vesselId);
            Assert.IsFalse(LockSystem.AcquireLock(lockDef, force: false, out _, requesterSubspace: 1),
                "Both cross-subspace and cross-agency mismatch must reject.");
        }

        // --- IsCrossAgencyReject — Stage 5.18d slice (c) peek helper -------
        //
        // Backs the LockSystemSender.SendLockAcquireMessage reject-message
        // emission. Distinguishes the cross-agency reject path specifically
        // from the other reject reasons (cross-subspace past, vessel-not-in-
        // store, existing-holder) so the LockRejectMsgData wire emission
        // fires only for cross-agency. These cases pin the peek's return
        // value against each bypass and the actual-reject branch.

        [TestMethod]
        public void IsCrossAgencyReject_CrossAgency_ReturnsTrueWithOwningAgencyOut()
        {
            // Alice owns the vessel; Bob asks for a Control lock. The peek
            // returns true and surfaces Alice's agency id for the wire payload.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsTrue(LockSystem.IsCrossAgencyReject(lockDef, out var owningAgencyId));
            Assert.AreEqual(_agencyAlice, owningAgencyId);
        }

        [TestMethod]
        public void IsCrossAgencyReject_SameAgency_ReturnsFalse()
        {
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;

            var lockDef = new LockDefinition(LockType.Control, "alice", _vesselId);
            Assert.IsFalse(LockSystem.IsCrossAgencyReject(lockDef, out var owningAgencyId));
            Assert.AreEqual(Guid.Empty, owningAgencyId);
        }

        [TestMethod]
        public void IsCrossAgencyReject_UnassignedSentinelVessel_ReturnsFalse()
        {
            // Spec §10 Q3: Unassigned vessels (Empty owner) are interactable
            // by any agency; the reject path doesn't fire. Peek must agree.
            SeedVessel(_vesselId, Guid.Empty);
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsFalse(LockSystem.IsCrossAgencyReject(lockDef, out var owningAgencyId));
            Assert.AreEqual(Guid.Empty, owningAgencyId);
        }

        [TestMethod]
        public void IsCrossAgencyReject_GateOff_ReturnsFalse()
        {
            // Gate off: the cross-agency surface is invisible. AcquireLock
            // would succeed here, but verify the peek also says no-reject so
            // the sender emits no Reject message under gate-off.
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsFalse(LockSystem.IsCrossAgencyReject(lockDef, out _));
        }

        [TestMethod]
        public void IsCrossAgencyReject_NonVesselScopedLock_ReturnsFalse()
        {
            // Spectator/AsteroidComet/Contract/Kerbal aren't gated by the
            // cross-agency check; the peek should also pass them.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Spectator, "bob");
            Assert.IsFalse(LockSystem.IsCrossAgencyReject(lockDef, out _));
        }

        [TestMethod]
        public void IsCrossAgencyReject_VesselNotInStore_ReturnsFalse()
        {
            // The vessel-not-in-store reject path is silent (different reason
            // than cross-agency). Peek returns false; sender falls through to
            // the legacy silent-reject path. Acceptable — vessel-not-in-store
            // is typically a race window, not an operator-visible decision.
            AgencySystem.AgencyByPlayerName["bob"] = _agencyBob;

            var lockDef = new LockDefinition(LockType.Control, "bob", Guid.NewGuid());
            Assert.IsFalse(LockSystem.IsCrossAgencyReject(lockDef, out _));
        }

        [TestMethod]
        public void IsCrossAgencyReject_RequesterHasNoAgencyMapping_ReturnsFalse()
        {
            // Bob has no agency mapping — the 5.17a bypass case. Cross-agency
            // check does NOT fire (lock-acquire succeeds via bypass); peek
            // must also return false.
            SeedVessel(_vesselId, _agencyAlice);
            AgencySystem.AgencyByPlayerName["alice"] = _agencyAlice;
            // bob deliberately absent from AgencyByPlayerName

            var lockDef = new LockDefinition(LockType.Control, "bob", _vesselId);
            Assert.IsFalse(LockSystem.IsCrossAgencyReject(lockDef, out _));
        }

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
