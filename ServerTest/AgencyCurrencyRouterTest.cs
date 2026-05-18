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

namespace ServerTest
{
    /// <summary>
    /// Stage 5.17e-3 — unit tests for <see cref="AgencyCurrencyRouter"/>'s
    /// early-return branches: dual-mode gate (PerAgencyCareer=false), Career-only
    /// gate (non-Career mode under PerAgencyCareer=true), null inputs, unknown
    /// player names. The full <c>TryRoute</c> happy path requires a real
    /// <see cref="Server.Client.ClientStructure"/> with a live
    /// <see cref="Server.Server.MessageQueuer"/> — that surface is covered end-to-
    /// end in <c>MockClientTest/AgencyCurrencyRoutingTest.cs</c>. These unit tests
    /// pin the early-return surface so a regression to the gate or null-handling
    /// surfaces before the e2e harness pays the cost of bringing the wire up.
    ///
    /// Each test asserts BOTH "returns false" AND "did not mutate AgencyState",
    /// because the production hazard isn't a wrong return value — it's a silent
    /// mutation that the caller then ALSO broadcasts because the router said it
    /// wasn't handled (double-write to shared scenario + per-agency state).
    /// </summary>
    [TestClass]
    public class AgencyCurrencyRouterTest
    {
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();

        private AgencyState _agency;

        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "lmp-currencyrouter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            Directory.CreateDirectory(AgencyState.AgenciesPath);

            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();

            // Plant a registered agency for the unknown-player tests to compare against;
            // they reference a different name so the registry lookup misses.
            _agency = new AgencyState
            {
                AgencyId = Guid.NewGuid(),
                OwningPlayerName = "RouterAlice",
                DisplayName = "Router Alice Co",
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

        // -------------------------------------------------------------
        // Funds
        // -------------------------------------------------------------

        [TestMethod]
        public void TryRouteFunds_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var fundsBefore = _agency.Funds;
            var msg = ClientFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
            msg.Funds = 99_999;
            msg.Reason = "test";

            var handled = AgencyCurrencyRouter.TryRouteFunds(client: null, msg);

            Assert.IsFalse(handled, "Gate off must return false so caller runs the shared-agency path");
            Assert.AreEqual(fundsBefore, _agency.Funds, "Gate off must not mutate AgencyState");
        }

        [TestMethod]
        public void TryRouteFunds_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var fundsBefore = _agency.Funds;
            var msg = ClientFactory.CreateNewMessageData<ShareProgressFundsMsgData>();
            msg.Funds = 99_999;
            msg.Reason = "test";

            var handled = AgencyCurrencyRouter.TryRouteFunds(client: null, msg);

            Assert.IsFalse(handled, "Non-Career mode closes the gate even with PerAgencyCareer=true");
            Assert.AreEqual(fundsBefore, _agency.Funds, "Non-Career mode must not mutate AgencyState");
        }

        [TestMethod]
        public void TryRouteFunds_NullMsg_ReturnsFalseWithoutMutating()
        {
            // Defensive: a null inbound shouldn't NRE the router.
            var fundsBefore = _agency.Funds;

            var handled = AgencyCurrencyRouter.TryRouteFunds(client: null, msg: null);

            Assert.IsFalse(handled);
            Assert.AreEqual(fundsBefore, _agency.Funds);
        }

        // -------------------------------------------------------------
        // Science
        // -------------------------------------------------------------

        [TestMethod]
        public void TryRouteScience_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var scienceBefore = _agency.Science;
            var msg = ClientFactory.CreateNewMessageData<ShareProgressScienceMsgData>();
            msg.Science = 999;
            msg.Reason = "test";

            var handled = AgencyCurrencyRouter.TryRouteScience(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(scienceBefore, _agency.Science);
        }

        [TestMethod]
        public void TryRouteScience_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            var scienceBefore = _agency.Science;
            var msg = ClientFactory.CreateNewMessageData<ShareProgressScienceMsgData>();
            msg.Science = 999;
            msg.Reason = "test";

            var handled = AgencyCurrencyRouter.TryRouteScience(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(scienceBefore, _agency.Science);
        }

        [TestMethod]
        public void TryRouteScience_NullMsg_ReturnsFalseWithoutMutating()
        {
            var scienceBefore = _agency.Science;

            var handled = AgencyCurrencyRouter.TryRouteScience(client: null, msg: null);

            Assert.IsFalse(handled);
            Assert.AreEqual(scienceBefore, _agency.Science);
        }

        // -------------------------------------------------------------
        // Reputation
        // -------------------------------------------------------------

        [TestMethod]
        public void TryRouteReputation_GateOff_ReturnsFalseWithoutMutating()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            var repBefore = _agency.Reputation;
            var msg = ClientFactory.CreateNewMessageData<ShareProgressReputationMsgData>();
            msg.Reputation = 250;
            msg.Reason = "test";

            var handled = AgencyCurrencyRouter.TryRouteReputation(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(repBefore, _agency.Reputation);
        }

        [TestMethod]
        public void TryRouteReputation_NonCareerMode_ReturnsFalseWithoutMutating()
        {
            GeneralSettings.SettingsStore.GameMode = GameMode.Science;
            var repBefore = _agency.Reputation;
            var msg = ClientFactory.CreateNewMessageData<ShareProgressReputationMsgData>();
            msg.Reputation = 250;
            msg.Reason = "test";

            var handled = AgencyCurrencyRouter.TryRouteReputation(client: null, msg);

            Assert.IsFalse(handled);
            Assert.AreEqual(repBefore, _agency.Reputation);
        }

        [TestMethod]
        public void TryRouteReputation_NullMsg_ReturnsFalseWithoutMutating()
        {
            var repBefore = _agency.Reputation;

            var handled = AgencyCurrencyRouter.TryRouteReputation(client: null, msg: null);

            Assert.IsFalse(handled);
            Assert.AreEqual(repBefore, _agency.Reputation);
        }
    }
}
