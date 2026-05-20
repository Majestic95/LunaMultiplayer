using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;
using System.Collections.Generic;

namespace ServerTest
{
    /// <summary>
    /// Phase 4 Slice E — unit tests for the deterministic core of
    /// <see cref="AgencyWolfCrewRouter"/>: the <c>Upsert</c> helper + the
    /// <c>TryRoute</c> early-return branches (gate-off, Sandbox, null
    /// client, null msg) + per-entry isolation + cross-agency-kerbal-
    /// reject (the distinctive Phase 4 surface). The full <c>TryRoute</c>
    /// path (live <see cref="Server.Client.ClientStructure"/> +
    /// <see cref="Server.Server.MessageQueuer"/> echo) gets end-to-end
    /// coverage in <c>MockClientTest.WolfCrewRouteRoutingTest</c> when it
    /// lands.
    ///
    /// <para>Test pattern mirrors <see cref="AgencyWolfHopperRouterTest"/>
    /// with the kerbal-authority gate as the extra surface. The
    /// <c>BuildKerbalAgencyMap</c> + production
    /// <see cref="Server.System.Vessel.VesselStoreSystem"/> dependency are
    /// kept at one remove: tests exercise the gate via
    /// <see cref="AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger"/>
    /// directly with a canned map, leaving the production
    /// <c>BuildKerbalAgencyMap</c> path for MockClientTest e2e
    /// coverage.</para>
    /// </summary>
    [TestClass]
    public class AgencyWolfCrewRouteRouterTest
    {
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();

        private AgencyState _agency;
        private Guid _bobAgencyId;

        [TestInitialize]
        public void Setup()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();

            _agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "WolfCrewAlice",
                DisplayName = "Wolf Alice Crew Co",
            };
            AgencySystem.Agencies[_agency.AgencyId] = _agency;
            AgencySystem.AgencyByPlayerName[_agency.OwningPlayerName] = _agency.AgencyId;

