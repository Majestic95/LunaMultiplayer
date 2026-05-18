using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the AgencyState ConfigNode round-trip added in Stage 5.14c. These
    /// tests pin the format contracts the rest of Stage 5 depends on:
    /// 1. scalar fields round-trip lossless (GUID, name strings, doubles),
    /// 2. doubles use invariant culture so a server with a comma-decimal locale doesn't
    ///    produce a file that fails to re-parse (BUG-013 family precedent),
    /// 3. parser tolerates the brace-wrapped form so an operator hand-editing the file
    ///    through a KSP-style ConfigNode tool can still load it back,
    /// 4. missing optional fields default cleanly (forward-compat with older files),
    /// 5. missing AgencyId throws — the GUID is the load-bearing identity, a missing
    ///    one signals a worse-than-default state.
    /// </summary>
    [TestClass]
    public class AgencyStateTest
    {
        [TestMethod]
        public void RoundTrip_PreservesAllScalarFields()
        {
            var original = NewSampleAgency();

            var serialized = original.Serialize();
            var restored = AgencyState.Parse(serialized);

            Assert.AreEqual(original.AgencyId, restored.AgencyId);
            Assert.AreEqual(original.OwningPlayerName, restored.OwningPlayerName);
            Assert.AreEqual(original.DisplayName, restored.DisplayName);
            Assert.AreEqual(original.Funds, restored.Funds);
            Assert.AreEqual(original.Science, restored.Science);
            Assert.AreEqual(original.Reputation, restored.Reputation);
        }

        [TestMethod]
        public void FilePath_UsesGuidNFormat()
        {
            // Filename must be the 32-char no-hyphen GUID form so operators get a
            // path-safe filename (no curly braces, no path separators that some shells
            // misread). The "N" format is what we promise per spec Q7 sign-off.
            var state = new AgencyState { AgencyId = Guid.Parse("12345678-1234-1234-1234-1234567890ab") };

            StringAssert.EndsWith(state.FilePath, "123456781234123412341234567890ab.txt");
            Assert.IsTrue(state.FilePath.IndexOf("{") < 0 && state.FilePath.IndexOf("}") < 0,
                "GUID 'N' format does NOT include braces; filename must not either");
        }

        [TestMethod]
        public void Serialize_UsesInvariantCultureForDoubles()
        {
            // Pin the BUG-013-family rule: a Windows server with German locale must not
            // produce "Funds = 12345,5" — that's locale-bleed-through and unparseable on
            // a different host. Force the test thread's culture to German and verify the
            // output stays "12345.5".
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var state = NewSampleAgency();
                state.Funds = 12345.5;

                var serialized = state.Serialize();

                StringAssert.Contains(serialized, "Funds = 12345.5",
                    "doubles must be culture-invariant — period decimal separator regardless of host locale");
                Assert.IsFalse(serialized.Contains("Funds = 12345,5"),
                    "comma decimal separator from host locale would break round-trip on a different host");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [TestMethod]
        public void Parse_ToleratesBraceWrappedInput()
        {
            // Operator workflow: a hand-edited file pasted from a KSP ConfigNode-style
            // tool may have outer {} braces. Parse must strip them, same convention as
            // ScenarioStoreSystem.LoadExistingScenarios.
            var state = NewSampleAgency();
            var wrapped = "{\n" + state.Serialize() + "\n}";

            var restored = AgencyState.Parse(wrapped);

            Assert.AreEqual(state.AgencyId, restored.AgencyId);
            Assert.AreEqual(state.DisplayName, restored.DisplayName);
        }

        [TestMethod]
        public void Parse_MissingOptionalFields_DefaultsToZeroOrEmpty()
        {
            // Forward-compat contract: an older AgencyState file written before a
            // future field exists must still load through the newer binary, with the
            // missing field defaulting to its C# zero. Today every scalar except
            // AgencyId is "optional" in this sense.
            var minimal = "AgencyId = " + Guid.NewGuid().ToString("N");

            var restored = AgencyState.Parse(minimal);

            Assert.AreEqual(string.Empty, restored.OwningPlayerName);
            Assert.AreEqual(string.Empty, restored.DisplayName);
            Assert.AreEqual(0d, restored.Funds);
            Assert.AreEqual(0d, restored.Science);
            Assert.AreEqual(0d, restored.Reputation);
        }

        [TestMethod]
        public void Parse_MissingAgencyId_Throws()
        {
            // The GUID is the canonical identity — without it we cannot route a save
            // back to a known file. Failing loudly here surfaces a corrupt-or-tampered
            // file at boot rather than letting an Empty Guid agency silently displace
            // a real one in the registry.
            var noId = "DisplayName = Orphan\nFunds = 1000";

            Assert.ThrowsException<FormatException>(() => AgencyState.Parse(noId));
        }

        [TestMethod]
        public void Parse_NullInput_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => AgencyState.Parse(null));
        }

        [TestMethod]
        public void FromConfigNode_NullNode_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() => AgencyState.FromConfigNode(null));
        }

        [TestMethod]
        public void ToConfigNode_ReadsBackThroughLunaConfigNode()
        {
            // Compose-then-decompose without going through string. Verifies the
            // ConfigNode itself carries the values; not just the text representation.
            var state = NewSampleAgency();

            var node = state.ToConfigNode();
            var restored = AgencyState.FromConfigNode(node);

            Assert.AreEqual(state.AgencyId, restored.AgencyId);
            Assert.AreEqual(state.OwningPlayerName, restored.OwningPlayerName);
            Assert.AreEqual(state.DisplayName, restored.DisplayName);
            Assert.AreEqual(state.Funds, restored.Funds);
            Assert.AreEqual(state.Science, restored.Science);
            Assert.AreEqual(state.Reputation, restored.Reputation);
        }

        // ---- Stage 5.17d: Contracts list round-trip ----

        [TestMethod]
        public void RoundTrip_PreservesContracts()
        {
            // Stage 5.17d: per-agency Contracts list must survive Serialize/Parse so
            // a server restart preserves the agency's Active+Finished contract pool.
            // Without persistence, any in-flight contract is silently dropped on restart
            // and the agency owner has no recovery path.
            var state = NewSampleAgency();
            var c1Guid = Guid.NewGuid();
            var c2Guid = Guid.NewGuid();
            state.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = c1Guid,
                State = "Active",
                Data = new byte[] { 1, 2, 3, 4, 5 },
                NumBytes = 5,
            });
            state.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = c2Guid,
                State = "Completed",
                Data = System.Text.Encoding.UTF8.GetBytes("guid = abc\nstate = Completed"),
                NumBytes = "guid = abc\nstate = Completed".Length,
            });

            var serialized = state.Serialize();
            var restored = AgencyState.Parse(serialized);

            Assert.AreEqual(2, restored.Contracts.Count);
            Assert.AreEqual(c1Guid, restored.Contracts[0].ContractGuid);
            Assert.AreEqual("Active", restored.Contracts[0].State);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, restored.Contracts[0].Data);
            Assert.AreEqual(c2Guid, restored.Contracts[1].ContractGuid);
            Assert.AreEqual("Completed", restored.Contracts[1].State);
            Assert.AreEqual(state.Contracts[1].NumBytes, restored.Contracts[1].NumBytes);
        }

        [TestMethod]
        public void Parse_MissingContractsNode_LoadsEmptyList()
        {
            // Forward-compat: 5.14c-era AgencyState files lack a CONTRACTS child node.
            // Loading them through the 5.17d parser must yield an empty Contracts list,
            // not throw. Without this, every pre-5.17d agency file would fail to load
            // on first boot after upgrading the server binary.
            var minimal = "AgencyId = " + Guid.NewGuid().ToString("N") + "\nDisplayName = Old Agency";

            var restored = AgencyState.Parse(minimal);

            Assert.AreEqual(0, restored.Contracts.Count);
        }

        [TestMethod]
        public void Serialize_EmptyContractsList_OmitsContractsNode()
        {
            // The CONTRACTS child node should only emit when the list is non-empty.
            // Operator workflow: a fresh agency before any contract Accept should
            // look identical to a 5.14c-era file. Keeps diffs minimal across upgrades.
            var state = NewSampleAgency();

            var serialized = state.Serialize();

            Assert.IsFalse(serialized.Contains("CONTRACTS"),
                "Empty Contracts list should not emit a CONTRACTS sub-node.");
        }

        [TestMethod]
        public void Parse_MalformedContractData_RecoversWithEmptyBytes()
        {
            // Per-entry isolation in the parser: if Base64 decode fails for one contract,
            // the slot loads with empty data (operator can see the broken contract in the
            // list) but the parent AgencyState load succeeds and sibling contracts are
            // preserved. Same shape as the router's per-contract exception isolation
            // (spec §2 Q6 commitment b).
            var guid = Guid.NewGuid();
            var hand = "AgencyId = " + Guid.NewGuid().ToString("N") + "\n" +
                       "CONTRACTS\n{\n" +
                       "  CONTRACT\n  {\n" +
                       "    Guid = " + guid.ToString("N") + "\n" +
                       "    State = Active\n" +
                       "    Data = !!not valid base64!!\n" +
                       "  }\n" +
                       "}\n";

            var restored = AgencyState.Parse(hand);

            Assert.AreEqual(1, restored.Contracts.Count, "Malformed Data must not drop the slot.");
            Assert.AreEqual(guid, restored.Contracts[0].ContractGuid);
            Assert.AreEqual("Active", restored.Contracts[0].State);
            Assert.AreEqual(0, restored.Contracts[0].NumBytes, "Malformed Data must reduce to empty NumBytes.");
        }

        // ---- Phase 3 Slice A: Kolony / Planetary / Orbital state round-trip ----

        [TestMethod]
        public void Phase3_RoundTrip_PreservesAllThreeDicts()
        {
            // Single-entry-per-dict round-trip pins the basic format contract:
            // every field of every entry type survives Serialize → Parse. Covers
            // pre-spec §7.a item "PopulatedDict_RoundTripsViaSerialize" + item
            // "Base64PayloadIntegrity_OrbitalTransfer" together.
            var state = NewSampleAgency();
            var kolonyKey = $"abcd1234abcd1234abcd1234abcd1234|3";
            state.KolonyEntries[kolonyKey] = new AgencyKolonyEntry
            {
                VesselId = "abcd1234abcd1234abcd1234abcd1234",
                BodyIndex = 3,
                LastUpdate = 1_000_000.5,
                KolonyDate = 999_999.25,
                GeologyResearch = 12.5,
                BotanyResearch = 7.0,
                KolonizationResearch = 3.125,
                Science = 42.0,
                Reputation = 1.5,
                Funds = 100.0,
                RepBoosters = 1,
                FundsBoosters = 2,
                ScienceBoosters = 3,
            };

            var planetaryVessel = Guid.NewGuid();
            var planetaryKey = $"3|Hydrates";
            state.PlanetaryEntries[planetaryKey] = new AgencyPlanetaryEntry
            {
                OwningVesselId = planetaryVessel,
                BodyIndex = 3,
                ResourceName = "Hydrates",
                StoredQuantity = 50_000.5,
            };

            var transferGuid = Guid.NewGuid();
            var originVessel = Guid.NewGuid();
            var destVessel = Guid.NewGuid();
            var payload = System.Text.Encoding.UTF8.GetBytes("TRANSFER { status = Launched }");
            state.OrbitalTransfers[transferGuid] = new AgencyOrbitalTransferEntry
            {
                TransferGuid = transferGuid,
                OriginVesselId = originVessel,
                DestinationVesselId = destVessel,
                Status = 1, // Launched
                StartTime = 12345.0,
                Duration = 600.0,
                PayloadBytes = payload,
                NumBytes = payload.Length,
            };

            var serialized = state.Serialize();
            var restored = AgencyState.Parse(serialized);

            Assert.AreEqual(1, restored.KolonyEntries.Count);
            var k = restored.KolonyEntries[kolonyKey];
            Assert.AreEqual("abcd1234abcd1234abcd1234abcd1234", k.VesselId);
            Assert.AreEqual(3, k.BodyIndex);
            Assert.AreEqual(1_000_000.5, k.LastUpdate);
            Assert.AreEqual(999_999.25, k.KolonyDate);
            Assert.AreEqual(12.5, k.GeologyResearch);
            Assert.AreEqual(7.0, k.BotanyResearch);
            Assert.AreEqual(3.125, k.KolonizationResearch);
            Assert.AreEqual(42.0, k.Science);
            Assert.AreEqual(1.5, k.Reputation);
            Assert.AreEqual(100.0, k.Funds);
            Assert.AreEqual(1, k.RepBoosters);
            Assert.AreEqual(2, k.FundsBoosters);
            Assert.AreEqual(3, k.ScienceBoosters);

            Assert.AreEqual(1, restored.PlanetaryEntries.Count);
            var p = restored.PlanetaryEntries[planetaryKey];
            Assert.AreEqual(planetaryVessel, p.OwningVesselId);
            Assert.AreEqual(3, p.BodyIndex);
            Assert.AreEqual("Hydrates", p.ResourceName);
            Assert.AreEqual(50_000.5, p.StoredQuantity);

            Assert.AreEqual(1, restored.OrbitalTransfers.Count);
            var o = restored.OrbitalTransfers[transferGuid];
            Assert.AreEqual(transferGuid, o.TransferGuid);
            Assert.AreEqual(originVessel, o.OriginVesselId);
            Assert.AreEqual(destVessel, o.DestinationVesselId);
            Assert.AreEqual(1, o.Status);
            Assert.AreEqual(12345.0, o.StartTime);
            Assert.AreEqual(600.0, o.Duration);
            CollectionAssert.AreEqual(payload, o.PayloadBytes);
            Assert.AreEqual(payload.Length, o.NumBytes);
        }

        [TestMethod]
        public void Phase3_Serialize_EmptyDicts_OmitsAllThreeNodes()
        {
            // Pre-spec §7.a item "EmptyDict_OmittedFromConfigNodeOutput": pristine
            // agencies (no Phase 3 state yet — fresh-create OR pre-Phase-3 upgrade)
            // must emit AgencyState files that are visually identical to 5.17e-era
            // shape. Operators diffing files across the upgrade should see no new
            // sub-nodes until Phase 3 routers actually populate entries.
            var state = NewSampleAgency();

            var serialized = state.Serialize();

            Assert.IsFalse(serialized.Contains("KOLONY_ENTRIES"),
                "Empty KolonyEntries dict must not emit KOLONY_ENTRIES sub-node.");
            Assert.IsFalse(serialized.Contains("PLANETARY_ENTRIES"),
                "Empty PlanetaryEntries dict must not emit PLANETARY_ENTRIES sub-node.");
            Assert.IsFalse(serialized.Contains("ORBITAL_TRANSFERS"),
                "Empty OrbitalTransfers dict must not emit ORBITAL_TRANSFERS sub-node.");
        }

        [TestMethod]
        public void Phase3_Parse_MissingPhase3Nodes_LoadsEmptyDicts()
        {
            // Pre-spec §7.a item "MissingChildNode_LoadsAsEmptyDict": forward-compat.
            // A 5.17e-era AgencyState file (no Phase 3 sub-nodes) must load through
            // the Phase 3 parser with empty Kolony/Planetary/Orbital dicts. Without
            // this, every pre-Phase-3 agency file would fail to load on the first
            // boot after the Slice A binary ships.
            var minimal = "AgencyId = " + Guid.NewGuid().ToString("N") + "\nDisplayName = Old Agency";

            var restored = AgencyState.Parse(minimal);

            Assert.AreEqual(0, restored.KolonyEntries.Count);
            Assert.AreEqual(0, restored.PlanetaryEntries.Count);
            Assert.AreEqual(0, restored.OrbitalTransfers.Count);
        }

        [TestMethod]
        public void Phase3_Parse_MalformedKolonyDouble_RecoversWithZero()
        {
            // Pre-spec §7.a item "MalformedEntry_IsolatedAndSkipped" (kolony flavor):
            // an operator-hand-edited file with an unparseable numeric value must
            // not abort the parent agency load. The malformed field defaults to 0
            // via ParseDoubleOrZero; the rest of the entry loads normally. Sibling
            // entries (and the parent agency) are unaffected. Same per-entry
            // isolation rule as Contracts (Parse_MalformedContractData test above).
            var hand = "AgencyId = " + Guid.NewGuid().ToString("N") + "\n" +
                       "KOLONY_ENTRIES\n{\n" +
                       "  KOLONY\n  {\n" +
                       "    VesselId = abcd\n" +
                       "    BodyIndex = 3\n" +
                       "    LastUpdate = NOT_A_DOUBLE\n" +
                       "    GeologyResearch = 12.5\n" +
                       "  }\n" +
                       "}\n";

            var restored = AgencyState.Parse(hand);

            Assert.AreEqual(1, restored.KolonyEntries.Count, "Malformed double must not drop the slot.");
            var entry = restored.KolonyEntries["abcd|3"];
            Assert.AreEqual(0d, entry.LastUpdate, "Malformed LastUpdate must default to 0.");
            Assert.AreEqual(12.5, entry.GeologyResearch, "Sibling fields must load normally.");
        }

        [TestMethod]
        public void Phase3_Parse_MalformedOrbitalPayloadBase64_RecoversWithEmptyBytes()
        {
            // Pre-spec §7.a item "MalformedEntry_IsolatedAndSkipped" (orbital
            // flavor) AND "Base64PayloadIntegrity_OrbitalTransfer" complement.
            // Per-entry isolation for the Base64 decoder: if PayloadBytes is
            // unparseable, the slot loads with empty bytes (operator can see the
            // broken transfer) but TransferGuid + Status + StartTime etc. survive.
            // Same rule as Contracts Parse_MalformedContractData.
            var guid = Guid.NewGuid();
            var hand = "AgencyId = " + Guid.NewGuid().ToString("N") + "\n" +
                       "ORBITAL_TRANSFERS\n{\n" +
                       "  TRANSFER\n  {\n" +
                       "    TransferGuid = " + guid.ToString("N") + "\n" +
                       "    Status = 1\n" +
                       "    StartTime = 12345\n" +
                       "    Duration = 600\n" +
                       "    PayloadBytes = !!not valid base64!!\n" +
                       "  }\n" +
                       "}\n";

            var restored = AgencyState.Parse(hand);

            Assert.AreEqual(1, restored.OrbitalTransfers.Count, "Malformed Base64 must not drop the slot.");
            var entry = restored.OrbitalTransfers[guid];
            Assert.AreEqual(guid, entry.TransferGuid);
            Assert.AreEqual(1, entry.Status);
            Assert.AreEqual(12345d, entry.StartTime);
            Assert.AreEqual(0, entry.NumBytes, "Malformed Base64 must reduce to empty NumBytes.");
            Assert.AreEqual(0, entry.PayloadBytes.Length);
        }

        [TestMethod]
        public void Phase3_RoundTrip_MultipleEntriesPerDict_PreservesAllKeys()
        {
            // Pre-spec §7.a item "ConcurrentSaveDuringMutate_NoTornState" is
            // covered by the router-level Slice B/C/D tests (this class doesn't
            // exercise the per-agency lock contract). This test instead catches
            // a related class of bug: iterator emits only the first entry per
            // dict. Pin all-keys-preserved across 3-5 entries per dict.
            var state = NewSampleAgency();

            for (var i = 0; i < 4; i++)
            {
                var v = $"vessel{i:D2}vessel{i:D2}vessel{i:D2}vessel{i:D2}"; // 32 chars
                state.KolonyEntries[$"{v}|{i}"] = new AgencyKolonyEntry
                {
                    VesselId = v,
                    BodyIndex = i,
                    GeologyResearch = i * 10.0,
                };
            }
            var planetaryVesselIds = new Dictionary<string, Guid>();
            for (var i = 0; i < 3; i++)
            {
                var vesselGuid = Guid.NewGuid();
                var key = $"5|Resource{i}";
                planetaryVesselIds[key] = vesselGuid;
                state.PlanetaryEntries[key] = new AgencyPlanetaryEntry
                {
                    OwningVesselId = vesselGuid,
                    BodyIndex = 5,
                    ResourceName = $"Resource{i}",
                    StoredQuantity = i * 100.0,
                };
            }
            var orbitalGuids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            foreach (var g in orbitalGuids)
            {
                state.OrbitalTransfers[g] = new AgencyOrbitalTransferEntry
                {
                    TransferGuid = g,
                    Status = 1,
                };
            }

            var restored = AgencyState.Parse(state.Serialize());

            Assert.AreEqual(4, restored.KolonyEntries.Count, "All 4 kolony entries must round-trip.");
            Assert.AreEqual(3, restored.PlanetaryEntries.Count, "All 3 planetary entries must round-trip.");
            Assert.AreEqual(5, restored.OrbitalTransfers.Count, "All 5 orbital transfers must round-trip.");

            // Per-key value-equality on planetary OwningVesselId — review-finding-#4
            // catches the class of regression where the iterator emits the right
            // COUNT of entries but Guid fields deserialise to Empty for siblings.
            foreach (var kvp in planetaryVesselIds)
            {
                Assert.AreEqual(kvp.Value, restored.PlanetaryEntries[kvp.Key].OwningVesselId,
                    $"Planetary OwningVesselId for {kvp.Key} did not round-trip correctly.");
            }
            foreach (var g in orbitalGuids)
            {
                Assert.IsTrue(restored.OrbitalTransfers.ContainsKey(g), $"Orbital transfer {g:N} missing from restored dict.");
                Assert.AreEqual(g, restored.OrbitalTransfers[g].TransferGuid,
                    $"Orbital transfer {g:N} TransferGuid did not round-trip correctly.");
            }
        }

        [TestMethod]
        public void Phase3_Serialize_UsesInvariantCultureForKolonyDoubles()
        {
            // Mirror of Serialize_UsesInvariantCultureForDoubles for the new
            // Phase 3 scalar fields. A Windows server with German locale must
            // not emit "GeologyResearch = 12,5" — the comma decimal separator
            // would fail to re-parse on a host with a different locale (the
            // BUG-013 family precedent that Stage 5.14c's invariant-culture
            // serialize was designed to prevent).
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var state = NewSampleAgency();
                state.KolonyEntries["abcd|0"] = new AgencyKolonyEntry
                {
                    VesselId = "abcd",
                    BodyIndex = 0,
                    GeologyResearch = 12.5,
                    Funds = 1234.5,
                };
                state.PlanetaryEntries["0|X"] = new AgencyPlanetaryEntry
                {
                    OwningVesselId = Guid.NewGuid(),
                    BodyIndex = 0,
                    ResourceName = "X",
                    StoredQuantity = 9876.5,
                };

                var serialized = state.Serialize();

                StringAssert.Contains(serialized, "GeologyResearch = 12.5",
                    "Kolony doubles must use period decimal separator regardless of host locale.");
                StringAssert.Contains(serialized, "Funds = 1234.5",
                    "Kolony Funds must use period decimal separator.");
                StringAssert.Contains(serialized, "StoredQuantity = 9876.5",
                    "Planetary StoredQuantity must use period decimal separator.");
                Assert.IsFalse(serialized.Contains(",5"),
                    "Comma decimal separator from host locale would break round-trip on a different host.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        private static AgencyState NewSampleAgency() => new AgencyState
        {
            AgencyId = Guid.NewGuid(),
            OwningPlayerName = "Majestic95",
            DisplayName = "Majestic95 Space Agency",
            Funds = 25_000.0,
            Science = 12.5,
            Reputation = 7.25,
        };
    }
}
