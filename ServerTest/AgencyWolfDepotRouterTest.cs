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
    /// Phase 4 Slice B — unit tests for the deterministic core of
    /// <see cref="AgencyWolfDepotRouter"/>: the <c>Upsert</c> helper + the
    /// <c>TryRoute</c> early-return branches (gate-off, Sandbox, null
    /// client, null msg, missing agency) + per-entry isolation. The full
    /// <c>TryRoute</c> path (live <see cref="Server.Client.ClientStructure"/>
    /// + <see cref="Server.Server.MessageQueuer"/> echo) gets end-to-end
    /// coverage in Slice B-2's MockClientTest harness.
    ///
    /// <para>Test pattern mirrors <c>AgencyKolonyRouterTest</c>: pass
    /// <c>client: null</c> for early-return branches; reach into the
    /// <c>internal</c> <c>Upsert</c> helper directly for the storage shape
    /// tests.</para>
    /// </summary>
    [TestClass]
    public class AgencyWolfDepotRouterTest
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
                OwningPlayerName = "WolfAlice",
                DisplayName = "Wolf Alice Logistics Co",
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
            var msg = BuildSingleEntryMsg("Duna", "Lowlands");

            var handled = AgencyWolfDepotRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller doesn't suppress the legacy SHA pass.");
            Assert.AreEqual(0, _agency.WolfDepots.Count,
                "Gate off must not mutate AgencyState.WolfDepots");
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            // Spec §10 Q-Mode Career-only sign-off: Sandbox closes the gate
            // even with PerAgencyCareer=true. Per-agency runtime is disabled.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg("Duna", "Lowlands");

            var handled = AgencyWolfDepotRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfDepots.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalseWithoutMutating()
        {
            var msg = BuildSingleEntryMsg("Duna", "Lowlands");

            var handled = AgencyWolfDepotRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Null client must early-return.");
            Assert.AreEqual(0, _agency.WolfDepots.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyWolfDepotRouter.TryRoute(client: null, msg: null);

            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert storage shape — the deterministic core of the router
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_SingleEntry_StoresUnderBodyBiomeKey()
        {
            var entry = new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                IsEstablished = true,
                IsSurveyed = false,
            };

            AgencyWolfDepotRouter.Upsert(_agency, entry);

            Assert.AreEqual(1, _agency.WolfDepots.Count);
            Assert.IsTrue(_agency.WolfDepots.TryGetValue("Duna|Lowlands", out var stored));
            Assert.AreEqual("Duna", stored.Body);
            Assert.AreEqual("Lowlands", stored.Biome);
            Assert.IsTrue(stored.IsEstablished);
            Assert.IsFalse(stored.IsSurveyed);
        }

        [TestMethod]
        public void Upsert_RepeatedSameKey_LastWriterWins()
        {
            AgencyWolfDepotRouter.Upsert(_agency, new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                IsEstablished = false,
            });
            AgencyWolfDepotRouter.Upsert(_agency, new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                IsEstablished = true,
            });

            Assert.AreEqual(1, _agency.WolfDepots.Count);
            Assert.IsTrue(_agency.WolfDepots["Duna|Lowlands"].IsEstablished,
                "Second write must replace first under same (Body, Biome) key.");
        }

        [TestMethod]
        public void Upsert_DistinctBodyBiome_StoresSeparately()
        {
            AgencyWolfDepotRouter.Upsert(_agency, new AgencyWolfDepotEntry { Body = "Mun", Biome = "Highlands" });
            AgencyWolfDepotRouter.Upsert(_agency, new AgencyWolfDepotEntry { Body = "Mun", Biome = "Craters" });

            Assert.AreEqual(2, _agency.WolfDepots.Count);
            Assert.IsTrue(_agency.WolfDepots.ContainsKey("Mun|Highlands"));
            Assert.IsTrue(_agency.WolfDepots.ContainsKey("Mun|Craters"));
        }

        [TestMethod]
        public void Upsert_DefensiveCopiesResourceStreams()
        {
            // Pre-spec §3.c: ResourceStreams is a mutable List<>; storing the
            // wire reference directly would let a subsequent re-arrival mutate
            // the stored entry in place. Defensive shallow-copy on store.
            var wireEntry = new AgencyWolfDepotEntry
            {
                Body = "Duna",
                Biome = "Lowlands",
                ResourceStreams = new List<AgencyWolfResourceStreamEntry>
                {
                    new AgencyWolfResourceStreamEntry { ResourceName = "Hydrates", Incoming = 100, Outgoing = 50 },
                },
            };

            AgencyWolfDepotRouter.Upsert(_agency, wireEntry);

            var stored = _agency.WolfDepots["Duna|Lowlands"];
            Assert.AreNotSame(wireEntry, stored,
                "Stored entry must be a defensive copy, not the wire reference.");
            Assert.AreNotSame(wireEntry.ResourceStreams, stored.ResourceStreams,
                "ResourceStreams list must be a defensive copy.");
            Assert.AreNotSame(wireEntry.ResourceStreams[0], stored.ResourceStreams[0],
                "Each ResourceStream entry must be a defensive copy.");
            Assert.AreEqual("Hydrates", stored.ResourceStreams[0].ResourceName);
            Assert.AreEqual(100, stored.ResourceStreams[0].Incoming);
            Assert.AreEqual(50, stored.ResourceStreams[0].Outgoing);

            // Mutating the wire entry must NOT affect the stored entry.
            wireEntry.ResourceStreams[0].Incoming = 999;
            Assert.AreEqual(100, stored.ResourceStreams[0].Incoming,
                "Stored copy must be isolated from wire-side mutation.");
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfDepotRouter.Upsert(agency: null, entry: new AgencyWolfDepotEntry { Body = "Mun", Biome = "Craters" }));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfDepotRouter.Upsert(_agency, entry: null));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfDepotStateMsgData BuildSingleEntryMsg(string body, string biome)
        {
            var msg = ClientFactory.CreateNewMessageData<AgencyWolfDepotStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfDepotEntry { Body = body, Biome = biome, IsEstablished = true },
            };
            return msg;
        }
    }
}
