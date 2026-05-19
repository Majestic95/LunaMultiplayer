using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;

namespace ServerTest
{
    /// <summary>
    /// Stage 5.18g — unit tests pinning <see cref="AgencyContractRouter.TryClaimContract"/>
    /// + <see cref="AgencyContractRouter.PreSeedClaimsFromAgencyState"/> +
    /// <see cref="AgencyContractRouter.ResetClaimedContracts"/>. Closes the BLOCKING
    /// simultaneous-Accept race surfaced by the 2026-05-19 pre-multiplayer full-analysis:
    /// the v3 hotfix (<c>042d2cb5</c>) made shared Offered/Generated contracts visible
    /// to peer agencies in real time, so two agencies receiving the same Offered guid
    /// could both promote it to a per-agency Active record and both collect the reward.
    ///
    /// <para><b>Test isolation.</b> The claim map is process-static, so each test calls
    /// <see cref="AgencyContractRouter.ResetClaimedContracts"/> in <c>[TestInitialize]</c>
    /// to prevent cross-test bleed. The same reset hook is wired into
    /// <c>ServerHarness.ResetPerTestState</c> so MockClient tests stay isolated too.</para>
    ///
    /// <para><b>Wire-level integration</b> (two MockClient instances Accepting the same
    /// guid in close succession) lives in <c>MockClientTest/AgencyContractRoutingTest.cs</c>.
    /// These unit tests pin the pure decision surface so a regression to the atomic
    /// first-wins contract or the pre-seed shape surfaces before the e2e harness pays
    /// the cost of bringing the wire up.</para>
    /// </summary>
    [TestClass]
    public class AgencyContractRouterClaimTest
    {
        [TestInitialize]
        public void Reset()
        {
            AgencyContractRouter.ResetClaimedContracts();
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Defensive: ensure a follow-on test class doesn't inherit our state.
            AgencyContractRouter.ResetClaimedContracts();
        }

        [TestMethod]
        public void TryClaimContract_NoPriorClaim_FirstAgencyWins()
        {
            // The base case the router relies on every time a new Offered contract
            // gets promoted to Active. The pre-5.18g code wrote into AgencyState.Contracts
            // unconditionally — this guard turns the write into a CAS.
            var contractGuid = Guid.NewGuid();
            var agencyA = Guid.NewGuid();

            var winner = AgencyContractRouter.TryClaimContract(contractGuid, agencyA);

            Assert.AreEqual(agencyA, winner, "First agency to claim must win the contract.");
            Assert.IsTrue(AgencyContractRouter.TryGetContractClaimant(contractGuid, out var stored));
            Assert.AreEqual(agencyA, stored, "Recorded claimant must match the winner.");
        }

        [TestMethod]
        public void TryClaimContract_SecondAgencyOnSameGuid_FirstAgencyStillWins()
        {
            // THE blocking scenario the audit caught. Without this guard, two
            // agencies' ApplyPerAgencyBatch calls land independently and both
            // promote the contract; both then collect the reward on completion.
            // Under the guard, the second agency's TryClaimContract returns the
            // FIRST agency's id and the router drops the contract from the batch.
            var contractGuid = Guid.NewGuid();
            var agencyA = Guid.NewGuid();
            var agencyB = Guid.NewGuid();

            var firstClaim = AgencyContractRouter.TryClaimContract(contractGuid, agencyA);
            var secondClaim = AgencyContractRouter.TryClaimContract(contractGuid, agencyB);

            Assert.AreEqual(agencyA, firstClaim);
            Assert.AreEqual(agencyA, secondClaim,
                "Second agency must see agencyA as the recorded claimant — router will drop the duplicate.");
        }

        [TestMethod]
        public void TryClaimContract_SameAgencyTwice_IsIdempotent()
        {
            // KSP's contract state machine can re-emit the same guid (Active → Completed
            // → echo cycle) and the router needs to upsert without false-rejecting. Same-
            // agency claim returns the same agency id; router proceeds with Upsert.
            var contractGuid = Guid.NewGuid();
            var agencyA = Guid.NewGuid();

            var first = AgencyContractRouter.TryClaimContract(contractGuid, agencyA);
            var second = AgencyContractRouter.TryClaimContract(contractGuid, agencyA);

            Assert.AreEqual(agencyA, first);
            Assert.AreEqual(agencyA, second, "Same-agency re-claim must be idempotent so legitimate state updates persist.");
            Assert.AreEqual(1, AgencyContractRouter.ClaimedContractsCount,
                "Re-claim must not double-count.");
        }