            // Bob exists as a second agency for cross-agency reject tests.
            _bobAgencyId = Guid.NewGuid();
            AgencySystem.Agencies[_bobAgencyId] = new AgencyState
            {
                AgencyId = _bobAgencyId,
                OwningPlayerName = "WolfCrewBob",
                DisplayName = "Wolf Bob Logistics",
            };
            AgencySystem.AgencyByPlayerName["WolfCrewBob"] = _bobAgencyId;
        }

        [TestCleanup]
        public void Teardown()
        {
            AgencySystem.Reset();
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
        }

        // -------------------------------------------------------------------
        // Early-return branches — dual-mode silence + defensive null handling
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRoute_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString("N"), "Duna", "Lowlands", "Mun", "Highlands");

            var handled = AgencyWolfCrewRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller doesn't suppress the legacy SHA pass.");
            Assert.AreEqual(0, _agency.WolfCrewRoutes.Count);
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString("N"), "Duna", "Lowlands", "Mun", "Highlands");

            var handled = AgencyWolfCrewRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfCrewRoutes.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalseWithoutMutating()
        {
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString("N"), "Duna", "Lowlands", "Mun", "Highlands");

            var handled = AgencyWolfCrewRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfCrewRoutes.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyWolfCrewRouter.TryRoute(client: null, msg: null);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert storage shape — partition key + nested passenger preservation
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_SingleEntry_StoresUnderUniqueIdKey_InNForm()
        {
            // UniqueId is Guid.NewGuid().ToString("N") per
            // CrewRoute.cs:90 — verify "N" form is preserved (32 hex
            // chars, no hyphens — distinct from Hoppers' with-hyphens
            // ToString() form).
            var id = Guid.NewGuid().ToString("N");
            Assert.AreEqual(32, id.Length);
            Assert.IsFalse(id.Contains("-"), "Test pre-condition: ToString(\"N\") form must NOT contain hyphens.");

            AgencyWolfCrewRouter.Upsert(_agency, new AgencyWolfCrewRouteEntry
            {
                UniqueId = id,
                OriginBody = "Kerbin",
                OriginBiome = "KSC",
                DestinationBody = "Mun",
                DestinationBiome = "MidlandCraters",
                FlightNumber = "3K7",
                FlightStatus = "Boarding",
                ArrivalTime = 12345.6789,
                Duration = 360.0,
                EconomyBerths = 4,
                LuxuryBerths = 1,
            });

            Assert.AreEqual(1, _agency.WolfCrewRoutes.Count);
            Assert.IsTrue(_agency.WolfCrewRoutes.TryGetValue(id, out var stored));
            Assert.AreEqual(id, stored.UniqueId, "UniqueId stored verbatim — no normalization.");
            Assert.AreEqual("Kerbin", stored.OriginBody);
            Assert.AreEqual("MidlandCraters", stored.DestinationBiome);
            Assert.AreEqual("3K7", stored.FlightNumber);
            Assert.AreEqual("Boarding", stored.FlightStatus);
            Assert.AreEqual(12345.6789, stored.ArrivalTime, 0.00001);
            Assert.AreEqual(360.0, stored.Duration, 0.00001);
            Assert.AreEqual(4, stored.EconomyBerths);
            Assert.AreEqual(1, stored.LuxuryBerths);
        }

        [TestMethod]
        public void Upsert_RepeatedSameId_LastWriterWins_FlightStatusTransitionPreserved()
        {
            var id = Guid.NewGuid().ToString("N");

            // First: Boarding state with empty passengers.
            AgencyWolfCrewRouter.Upsert(_agency, new AgencyWolfCrewRouteEntry
            {
                UniqueId = id,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Boarding",
                ArrivalTime = 0d,
                Duration = 360.0,
                EconomyBerths = 4, LuxuryBerths = 1,
            });

            // Second: Launch transition — FlightStatus moves to Enroute,
            // ArrivalTime is set. Passengers list now populated. This is
            // the WOLF Launch postfix's expected wire shape.
            AgencyWolfCrewRouter.Upsert(_agency, new AgencyWolfCrewRouteEntry
            {
                UniqueId = id,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Enroute",
                ArrivalTime = 99999.0,
                Duration = 360.0,
                EconomyBerths = 4, LuxuryBerths = 1,
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Jebediah Kerman", DisplayName = "Jeb", IsTourist = false, Occupation = "Pilot", Stars = 5 },
                },
            });

            Assert.AreEqual(1, _agency.WolfCrewRoutes.Count);
            var stored = _agency.WolfCrewRoutes[id];
            Assert.AreEqual("Enroute", stored.FlightStatus, "Second write must replace first — matches WOLF Boarding→Enroute transition.");
            Assert.AreEqual(99999.0, stored.ArrivalTime, 0.00001);
            Assert.AreEqual(1, stored.Passengers.Count);
            Assert.AreEqual("Jebediah Kerman", stored.Passengers[0].Name);
            Assert.AreEqual(5, stored.Passengers[0].Stars);
        }

        [TestMethod]
        public void Upsert_DefensiveCopy_StoredEntryIsolatedFromWireMutation()
        {
            var id = Guid.NewGuid().ToString("N");
            var wirePassengers = new List<AgencyWolfPassengerEntry>
            {
                new AgencyWolfPassengerEntry { Name = "Jeb", DisplayName = "Jebediah Kerman", IsTourist = false, Occupation = "Pilot", Stars = 5 },
            };
            var wireEntry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = id,
                OriginBody = "Kerbin", OriginBiome = "KSC",
                DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                FlightStatus = "Boarding",
                Passengers = wirePassengers,
            };

            AgencyWolfCrewRouter.Upsert(_agency, wireEntry);

            var stored = _agency.WolfCrewRoutes[id];
            Assert.AreNotSame(wireEntry, stored, "Stored must be a defensive copy of the outer entry.");
            Assert.AreNotSame(wirePassengers, stored.Passengers, "Stored Passengers list must be a defensive copy.");
            if (wirePassengers.Count > 0 && stored.Passengers.Count > 0)
                Assert.AreNotSame(wirePassengers[0], stored.Passengers[0], "Stored Passenger entries must be defensive copies.");

            // Mutate wire after store — stored snapshot must NOT change.
            wireEntry.FlightStatus = "MUTATED";
            wireEntry.OriginBody = "MUTATED";
            wirePassengers.Add(new AgencyWolfPassengerEntry { Name = "MUTATED" });
            wirePassengers[0].Name = "MUTATED";

            Assert.AreEqual("Boarding", stored.FlightStatus, "Wire mutation must not bleed into stored snapshot.");
            Assert.AreEqual("Kerbin", stored.OriginBody);
            Assert.AreEqual(1, stored.Passengers.Count, "Adding to wire-side passenger list must not bleed into stored.");
            Assert.AreEqual("Jeb", stored.Passengers[0].Name);
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfCrewRouter.Upsert(agency: null, entry: new AgencyWolfCrewRouteEntry
                {
                    UniqueId = Guid.NewGuid().ToString("N"),
                    OriginBody = "Kerbin", OriginBiome = "KSC",
                    DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                }));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfCrewRouter.Upsert(_agency, entry: null));
        }

        [TestMethod]
        public void Upsert_EmptyUniqueId_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                AgencyWolfCrewRouter.Upsert(_agency, new AgencyWolfCrewRouteEntry
                {
                    UniqueId = string.Empty,
                    OriginBody = "Kerbin", OriginBiome = "KSC",
                    DestinationBody = "Mun", DestinationBiome = "MidlandCraters",
                }));
        }

        // -------------------------------------------------------------------
        // Cross-agency kerbal reject — Phase 4's distinctive surface
        // -------------------------------------------------------------------

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_SameAgencyKerbal_Accepts()
        {
            // Alice's kerbal aboard Alice's vessel — allowed.
            var map = new Dictionary<string, Guid>
            {
                { "Jebediah Kerman", _agency.AgencyId },
            };
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = "abc",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Jebediah Kerman" },
                },
            };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, map, out var offending, out var offAg);

            Assert.IsFalse(rejected, "Same-agency passenger must NOT be rejected.");
            Assert.IsNull(offending);
            Assert.AreEqual(Guid.Empty, offAg);
        }

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_CrossAgencyKerbal_RejectsWithDetails()
        {
            // Bill (Bob's kerbal) on Alice's wire entry — rejected.
            var map = new Dictionary<string, Guid>
            {
                { "Jebediah Kerman", _agency.AgencyId },
                { "Bill Kerman", _bobAgencyId },
            };
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = "abc",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Jebediah Kerman" },
                    new AgencyWolfPassengerEntry { Name = "Bill Kerman" },
                },
            };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, map, out var offending, out var offAg);

            Assert.IsTrue(rejected, "Cross-agency passenger must be rejected.");
            Assert.AreEqual("Bill Kerman", offending);
            Assert.AreEqual(_bobAgencyId, offAg);
        }

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_UnassignedSentinelVesselKerbal_Bypasses()
        {
            // Kerbal on a Guid.Empty (Unassigned-sentinel) vessel — spec §10 Q3
            // bypass: any agency may interact.
            var map = new Dictionary<string, Guid>
            {
                { "Sentinel Kerman", Guid.Empty },
            };
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = "abc",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Sentinel Kerman" },
                },
            };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, map, out _, out _);

            Assert.IsFalse(rejected, "Unassigned-sentinel-vessel kerbal must bypass cross-agency check per spec §10 Q3.");
        }

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_KerbalNotInAnyVessel_Bypasses()
        {
            // Kerbal not aboard any vessel (AC-pool / KIA / never-launched)
            // — bypass: any agency may interact (typical Embark workflow
            // starts here).
            var map = new Dictionary<string, Guid>(); // Empty: nobody is aboard any vessel.
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = "abc",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "ACPool Kerman" },
                },
            };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, map, out _, out _);

            Assert.IsFalse(rejected, "Kerbal not aboard any vessel must bypass — typical Embark workflow.");
        }

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_EmptyPassengerName_DefensiveBypass()
        {
            // Defensive fall-through for a malformed passenger entry
            // (Name empty). Per-entry validation upstream rejects the
            // entry separately; the reject gate just doesn't trip.
            var map = new Dictionary<string, Guid>();
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = "abc",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = string.Empty },
                    new AgencyWolfPassengerEntry { Name = null },
                },
            };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, map, out _, out _);

            Assert.IsFalse(rejected);
        }

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_EmptyPassengersList_ReturnsFalse()
        {
            var map = new Dictionary<string, Guid>();
            var entry = new AgencyWolfCrewRouteEntry { UniqueId = "abc" };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, map, out _, out _);

            Assert.IsFalse(rejected, "An entry with no passengers cannot have a cross-agency passenger.");
        }

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_NonEmptyEntryPreservedOverEmptyReplacement()
        {
            // [Multi-lens MUST FIX #3 — preservation rule] mirrors the
            // [[5.18b relay-vs-store note]] semantics: when the kerbal-
            // agency map already records a real (non-Empty) agency for a
            // kerbal, a subsequent Empty-sentinel-vessel sighting of the
            // same kerbal must NOT downgrade the record. Defends against
            // a modified client that launders a target kerbal into a
            // freshly-minted Empty-stamp vessel to bypass the cross-
            // agency reject.
            //
            // The reject helper itself doesn't run the preservation logic
            // (that lives in BuildKerbalAgencyMap), but we can verify the
            // downstream behavior: given the protected map (Bill -> Bob,
            // not Bill -> Empty), an Alice-side wire with Bill rejects.
            var protectedMap = new Dictionary<string, Guid>
            {
                { "Bill Kerman", _bobAgencyId }, // Bob's agency stamped first; Empty sighting wouldn't downgrade
            };
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = "abc",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Bill Kerman" },
                },
            };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, protectedMap, out var offending, out _);

            Assert.IsTrue(rejected, "Protected non-Empty entry must still reject the cross-agency Embark.");
            Assert.AreEqual("Bill Kerman", offending);
        }

        [TestMethod]
        public void RejectIfCrossAgencyPassenger_FirstMismatchShortCircuits()
        {
            // When multiple passengers fail, return the FIRST offender —
            // operator-visible Warning log gets a deterministic offender
            // identity (not a random one).
            var map = new Dictionary<string, Guid>
            {
                { "Bill Kerman", _bobAgencyId },
                { "Bob Kerman", _bobAgencyId },
            };
            var entry = new AgencyWolfCrewRouteEntry
            {
                UniqueId = "abc",
                Passengers = new List<AgencyWolfPassengerEntry>
                {
                    new AgencyWolfPassengerEntry { Name = "Bill Kerman" },
                    new AgencyWolfPassengerEntry { Name = "Bob Kerman" },
                },
            };

            var rejected = AgencyWolfCrewRouter.RejectIfCrossAgencyPassenger(entry, _agency.AgencyId, map, out var offending, out _);

            Assert.IsTrue(rejected);
            Assert.AreEqual("Bill Kerman", offending, "First-in-passenger-order mismatch must be reported.");
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfCrewRouteStateMsgData BuildSingleEntryMsg(string id, string oBody, string oBiome, string dBody, string dBiome)
        {
            var msg = ClientFactory.CreateNewMessageData<AgencyWolfCrewRouteStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfCrewRouteEntry
                {
                    UniqueId = id,
                    OriginBody = oBody,
                    OriginBiome = oBiome,
                    DestinationBody = dBody,
                    DestinationBiome = dBiome,
                    FlightStatus = "Boarding",
                    EconomyBerths = 4,
                    LuxuryBerths = 1,
                    Duration = 360.0,
                },
            };
            return msg;
        }
    }
}
