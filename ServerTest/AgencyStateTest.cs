using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;
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
