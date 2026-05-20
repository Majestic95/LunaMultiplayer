using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Settings.Structures;
using Server.System.Agency;
using System;

namespace ServerTest
{
    /// <summary>
    /// [Phase 4 Slice D] Unit tests for the TERMINALS splice in
    /// <see cref="AgencyScenarioProjector"/>'s <c>SpliceAgencyWolfState</c>.
    /// Unlike Hoppers (and Routes / CrewRoutes), terminals have NO FK sweep
    /// — WOLF's <c>ScenarioPersister.OnLoad</c> at
    /// <c>ScenarioPersister.cs:343-353</c> loads terminals directly via
    /// <c>TerminalMetadata.OnLoad</c> with no depot lookup, so a terminal
    /// can persist independent of depot existence.
    /// </summary>
    [TestClass]
    public class AgencyWolfTerminalProjectorTest
    {
        [TestInitialize]
        public void Setup()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = true;
            GeneralSettings.SettingsStore.GameMode = GameMode.Career;
            AgencySystem.Reset();
        }

        [TestCleanup]
        public void Teardown()
        {
            GameplaySettings.SettingsStore.PerAgencyCareer = false;
            GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
            AgencySystem.Reset();
        }

        [TestMethod]
        public void Project_AgencyWithTerminal_SplicesTERMINAL()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var terminalId = Guid.NewGuid().ToString("N");
            agency.WolfTerminals[terminalId] = new AgencyWolfTerminalEntry
            {
                Id = terminalId, Body = "Duna", Biome = "Lowlands",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, "TERMINALS");
            StringAssert.Contains(result, $"Id = {terminalId}",
                "Terminal Id (N-form, no hyphens) emitted verbatim.");
            StringAssert.Contains(result, "Body = Duna");
            StringAssert.Contains(result, "Biome = Lowlands");
        }

        [TestMethod]
        public void Project_TerminalWithMissingDepot_StillEmitted_NoFKSweep()
        {
            // Terminals don't depend on depots in WOLF's OnLoad — the
            // projector mirrors that contract by NOT applying an FK sweep.
            // A terminal references body/biome that no per-agency depot
            // covers but still emits on the wire.
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // No depots at all.
            var terminalId = Guid.NewGuid().ToString("N");
            agency.WolfTerminals[terminalId] = new AgencyWolfTerminalEntry
            {
                Id = terminalId, Body = "Duna", Biome = "Lowlands",
            };

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            StringAssert.Contains(result, $"Id = {terminalId}",
                "Terminal must emit even without a corresponding depot — matches WOLF's OnLoad contract at ScenarioPersister.cs:343-353.");
        }

        [TestMethod]
        public void Project_EmptyTerminalsDict_NoContainerEmitted()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            // No terminals.

            var input = "name = WOLF_ScenarioModule\n";
            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("TERMINALS"),
                "Empty WolfTerminals dict must not emit a TERMINALS container (lazy-allocate).");
        }

        [TestMethod]
        public void Project_StripsSharedTERMINALS_BeforeSplicing()
        {
            var agency = new AgencyState { AgencyId = Guid.NewGuid() };
            var terminalId = Guid.NewGuid().ToString("N");
            agency.WolfTerminals[terminalId] = new AgencyWolfTerminalEntry
            {
                Id = terminalId, Body = "Duna", Biome = "Lowlands",
            };

            var input = "name = WOLF_ScenarioModule\n" +
                        "TERMINALS\n{\n" +
                        "\tTERMINAL\n\t{\n\t\tId = peer-terminal-id\n\t\tBody = PeerBody\n\t}\n" +
                        "}\n";

            var result = AgencyScenarioProjector.Project("WOLF_ScenarioModule", input, agency);

            Assert.IsFalse(result.Contains("peer-terminal-id"),
                "Peer's TERMINAL must be stripped before per-agency splice.");
            Assert.IsFalse(result.Contains("Body = PeerBody"),
                "Peer's body must not leak through.");
            StringAssert.Contains(result, $"Id = {terminalId}",
                "Own terminal still spliced in.");
        }
    }
}