        [TestMethod]
        public void TryClaimContract_DifferentGuids_DoNotInterfere()
        {
            // Sanity: the dictionary is keyed by guid, so different contracts
            // claimed by different agencies must not affect each other.
            var guid1 = Guid.NewGuid();
            var guid2 = Guid.NewGuid();
            var agencyA = Guid.NewGuid();
            var agencyB = Guid.NewGuid();

            Assert.AreEqual(agencyA, AgencyContractRouter.TryClaimContract(guid1, agencyA));
            Assert.AreEqual(agencyB, AgencyContractRouter.TryClaimContract(guid2, agencyB));

            Assert.IsTrue(AgencyContractRouter.TryGetContractClaimant(guid1, out var claimant1));
            Assert.IsTrue(AgencyContractRouter.TryGetContractClaimant(guid2, out var claimant2));
            Assert.AreEqual(agencyA, claimant1);
            Assert.AreEqual(agencyB, claimant2);
        }

        [TestMethod]
        public void TryGetContractClaimant_UnclaimedGuid_ReturnsFalse()
        {
            // Defensive: a probe for a never-claimed guid returns false so admin
            // tooling can distinguish "never accepted" from "claimed by agency X".
            Assert.IsFalse(AgencyContractRouter.TryGetContractClaimant(Guid.NewGuid(), out _));
        }

        [TestMethod]
        public void PreSeedClaimsFromAgencyState_PopulatesFromPersistedContracts()
        {
            // Post-restart scenario: Agency A's claim persisted to disk; the boot path
            // (AgencySystem.LoadExistingAgencies) calls PreSeedClaimsFromAgencyState
            // for each loaded agency so the in-memory claim map matches disk reality.
            // Without this, Agency B reconnects first after a restart and could win
            // the in-memory race despite A's on-disk claim.
            var agencyId = Guid.NewGuid();
            var contractGuid1 = Guid.NewGuid();
            var contractGuid2 = Guid.NewGuid();
            var agency = new AgencyState { AgencyId = agencyId };
            agency.Contracts.Add(new AgencyContractEntry { ContractGuid = contractGuid1, State = "Active" });
            agency.Contracts.Add(new AgencyContractEntry { ContractGuid = contractGuid2, State = "Completed" });

            AgencyContractRouter.PreSeedClaimsFromAgencyState(agencyId, agency);

            Assert.AreEqual(2, AgencyContractRouter.ClaimedContractsCount);
            Assert.IsTrue(AgencyContractRouter.TryGetContractClaimant(contractGuid1, out var c1));
            Assert.IsTrue(AgencyContractRouter.TryGetContractClaimant(contractGuid2, out var c2));
            Assert.AreEqual(agencyId, c1);
            Assert.AreEqual(agencyId, c2);
        }

        [TestMethod]
        public void PreSeedClaimsFromAgencyState_NullAgency_NoOp()
        {
            // Defensive: heal-on-bak paths can pass a null state; pre-seed must not NRE.
            AgencyContractRouter.PreSeedClaimsFromAgencyState(Guid.NewGuid(), null);
            Assert.AreEqual(0, AgencyContractRouter.ClaimedContractsCount);
        }

        [TestMethod]
        public void PreSeedClaimsFromAgencyState_EmptyContractsList_NoOp()
        {
            // A freshly-minted agency has no Contracts yet; pre-seed must not error.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            AgencyContractRouter.PreSeedClaimsFromAgencyState(agency.AgencyId, agency);
            Assert.AreEqual(0, AgencyContractRouter.ClaimedContractsCount);
        }

        [TestMethod]
        public void PreSeedClaimsFromAgencyState_PreserveExistingClaimOnCollision()
        {
            // Edge case: two agencies on disk somehow claim the same guid (an already-
            // broken pre-5.18g universe). Pre-seed uses TryAdd so the FIRST agency
            // loaded wins; subsequent calls leave the existing claim alone. The
            // operator must clean up via /setvesselagency / /transferagency; we
            // preserve pre-5.18g state rather than silently overwriting.
            var contractGuid = Guid.NewGuid();
            var agencyA = Guid.NewGuid();
            var agencyB = Guid.NewGuid();

            var stateA = new AgencyState { AgencyId = agencyA };
            stateA.Contracts.Add(new AgencyContractEntry { ContractGuid = contractGuid, State = "Active" });
            var stateB = new AgencyState { AgencyId = agencyB };
            stateB.Contracts.Add(new AgencyContractEntry { ContractGuid = contractGuid, State = "Active" });

            AgencyContractRouter.PreSeedClaimsFromAgencyState(agencyA, stateA);
            AgencyContractRouter.PreSeedClaimsFromAgencyState(agencyB, stateB);

            Assert.IsTrue(AgencyContractRouter.TryGetContractClaimant(contractGuid, out var owner));
            Assert.AreEqual(agencyA, owner, "First pre-seed wins; the duplicate state on disk is preserved as-is.");
        }

