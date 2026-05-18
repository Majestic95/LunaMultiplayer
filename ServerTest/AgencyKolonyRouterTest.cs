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
    /// Phase 3 Slice B — unit tests for the deterministic core of
    /// <see cref="AgencyKolonyRouter"/>: the <c>Upsert</c> helper + the
    /// <c>TryRoute</c> early-return branches (gate-off, Sandbox, null
    /// client, null msg, missing agency). The full <c>TryRoute</c> path
    /// (vessel-store + cross-agency rejection + Unassigned-sentinel bypass +
    /// owner-only echo) requires a live <see cref="Server.Client.ClientStructure"/>
    /// + <see cref="Server.Server.MessageQueuer"/>; that surface is covered
    /// end-to-end in <c>MockClientTest/AgencyKolonyRoutingTest.cs</c>.
    ///
    /// <para>Test pattern mirrors <c>AgencyTechRouterTest</c> (Stage 5.17e-4):
    /// pass <c>client: null</c> for early-return branches; reach into the
    /// <c>internal</c> <c>Upsert</c> helper directly for the storage shape
    /// tests.</para>
    /// </summary>
    [TestClass]
    public class AgencyKolonyRouterTest
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
                OwningPlayerName = "KolonyAlice",
                DisplayName = "Kolony Alice Co",
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
            var msg = BuildSingleEntryMsg("11111111111111111111111111111111", bodyIndex: 1);

            var handled = AgencyKolonyRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller doesn't suppress the legacy SHA pass.");
            Assert.AreEqual(0, _agency.KolonyEntries.Count,
                "Gate off must not mutate AgencyState.KolonyEntries");
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            // Spec §10 Q-Mode Career-only sign-off: Sandbox closes the gate even
            // with PerAgencyCareer=true. Per-agency runtime is disabled; the
            // postfix is also a no-op so this branch shouldn't fire in practice.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg("22222222222222222222222222222222", bodyIndex: 2);

            var handled = AgencyKolonyRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.KolonyEntries.Count);
        }

        [TestMethod]
        public void TryRoute_ScienceMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var msg = BuildSingleEntryMsg("33333333333333333333333333333333", bodyIndex: 3);

            var handled = AgencyKolonyRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.KolonyEntries.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalse()
        {
            // Defensive: under gate=on, a null client (no source attribution)
            // must return false rather than NPE on client.PlayerName.
            var msg = BuildSingleEntryMsg("44444444444444444444444444444444", bodyIndex: 4);

            var handled = AgencyKolonyRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.KolonyEntries.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyKolonyRouter.TryRoute(client: null, msg: null);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert helper — verify storage shape independent of the wire path
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_NewEntry_AppendsToKolonyEntriesUnderCompositeKey()
        {
            // The router's dict key is $"{vesselId:N}|{bodyIndex}" so two entries
            // for the same vessel on different body indices each get a distinct
            // slot (verified separately below). A fresh entry creates the slot.
            var vesselId = Guid.NewGuid().ToString("N");
            var entry = NewEntry(vesselId, bodyIndex: 5, geology: 12.5);

            AgencyKolonyRouter.Upsert(_agency, entry);

            var expectedKey = $"{vesselId}|5";
            Assert.AreEqual(1, _agency.KolonyEntries.Count);
            Assert.IsTrue(_agency.KolonyEntries.ContainsKey(expectedKey),
                $"Composite key '{expectedKey}' missing — Upsert must key by vesselId|bodyIndex.");
            Assert.AreEqual(12.5, _agency.KolonyEntries[expectedKey].GeologyResearch);
        }

        [TestMethod]
        public void Upsert_ExistingKey_ReplacesEntryInPlace_NotAppend()
        {
            // Upsert semantics: a re-arrival with the same (vesselId, bodyIndex)
            // overwrites the prior snapshot, never duplicates. Without this the
            // per-agency dict would grow unbounded across the lifetime of a base.
            var vesselId = Guid.NewGuid().ToString("N");
            _agency.KolonyEntries[$"{vesselId}|7"] = NewEntry(vesselId, 7, geology: 1.0);

            var newer = NewEntry(vesselId, 7, geology: 99.0);
            AgencyKolonyRouter.Upsert(_agency, newer);

            Assert.AreEqual(1, _agency.KolonyEntries.Count,
                "Upsert must not append on duplicate composite key.");
            Assert.AreEqual(99.0, _agency.KolonyEntries[$"{vesselId}|7"].GeologyResearch,
                "Upsert must overwrite the prior GeologyResearch value.");
        }

        [TestMethod]
        public void Upsert_SameVesselDifferentBodyIndex_DistinctEntries()
        {
            // The partition key includes body — a single vessel landing on
            // multiple bodies (improbable but possible via science vessels)
            // gets one entry per (vesselId, bodyIndex) pair.
            var vesselId = Guid.NewGuid().ToString("N");

            AgencyKolonyRouter.Upsert(_agency, NewEntry(vesselId, 5, geology: 1.0));
            AgencyKolonyRouter.Upsert(_agency, NewEntry(vesselId, 8, geology: 2.0));

            Assert.AreEqual(2, _agency.KolonyEntries.Count,
                "Same vessel, different body indices must produce distinct dict entries.");
            Assert.AreEqual(1.0, _agency.KolonyEntries[$"{vesselId}|5"].GeologyResearch);
            Assert.AreEqual(2.0, _agency.KolonyEntries[$"{vesselId}|8"].GeologyResearch);
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            var entry = NewEntry("aabbccddeeff00112233445566778899", 1, geology: 0);
            Assert.ThrowsException<ArgumentNullException>(() => AgencyKolonyRouter.Upsert(null, entry));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => AgencyKolonyRouter.Upsert(_agency, null));
        }

        // -------------------------------------------------------------------
        // Persistence round-trip — pin the serialize/parse contract
        // -------------------------------------------------------------------

        [TestMethod]
        public void AgencyState_KolonyEntries_RoundTripPreservesAllFields()
        {
            // The 13-field entry round-trips through Serialize → Parse intact.
            // A regression in invariant-culture handling or field ordering would
            // surface here, not in the e2e suite.
            var vesselId = Guid.NewGuid().ToString("N");
            var key = $"{vesselId}|3";
            _agency.KolonyEntries[key] = new AgencyKolonyEntry
            {
                VesselId = vesselId,
                BodyIndex = 3,
                LastUpdate = 12345.678,
                KolonyDate = 11111.222,
                GeologyResearch = 100.5,
                BotanyResearch = 200.5,
                KolonizationResearch = 300.5,
                Science = 50.25,
                Reputation = -10.5,  // negative + decimal — locale stress
                Funds = 9999.99,
                RepBoosters = 2,
                FundsBoosters = 3,
                ScienceBoosters = 4,
            };

            var roundTripped = AgencyState.Parse(_agency.Serialize());

            Assert.AreEqual(1, roundTripped.KolonyEntries.Count);
            Assert.IsTrue(roundTripped.KolonyEntries.ContainsKey(key));
            var rt = roundTripped.KolonyEntries[key];
            Assert.AreEqual(vesselId, rt.VesselId);
            Assert.AreEqual(3, rt.BodyIndex);
            Assert.AreEqual(12345.678, rt.LastUpdate);
            Assert.AreEqual(11111.222, rt.KolonyDate);
            Assert.AreEqual(100.5, rt.GeologyResearch);
            Assert.AreEqual(200.5, rt.BotanyResearch);
            Assert.AreEqual(300.5, rt.KolonizationResearch);
            Assert.AreEqual(50.25, rt.Science);
            Assert.AreEqual(-10.5, rt.Reputation);
            Assert.AreEqual(9999.99, rt.Funds);
            Assert.AreEqual(2, rt.RepBoosters);
            Assert.AreEqual(3, rt.FundsBoosters);
            Assert.AreEqual(4, rt.ScienceBoosters);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyKolonyEntry NewEntry(string vesselId, int bodyIndex, double geology)
        {
            return new AgencyKolonyEntry
            {
                VesselId = vesselId,
                BodyIndex = bodyIndex,
                GeologyResearch = geology,
            };
        }

        private static AgencyKolonyStateMsgData BuildSingleEntryMsg(string vesselId, int bodyIndex)
        {
            // ClientMessageFactory hands out the message via reflection — the
            // public ctor is internal so direct `new` is forbidden from
            // ServerTest (LmpCommon doesn't grant InternalsVisibleTo).
            var msg = ClientFactory.CreateNewMessageData<AgencyKolonyStateMsgData>();
            msg.AgencyId = Guid.Empty;
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyKolonyEntry
                {
                    VesselId = vesselId,
                    BodyIndex = bodyIndex,
                }
            };
            return msg;
        }
    }
}
