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
    /// Phase 4 Slice C — unit tests for the deterministic core of
    /// <see cref="AgencyWolfRouteRouter"/>: the <c>Upsert</c> helper + the
    /// <c>TryRoute</c> early-return branches (gate-off, Sandbox, null
    /// client, null msg) + per-entry isolation. The full <c>TryRoute</c>
    /// path (live <see cref="Server.Client.ClientStructure"/> +
    /// <see cref="Server.Server.MessageQueuer"/> echo) gets end-to-end
    /// coverage in <c>MockClientTest.WolfRouteRoutingTest</c>.
    ///
    /// <para>Test pattern mirrors <see cref="AgencyWolfDepotRouterTest"/>:
    /// pass <c>client: null</c> for early-return branches; reach into the
    /// <c>internal</c> <c>Upsert</c> helper directly for the storage shape
    /// tests.</para>
    /// </summary>
    [TestClass]
    public class AgencyWolfRouteRouterTest
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
                OwningPlayerName = "WolfRouteAlice",
                DisplayName = "Wolf Alice Cargo Co",
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
        // Early-return branches — dual-mode silence + defensive null handling
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRoute_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildSingleEntryMsg("Duna", "Lowlands", "Mun", "Highlands");

            var handled = AgencyWolfRouteRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller doesn't suppress the legacy SHA pass.");
            Assert.AreEqual(0, _agency.WolfRoutes.Count, "Gate off must not mutate AgencyState.WolfRoutes");
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            // Spec §10 Q-Mode Career-only sign-off: Sandbox closes the gate
            // even with PerAgencyCareer=true. Per-agency runtime is disabled.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg("Duna", "Lowlands", "Mun", "Highlands");

            var handled = AgencyWolfRouteRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfRoutes.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalseWithoutMutating()
        {
            var msg = BuildSingleEntryMsg("Duna", "Lowlands", "Mun", "Highlands");

            var handled = AgencyWolfRouteRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Null client must early-return.");
            Assert.AreEqual(0, _agency.WolfRoutes.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyWolfRouteRouter.TryRoute(client: null, msg: null);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert storage shape — the deterministic core of the router
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_SingleEntry_StoresUnderFourStringCompositeKey()
        {
            var entry = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 500,
            };

            AgencyWolfRouteRouter.Upsert(_agency, entry);

            Assert.AreEqual(1, _agency.WolfRoutes.Count);
            Assert.IsTrue(_agency.WolfRoutes.TryGetValue("Duna|Lowlands|Mun|Highlands", out var stored));
            Assert.AreEqual("Duna", stored.OriginBody);
            Assert.AreEqual("Lowlands", stored.OriginBiome);
            Assert.AreEqual("Mun", stored.DestinationBody);
            Assert.AreEqual("Highlands", stored.DestinationBiome);
            Assert.AreEqual(500, stored.Payload);
        }

        [TestMethod]
        public void Upsert_RepeatedSameKey_LastWriterWins()
        {
            AgencyWolfRouteRouter.Upsert(_agency, new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 500,
            });
            AgencyWolfRouteRouter.Upsert(_agency, new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 800,
            });

            Assert.AreEqual(1, _agency.WolfRoutes.Count);
            Assert.AreEqual(800, _agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"].Payload,
                "Second write must replace first under same composite key (matches WOLF's IncreasePayload-on-re-CreateRoute semantics).");
        }

        [TestMethod]
        public void Upsert_DistinctRoutesSameOrigin_StoresSeparately()
        {
            AgencyWolfRouteRouter.Upsert(_agency, new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 500,
            });
            AgencyWolfRouteRouter.Upsert(_agency, new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Kerbin",
                DestinationBiome = "Shores",
                Payload = 300,
            });

            Assert.AreEqual(2, _agency.WolfRoutes.Count);
            Assert.IsTrue(_agency.WolfRoutes.ContainsKey("Duna|Lowlands|Mun|Highlands"));
            Assert.IsTrue(_agency.WolfRoutes.ContainsKey("Duna|Lowlands|Kerbin|Shores"));
        }

        [TestMethod]
        public void Upsert_DefensiveCopiesResources()
        {
            // Pre-spec §3.c: Resources is a mutable List<>; storing the wire
            // reference directly would let a subsequent re-arrival mutate the
            // stored entry in place. Defensive shallow-copy on store.
            var wireEntry = new AgencyWolfRouteEntry
            {
                OriginBody = "Duna",
                OriginBiome = "Lowlands",
                DestinationBody = "Mun",
                DestinationBiome = "Highlands",
                Payload = 500,
                Resources = new List<AgencyWolfRouteResourceEntry>
                {
                    new AgencyWolfRouteResourceEntry { ResourceName = "Hydrates", Quantity = 100 },
                },
            };

            AgencyWolfRouteRouter.Upsert(_agency, wireEntry);

            var stored = _agency.WolfRoutes["Duna|Lowlands|Mun|Highlands"];
            Assert.AreNotSame(wireEntry, stored,
                "Stored entry must be a defensive copy, not the wire reference.");
            Assert.AreNotSame(wireEntry.Resources, stored.Resources,
                "Resources list must be a defensive copy.");
            Assert.AreNotSame(wireEntry.Resources[0], stored.Resources[0],
                "Each Resource entry must be a defensive copy.");
            Assert.AreEqual("Hydrates", stored.Resources[0].ResourceName);
            Assert.AreEqual(100, stored.Resources[0].Quantity);

            // Mutating the wire entry must NOT affect the stored entry.
            wireEntry.Resources[0].Quantity = 999;
            Assert.AreEqual(100, stored.Resources[0].Quantity,
                "Stored copy must be isolated from wire-side mutation.");
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfRouteRouter.Upsert(agency: null, entry: new AgencyWolfRouteEntry
                {
                    OriginBody = "Duna",
                    OriginBiome = "Lowlands",
                    DestinationBody = "Mun",
                    DestinationBiome = "Highlands",
                }));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfRouteRouter.Upsert(_agency, entry: null));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfRouteStateMsgData BuildSingleEntryMsg(
            string originBody, string originBiome, string destBody, string destBiome)
        {
            var msg = ClientFactory.CreateNewMessageData<AgencyWolfRouteStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfRouteEntry
                {
                    OriginBody = originBody,
                    OriginBiome = originBiome,
                    DestinationBody = destBody,
                    DestinationBiome = destBiome,
                    Payload = 500,
                },
            };
            return msg;
        }
    }
}
