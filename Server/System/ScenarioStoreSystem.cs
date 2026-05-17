using LunaConfigNode;
using LunaConfigNode.CfgNode;
using Server.Settings.Structures;
using Server.System.Scenario;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace Server.System
{
    /// <summary>
    /// Here we keep a copy of all the scnarios modules in ConfigNode format and we also save them to files at a specified rate
    /// </summary>
    public static class ScenarioStoreSystem
    {
        public static ConcurrentDictionary<string, ConfigNode> CurrentScenarios = new ConcurrentDictionary<string, ConfigNode>();

        private static readonly object BackupLock = new object();

        /// <summary>
        /// Returns a scenario as text for sending to clients.
        /// Must NOT have outer { } braces — KSP's RecurseFormat treats bare lines as
        /// root-level key-value pairs, which is what ProtoScenarioModule expects.
        /// Wrapping in braces would nest everything in an unnamed child node,
        /// causing node.GetValue("name") to return null on the client.
        ///
        /// [fix:BUG-033] Serialized under the per-scenario writer lock for the same
        /// reason BackupScenarios is — without it, the ToString iterator races against
        /// concurrent AddNode/RemoveNode/ReplaceNode in a ScenarioDataUpdater writer
        /// and either throws InvalidOperationException or returns a corrupt string.
        /// Called from ScenarioSystem.SendScenarioModules on every client handshake.
        /// </summary>
        public static string GetScenarioInConfigNodeFormat(string scenarioName)
        {
            if (!CurrentScenarios.TryGetValue(scenarioName, out var scenario))
                return null;

            return SerializeUnderWriterLock(scenarioName, scenario);
        }

        /// <summary>
        /// Load the stored scenarios into the dictionary
        /// </summary>
        public static void LoadExistingScenarios(bool createdFromScratch)
        {
            ChangeExistingScenarioFormats();
            lock (BackupLock)
            {
                foreach (var file in Directory.GetFiles(ScenarioSystem.ScenariosPath).Where(f => Path.GetExtension(f) == ScenarioSystem.ScenarioFileFormat))
                {
                    var raw = File.ReadAllText(file).Trim();
                    if (raw.StartsWith("{") && raw.EndsWith("}"))
                        raw = raw.Substring(1, raw.Length - 2);

                    CurrentScenarios.TryAdd(Path.GetFileNameWithoutExtension(file), new ConfigNode(raw) { Name = Path.GetFileNameWithoutExtension(file) });
                }

                if (createdFromScratch)
                {
                    ScenarioDataUpdater.WriteScienceDataToFile(GameplaySettings.SettingsStore.StartingScience);
                    ScenarioDataUpdater.WriteReputationDataToFile(GameplaySettings.SettingsStore.StartingReputation);
                    ScenarioDataUpdater.WriteFundsDataToFile(GameplaySettings.SettingsStore.StartingFunds);
                }
            }
        }

        /// <summary>
        /// Transform OLD Xml scenarios into the new format
        /// TODO: Remove this for next version
        /// </summary>
        public static void ChangeExistingScenarioFormats()
        {
            lock (BackupLock)
            {
                foreach (var file in Directory.GetFiles(ScenarioSystem.ScenariosPath).Where(f => Path.GetExtension(f) == ".xml"))
                {
                    var vesselAsCfgNode = XmlConverter.ConvertToConfigNode(FileHandler.ReadFileText(file));
                    FileHandler.WriteToFile(file.Replace(".xml", ".txt"), vesselAsCfgNode);
                    FileHandler.FileDelete(file);
                }
            }
        }

        /// <summary>
        /// Actually performs the backup of the scenarios to file.
        ///
        /// [fix:BUG-033] Each scenario is serialized under its matching writer lock
        /// (<see cref="ScenarioDataUpdater.GetSemaphore"/>) so <see cref="LunaConfigNode.CfgNode.ConfigNode.ToString"/>
        /// does not race with an in-flight AddNode/RemoveNode/ReplaceNode on the same
        /// <see cref="LunaConfigNode.CfgNode.ConfigNode"/> instance. The disk write is
        /// performed OUTSIDE the lock so I/O latency does not extend the writer-blocking
        /// window. The outer <see cref="BackupLock"/> is deliberately NOT taken here —
        /// holding both <see cref="BackupLock"/> and a per-scenario semaphore would
        /// deadlock against the <see cref="ScenarioPartPurchaseDataUpdater"/> path that
        /// already calls <see cref="BackupScenarios"/> from inside its own semaphore
        /// (classic AB-BA cycle). <see cref="BackupLock"/> remains on the startup-only
        /// load/migration methods because they have no overlap with this code path.
        /// </summary>
        public static void BackupScenarios()
        {
            var scenariosInXml = CurrentScenarios.ToArray();
            foreach (var scenario in scenariosInXml)
            {
                var serialized = SerializeUnderWriterLock(scenario.Key, scenario.Value);
                FileHandler.WriteToFile(
                    Path.Combine(ScenarioSystem.ScenariosPath, $"{scenario.Key}{ScenarioSystem.ScenarioFileFormat}"),
                    serialized);
            }
        }

        /// <summary>
        /// [fix:BUG-033] Serialize one scenario ConfigNode for backup under the same per-scenario
        /// lock used by <see cref="Scenario.ScenarioDataUpdater"/> writers. Extracted from
        /// <see cref="BackupScenarios"/> so the concurrency-regression test in ServerTest can
        /// exercise the lock contract without going through the disk-writing call path.
        /// </summary>
        internal static string SerializeUnderWriterLock(string scenarioName, ConfigNode scenario)
        {
            lock (Scenario.ScenarioDataUpdater.GetSemaphore(scenarioName))
            {
                return scenario.ToString();
            }
        }
    }
}
