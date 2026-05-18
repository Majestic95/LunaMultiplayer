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
    /// Stage 5.17e-5 — unit tests for <see cref="AgencyResearchRouter"/>'s
    /// three TryRoute methods (ScienceSubject / PartPurchase /
    /// ExperimentalPart) + the matching <see cref="AgencyState"/> persistence
    /// round-trips for SUBJECTS / PURCHASED_PARTS / EXPERIMENTAL_PARTS child
    /// nodes. Same structure as <see cref="AgencyTechRouterTest"/>: cover the
    /// early-return branches that don't need a live ClientStructure; defer
    /// happy-path wire coverage to <c>MockClientTest/AgencyResearchRoutingTest</c>.
    /// </summary>
    [TestClass]
    public class AgencyResearchRouterTest
    {
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();

        private AgencyState _agency;

        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "lmp-researchrouter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();

            _agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "ResearchAlice",
                DisplayName = "Research Alice Co",
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

        // -------------------------------------------------------------------
        // ScienceSubject
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRouteScienceSubject_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildSubjectMsg("crewReport@Kerbin");
            var handled = AgencyResearchRouter.TryRouteScienceSubject(client: null, msg);
            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.ScienceSubjects.Count);
        }

        [TestMethod]
        public void TryRouteScienceSubject_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var msg = BuildSubjectMsg("crewReport@Kerbin");
            var handled = AgencyResearchRouter.TryRouteScienceSubject(client: null, msg);
            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.ScienceSubjects.Count);
        }

        [TestMethod]
        public void TryRouteScienceSubject_EmptyId_ReturnsFalseWithoutMutating()
        {
            var msg = BuildSubjectMsg(string.Empty);
            var handled = AgencyResearchRouter.TryRouteScienceSubject(client: null, msg);
            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.ScienceSubjects.Count);
        }

        [TestMethod]
        public void TryRouteScienceSubject_NullMsg_ReturnsFalse()
        {
            Assert.IsFalse(AgencyResearchRouter.TryRouteScienceSubject(client: null, msg: null));
        }

        // -------------------------------------------------------------------
        // PartPurchase
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRoutePartPurchase_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildPartMsg("basicRocketry", "RTG10");
            var handled = AgencyResearchRouter.TryRoutePartPurchase(client: null, msg);
            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.PurchasedParts.Count);
        }

        [TestMethod]
        public void TryRoutePartPurchase_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var msg = BuildPartMsg("basicRocketry", "RTG10");
            var handled = AgencyResearchRouter.TryRoutePartPurchase(client: null, msg);
            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.PurchasedParts.Count);
        }

        [TestMethod]
        public void TryRoutePartPurchase_EmptyTechOrPart_ReturnsFalse()
        {
            Assert.IsFalse(AgencyResearchRouter.TryRoutePartPurchase(client: null, BuildPartMsg(string.Empty, "RTG10")));
            Assert.IsFalse(AgencyResearchRouter.TryRoutePartPurchase(client: null, BuildPartMsg("basicRocketry", string.Empty)));
        }

        [TestMethod]
        public void TryRoutePartPurchase_NullMsg_ReturnsFalse()
        {
            Assert.IsFalse(AgencyResearchRouter.TryRoutePartPurchase(client: null, msg: null));
        }

        // -------------------------------------------------------------------
        // ExperimentalPart
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryRouteExperimentalPart_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var msg = BuildExpMsg("RTG10", 1);
            var handled = AgencyResearchRouter.TryRouteExperimentalPart(client: null, msg);
            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.ExperimentalParts.Count);
        }

        [TestMethod]
        public void TryRouteExperimentalPart_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var msg = BuildExpMsg("RTG10", 1);
            var handled = AgencyResearchRouter.TryRouteExperimentalPart(client: null, msg);
            Assert.IsFalse(handled);
            Assert.AreEqual(0, _agency.ExperimentalParts.Count);
        }

        [TestMethod]
        public void TryRouteExperimentalPart_EmptyPartName_ReturnsFalse()
        {
            Assert.IsFalse(AgencyResearchRouter.TryRouteExperimentalPart(client: null, BuildExpMsg(string.Empty, 1)));
        }

        [TestMethod]
        public void TryRouteExperimentalPart_NullMsg_ReturnsFalse()
        {
            Assert.IsFalse(AgencyResearchRouter.TryRouteExperimentalPart(client: null, msg: null));
        }

        // -------------------------------------------------------------------
        // AgencyState round-trip
        // -------------------------------------------------------------------

        [TestMethod]
        public void AgencyState_AllThreeCollectionsRoundTrip()
        {
            _agency.ScienceSubjects["crewReport@Kerbin"] = new AgencyScienceSubjectEntry
            {
                SubjectId = "crewReport@Kerbin",
                Data = Encoding.UTF8.GetBytes("id = crewReport@Kerbin\ndataScale = 1.5"),
            };
            _agency.ScienceSubjects["crewReport@Kerbin"].NumBytes = _agency.ScienceSubjects["crewReport@Kerbin"].Data.Length;

            _agency.PurchasedParts["basicRocketry"] = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                { "RTG10", "fuelTank1-1" };

            _agency.ExperimentalParts["expPart1"] = 3;
            _agency.ExperimentalParts["expPart2"] = 1;

            var roundTripped = AgencyState.Parse(_agency.Serialize());

            Assert.AreEqual(1, roundTripped.ScienceSubjects.Count);
            Assert.IsTrue(roundTripped.ScienceSubjects.ContainsKey("crewReport@Kerbin"));
            CollectionAssert.AreEqual(
                _agency.ScienceSubjects["crewReport@Kerbin"].Data,
                roundTripped.ScienceSubjects["crewReport@Kerbin"].Data);

            Assert.AreEqual(1, roundTripped.PurchasedParts.Count);
            Assert.IsTrue(roundTripped.PurchasedParts.ContainsKey("basicRocketry"));
            Assert.AreEqual(2, roundTripped.PurchasedParts["basicRocketry"].Count);
            Assert.IsTrue(roundTripped.PurchasedParts["basicRocketry"].Contains("RTG10"));
            Assert.IsTrue(roundTripped.PurchasedParts["basicRocketry"].Contains("fuelTank1-1"));

            Assert.AreEqual(2, roundTripped.ExperimentalParts.Count);
            Assert.AreEqual(3, roundTripped.ExperimentalParts["expPart1"]);
            Assert.AreEqual(1, roundTripped.ExperimentalParts["expPart2"]);
        }

        [TestMethod]
        public void AgencyState_EmptyCollections_OmitNodes()
        {
            var serialized = _agency.Serialize();
            Assert.IsFalse(serialized.Contains("SUBJECTS"));
            Assert.IsFalse(serialized.Contains("PURCHASED_PARTS"));
            Assert.IsFalse(serialized.Contains("EXPERIMENTAL_PARTS"));
        }

        [TestMethod]
        public void AgencyState_ParseMissingNodes_LoadsEmptyCollections()
        {
            var fileText = "AgencyId = " + _agency.AgencyId.ToString("N") + "\n" +
                           "OwningPlayerName = R\nDisplayName = R\n" +
                           "Funds = 0\nScience = 0\nReputation = 0\n";

            var parsed = AgencyState.Parse(fileText);

            Assert.AreEqual(0, parsed.ScienceSubjects.Count);
            Assert.AreEqual(0, parsed.PurchasedParts.Count);
            Assert.AreEqual(0, parsed.ExperimentalParts.Count);
        }

        [TestMethod]
        public void AgencyState_ExperimentalPart_CountZero_OmittedOnSerialize()
        {
            _agency.ExperimentalParts["expPart"] = 0;
            var serialized = _agency.Serialize();
            Assert.IsFalse(serialized.Contains("EXPERIMENTAL_PARTS"),
                "Count=0 entries must be omitted on serialize (matches shared writer's remove semantics).");
        }

        [TestMethod]
        public void AgencyState_BraceWrappedRoundTrip_AllNewCollections()
        {
            // [Round-1 review MUST FIX] Operator hand-edited agency files often
            // appear in KSP's brace-wrapped ConfigNode format (the outer wrapper
            // is a KSP serializer artifact). The Parse path already brace-strips
            // for scalar fields; verify the three NEW nested nodes (SUBJECTS,
            // PURCHASED_PARTS, EXPERIMENTAL_PARTS) also round-trip through
            // brace-wrapped input.
            _agency.ScienceSubjects["s1"] = new AgencyScienceSubjectEntry
            {
                SubjectId = "s1",
                Data = Encoding.UTF8.GetBytes("id = s1"),
            };
            _agency.ScienceSubjects["s1"].NumBytes = _agency.ScienceSubjects["s1"].Data.Length;
            _agency.PurchasedParts["t1"] = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { "p1" };
            _agency.ExperimentalParts["e1"] = 4;

            var braceWrapped = "{\n" + _agency.Serialize() + "\n}";
            var roundTripped = AgencyState.Parse(braceWrapped);

            Assert.AreEqual(1, roundTripped.ScienceSubjects.Count, "Brace-wrapped SUBJECTS lost on re-parse.");
            Assert.AreEqual(1, roundTripped.PurchasedParts.Count, "Brace-wrapped PURCHASED_PARTS lost on re-parse.");
            Assert.AreEqual(1, roundTripped.ExperimentalParts.Count, "Brace-wrapped EXPERIMENTAL_PARTS lost on re-parse.");
        }

        // ----- helpers -----------------------------------------------------

        private static ShareProgressScienceSubjectMsgData BuildSubjectMsg(string id)
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressScienceSubjectMsgData>();
            msg.ScienceSubject.Id = id;
            var payload = $"id = {id}\ndataScale = 1.0";
            msg.ScienceSubject.Data = Encoding.UTF8.GetBytes(payload);
            msg.ScienceSubject.NumBytes = msg.ScienceSubject.Data.Length;
            return msg;
        }

        private static ShareProgressPartPurchaseMsgData BuildPartMsg(string techId, string partName)
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressPartPurchaseMsgData>();
            msg.TechId = techId;
            msg.PartName = partName;
            return msg;
        }

        private static ShareProgressExperimentalPartMsgData BuildExpMsg(string partName, int count)
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressExperimentalPartMsgData>();
            msg.PartName = partName;
            msg.Count = count;
            return msg;
        }
    }
}
