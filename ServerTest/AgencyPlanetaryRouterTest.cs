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
    /// Phase 3 Slice C — unit tests for the deterministic core of
    /// <see cref="AgencyPlanetaryRouter"/>: the <c>Upsert</c> helper + the
    /// <c>TryRoute</c> early-return branches. Same shape as the Slice B
    /// <see cref="AgencyKolonyRouterTest"/>. End-to-end wire coverage (gate-on
    /// cross-agency privacy across two clients) is in
    /// <c>MockClientTest/AgencyPlanetaryRoutingTest.cs</c>.
    ///
    /// <para>Test pattern mirrors <see cref="AgencyKolonyRouterTest"/>:
    /// early-return branches pass <c>client: null</c>; reach into the
    /// <c>internal</c> <c>Upsert</c> helper directly for the storage shape
    /// tests. Differences from kolony: body-and-resource keyed (NOT
    /// vessel-keyed), <c>OwningVesselId</c> is typed Guid (no string parse
    /// step), no field-name mapping (4 fields all map 1:1 to MKS' on-disk
    /// names).</para>
    /// </summary>
    [TestClass]
    public class AgencyPlanetaryRouterTest
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
                OwningPlayerName = "PlanetaryAlice",
                DisplayName = "Planetary Alice Co",
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
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), bodyIndex: 1, resourceName: "Hydrates", quantity: 100);

            var handled = AgencyPlanetaryRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller doesn't suppress the legacy SHA pass.");
            Assert.AreEqual(0, _agency.PlanetaryEntries.Count,
                "Gate off must not mutate AgencyState.PlanetaryEntries");
        }

        [TestMethod]
        public void TryRoute_SandboxMode_ReturnsFalseWithoutMutating()
        {
            // Spec §10 Q-Mode Career-only sign-off: Sandbox closes the gate even
            // with PerAgencyCareer=true. Per-agency runtime is disabled; the
            // postfix is also a no-op so this branch shouldn't fire in practice.
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), bodyIndex: 2, resourceName: "Karbonite", quantity: 50);

            var handled = AgencyPlanetaryRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.PlanetaryEntries.Count);
        }

        [TestMethod]
        public void TryRoute_ScienceMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), bodyIndex: 3, resourceName: "Hydrates", quantity: 1);

            var handled = AgencyPlanetaryRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.PlanetaryEntries.Count);
        }

        [TestMethod]
        public void TryRoute_NullClient_ReturnsFalse()
        {
            // Defensive: under gate=on, a null client (no source attribution)
            // must return false rather than NPE on client.PlayerName.
            var msg = BuildSingleEntryMsg(Guid.NewGuid(), bodyIndex: 4, resourceName: "Hydrates", quantity: 1);

            var handled = AgencyPlanetaryRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.PlanetaryEntries.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyPlanetaryRouter.TryRoute(client: null, msg: null);
            Assert.IsFalse(handled);
        }

        // -------------------------------------------------------------------
        // Upsert helper — verify storage shape independent of the wire path
        // -------------------------------------------------------------------

        [TestMethod]
        public void Upsert_NewEntry_AppendsUnderBodyResourceCompositeKey()
        {
            // The router's dict key is $"{bodyIndex}|{resourceName}" — distinct
            // from Slice B kolony's vessel-and-body key. Two entries for the
            // same body but different resources each get a distinct slot.
            var vesselId = Guid.NewGuid();
            var entry = NewEntry(vesselId, bodyIndex: 5, resourceName: "Hydrates", quantity: 12.5);

            AgencyPlanetaryRouter.Upsert(_agency, entry);

            const string expectedKey = "5|Hydrates";
            Assert.AreEqual(1, _agency.PlanetaryEntries.Count);
            Assert.IsTrue(_agency.PlanetaryEntries.ContainsKey(expectedKey),
                $"Composite key '{expectedKey}' missing — Upsert must key by bodyIndex|resourceName.");
            Assert.AreEqual(12.5, _agency.PlanetaryEntries[expectedKey].StoredQuantity);
            Assert.AreEqual(vesselId, _agency.PlanetaryEntries[expectedKey].OwningVesselId,
                "OwningVesselId must round-trip through Upsert intact.");
        }

        [TestMethod]
        public void Upsert_ExistingKey_ReplacesEntryInPlace_NotAppend()
        {
            // Upsert semantics: a re-arrival with the same (bodyIndex, resourceName)
            // overwrites the prior snapshot, never duplicates. The OwningVesselId
            // of the second entry wins (last writer wins) — body-keyed partition
            // means we DO want the latest vessel-attribution even if a different
            // vessel of the same agency was the original writer.
            var vesselA = Guid.NewGuid();
            var vesselB = Guid.NewGuid();
            _agency.PlanetaryEntries["7|Karbonite"] = NewEntry(vesselA, 7, "Karbonite", 1.0);

            var newer = NewEntry(vesselB, 7, "Karbonite", 99.0);
            AgencyPlanetaryRouter.Upsert(_agency, newer);

            Assert.AreEqual(1, _agency.PlanetaryEntries.Count,
                "Upsert must not append on duplicate composite key.");
            Assert.AreEqual(99.0, _agency.PlanetaryEntries["7|Karbonite"].StoredQuantity,
                "Upsert must overwrite the prior StoredQuantity value.");
            Assert.AreEqual(vesselB, _agency.PlanetaryEntries["7|Karbonite"].OwningVesselId,
                "Upsert must overwrite the prior OwningVesselId (last-writer-wins on body-keyed partition).");
        }

        [TestMethod]
        public void Upsert_SameBodyDifferentResources_DistinctEntries()
        {
            // Same body, different resources -> two distinct dict slots. A
            // single warehouse vessel pumping Hydrates AND Karbonite on Duna
            // produces 2 entries.
            var vesselId = Guid.NewGuid();

            AgencyPlanetaryRouter.Upsert(_agency, NewEntry(vesselId, 5, "Hydrates", 1.0));
            AgencyPlanetaryRouter.Upsert(_agency, NewEntry(vesselId, 5, "Karbonite", 2.0));

            Assert.AreEqual(2, _agency.PlanetaryEntries.Count,
                "Same body, different resources must produce distinct dict entries.");
            Assert.AreEqual(1.0, _agency.PlanetaryEntries["5|Hydrates"].StoredQuantity);
            Assert.AreEqual(2.0, _agency.PlanetaryEntries["5|Karbonite"].StoredQuantity);
        }

        [TestMethod]
        public void Upsert_SameResourceDifferentBodies_DistinctEntries()
        {
            // Same resource, different bodies -> two distinct dict slots. The
            // same agency mining Hydrates on Duna AND Eve gets one entry per
            // body. Symmetric to the SameBodyDifferentResources case.
            var vesselId = Guid.NewGuid();

            AgencyPlanetaryRouter.Upsert(_agency, NewEntry(vesselId, 5, "Hydrates", 100.0));
            AgencyPlanetaryRouter.Upsert(_agency, NewEntry(vesselId, 6, "Hydrates", 200.0));

            Assert.AreEqual(2, _agency.PlanetaryEntries.Count,
                "Same resource, different bodies must produce distinct dict entries.");
            Assert.AreEqual(100.0, _agency.PlanetaryEntries["5|Hydrates"].StoredQuantity);
            Assert.AreEqual(200.0, _agency.PlanetaryEntries["6|Hydrates"].StoredQuantity);
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            var entry = NewEntry(Guid.NewGuid(), 1, "Hydrates", 0);
            Assert.ThrowsException<ArgumentNullException>(() => AgencyPlanetaryRouter.Upsert(null, entry));
        }

        [TestMethod]
        public void Upsert_NullEntry_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => AgencyPlanetaryRouter.Upsert(_agency, null));
        }

        // -------------------------------------------------------------------
        // Persistence round-trip — pin the serialize/parse contract
        // -------------------------------------------------------------------

        [TestMethod]
        public void AgencyState_PlanetaryEntries_RoundTripPreservesAllFields()
        {
            // The 4-field entry round-trips through Serialize → Parse intact.
            // Locale stress (negative + decimal) catches invariant-culture
            // handling regressions; the Slice A AgencyState ConfigNode round-
            // trip already handles this, but we pin it here for the router's
            // upsert path specifically. (Same shape as Slice B's analogous
            // kolony test.)
            var vesselId = Guid.NewGuid();
            const string key = "3|MetallicOre";
            _agency.PlanetaryEntries[key] = new AgencyPlanetaryEntry
            {
                OwningVesselId = vesselId,
                BodyIndex = 3,
                ResourceName = "MetallicOre",
                StoredQuantity = -12345.6789,
            };

            var roundTripped = AgencyState.Parse(_agency.Serialize());

            Assert.AreEqual(1, roundTripped.PlanetaryEntries.Count);
            Assert.IsTrue(roundTripped.PlanetaryEntries.ContainsKey(key));
            var rt = roundTripped.PlanetaryEntries[key];
            Assert.AreEqual(vesselId, rt.OwningVesselId);
            Assert.AreEqual(3, rt.BodyIndex);
            Assert.AreEqual("MetallicOre", rt.ResourceName);
            Assert.AreEqual(-12345.6789, rt.StoredQuantity);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static AgencyPlanetaryEntry NewEntry(Guid vesselId, int bodyIndex, string resourceName, double quantity)
        {
            return new AgencyPlanetaryEntry
            {
                OwningVesselId = vesselId,
                BodyIndex = bodyIndex,
                ResourceName = resourceName,
                StoredQuantity = quantity,
            };
        }

        private static AgencyPlanetaryStateMsgData BuildSingleEntryMsg(Guid vesselId, int bodyIndex, string resourceName, double quantity)
        {
            var msg = ClientFactory.CreateNewMessageData<AgencyPlanetaryStateMsgData>();
            msg.AgencyId = Guid.Empty;
            msg.EntryCount = 1;
            msg.Entries = new[]
            {
                new AgencyPlanetaryEntry
                {
                    OwningVesselId = vesselId,
                    BodyIndex = bodyIndex,
                    ResourceName = resourceName,
                    StoredQuantity = quantity,
                }
            };
            return msg;
        }
    }
}
