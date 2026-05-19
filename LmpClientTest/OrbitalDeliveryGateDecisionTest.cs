using LmpClient.Systems.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LmpClientTest
{
    /// <summary>
    /// [Phase 3 Slice D-2] Pins every branch of
    /// <see cref="OrbitalDeliveryGate.ShouldExecuteDelivery"/> + the
    /// passthrough rules at the top of the method.
    ///
    /// <para>The helper closes the per-frame double-spend hazard described in
    /// the Phase 3 pre-spec §1.c. Under pre-Phase-3 baseline every peer in
    /// physics range of a delivery destination would mutate the destination's
    /// resource amounts in lockstep — multi-applying the delivery. This
    /// helper is the gate.</para>
    ///
    /// <para><b>Decision-table coverage (pre-spec §2.d, revised per
    /// 1-player-per-agency invariant).</b> The full table has 9 rows under
    /// active status + passthrough rules at top. We pin every row + the
    /// non-active passthrough + the destination-Empty bypass + the
    /// null-callable defensive guard. CLAUDE.md "pure-helper extraction" note
    /// guides "one case per AND-chain guard" — see the per-test commentary
    /// for the specific guard each case exercises.</para>
    /// </summary>
    [TestClass]
    public class OrbitalDeliveryGateDecisionTest
    {
        private const string LocalPlayer = "Alice";
        private const string RemotePlayer = "Bob";

        private static readonly Guid VesselId = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid LocalAgency = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid RemoteAgency = new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        private static Func<Guid, Guid?> OwnedBy(Guid agency) => _ => agency;
        private static Func<Guid, Guid?> Unknown() => _ => null;
        private static Func<Guid, string> LockedBy(string player) => _ => player;
        private static Func<Guid, string> NoLock() => _ => string.Empty;
        private static Func<Guid, string> NullLock() => _ => null;

        // ---------- Passthrough rules ----------

        [TestMethod]
        public void Status_NotLaunchedOrReturning_Passthrough()
        {
            // The helper takes isActiveStatus=false for any non-Launched/
            // non-Returning value. Stock Deliver's own failure paths
            // (Failed, Cancelled, Delivered, Partial, PreLaunch) handle these
            // cleanly; pre-empting here would mask diagnostic Status writes.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: false,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(RemoteAgency),
                getUpdateLockOwner: LockedBy(RemotePlayer)));
        }

        [TestMethod]
        public void DestinationEmpty_Passthrough()
        {
            // ResolveDestinationVesselGuid returned Empty — vessel couldn't be
            // resolved. Pass through so stock Deliver's "destination no longer
            // exists" path fires with the correct status message.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: Guid.Empty,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: LockedBy(LocalPlayer)));
        }

        [TestMethod]
        public void NullCallables_Passthrough()
        {
            // Defensive null-callable guard. Production prefix always supplies
            // real callables; this protects test surfaces and a hypothetical
            // future caller that wired the helper into a non-prefix path.
            // Don't false-skip when the resolver path itself is broken — let
            // stock Deliver run; it has its own destination-null guard.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: null,
                getUpdateLockOwner: LockedBy(LocalPlayer)));
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: null));
        }

        // ---------- Gate OFF rows ----------

        [TestMethod]
        public void GateOff_LockHolderLocal_Execute()
        {
            // Pre-spec §2.d row 1: gate=off + local lock holder → Execute.
            // The lock-holder check is the sole gate=off authority; KSP's
            // single-Control-per-vessel ensures one elected executor.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: false,
                localPlayerName: LocalPlayer,
                localAgencyId: Guid.Empty, // gate=off — agency irrelevant
                getOwningAgency: OwnedBy(LocalAgency), // ignored under gate=off
                getUpdateLockOwner: LockedBy(LocalPlayer)));
        }

        [TestMethod]
        public void GateOff_LockHolderRemote_Skip()
        {
            // Pre-spec §2.d row 2: gate=off + remote lock holder → Skip.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: false,
                localPlayerName: LocalPlayer,
                localAgencyId: Guid.Empty,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: LockedBy(RemotePlayer)));
        }

        [TestMethod]
        public void GateOff_LockHolderEmpty_Skip()
        {
            // Pre-spec §2.d row 2 sub-case: empty lock owner is transient
            // post-unload state. Never assume go-ahead — wait for an explicit
            // lock-acquire round-trip.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: false,
                localPlayerName: LocalPlayer,
                localAgencyId: Guid.Empty,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: NoLock()));
        }

        [TestMethod]
        public void GateOff_LockHolderNull_Skip()
        {
            // The XML on getUpdateLockOwner says "Returns null/empty when no
            // lock holder exists" — both forms are part of the production
            // lock-query contract. The helper uses string.IsNullOrEmpty to
            // collapse them; if a future refactor swapped to a "lockOwner ==
            // \"\"" check it would silently pass NoLock() but skip null. Pin
            // both branches.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: false,
                localPlayerName: LocalPlayer,
                localAgencyId: Guid.Empty,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: NullLock()));
        }

        // ---------- Gate ON rows ----------

        [TestMethod]
        public void GateOn_SameAgency_LockHolderLocal_Execute()
        {
            // Pre-spec §2.d row 3: gate=on + same agency + local lock holder
            // → Execute. The primary authority is the agency match; the lock
            // check is redundant under 1:1 but evaluated uniformly.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: LockedBy(LocalPlayer)));
        }

        [TestMethod]
        public void GateOn_SameAgency_LockHolderRemote_Skip()
        {
            // Pre-spec §2.d row 4: gate=on + same agency + non-local lock
            // holder → Skip (defensive). Under 1:1 the LockSystem 5.17a guard
            // rejects cross-agency lock acquires, so any non-local holder
            // under same-agency is transient post-unload or pre-acquire — not
            // a legitimate cross-player execution.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: LockedBy(RemotePlayer)));
        }

        [TestMethod]
        public void GateOn_SameAgency_LockHolderEmpty_Skip()
        {
            // Pre-spec §2.d row 4 sub-case: "other / empty" lock holder under
            // same-agency. The transient post-unload window (no lock holder
            // yet — vessel just left physics range and the next acquire
            // hasn't completed) must NOT auto-execute even though the agency
            // matches. The defensive lock-holder check is what catches this.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(LocalAgency),
                getUpdateLockOwner: NoLock()));
        }

        [TestMethod]
        public void GateOn_DifferentAgency_Skip()
        {
            // Pre-spec §2.d row 5: gate=on + cross-agency (non-Empty) → Skip
            // unconditionally. Local lock holder OR not — the owning agency's
            // player executes. Tests with local lock-holder to confirm the
            // agency check overrides the lock check.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(RemoteAgency),
                getUpdateLockOwner: LockedBy(LocalPlayer)));
        }

        [TestMethod]
        public void GateOn_UnassignedSentinel_LockHolderLocal_Execute()
        {
            // Pre-spec §2.d row 6: gate=on + destination's agency is
            // Guid.Empty (Unassigned sentinel per spec §10 Q3) + local lock
            // holder → Execute. Tie-break by lock when agency cannot decide.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(Guid.Empty),
                getUpdateLockOwner: LockedBy(LocalPlayer)));
        }

        [TestMethod]
        public void GateOn_UnassignedSentinel_LockHolderRemote_Skip()
        {
            // Pre-spec §2.d row 7: gate=on + Unassigned + remote lock holder
            // → Skip. Defer to the remote peer holding the Update lock.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: OwnedBy(Guid.Empty),
                getUpdateLockOwner: LockedBy(RemotePlayer)));
        }

        [TestMethod]
        public void GateOn_DestAgencyUnknown_LockHolderLocal_Execute()
        {
            // Pre-spec §2.d row 8: gate=on + getOwningAgency returns null
            // (5.18a mirror not yet populated — connect-race window) + local
            // lock holder → Execute. Defensive bypass on the agency check;
            // same shape as LockSystem.cs:83-86's unmapped-requester branch.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: Unknown(),
                getUpdateLockOwner: LockedBy(LocalPlayer)));
        }

        [TestMethod]
        public void GateOn_DestAgencyUnknown_LockHolderRemote_Skip()
        {
            // Pre-spec §2.d row 9: gate=on + getOwningAgency returns null +
            // remote lock holder → Skip. Defensive bypass falls back to
            // lock-holder authority, which is non-local here.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: LocalAgency,
                getOwningAgency: Unknown(),
                getUpdateLockOwner: LockedBy(RemotePlayer)));
        }

        [TestMethod]
        public void GateOn_LocalAgencyUnknown_LockHolderLocal_Execute()
        {
            // Connect-race extension: gate=on + LOCAL agency is Guid.Empty
            // (5.18a Handshake not yet processed) + destination has a real
            // agency + local lock holder → Execute. Blanket-skip here would
            // falsely flag the local player as a delegating peer for one or
            // more frames during reconnect to a vessel they already own.
            // Defer to lock-holder check, same shape as the destAgency=null
            // branch.
            Assert.IsTrue(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: Guid.Empty,
                getOwningAgency: OwnedBy(RemoteAgency),
                getUpdateLockOwner: LockedBy(LocalPlayer)));
        }

        [TestMethod]
        public void GateOn_LocalAgencyUnknown_LockHolderRemote_Skip()
        {
            // Symmetric: gate=on + local agency unknown + remote lock holder
            // → Skip.
            Assert.IsFalse(OrbitalDeliveryGate.ShouldExecuteDelivery(
                destinationVesselId: VesselId,
                isActiveStatus: true,
                perAgencyEnabled: true,
                localPlayerName: LocalPlayer,
                localAgencyId: Guid.Empty,
                getOwningAgency: OwnedBy(RemoteAgency),
                getUpdateLockOwner: LockedBy(RemotePlayer)));
        }

        // ---------- ResolveDestinationVesselGuid ----------

        [TestMethod]
        public void Resolve_NullResolver_Empty()
        {
            // Defensive null-callable. Production call site always supplies a
            // closure; protects test surfaces and a hypothetical future
            // misuse.
            Assert.AreEqual(Guid.Empty, OrbitalDeliveryGate.ResolveDestinationVesselGuid(null));
        }

        [TestMethod]
        public void Resolve_ResolverThrows_Empty()
        {
            // KSP's FlightGlobals.Vessels enumeration can throw during scene
            // transition. The prefix runs in a FixedUpdate-driven coroutine
            // and must not propagate KSP-internal exceptions — return Empty
            // and let stock Deliver's destination-null path handle the
            // diagnostic Status write.
            Assert.AreEqual(Guid.Empty, OrbitalDeliveryGate.ResolveDestinationVesselGuid(
                () => throw new InvalidOperationException("KSP scene transition")));
        }

        [TestMethod]
        public void Resolve_ResolverReturnsValue_PassesThrough()
        {
            var expected = new Guid("33333333-3333-3333-3333-333333333333");
            Assert.AreEqual(expected, OrbitalDeliveryGate.ResolveDestinationVesselGuid(() => expected));
        }
    }
}
