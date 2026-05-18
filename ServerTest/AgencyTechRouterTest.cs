using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.ShareProgress;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using Server.System.Agency;
using System;
using System.IO;
using System.Text;

namespace ServerTest
{
    /// <summary>
    /// Stage 5.17e-4 — unit tests for <see cref="AgencyTechRouter"/>'s
    /// early-return branches + the per-agency BUG-025 dedup happy path. The router
    /// is invoked with a real <see cref="AgencyState"/> seeded into the registry;
    /// the rejection-send and SaveAgency happy paths require a live
    /// <see cref="Server.Client.ClientStructure"/> + <see cref="Server.Server.MessageQueuer"/>
    /// and are covered end-to-end in <c>MockClientTest/AgencyTechRoutingTest.cs</c>.
    /// These unit tests pin the deterministic dedup decision (Did the tech land
    /// in <see cref="AgencyState.TechNodes"/>? Did the rejection fire instead of
    /// an add?) so a regression to the scope-of-dedup (per-agency vs global)
    /// surfaces immediately.
    /// </summary>
    [TestClass]
    public class AgencyTechRouterTest
    {
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();

        private AgencyState _agency;

        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "lmp-techrouter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();

            _agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "TechAlice",
                DisplayName = "Tech Alice Co",
                Funds = 25_000,
                Science = 100,
                Reputation = 50,
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

            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, recursive: true);
        }

        [TestMethod]
        public void TryRoute_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildTechMsg("start", cost: 10f);

            var handled = AgencyTechRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller runs the shared-agency BUG-025 path");
            Assert.AreEqual(0, _agency.TechNodes.Count,
                "Gate off must not mutate AgencyState.TechNodes");
        }

        [TestMethod]
        public void TryRoute_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var msg = BuildTechMsg("start", cost: 10f);

            var handled = AgencyTechRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled, "Non-Career mode closes the gate even with PerAgencyCareer=true");
            Assert.AreEqual(0, _agency.TechNodes.Count);
        }

        [TestMethod]
        public void TryRoute_NullMsg_ReturnsFalse()
        {
            var handled = AgencyTechRouter.TryRoute(client: null, msg: null);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.TechNodes.Count);
        }

        [TestMethod]
        public void TryRoute_NullTechNode_ReturnsFalse()
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressTechnologyMsgData>();
            msg.TechNode = null;

            var handled = AgencyTechRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.TechNodes.Count);
        }

        [TestMethod]
        public void TryRoute_EmptyTechId_ReturnsFalse()
        {
            // Defensive: a malformed inbound with empty Id must fall through to the
            // legacy path rather than corrupting per-agency state with a "" key.
            var msg = ClientFactory.CreateNewMessageData<ShareProgressTechnologyMsgData>();
            msg.TechNode.Id = string.Empty;

            var handled = AgencyTechRouter.TryRoute(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.TechNodes.Count);
        }

        // -------------------------------------------------------------------
        // Per-agency dedup behavior — directly verified via state mutation
        // -------------------------------------------------------------------

        [TestMethod]
        public void AgencyState_TechNodesRoundTrip_PreservesIdsAndBytes()
        {
            // Pin the disk round-trip independently of the router so a regression
            // in serialization surfaces here, not in the harder-to-debug e2e suite.
            var entry = new AgencyTechNodeEntry
            {
                TechId = "start",
                Data = Encoding.UTF8.GetBytes("id = start\ncost = 0\nstate = Available"),
                NumBytes = 0, // intentionally set below
            };
            entry.NumBytes = entry.Data.Length;
            _agency.TechNodes[entry.TechId] = entry;

            var roundTripped = AgencyState.Parse(_agency.Serialize());

            Assert.AreEqual(1, roundTripped.TechNodes.Count);
            Assert.IsTrue(roundTripped.TechNodes.ContainsKey("start"));
            CollectionAssert.AreEqual(entry.Data, roundTripped.TechNodes["start"].Data,
                "Tech payload bytes must round-trip lossless through Base64.");
            Assert.AreEqual(entry.NumBytes, roundTripped.TechNodes["start"].NumBytes);
        }

        [TestMethod]
        public void AgencyState_EmptyTechNodes_OmitsTechtreeNode()
        {
            // Operator-friendly diff: pristine/empty agency files don't gain an
            // empty TECHTREE block. Same shape as Contracts.
            var serialized = _agency.Serialize();
            Assert.IsFalse(serialized.Contains("TECHTREE"),
                "Empty TechNodes must NOT emit the TECHTREE child node.");
        }

        [TestMethod]
        public void AgencyState_ParseMissingTechtree_LoadsEmptyDictionary()
        {
            // Forward-compat: an agency file saved by a pre-5.17e-4 build (no
            // TECHTREE node) must load cleanly with an empty TechNodes dict.
            var fileText = "AgencyId = " + _agency.AgencyId.ToString("N") + "\n" +
                           "OwningPlayerName = TechAlice\nDisplayName = Tech Alice Co\n" +
                           "Funds = 1000\nScience = 50\nReputation = 5\n";

            var parsed = AgencyState.Parse(fileText);

            Assert.AreEqual(0, parsed.TechNodes.Count,
                "Missing TECHTREE node must parse as empty TechNodes, not throw.");
        }

        [TestMethod]
        public void AgencyState_ParseMissingDataValue_KeepsEntryWithEmptyData()
        {
            // [Round-1 review SHOULD FIX] Operator hand-edit case: TECH entry has
            // an Id but no Data value at all. Per-entry isolation contract says
            // the slot must still be present (so per-agency BUG-025 dedup
            // recognises the tech as unlocked) but Data is empty.
            var fileText = "AgencyId = " + _agency.AgencyId.ToString("N") + "\n" +
                           "OwningPlayerName = TechAlice\nDisplayName = Tech Alice Co\n" +
                           "Funds = 0\nScience = 0\nReputation = 0\n" +
                           "TECHTREE\n{\n  TECH\n  {\n    Id = idOnlyTech\n  }\n}\n";

            var parsed = AgencyState.Parse(fileText);

            Assert.AreEqual(1, parsed.TechNodes.Count);
            Assert.IsTrue(parsed.TechNodes.ContainsKey("idOnlyTech"));
            Assert.AreEqual(0, parsed.TechNodes["idOnlyTech"].NumBytes,
                "Missing Data value must coerce to empty bytes, preserving the slot for dedup.");
        }

        [TestMethod]
        public void AgencyState_ParseMalformedBase64_LengthNotMod4_KeepsEntryWithEmptyData()
        {
            // [Round-1 review CONSIDER] Base64 length-not-divisible-by-4 is a more
            // realistic operator-hand-edit corruption than the random-non-alphabet
            // case below. Convert.FromBase64String throws FormatException for both;
            // verify the parser handles this branch too.
            var fileText = "AgencyId = " + _agency.AgencyId.ToString("N") + "\n" +
                           "OwningPlayerName = TechAlice\nDisplayName = Tech Alice Co\n" +
                           "Funds = 0\nScience = 0\nReputation = 0\n" +
                           "TECHTREE\n{\n  TECH\n  {\n    Id = lenBadTech\n    Data = YWJ\n  }\n}\n";

            var parsed = AgencyState.Parse(fileText);

            Assert.AreEqual(1, parsed.TechNodes.Count);
            Assert.IsTrue(parsed.TechNodes.ContainsKey("lenBadTech"));
            Assert.AreEqual(0, parsed.TechNodes["lenBadTech"].NumBytes,
                "Length-not-mod-4 Base64 must coerce to empty bytes, preserving the slot for dedup.");
        }

        [TestMethod]
        public void AgencyState_ParseMalformedBase64_KeepsEntryWithEmptyData()
        {
            // Per-entry isolation: a malformed Base64 payload in one TECH node
            // must not abort the parent AgencyState load. Entry should still be
            // present (so BUG-025 dedup still recognises the tech as unlocked)
            // but Data is empty.
            var fileText = "AgencyId = " + _agency.AgencyId.ToString("N") + "\n" +
                           "OwningPlayerName = TechAlice\nDisplayName = Tech Alice Co\n" +
                           "Funds = 0\nScience = 0\nReputation = 0\n" +
                           "TECHTREE\n{\n  TECH\n  {\n    Id = brokenTech\n    Data = not-valid-base64!!\n  }\n}\n";

            var parsed = AgencyState.Parse(fileText);

            Assert.AreEqual(1, parsed.TechNodes.Count);
            Assert.IsTrue(parsed.TechNodes.ContainsKey("brokenTech"));
            Assert.AreEqual(0, parsed.TechNodes["brokenTech"].NumBytes,
                "Malformed Base64 must coerce to empty Data, preserving the slot for dedup.");
        }

        private static ShareProgressTechnologyMsgData BuildTechMsg(string techId, float cost)
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressTechnologyMsgData>();
            msg.TechNode.Id = techId;
            var payload = $"id = {techId}\ncost = {cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}\nstate = Available";
            msg.TechNode.Data = Encoding.UTF8.GetBytes(payload);
            msg.TechNode.NumBytes = msg.TechNode.Data.Length;
            return msg;
        }
    }
}
