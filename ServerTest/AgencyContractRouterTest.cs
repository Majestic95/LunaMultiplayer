using LmpCommon.Message.Data.ShareProgress;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;
using System;
using System.Text;

namespace ServerTest
{
    /// <summary>
    /// Stage 5.17d — unit tests for the deterministic core of <see cref="AgencyContractRouter"/>:
    /// state classification (<c>ReadContractState</c>) and per-agency upsert
    /// (<c>Upsert</c>). The router's full <c>TryRoute</c> path requires a real
    /// <see cref="Server.Client.ClientStructure"/> with a live <c>SendMessageQueue</c> and
    /// the production <see cref="Server.Server.MessageQueuer"/> infrastructure; that
    /// surface is covered end-to-end in <c>MockClientTest/AgencyContractRoutingTest.cs</c>.
    /// These unit tests pin the pure logic so a regression to classification or upsert
    /// surfaces before the e2e harness pays the cost of bringing the wire up.
    ///
    /// All test inputs use the DECOMPRESSED contract-byte form — the same form the
    /// router sees after Lidgren's <see cref="ContractInfo.Deserialize"/> path has run
    /// <c>Common.ThreadSafeDecompress</c> on the wire payload. The router never holds
    /// compressed bytes in its hot path.
    /// </summary>
    [TestClass]
    public class AgencyContractRouterTest
    {
        [TestMethod]
        public void ReadContractState_BareKeyValueForm_ReturnsStateValue()
        {
            // The proto form ScenarioDataUpdater.ParseClientConfigNode handles in
            // production: bare lines, no outer braces.
            var contract = NewContract(Guid.NewGuid(),
                "guid = " + Guid.NewGuid().ToString("N") + "\nstate = Active\nprestige = 0");

            var state = AgencyContractRouter.ReadContractState(contract);

            Assert.AreEqual("Active", state);
        }

        [TestMethod]
        public void ReadContractState_BraceWrappedForm_StripsBracesAndReadsState()
        {
            // KSP's ConfigNode.WriteNode wraps unnamed nodes in "{\n\t...\n}" — the router
            // must strip the wrapper before LunaConfigNode parses, matching the
            // ScenarioDataUpdater.ParseClientConfigNode contract for inbound contracts.
            var contract = NewContract(Guid.NewGuid(),
                "{\n\tguid = abc\n\tstate = Completed\n}");

            Assert.AreEqual("Completed", AgencyContractRouter.ReadContractState(contract));
        }

        [TestMethod]
        public void ReadContractState_MissingStateField_ReturnsEmpty()
        {
            // Defensive: a contract payload that somehow doesn't carry a state line
            // gets classified as non-Offered (per-agency) so a malformed payload doesn't
            // accidentally pollute the shared Offered pool. Empty string is the signal.
            var contract = NewContract(Guid.NewGuid(),
                "guid = " + Guid.NewGuid().ToString("N") + "\nprestige = 0");

            Assert.AreEqual(string.Empty, AgencyContractRouter.ReadContractState(contract));
        }

        [TestMethod]
        public void ReadContractState_ZeroBytes_ReturnsEmpty()
        {
            // Defensive against an empty payload (test harness mis-built, or a wire
            // peer that mis-built ContractInfo). Empty input ⇒ empty state ⇒ classified
            // as per-agency (same as the missing-field case).
            var contract = new ContractInfo { ContractGuid = Guid.NewGuid(), NumBytes = 0, Data = new byte[0] };

            Assert.AreEqual(string.Empty, AgencyContractRouter.ReadContractState(contract));
        }

        [TestMethod]
        public void Upsert_NewContract_AppendsToAgencyContracts()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var guid = Guid.NewGuid();
            var contract = NewContract(guid, "guid = " + guid.ToString("N") + "\nstate = Active");

            AgencyContractRouter.Upsert(agency, contract);

            Assert.AreEqual(1, agency.Contracts.Count);
            Assert.AreEqual(guid, agency.Contracts[0].ContractGuid);
            Assert.AreEqual("Active", agency.Contracts[0].State);
        }

