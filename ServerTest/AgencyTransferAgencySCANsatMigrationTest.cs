using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;
using System.Collections.Generic;

namespace ServerTest
{
    /// <summary>
    /// [Mod-compat S2 — Decision §3 + D3] Unit tests for
    /// <see cref="AgencyScanRouter.MigrateForVesselTransfer"/>. Mirrors the
    /// kolony precedent (<see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/>):
    /// vessel-keyed migration A→B; per-body Coverage stays put (body-keyed,
    /// agency-scoped). Single-entry semantics — at most one record moves
    /// per call.
    ///
    /// <para>End-to-end orchestration through
    /// <c>SetVesselAgencyCommand</c> is covered in
    /// <c>SetVesselAgencyCommandTest</c> (alongside the kolony / orbital /
    /// planetary migration calls); these tests pin the helper's own
    /// migration semantics directly.</para>
    /// </summary>
    [TestClass]
    public class AgencyTransferAgencySCANsatMigrationTest
    {
        [TestMethod]
        public void Migrate_VesselWithMultiSensorRecord_MovesEntryIntact()
        {
            var source = new AgencyState { AgencyId = Guid.NewGuid() };
            var dest = new AgencyState { AgencyId = Guid.NewGuid() };
            var movedVessel = Guid.NewGuid();
            source.Scanners[movedVessel] = new AgencyScannerEntry
            {
                VesselId = movedVessel,
                VesselName = "MultiScout",
                Sensors = new List<AgencyScannerSensorRecord>
                {
                    new AgencyScannerSensorRecord { SensorType = 16, Fov = 3f, MinAlt = 1000, MaxAlt = 100000, BestAlt = 50000 },
                    new AgencyScannerSensorRecord { SensorType = 8,  Fov = 1f, MinAlt = 0,    MaxAlt = 1000,   BestAlt = 500 },
                },
            };

            var result = AgencyScanRouter.MigrateForVesselTransfer(source, dest, movedVessel);

            Assert.AreEqual(movedVessel, result.RemovedVesselId, "Result must reflect the moved vessel id");
            Assert.IsNotNull(result.AddedEntry);
            Assert.AreEqual(2, result.AddedEntry.Sensors.Count, "Nested Sensor list preserved through migration");

            Assert.IsFalse(source.Scanners.ContainsKey(movedVessel),
                "Source must no longer contain the moved entry");
            Assert.IsTrue(dest.Scanners.TryGetValue(movedVessel, out var destEntry),
                "Destination must contain the migrated entry");
            Assert.AreEqual(2, destEntry.Sensors.Count);
            Assert.AreEqual("MultiScout", destEntry.VesselName);
        }

        [TestMethod]
        public void Migrate_VesselWithNoScannerRecord_NoOp()
        {
            var source = new AgencyState { AgencyId = Guid.NewGuid() };
            var dest = new AgencyState { AgencyId = Guid.NewGuid() };
            var unknownVessel = Guid.NewGuid();

            var result = AgencyScanRouter.MigrateForVesselTransfer(source, dest, unknownVessel);

            Assert.AreEqual(Guid.Empty, result.RemovedVesselId, "No-migration sentinel is Empty");
            Assert.IsNull(result.AddedEntry);
            Assert.AreEqual(0, source.Scanners.Count);
            Assert.AreEqual(0, dest.Scanners.Count);
        }

        [TestMethod]
        public void Migrate_DestinationCollision_SourceWinsWithWarning()
        {
            // Defensive: by construction a vessel only belongs to one agency at
            // a time so destination can't already hold the moved key. If it
            // does (operator hand-edit / failed prior migration), source's
            // entry overwrites — more recent.
            var source = new AgencyState { AgencyId = Guid.NewGuid() };
            var dest = new AgencyState { AgencyId = Guid.NewGuid() };
            var movedVessel = Guid.NewGuid();
            source.Scanners[movedVessel] = new AgencyScannerEntry
            {
                VesselId = movedVessel,
                VesselName = "Source-version",
                Sensors = new List<AgencyScannerSensorRecord> { new AgencyScannerSensorRecord { SensorType = 16 } },
            };
            dest.Scanners[movedVessel] = new AgencyScannerEntry
            {
                VesselId = movedVessel,
                VesselName = "Stale-dest-version",
                Sensors = new List<AgencyScannerSensorRecord>(),
            };

            AgencyScanRouter.MigrateForVesselTransfer(source, dest, movedVessel);

            Assert.IsTrue(dest.Scanners.TryGetValue(movedVessel, out var entry));
            Assert.AreEqual("Source-version", entry.VesselName,
                "Collision policy: source-wins (more recent)");
            Assert.AreEqual(1, entry.Sensors.Count);
        }

        [TestMethod]
        public void Migrate_CoverageNotTouched()
        {
            // Decision §3 — per-body Coverage stays put (body-keyed,
            // agency-scoped). A's discoveries of Eve stay A's; B retains B's.
            var source = new AgencyState { AgencyId = Guid.NewGuid() };
            var dest = new AgencyState { AgencyId = Guid.NewGuid() };
            var movedVessel = Guid.NewGuid();
            source.Coverage["Eve"] = new AgencyCoverageBodyEntry { BodyName = "Eve", Map = "A_EVE" };
            dest.Coverage["Eve"] = new AgencyCoverageBodyEntry { BodyName = "Eve", Map = "B_EVE" };
            source.Scanners[movedVessel] = new AgencyScannerEntry { VesselId = movedVessel, Sensors = new List<AgencyScannerSensorRecord>() };

            AgencyScanRouter.MigrateForVesselTransfer(source, dest, movedVessel);

            Assert.AreEqual(1, source.Coverage.Count, "Source Coverage untouched");
            Assert.AreEqual("A_EVE", source.Coverage["Eve"].Map);
            Assert.AreEqual(1, dest.Coverage.Count, "Destination Coverage untouched");
            Assert.AreEqual("B_EVE", dest.Coverage["Eve"].Map);
        }

        [TestMethod]
        public void Migrate_SameAgency_NoOp()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var vesselId = Guid.NewGuid();
            agency.Scanners[vesselId] = new AgencyScannerEntry { VesselId = vesselId, Sensors = new List<AgencyScannerSensorRecord>() };

            var result = AgencyScanRouter.MigrateForVesselTransfer(agency, agency, vesselId);

            Assert.AreEqual(Guid.Empty, result.RemovedVesselId);
            Assert.AreEqual(1, agency.Scanners.Count, "Same-agency migration is a no-op (defensive)");
        }

        [TestMethod]
        public void Migrate_EmptyVesselId_NoOp()
        {
            var source = new AgencyState { AgencyId = Guid.NewGuid() };
            var dest = new AgencyState { AgencyId = Guid.NewGuid() };

            var result = AgencyScanRouter.MigrateForVesselTransfer(source, dest, Guid.Empty);

            Assert.AreEqual(Guid.Empty, result.RemovedVesselId);
            Assert.IsNull(result.AddedEntry);
        }
    }
}