        [TestMethod]
        public void ResetClaimedContracts_ClearsMap()
        {
            // Harness reset hook (ServerHarness.ResetPerTestState) and any future
            // operator-driven debug command relies on this clearing the entire map.
            AgencyContractRouter.TryClaimContract(Guid.NewGuid(), Guid.NewGuid());
            AgencyContractRouter.TryClaimContract(Guid.NewGuid(), Guid.NewGuid());
            Assert.AreEqual(2, AgencyContractRouter.ClaimedContractsCount);

            AgencyContractRouter.ResetClaimedContracts();

            Assert.AreEqual(0, AgencyContractRouter.ClaimedContractsCount);
        }

        [TestMethod]
        public void EvictClaimsForAgency_RemovesOnlyTargetAgencyEntries()
        {
            // /deleteagency follow-up (multi-lens consumer + integration finding):
            // when an agency is deleted, the in-memory claim map must release the
            // guids that agency held — otherwise the deleted agency's id continues
            // to "win" future claims for those guids forever (blocking re-Accepts
            // by a fresh agency under the same player). Foreign-agency claims must
            // be untouched.
            var agencyA = Guid.NewGuid();
            var agencyB = Guid.NewGuid();
            var aliceContract1 = Guid.NewGuid();
            var aliceContract2 = Guid.NewGuid();
            var bobContract = Guid.NewGuid();

            AgencyContractRouter.TryClaimContract(aliceContract1, agencyA);
            AgencyContractRouter.TryClaimContract(aliceContract2, agencyA);
            AgencyContractRouter.TryClaimContract(bobContract, agencyB);
            Assert.AreEqual(3, AgencyContractRouter.ClaimedContractsCount);

            var evicted = AgencyContractRouter.EvictClaimsForAgency(agencyA);

            Assert.AreEqual(2, evicted, "Should report exactly the count of agencyA's evicted claims.");
            Assert.AreEqual(1, AgencyContractRouter.ClaimedContractsCount,
                "agencyB's claim must survive the eviction.");
            Assert.IsFalse(AgencyContractRouter.TryGetContractClaimant(aliceContract1, out _));
            Assert.IsFalse(AgencyContractRouter.TryGetContractClaimant(aliceContract2, out _));
            Assert.IsTrue(AgencyContractRouter.TryGetContractClaimant(bobContract, out var bobClaimant));
            Assert.AreEqual(agencyB, bobClaimant);
        }

        [TestMethod]
        public void EvictClaimsForAgency_NoMatchingEntries_ReportsZero()
        {
            // Defensive: deleting an agency that never Accepted anything (or whose
            // claims were already evicted) must not throw or report negative.
            AgencyContractRouter.TryClaimContract(Guid.NewGuid(), Guid.NewGuid());

            var evicted = AgencyContractRouter.EvictClaimsForAgency(Guid.NewGuid());

            Assert.AreEqual(0, evicted);
            Assert.AreEqual(1, AgencyContractRouter.ClaimedContractsCount,
                "Eviction of an unknown agency id must not affect existing claims.");
        }

        [TestMethod]
        public void EvictClaimsForAgency_AfterEvict_GuidCanBeReclaimedByDifferentAgency()
        {
            // The post-eviction recovery path: after Agency A is deleted and its
            // claim on contract X is evicted, a fresh Agency Z minted under the
            // same player can re-Accept contract X. Without EvictClaimsForAgency
            // this would silently drop.
            var agencyA = Guid.NewGuid();
            var agencyZ = Guid.NewGuid();
            var contractGuid = Guid.NewGuid();

            AgencyContractRouter.TryClaimContract(contractGuid, agencyA);
            AgencyContractRouter.EvictClaimsForAgency(agencyA);

            var newOwner = AgencyContractRouter.TryClaimContract(contractGuid, agencyZ);

            Assert.AreEqual(agencyZ, newOwner,
                "After eviction, the guid must be claimable by a different agency (closes the deleted-agency reuse-path).");
        }
    }
}