        [TestMethod]
        public void Upsert_ExistingGuid_ReplacesEntryInPlace_NotAppend()
        {
            // Upsert semantics: a re-arrival of the same contract guid overwrites the
            // prior snapshot, never duplicates. The shared ScenarioContractsDataUpdater
            // does the same; AgencyContractRouter must match so the per-agency Active
            // list doesn't grow unbounded on the second update for the same contract.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var guid = Guid.NewGuid();
            agency.Contracts.Add(new AgencyContractEntry { ContractGuid = guid, State = "Active", Data = new byte[] { 1, 2, 3 }, NumBytes = 3 });

            var newer = NewContract(guid, "guid = " + guid.ToString("N") + "\nstate = Completed\nvalues = 1,2,3");

            AgencyContractRouter.Upsert(agency, newer);

            Assert.AreEqual(1, agency.Contracts.Count, "Upsert must not append for a duplicate guid.");
            Assert.AreEqual("Completed", agency.Contracts[0].State, "State did not move from Active → Completed on re-arrival.");
        }

        [TestMethod]
        public void Upsert_DefensiveCopyOfDataBytes()
        {
            // The wire ContractInfo's Data array is mutated by Common.ThreadSafeCompress
            // on subsequent serialize calls (compression operates in-place on Data, ref).
            // If the router stored the reference directly, any later re-serialize of the
            // same ContractInfo would silently corrupt the persisted bytes. Verify the
            // stored entry holds an independent buffer.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var guid = Guid.NewGuid();
            var payload = Encoding.UTF8.GetBytes("guid = " + guid.ToString("N") + "\nstate = Active");
            var contract = new ContractInfo { ContractGuid = guid, NumBytes = payload.Length, Data = payload };

            AgencyContractRouter.Upsert(agency, contract);

            // Mutate the source array AFTER store. The stored entry must not see it.
            for (var i = 0; i < payload.Length; i++)
                payload[i] = 0xFF;

            var stored = agency.Contracts[0];
            Assert.AreNotSame(payload, stored.Data, "Upsert must defensive-copy contract Data — held the wire array directly.");
            for (var i = 0; i < stored.NumBytes; i++)
                Assert.AreNotEqual(0xFF, stored.Data[i], $"Stored Data[{i}] was mutated by post-store source mutation — defensive copy failed.");
        }

        [TestMethod]
        public void Upsert_NullAgency_Throws()
        {
            var contract = NewContract(Guid.NewGuid(), "state = Active");
            Assert.ThrowsException<ArgumentNullException>(() => AgencyContractRouter.Upsert(null, contract));
        }

        [TestMethod]
        public void Upsert_NullContract_Throws()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            Assert.ThrowsException<ArgumentNullException>(() => AgencyContractRouter.Upsert(agency, null));
        }

        [TestMethod]
        public void Upsert_DataLenClampedToArrayLength()
        {
            // Defensive against a malformed wire payload where ContractInfo.NumBytes
            // exceeds Data.Length (would otherwise let an oversized NumBytes drive a
            // larger-than-input Buffer.BlockCopy and overrun garbage from the unused
            // tail of the array buffer). Verify that NumBytes is clamped to Data.Length.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var guid = Guid.NewGuid();
            var realBytes = Encoding.UTF8.GetBytes("guid = " + guid.ToString("N") + "\nstate = Failed");
            var contract = new ContractInfo
            {
                ContractGuid = guid,
                Data = realBytes,
                NumBytes = realBytes.Length + 100, // lies — claim more than we actually have
            };

            AgencyContractRouter.Upsert(agency, contract);

            Assert.AreEqual(realBytes.Length, agency.Contracts[0].NumBytes,
                "Upsert must clamp NumBytes to Data.Length on the inbound payload.");
        }

        private static ContractInfo NewContract(Guid contractGuid, string serializedConfigNode)
        {
            var bytes = Encoding.UTF8.GetBytes(serializedConfigNode);
            return new ContractInfo
            {
                ContractGuid = contractGuid,
                Data = bytes,
                NumBytes = bytes.Length,
            };
        }
    }
}
