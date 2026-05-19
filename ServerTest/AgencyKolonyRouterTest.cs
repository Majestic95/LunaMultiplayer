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
        // [Phase 3 Slice E-1] MigrateForVesselTransfer — prefix-scan migration
        // helper for the upcoming setvesselagency admin command (E-2).
        // -------------------------------------------------------------------

        [TestMethod]
        public void Migrate_SourceEmpty_ReturnsEmptyResult()
        {
            // Defensive: a vessel transfer where source agency has zero
            // kolony entries (the vessel never contributed) must not throw
            // or fabricate fake removals.
            var dest = NewSecondaryAgency("KolonyBob");
            var vesselId = Guid.NewGuid();

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, vesselId);

            Assert.AreEqual(0, result.RemovedKeys.Count);
            Assert.AreEqual(0, result.AddedEntries.Count);
            Assert.AreEqual(0, _agency.KolonyEntries.Count);
            Assert.AreEqual(0, dest.KolonyEntries.Count);
        }

        [TestMethod]
        public void Migrate_VesselWithMultipleBodyEntries_MovesAllToDestination()
        {
            // A vessel touring Mun + Minmus + Duna has three (vesselId, body)
            // entries. All three migrate together — the body-index suffix
            // is arbitrary, the prefix scan catches every match.
            var movedVesselId = Guid.NewGuid();
            var vesselIdN = movedVesselId.ToString("N");
            _agency.KolonyEntries[$"{vesselIdN}|2"] = NewEntry(vesselIdN, 2, 100.0);
            _agency.KolonyEntries[$"{vesselIdN}|5"] = NewEntry(vesselIdN, 5, 200.0);
            _agency.KolonyEntries[$"{vesselIdN}|7"] = NewEntry(vesselIdN, 7, 300.0);

            var dest = NewSecondaryAgency("KolonyBob");

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(3, result.RemovedKeys.Count);
            Assert.AreEqual(3, result.AddedEntries.Count);
            Assert.AreEqual(0, _agency.KolonyEntries.Count, "Source must be empty post-migration.");
            Assert.AreEqual(3, dest.KolonyEntries.Count, "Destination must hold all moved entries.");
            Assert.IsTrue(dest.KolonyEntries.ContainsKey($"{vesselIdN}|2"));
            Assert.IsTrue(dest.KolonyEntries.ContainsKey($"{vesselIdN}|5"));
            Assert.IsTrue(dest.KolonyEntries.ContainsKey($"{vesselIdN}|7"));
        }

        [TestMethod]
        public void Migrate_VesselWithEntries_LeavesOtherVesselsEntriesAlone()
        {
            // Source has entries for two different vessels. Migrating one
            // vessel must NOT touch the other vessel's entries.
            var movedVesselId = Guid.NewGuid();
            var otherVesselId = Guid.NewGuid();
            var movedN = movedVesselId.ToString("N");
            var otherN = otherVesselId.ToString("N");
            _agency.KolonyEntries[$"{movedN}|2"] = NewEntry(movedN, 2, 100.0);
            _agency.KolonyEntries[$"{otherN}|2"] = NewEntry(otherN, 2, 500.0);
            _agency.KolonyEntries[$"{otherN}|5"] = NewEntry(otherN, 5, 600.0);

            var dest = NewSecondaryAgency("KolonyBob");

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(1, result.RemovedKeys.Count, "Only the moved vessel's entry migrates.");
            Assert.AreEqual(2, _agency.KolonyEntries.Count, "Other vessel's entries stay in source.");
            Assert.IsTrue(_agency.KolonyEntries.ContainsKey($"{otherN}|2"));
            Assert.IsTrue(_agency.KolonyEntries.ContainsKey($"{otherN}|5"));
            Assert.AreEqual(1, dest.KolonyEntries.Count);
            Assert.IsTrue(dest.KolonyEntries.ContainsKey($"{movedN}|2"));
        }

        [TestMethod]
        public void Migrate_VesselNotInSource_ReturnsEmpty()
        {
            // Source has entries for vessel X; the operator transfers vessel
            // Y (which has no kolony state in this agency). No-op.
            var presentVesselId = Guid.NewGuid().ToString("N");
            _agency.KolonyEntries[$"{presentVesselId}|2"] = NewEntry(presentVesselId, 2, 100.0);
            var dest = NewSecondaryAgency("KolonyBob");

            var movedVesselId = Guid.NewGuid();  // never contributed
            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(0, result.RemovedKeys.Count);
            Assert.AreEqual(1, _agency.KolonyEntries.Count);
            Assert.AreEqual(0, dest.KolonyEntries.Count);
        }

        [TestMethod]
        public void Migrate_SameAgencyInstance_ReturnsEmptyWithoutMutation()
        {
            // Defensive: same-source-same-dest is a logical no-op. The E-2
            // command's same-stamp short-circuit catches this earlier, but
            // a buggy caller bypassing that check must not corrupt the
            // agency's own state.
            var vesselN = Guid.NewGuid().ToString("N");
            _agency.KolonyEntries[$"{vesselN}|2"] = NewEntry(vesselN, 2, 100.0);

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, _agency, Guid.Parse(vesselN));

            Assert.AreEqual(0, result.RemovedKeys.Count);
            Assert.AreEqual(1, _agency.KolonyEntries.Count, "Same-agency call must leave the entry in place.");
        }

        [TestMethod]
        public void Migrate_MovedVesselIdEmpty_ReturnsEmpty()
        {
            // Defensive: Guid.Empty is the Unassigned-vessel sentinel
            // (spec §10 Q3). A caller passing Empty has confused vessel-id
            // with agency-id. No-op + no mutation.
            var vesselN = Guid.NewGuid().ToString("N");
            _agency.KolonyEntries[$"{vesselN}|2"] = NewEntry(vesselN, 2, 100.0);
            var dest = NewSecondaryAgency("KolonyBob");

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, Guid.Empty);

            Assert.AreEqual(0, result.RemovedKeys.Count);
            Assert.AreEqual(1, _agency.KolonyEntries.Count);
            Assert.AreEqual(0, dest.KolonyEntries.Count);
        }

        [TestMethod]
        public void Migrate_NullSource_ThrowsArgumentNull()
        {
            var dest = NewSecondaryAgency("KolonyBob");
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyKolonyRouter.MigrateForVesselTransfer(null, dest, Guid.NewGuid()));
        }

        [TestMethod]
        public void Migrate_NullDestination_ThrowsArgumentNull()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                AgencyKolonyRouter.MigrateForVesselTransfer(_agency, null, Guid.NewGuid()));
        }

        [TestMethod]
        public void Migrate_DestinationCollisionAtSameKey_SourceEntryOverwritesDestination()
        {
            // Defensive: by construction a vessel only belongs to one
            // agency at a time so destination CANNOT legitimately have the
            // same {vesselId|body} key as source. If a collision somehow
            // exists (operator hand-edit, prior failed migration), the
            // helper's documented preference (XML lines 290-293) is source-
            // wins — source held the vessel until now so its entry is more
            // recent.
            var movedVesselId = Guid.NewGuid();
            var movedN = movedVesselId.ToString("N");
            var sourceEntry = NewEntry(movedN, 5, 999.0);
            var destStaleEntry = NewEntry(movedN, 5, 1.0);
            _agency.KolonyEntries[$"{movedN}|5"] = sourceEntry;
            var dest = NewSecondaryAgency("KolonyBob");
            dest.KolonyEntries[$"{movedN}|5"] = destStaleEntry;

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(1, result.RemovedKeys.Count);
            Assert.AreEqual(1, dest.KolonyEntries.Count);
            Assert.AreSame(sourceEntry, dest.KolonyEntries[$"{movedN}|5"],
                "Source's entry must replace destination's stale entry on collision.");
            Assert.AreEqual(999.0, dest.KolonyEntries[$"{movedN}|5"].GeologyResearch);
        }

        [TestMethod]
        public void Migrate_VesselIdFormMismatch_DirectlyUpsertedDFormEntry_DoesNotMatch()
        {
            // Contract sharp-edge: the router's TryRoute normalizes wire
            // VesselId to "N" form before Upsert (router.cs:126), so live
            // wire entries always use 32-hex-no-hyphens. But a future
            // direct caller (Slice E-2 admin, operator script,
            // ServerTest helper) bypassing TryRoute could call Upsert
            // directly with a hyphenated "D" form. Migration uses
            // StartsWith(StringComparison.Ordinal) — a "D" form ("N" with
            // 4 hyphens inserted) would NOT match an "N"-form
            // movedVesselId prefix, so the entry would NOT migrate.
            // This is a documented sharp-edge — the live wire path is
            // safe by ingest normalization, but the Slice E-2 author MUST
            // use the same "N" form convention if they call Upsert
            // directly. This test pins the asymmetry.
            var movedVesselId = Guid.NewGuid();
            var movedN = movedVesselId.ToString("N");
            var movedD = movedVesselId.ToString("D");  // hyphenated 36-char form
            // Hand-insert a D-form entry — bypass Upsert's $"{VesselId}|{bodyIndex}"
            // composition by writing directly to the dict with a D-form key.
            _agency.KolonyEntries[$"{movedD}|5"] = NewEntry(movedD, 5, 100.0);
            var dest = NewSecondaryAgency("KolonyBob");

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(0, result.RemovedKeys.Count,
                "D-form key (with hyphens) does NOT match N-form prefix — direct-Upsert callers must use N form.");
            Assert.AreEqual(1, _agency.KolonyEntries.Count, "Mismatched-form entry stays in source.");
            Assert.AreEqual(0, dest.KolonyEntries.Count);
        }

        [TestMethod]
        public void Migrate_PrefixCollision_PipeSeparatorPreventsSubstringFalseMatch()
        {
            // Regression guard: the prefix-scan uses "{vesselId:N}|" — the
            // trailing "|" is load-bearing. Without it, a vessel whose Guid
            // is a PREFIX of another vessel's Guid (extremely unlikely with
            // random Guids, but constructible) would false-positive. Test
            // that the "|" separator is part of the prefix by constructing
            // two synthetic vessel-id strings where one is a prefix of the
            // other and a body-index suffix completes the longer key.
            // The dict key shape forces the "|" separator immediately after
            // the 32-char vesselId, so prefix-scanning "{shorter}|" can
            // never match "{shorter}AB|N" — but the regression bait would
            // be a typo'd scan that strips the "|".
            var movedVesselId = Guid.NewGuid();
            var movedN = movedVesselId.ToString("N");
            // Construct a key that BEGINS with movedN but is NOT a valid
            // body-index match — this synthesises the "vessel-id-as-prefix"
            // hazard. The exact-match prefix "{movedN}|" must catch only the
            // legitimate entry.
            _agency.KolonyEntries[$"{movedN}|3"] = NewEntry(movedN, 3, 100.0);
            // A nonsense key with movedN as a non-pipe-bounded prefix —
            // this can't legitimately exist (Upsert always emits "{vesselId}|"),
            // but the test pins the prefix scan's exactness for future safety.
            _agency.KolonyEntries[$"{movedN}EXTRA|9"] = NewEntry($"{movedN}EXTRA", 9, 200.0);
            var dest = NewSecondaryAgency("KolonyBob");

            var result = AgencyKolonyRouter.MigrateForVesselTransfer(_agency, dest, movedVesselId);

            Assert.AreEqual(1, result.RemovedKeys.Count, "Only the legitimate {vesselId}|N key migrates.");
            Assert.AreEqual($"{movedN}|3", result.RemovedKeys[0]);
            Assert.AreEqual(1, _agency.KolonyEntries.Count, "Synthetic prefix-collision entry stays in source.");
            Assert.IsTrue(_agency.KolonyEntries.ContainsKey($"{movedN}EXTRA|9"));
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
