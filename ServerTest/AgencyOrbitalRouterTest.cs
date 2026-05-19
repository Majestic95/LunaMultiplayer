using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;

namespace ServerTest
{
    /// <summary>
    /// Phase 3 Slice D-1 — unit tests for the deterministic core of
    /// <see cref="AgencyOrbitalRouter"/>: the <c>Upsert</c> helper + the
    /// <c>TryRoute</c> early-return branches. Same shape as the Slice B
    /// <see cref="AgencyKolonyRouterTest"/> + Slice C
    /// <see cref="AgencyPlanetaryRouterTest"/>. End-to-end wire coverage
    /// (gate-on cross-agency privacy + Deliver-prefix decision) lives in
    /// <c>MockClientTest/AgencyOrbitalRoutingTest.cs</c> + <c>LmpClientTest/OrbitalDeliveryGateDecisionTest.cs</c> —
    /// both deferred to Slice D-2.
    ///
    /// <para>Test pattern mirrors <see cref="AgencyPlanetaryRouterTest"/>:
    /// early-return branches pass <c>client: null</c>; reach into the
    /// <c>internal</c> <c>Upsert</c> helper directly for the storage shape
    /// tests. Differences from Slice B/C:</para>
    /// <list type="bullet">
    ///   <item><b>Direct Guid key</b> — <see cref="AgencyOrbitalTransferEntry.TransferGuid"/>
    ///        with no string composition (distinct from kolony's
    ///        <c>$"{vesselId:N}|{bodyIndex}"</c> and planetary's
    ///        <c>$"{bodyIndex}|{resourceName}"</c>).</item>
    ///   <item><b>Defensive copy test</b> unique to orbital: mutating the
    ///        source <see cref="AgencyOrbitalTransferEntry.PayloadBytes"/>
    ///        after <c>Upsert</c> must NOT affect the stored entry's
    ///        bytes. Slice B/C entries have no mutable byte[] fields so
    ///        the equivalent test would be vacuous.</item>
    ///   <item><b>Persistence round-trip includes PayloadBytes</b> —
    ///        Base64-roundtripped via <see cref="AgencyState.Serialize"/>
    ///        / <see cref="AgencyState.Parse"/>.</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class AgencyOrbitalRouterTest
    {
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();

        private AgencyState _agency;

        [TestInitialize]
        public void Setup()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();

            _agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "OrbitalAlice",
                DisplayName = "Orbital Alice Co",
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
        }

