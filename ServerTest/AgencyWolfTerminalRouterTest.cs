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
    /// Phase 4 Slice D — unit tests for <see cref="AgencyWolfTerminalRouter"/>.
    /// Same coverage shape as <see cref="AgencyWolfHopperRouterTest"/> but
    /// asserts the "N"-form Guid key (no hyphens) preservation and the
    /// no-FK-sweep contract.
    /// </summary>
    [TestClass]
    public class AgencyWolfTerminalRouterTest
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
                OwningPlayerName = "WolfTerminalAlice",
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
        // Early-return branches
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRoute_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString("N"), "Duna", "Lowlands");

            var handled = AgencyWolfTerminalRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfTerminals.Count);
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg(Guid.NewGuid().ToString("N"), "Duna", "Lowlands");

            var handled = AgencyWolfTerminalRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.WolfTerminals.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyWolfTerminalRouter.TryRoute(client: null, msg: null);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert storage shape — N-form (no hyphens) preservation
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_SingleEntry_StoresUnderNFormIdKey_NoHyphens()
        {
            // Terminal.Id is Guid.NewGuid().ToString("N") — 32 hex chars no
            // hyphens per TerminalMetadata.cs:15. Verify no normalization
            // and that the form is preserved as-is.
            var id = Guid.NewGuid().ToString("N");
            Assert.IsFalse(id.Contains("-"), "Test pre-condition: 'N' form must NOT contain hyphens.");
            Assert.AreEqual(32, id.Length);

            AgencyWolfTerminalRouter.Upsert(_agency, new AgencyWolfTerminalEntry
            {
                Id = id, Body = "Duna", Biome = "Lowlands",
            });

            Assert.AreEqual(1, _agency.WolfTerminals.Count);
            Assert.IsTrue(_agency.WolfTerminals.TryGetValue(id, out var stored));
            Assert.AreEqual(id, stored.Id, "Id stored verbatim — preserving the N-form distinction from Hopper's with-hyphens form.");
            Assert.AreEqual("Duna", stored.Body);
            Assert.AreEqual("Lowlands", stored.Biome);
        }

        [TestMethod]
        public void Upsert_NFormVsHyphenedForm_AreDistinctKeys()
        {
            // Spec invariant: do NOT normalize between Hopper's with-hyphens
            // and Terminal's "N" form. They MAY in theory collide on a guid
            // value but the string representation is different — preserve
            // the form. This test asserts the comparer treats them distinctly.
            var raw = Guid.NewGuid();
            var hyphenedForm = raw.ToString();
            var nForm = raw.ToString("N");
            Assert.AreNotEqual(hyphenedForm, nForm, "Test pre-condition: forms must be string-distinct.");

            AgencyWolfTerminalRouter.Upsert(_agency, new AgencyWolfTerminalEntry
            {
                Id = nForm, Body = "Duna", Biome = "Lowlands",
            });
            AgencyWolfTerminalRouter.Upsert(_agency, new AgencyWolfTerminalEntry
            {
                Id = hyphenedForm, Body = "Mun", Biome = "Highlands",
            });

            Assert.AreEqual(2, _agency.WolfTerminals.Count,
                "Ordinal string comparer must treat hyphened and N-form keys as distinct.");
        }

        [TestMethod]
        public void Upsert_DefensiveCopy()
        {
            var id = Guid.NewGuid().ToString("N");
            var wireEntry = new AgencyWolfTerminalEntry
            {
                Id = id, Body = "Duna", Biome = "Lowlands",
            };

            AgencyWolfTerminalRouter.Upsert(_agency, wireEntry);

            var stored = _agency.WolfTerminals[id];
            Assert.AreNotSame(wireEntry, stored);

            wireEntry.Body = "MUTATED";
            Assert.AreEqual("Duna", stored.Body);
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfTerminalRouter.Upsert(agency: null, entry: new AgencyWolfTerminalEntry
                {
                    Id = Guid.NewGuid().ToString("N"), Body = "Duna", Biome = "Lowlands",
                }));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyWolfTerminalRouter.Upsert(_agency, entry: null));
        }

        [TestMethod]
        public void Upsert_EmptyId_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                AgencyWolfTerminalRouter.Upsert(_agency, new AgencyWolfTerminalEntry
                {
                    Id = string.Empty, Body = "Duna", Biome = "Lowlands",
                }));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyWolfTerminalStateMsgData BuildSingleEntryMsg(string id, string body, string biome)
        {
            var msg = ClientFactory.CreateNewMessageData<AgencyWolfTerminalStateMsgData>();
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyWolfTerminalEntry { Id = id, Body = body, Biome = biome },
            };
            return msg;
        }
    }
}
