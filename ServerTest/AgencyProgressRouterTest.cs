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
    /// Stage 5.17e-6 — unit tests for <see cref="AgencyProgressRouter"/>'s
    /// three TryRoute methods (Strategy / Achievement / FacilityUpgrade) +
    /// matching AgencyState persistence round-trips for STRATEGIES /
    /// ACHIEVEMENTS / FACILITY_LEVELS child nodes. Same shape as
    /// <see cref="AgencyResearchRouterTest"/>.
    /// </summary>
    [TestClass]
    public class AgencyProgressRouterTest
    {
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();
        private AgencyState _agency;

        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "lmp-progressrouter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();

            _agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "ProgAlice",
                DisplayName = "Prog Alice Co",
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

        // Strategy ---------------------------------------------------------

        [TestMethod]
        public void TryRouteStrategy_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            Assert.IsFalse(AgencyProgressRouter.TryRouteStrategy(null, BuildStratMsg("AggressiveNegotiations")));
            Assert.AreEqual(0, _agency.Strategies.Count);
        }

        [TestMethod]
        public void TryRouteStrategy_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            Assert.IsFalse(AgencyProgressRouter.TryRouteStrategy(null, BuildStratMsg("X")));
            Assert.AreEqual(0, _agency.Strategies.Count);
        }

        [TestMethod]
        public void TryRouteStrategy_NullOrEmptyName_ReturnsFalse()
        {
            Assert.IsFalse(AgencyProgressRouter.TryRouteStrategy(null, BuildStratMsg(string.Empty)));
            Assert.IsFalse(AgencyProgressRouter.TryRouteStrategy(null, null));
        }

        // Achievement ------------------------------------------------------

        [TestMethod]
        public void TryRouteAchievement_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            Assert.IsFalse(AgencyProgressRouter.TryRouteAchievement(null, BuildAchMsg("FirstLaunch")));
            Assert.AreEqual(0, _agency.Achievements.Count);
        }

        [TestMethod]
        public void TryRouteAchievement_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            Assert.IsFalse(AgencyProgressRouter.TryRouteAchievement(null, BuildAchMsg("X")));
            Assert.AreEqual(0, _agency.Achievements.Count);
        }

        [TestMethod]
        public void TryRouteAchievement_NullOrEmptyId_ReturnsFalse()
        {
            Assert.IsFalse(AgencyProgressRouter.TryRouteAchievement(null, BuildAchMsg(string.Empty)));
            Assert.IsFalse(AgencyProgressRouter.TryRouteAchievement(null, null));
        }

        // FacilityUpgrade --------------------------------------------------

        [TestMethod]
        public void TryRouteFacilityUpgrade_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            Assert.IsFalse(AgencyProgressRouter.TryRouteFacilityUpgrade(null, BuildFacMsg("SpaceCenter/LaunchPad", 0.5f)));
            Assert.AreEqual(0, _agency.FacilityLevels.Count);
        }

        [TestMethod]
        public void TryRouteFacilityUpgrade_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            Assert.IsFalse(AgencyProgressRouter.TryRouteFacilityUpgrade(null, BuildFacMsg("SpaceCenter/LaunchPad", 0.5f)));
            Assert.AreEqual(0, _agency.FacilityLevels.Count);
        }

        [TestMethod]
        public void TryRouteFacilityUpgrade_NullOrEmptyId_ReturnsFalse()
        {
            Assert.IsFalse(AgencyProgressRouter.TryRouteFacilityUpgrade(null, BuildFacMsg(string.Empty, 0.5f)));
            Assert.IsFalse(AgencyProgressRouter.TryRouteFacilityUpgrade(null, null));
        }

        // AgencyState round-trip -------------------------------------------

        [TestMethod]
        public void AgencyState_AllThreeNewCollections_RoundTrip()
        {
            _agency.Strategies["AggressiveNegotiations"] = new AgencyStrategyEntry
            {
                StrategyName = "AggressiveNegotiations",
                Data = Encoding.UTF8.GetBytes("name = AggressiveNegotiations\nfactor = 0.5"),
            };
            _agency.Strategies["AggressiveNegotiations"].NumBytes = _agency.Strategies["AggressiveNegotiations"].Data.Length;
            _agency.Achievements["Kerbin/RocketLaunch"] = new AgencyAchievementEntry
            {
                Id = "Kerbin/RocketLaunch",
                Data = Encoding.UTF8.GetBytes("completed = True\nrewarded = True"),
            };
            _agency.Achievements["Kerbin/RocketLaunch"].NumBytes = _agency.Achievements["Kerbin/RocketLaunch"].Data.Length;
            _agency.FacilityLevels["SpaceCenter/LaunchPad"] = 0.5f;
            _agency.FacilityLevels["SpaceCenter/VehicleAssemblyBuilding"] = 1.0f;

            var roundTripped = AgencyState.Parse(_agency.Serialize());

            Assert.AreEqual(1, roundTripped.Strategies.Count);
            Assert.IsTrue(roundTripped.Strategies.ContainsKey("AggressiveNegotiations"));
            CollectionAssert.AreEqual(
                _agency.Strategies["AggressiveNegotiations"].Data,
                roundTripped.Strategies["AggressiveNegotiations"].Data);

            Assert.AreEqual(1, roundTripped.Achievements.Count);
            Assert.IsTrue(roundTripped.Achievements.ContainsKey("Kerbin/RocketLaunch"));

            Assert.AreEqual(2, roundTripped.FacilityLevels.Count);
            Assert.AreEqual(0.5f, roundTripped.FacilityLevels["SpaceCenter/LaunchPad"]);
            Assert.AreEqual(1.0f, roundTripped.FacilityLevels["SpaceCenter/VehicleAssemblyBuilding"]);
        }

        [TestMethod]
        public void AgencyState_EmptyCollections_OmitNodes()
        {
            var serialized = _agency.Serialize();
            Assert.IsFalse(serialized.Contains("STRATEGIES"));
            Assert.IsFalse(serialized.Contains("ACHIEVEMENTS"));
            Assert.IsFalse(serialized.Contains("FACILITY_LEVELS"));
        }

        [TestMethod]
        public void AgencyState_ParseMissingNodes_LoadsEmptyCollections()
        {
            var fileText = "AgencyId = " + _agency.AgencyId.ToString("N") + "\n" +
                           "OwningPlayerName = P\nDisplayName = P\n" +
                           "Funds = 0\nScience = 0\nReputation = 0\n";
            var parsed = AgencyState.Parse(fileText);
            Assert.AreEqual(0, parsed.Strategies.Count);
            Assert.AreEqual(0, parsed.Achievements.Count);
            Assert.AreEqual(0, parsed.FacilityLevels.Count);
        }

        // helpers ----------------------------------------------------------

        private static ShareProgressStrategyMsgData BuildStratMsg(string name)
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressStrategyMsgData>();
            msg.Strategy.Name = name;
            var payload = $"name = {name}";
            msg.Strategy.Data = Encoding.UTF8.GetBytes(payload);
            msg.Strategy.NumBytes = msg.Strategy.Data.Length;
            return msg;
        }

        private static ShareProgressAchievementsMsgData BuildAchMsg(string id)
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressAchievementsMsgData>();
            msg.Id = id;
            var payload = $"completed = True";
            msg.Data = Encoding.UTF8.GetBytes(payload);
            msg.NumBytes = msg.Data.Length;
            return msg;
        }

        private static ShareProgressFacilityUpgradeMsgData BuildFacMsg(string id, float normLevel)
        {
            var msg = ClientFactory.CreateNewMessageData<ShareProgressFacilityUpgradeMsgData>();
            msg.FacilityId = id;
            msg.NormLevel = normLevel;
            msg.Level = 1;
            return msg;
        }
    }
}