        // -------------------------------------------------------------------
        // Early-return branches — verify dual-mode silence + defensive null handling
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRoute_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, startTime: 1.0, duration: 60.0);

            var handled = AgencyOrbitalRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller doesn't suppress the legacy SHA pass.");
            Assert.AreEqual(0, _agency.OrbitalTransfers.Count,
                "Gate off must not mutate AgencyState.OrbitalTransfers");
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            // Spec §10 Q-Mode Career-only sign-off: Sandbox closes the gate even
            // with PerAgencyCareer=true.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 1);

            var handled = AgencyOrbitalRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void TryRoute_ScienceMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 1);

            var handled = AgencyOrbitalRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalse()
        {
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 1);

            var handled = AgencyOrbitalRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyOrbitalRouter.TryRoute(client: null, msg: null);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert helper — verify storage shape independent of the wire path
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_NewEntry_KeyedByTransferGuid()
        {
            // Distinct from Slice B/C: dict key IS the TransferGuid directly,
            // no string composition.
            var transferGuid = Guid.NewGuid();
            var entry = NewEntry(transferGuid, Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);

            AgencyOrbitalRouter.Upsert(_agency, entry);

            Assert.AreEqual(1, _agency.OrbitalTransfers.Count);
            Assert.IsTrue(_agency.OrbitalTransfers.ContainsKey(transferGuid),
                "Upsert must key by TransferGuid directly (no string composition).");
        }

        [TestMethod]
        public void Upsert_ExistingKey_ReplacesEntryInPlace_NotAppend()
        {
            // Same TransferGuid re-arrives with a state-machine transition
            // (Launched → Delivered, for example). The dict slot is updated
            // in place; we never double-store a transfer.
            var transferGuid = Guid.NewGuid();
            var origin = Guid.NewGuid();
            var destination = Guid.NewGuid();

            var first = NewEntry(transferGuid, origin, destination,
                AgencyOrbitalTransferEntry.StatusLaunched, startTime: 100, duration: 60);
            AgencyOrbitalRouter.Upsert(_agency, first);

            // State transition: Launched → Delivered.
            var second = NewEntry(transferGuid, origin, destination,
                AgencyOrbitalTransferEntry.StatusDelivered, startTime: 100, duration: 60);
            AgencyOrbitalRouter.Upsert(_agency, second);

            Assert.AreEqual(1, _agency.OrbitalTransfers.Count,
                "Upsert must not append on duplicate TransferGuid.");
            Assert.AreEqual(AgencyOrbitalTransferEntry.StatusDelivered,
                _agency.OrbitalTransfers[transferGuid].Status,
                "Upsert must overwrite the prior Status value (state-machine transition).");
        }

        [TestMethod]
        public void Upsert_DifferentTransferGuids_DistinctEntries()
        {
            // Two pending transfers from the same origin to the same destination
            // (different cargo, different transfer IDs) coexist as distinct
            // entries.
            var origin = Guid.NewGuid();
            var destination = Guid.NewGuid();

            AgencyOrbitalRouter.Upsert(_agency, NewEntry(Guid.NewGuid(), origin, destination,
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0));
            AgencyOrbitalRouter.Upsert(_agency, NewEntry(Guid.NewGuid(), origin, destination,
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0));

            Assert.AreEqual(2, _agency.OrbitalTransfers.Count,
                "Different TransferGuids must produce distinct dict entries.");
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            var entry = NewEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);
            Assert.ThrowsException<ArgumentNullException>(() => AgencyOrbitalRouter.Upsert(null, entry));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => AgencyOrbitalRouter.Upsert(_agency, null));
        }

        // -------------------------------------------------------------------
        // Defensive byte-copy contract — unique to orbital (PayloadBytes is
        // the first mutable byte[] in any Phase 3 entry per pre-spec §3.c)
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_DoesNotAliasPayloadBytes_StoredEntryRetainsCallerReference()
        {
            // Upsert by itself does NOT copy — it inserts the entry instance
            // the caller passed. The CALLER (TryRoute) is responsible for the
            // defensive Buffer.BlockCopy before invoking Upsert. This test
            // pins the Upsert contract: the dict holds whatever entry the
            // caller hands it, byte-aliased or not. The router's TryRoute
            // path additionally constructs a fresh entry per inbound message
            // with a freshly-allocated byte[] (validated by the e2e MockClient
            // test in D-2).
            var transferGuid = Guid.NewGuid();
            var srcBytes = new byte[] { 1, 2, 3, 4 };
            var entry = NewEntry(transferGuid, Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);
            entry.PayloadBytes = srcBytes;
            entry.NumBytes = srcBytes.Length;

            AgencyOrbitalRouter.Upsert(_agency, entry);

            Assert.AreSame(srcBytes, _agency.OrbitalTransfers[transferGuid].PayloadBytes,
                "Upsert preserves caller-supplied byte[] reference — the defensive copy is the caller's responsibility (TryRoute does it pre-Upsert).");
        }

        // -------------------------------------------------------------------
        // Persistence round-trip — pin the serialize/parse contract incl.
        // Base64-encoded PayloadBytes
        // -------------------------------------------------------------------

        [TestMethod]
        public void AgencyState_OrbitalTransfers_RoundTripPreservesAllFields()
        {
            // The 7+1-field entry round-trips through Serialize → Parse intact.
            // Locale stress on StartTime/Duration; Base64 round-trip on
            // PayloadBytes; opaque-int Status round-trip.
            var transferGuid = Guid.NewGuid();
            var origin = Guid.NewGuid();
            var destination = Guid.NewGuid();
            var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42, 0xFF };
            _agency.OrbitalTransfers[transferGuid] = new AgencyOrbitalTransferEntry
            {
                TransferGuid = transferGuid,
                OriginVesselId = origin,
                DestinationVesselId = destination,
                Status = AgencyOrbitalTransferEntry.StatusLaunched,
                StartTime = -12345.6789,
                Duration = 9876.5432,
                PayloadBytes = payload,
                NumBytes = payload.Length,
            };

            var roundTripped = AgencyState.Parse(_agency.Serialize());

            Assert.AreEqual(1, roundTripped.OrbitalTransfers.Count);
            Assert.IsTrue(roundTripped.OrbitalTransfers.ContainsKey(transferGuid));
            var rt = roundTripped.OrbitalTransfers[transferGuid];
            Assert.AreEqual(transferGuid, rt.TransferGuid);
            Assert.AreEqual(origin, rt.OriginVesselId);
            Assert.AreEqual(destination, rt.DestinationVesselId);
            Assert.AreEqual(AgencyOrbitalTransferEntry.StatusLaunched, rt.Status);
            Assert.AreEqual(-12345.6789, rt.StartTime);
            Assert.AreEqual(9876.5432, rt.Duration);
            Assert.AreEqual(payload.Length, rt.NumBytes);
            CollectionAssert.AreEqual(payload, rt.PayloadBytes,
                "PayloadBytes must round-trip through Base64 byte-for-byte.");
        }

        [TestMethod]
        public void AgencyState_OrbitalTransfers_EmptyPayloadRoundTripsCleanly()
        {
            // A transfer with NumBytes=0 / empty PayloadBytes round-trips
            // without throwing on Base64 decode. Operator hand-edited / Slice
            // E migration path could produce this.
            var transferGuid = Guid.NewGuid();
            _agency.OrbitalTransfers[transferGuid] = new AgencyOrbitalTransferEntry
            {
                TransferGuid = transferGuid,
                OriginVesselId = Guid.NewGuid(),
                DestinationVesselId = Guid.NewGuid(),
                Status = AgencyOrbitalTransferEntry.StatusReturning,
                StartTime = 0,
                Duration = 0,
                PayloadBytes = Array.Empty<byte>(),
                NumBytes = 0,
            };

            var roundTripped = AgencyState.Parse(_agency.Serialize());

            Assert.AreEqual(1, roundTripped.OrbitalTransfers.Count);
            Assert.AreEqual(0, roundTripped.OrbitalTransfers[transferGuid].NumBytes);
            Assert.IsNotNull(roundTripped.OrbitalTransfers[transferGuid].PayloadBytes);
            Assert.AreEqual(0, roundTripped.OrbitalTransfers[transferGuid].PayloadBytes.Length);
        }

        // -------------------------------------------------------------------
        // [Phase 3 Slice E-1] MigrateForVesselTransfer — value-field-scan
        // migration for the upcoming setvesselagency admin command (E-2).
        // Q1 KEEP: Origin-only matches stay in source agency; only
        // Destination matches migrate.
        // -------------------------------------------------------------------

        [TestMethod]
        public void Migrate_SourceEmpty_ReturnsEmptyResult()
        {
            var dest = NewSecondaryAgency("OrbitalBob");

            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, dest, Guid.NewGuid());

            Assert.AreEqual(0, result.RemovedTransferGuids.Count);
            Assert.AreEqual(0, result.AddedEntries.Count);
            Assert.AreEqual(0, result.OriginOnlyKeptCount);
        }

        [TestMethod]
        public void Migrate_DestinationMatch_MovesTransferToDestinationAgency()
        {
            var movedVesselId = Guid.NewGuid();
            var originVesselId = Guid.NewGuid();
            var transferGuid = Guid.NewGuid();
            _agency.OrbitalTransfers[transferGuid] = NewEntry(
                transferGuid, originVesselId, movedVesselId,
                AgencyOrbitalTransferEntry.StatusLaunched, 100.0, 200.0);
            var dest = NewSecondaryAgency("OrbitalBob");

            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(1, result.RemovedTransferGuids.Count);
            Assert.AreEqual(transferGuid, result.RemovedTransferGuids[0]);
            Assert.AreEqual(1, result.AddedEntries.Count);
            Assert.AreEqual(0, result.OriginOnlyKeptCount);
            Assert.AreEqual(0, _agency.OrbitalTransfers.Count);
            Assert.AreEqual(1, dest.OrbitalTransfers.Count);
            Assert.IsTrue(dest.OrbitalTransfers.ContainsKey(transferGuid));
        }

        [TestMethod]
        public void Migrate_OriginOnlyMatch_KeepsInSource_ReportsCountForLog()
        {
            // Q1 KEEP per operator session 29: Origin-only match (vessel is
            // the source of a shipped-out transfer) stays in source agency.
            // Source loses the vessel but retains the launch obligation.
            var movedVesselId = Guid.NewGuid();
            var destinationVesselId = Guid.NewGuid();
            var transferGuid = Guid.NewGuid();
            _agency.OrbitalTransfers[transferGuid] = NewEntry(
                transferGuid, movedVesselId, destinationVesselId,
                AgencyOrbitalTransferEntry.StatusLaunched, 100.0, 200.0);
            var dest = NewSecondaryAgency("OrbitalBob");

            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(0, result.RemovedTransferGuids.Count, "Origin-only match must NOT migrate (Q1 KEEP).");
            Assert.AreEqual(0, result.AddedEntries.Count);
            Assert.AreEqual(1, result.OriginOnlyKeptCount, "Caller diagnostic counts the Origin-only kept transfers for the operator log.");
            Assert.AreEqual(1, result.OriginOnlyKeptGuids.Count,
                "Per-guid list mirrors the count — operator log emits one line per kept obligation.");
            Assert.AreEqual(transferGuid, result.OriginOnlyKeptGuids[0],
                "OriginOnlyKeptGuids carries the actual transfer-guids for the operator audit trail.");
            Assert.AreEqual(1, _agency.OrbitalTransfers.Count, "Source agency keeps the in-flight obligation.");
            Assert.AreEqual(0, dest.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void Migrate_NeitherOriginNorDestination_NoAction()
        {
            // Source has a transfer between two other vessels; the moved
            // vessel touches neither.
            var unrelatedOrigin = Guid.NewGuid();
            var unrelatedDest = Guid.NewGuid();
            var transferGuid = Guid.NewGuid();
            _agency.OrbitalTransfers[transferGuid] = NewEntry(
                transferGuid, unrelatedOrigin, unrelatedDest,
                AgencyOrbitalTransferEntry.StatusLaunched, 100.0, 200.0);
            var dest = NewSecondaryAgency("OrbitalBob");

            var movedVesselId = Guid.NewGuid();
            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(0, result.RemovedTransferGuids.Count);
            Assert.AreEqual(0, result.OriginOnlyKeptCount);
            Assert.AreEqual(1, _agency.OrbitalTransfers.Count);
            Assert.AreEqual(0, dest.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void Migrate_SelfTransfer_OriginEqualsDestination_MovesViaDestinationPath()
        {
            // Self-delivery: a single vessel is both Origin AND Destination
            // (rare in MKS but valid for in-place resource flow). Per
            // pre-spec §4.e line 217-218, "prefer the Destination-match path
            // (deliverer authority)." The migration must MOVE (not double-
            // count or keep both branches).
            var movedVesselId = Guid.NewGuid();
            var transferGuid = Guid.NewGuid();
            _agency.OrbitalTransfers[transferGuid] = NewEntry(
                transferGuid, movedVesselId, movedVesselId,
                AgencyOrbitalTransferEntry.StatusLaunched, 100.0, 200.0);
            var dest = NewSecondaryAgency("OrbitalBob");

            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(1, result.RemovedTransferGuids.Count, "Self-transfer migrates via Destination-match path.");
            Assert.AreEqual(0, result.OriginOnlyKeptCount, "Self-transfer must NOT also be counted as Origin-keep.");
            Assert.AreEqual(0, _agency.OrbitalTransfers.Count);
            Assert.AreEqual(1, dest.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void Migrate_MixedTransfers_OnlyDestinationMatchesMigrate()
        {
            // Realistic scenario: source has three transfers — one where the
            // moved vessel is Destination (migrates), one where it's Origin
            // only (keeps), one unrelated (untouched).
            var movedVesselId = Guid.NewGuid();
            var t1 = Guid.NewGuid();  // moved-vessel is Destination → migrate
            var t2 = Guid.NewGuid();  // moved-vessel is Origin-only → keep
            var t3 = Guid.NewGuid();  // unrelated → untouched
            _agency.OrbitalTransfers[t1] = NewEntry(t1, Guid.NewGuid(), movedVesselId,
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);
            _agency.OrbitalTransfers[t2] = NewEntry(t2, movedVesselId, Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);
            _agency.OrbitalTransfers[t3] = NewEntry(t3, Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);
            var dest = NewSecondaryAgency("OrbitalBob");

            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(1, result.RemovedTransferGuids.Count);
            Assert.AreEqual(t1, result.RemovedTransferGuids[0]);
            Assert.AreEqual(1, result.OriginOnlyKeptCount);
            Assert.AreEqual(2, _agency.OrbitalTransfers.Count, "Origin-keep + unrelated stay in source.");
            Assert.IsTrue(_agency.OrbitalTransfers.ContainsKey(t2));
            Assert.IsTrue(_agency.OrbitalTransfers.ContainsKey(t3));
            Assert.AreEqual(1, dest.OrbitalTransfers.Count);
            Assert.IsTrue(dest.OrbitalTransfers.ContainsKey(t1));
        }

        [TestMethod]
        public void Migrate_MovedVesselIdEmpty_ReturnsEmpty()
        {
            var transferGuid = Guid.NewGuid();
            _agency.OrbitalTransfers[transferGuid] = NewEntry(
                transferGuid, Guid.NewGuid(), Guid.NewGuid(),
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);
            var dest = NewSecondaryAgency("OrbitalBob");

            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, dest, Guid.Empty);

            Assert.AreEqual(0, result.RemovedTransferGuids.Count);
            Assert.AreEqual(0, result.OriginOnlyKeptCount);
            Assert.AreEqual(1, _agency.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void Migrate_SameAgencyInstance_ReturnsEmptyWithoutMutation()
        {
            var transferGuid = Guid.NewGuid();
            var movedVesselId = Guid.NewGuid();
            _agency.OrbitalTransfers[transferGuid] = NewEntry(
                transferGuid, Guid.NewGuid(), movedVesselId,
                AgencyOrbitalTransferEntry.StatusLaunched, 0, 0);

            var result = AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, _agency, movedVesselId);

            Assert.AreEqual(0, result.RemovedTransferGuids.Count);
            Assert.AreEqual(1, _agency.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void Migrate_NullSourceOrDestination_ThrowsArgumentNull()
        {
            var dest = NewSecondaryAgency("OrbitalBob");
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyOrbitalRouter.MigrateForVesselTransfer(null, dest, Guid.NewGuid()));
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyOrbitalRouter.MigrateForVesselTransfer(_agency, null, Guid.NewGuid()));
        }

        private static AgencyState NewSecondaryAgency(string ownerName)
        {
            var dest = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = ownerName,
                DisplayName = ownerName + " Co",
            };
            AgencySystem.Agencies[dest.AgencyId] = dest;
            AgencySystem.AgencyByPlayerName[dest.OwningPlayerName] = dest.AgencyId;
            return dest;
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyOrbitalTransferEntry NewEntry(Guid transferGuid, Guid origin, Guid destination,
            int status, double startTime, double duration)
        {
            return new AgencyOrbitalTransferEntry
            {
                TransferGuid = transferGuid,
                OriginVesselId = origin,
                DestinationVesselId = destination,
                Status = status,
                StartTime = startTime,
                Duration = duration,
                PayloadBytes = Array.Empty<byte>(),
                NumBytes = 0,
            };
        }

        private static AgencyOrbitalStateMsgData BuildSingleEntryMsg(Guid transferGuid, Guid origin, Guid destination,
            int status, double startTime, double duration)
        {
            var msg = ClientFactory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msg.AgencyId = Guid.Empty;
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyOrbitalTransferEntry
                {
                    TransferGuid = transferGuid,
                    OriginVesselId = origin,
                    DestinationVesselId = destination,
                    Status = status,
                    StartTime = startTime,
                    Duration = duration,
                    PayloadBytes = Array.Empty<byte>(),
                    NumBytes = 0,
                }
            };
            return msg;
        }
    }
}
