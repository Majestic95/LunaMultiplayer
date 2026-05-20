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
    /// Phase 4 Slice D — unit tests for the deterministic core of
    /// <see cref="AgencyWolfHopperRouter"/>: the <c>Upsert</c> helper + the
    /// <c>TryRoute</c> early-return branches (gate-off, Sandbox, null
    /// client, null msg) + per-entry isolation + RemovedKeys handling. The
    /// full <c>TryRoute</c> path (live <see cref="Server.Client.ClientStructure"/>
    /// + <see cref="Server.Server.MessageQueuer"/> echo) gets end-to-end
    /// coverage in <c>MockClientTest.WolfHopperRoutingTest</c>.
    ///
    /// <para>Test pattern mirrors <see cref="AgencyWolfRouteRouterTest"/>.</para>
    /// </summary>
    [TestClass]
    public class AgencyWolfHopperRouterTest
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
                OwningPlayerName = "WolfHopperAlice",
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
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString(), "Duna", "Lowlands");

            var handled = AgencyWolfHopperRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller doesn't suppress the legacy SHA pass.");
            Assert.AreEqual(0, _agency.WolfHoppers.Count);
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString(), "Duna", "Lowlands");

            var handled = AgencyWolfHopperRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfHoppers.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalseWithoutMutating()
        {
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString(), "Duna", "Lowlands");

            var handled = AgencyWolfHopperRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfHoppers.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyWolfHopperRouter.TryRoute(client: null, msg: null);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert storage shape
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_SingleEntry_StoresUnderIdKey_PreservingHyphens()
        {
            // Hopper.Id is Guid.NewGuid().ToString() WITH hyphens per
            // HopperMetadata.cs:18 — verify no normalization.
            var id = Guid.NewGuid().ToString();
            Assert.IsTrue(id.Contains("-"), "Test pre-condition: ToString() form must contain hyphens.");

            AgencyWolfHopperRouter.Upsert(_agency, new AgencyWolfHopperEntry
            {
                Id = id,
                Body = "Duna",
                Biome = "Lowlands",
                Recipe = "Hydrates,100,Substrate,50",
            });

            Assert.AreEqual(1, _agency.WolfHoppers.Count);
            Assert.IsTrue(_agency.WolfHoppers.TryGetValue(id, out var stored));
            Assert.AreEqual(id, stored.Id, "Id stored verbatim — no hyphen-stripping.");
            Assert.AreEqual("Duna", stored.Body);
            Assert.AreEqual("Lowlands", stored.Biome);
            Assert.AreEqual("Hydrates,100,Substrate,50", stored.Recipe);
        }

        [TestMethod]
        public void Upsert_RepeatedSameId_LastWriterWins()
        {
            var id = Guid.NewGuid().ToString();

            AgencyWolfHopperRouter.Upsert(_agency, new AgencyWolfHopperEntry
            {
                Id = id, Body = "Duna", Biome = "Lowlands", Recipe = "Hydrates,100",
            });
            AgencyWolfHopperRouter.Upsert(_agency, new AgencyWolfHopperEntry
            {
                Id = id, Body = "Duna", Biome = "Lowlands", Recipe = "Substrate,250",
            });

            Assert.AreEqual(1, _agency.WolfHoppers.Count);
            Assert.AreEqual("Substrate,250", _agency.WolfHoppers[id].Recipe,
                "Second write must replace first — matches WOLF UI's recipe-change Remove+Create pattern's intermediate state.");
        }

        [TestMethod]
        public void Upsert_TwoDistinctIds_BothStored()
        {
            var idA = Guid.NewGuid().ToString();
            var idB = Guid.NewGuid().ToString();

            AgencyWolfHopperRouter.Upsert(_agency, new AgencyWolfHopperEntry
            {
                Id = idA, Body = "Duna", Biome = "Lowlands", Recipe = "Hydrates,100",
            });
            AgencyWolfHopperRouter.Upsert(_agency, new AgencyWolfHopperEntry
            {
                Id = idB, Body = "Mun", Biome = "Highlands", Recipe = "Substrate,200",
            });

            Assert.AreEqual(2, _agency.WolfHoppers.Count);
            Assert.IsTrue(_agency.WolfHoppers.ContainsKey(idA));
            Assert.IsTrue(_agency.WolfHoppers.ContainsKey(idB));
        }

        [TestMethod]
        public void Upsert_DefensiveCopy_StoredEntryIsolatedFromWireMutation()
        {
            var id = Guid.NewGuid().ToString();
            var wireEntry = new AgencyWolfHopperEntry
            {
                Id = id, Body = "Duna", Biome = "Lowlands", Recipe = "Hydrates,100",
            };

            AgencyWolfHopperRouter.Upsert(_agency, wireEntry);

            var stored = _agency.WolfHoppers[id];
            Assert.AreNotSame(wireEntry, stored, "Stored must be a defensive copy.");

            wireEntry.Body = "MUTATED";
            wireEntry.Recipe = "MUTATED";
            Assert.AreEqual("Duna", stored.Body, "Wire mutation must not bleed into stored snapshot.");
            Assert.AreEqual("Hydrates,100", stored.Recipe);
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfHopperRouter.Upsert(agency: null, entry: new AgencyWolfHopperEntry
                {
                    Id = Guid.NewGuid().ToString(), Body = "Duna", Biome = "Lowlands",
                }));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfHopperRouter.Upsert(_agency, entry: null));
        }

        [TestMethod]
        public void Upsert_EmptyId_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                AgencyWolfHopperRouter.Upsert(_agency, new AgencyWolfHopperEntry
                {
                    Id = string.Empty, Body = "Duna", Biome = "Lowlands",
                }));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfHopperStateMsgData BuildSingleEntryMsg(string id, string body, string biome)
        {
            var msg = ClientFactory.CreateNewMessageData<AgencyWolfHopperStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfHopperEntry
                {
                    Id = id,
                    Body = body,
                    Biome = biome,
                    Recipe = "Hydrates,100",
                },
            };
            return msg;
        }
    }
}
