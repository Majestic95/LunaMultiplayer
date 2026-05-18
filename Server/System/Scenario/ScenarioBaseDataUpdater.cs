using LunaConfigNode.CfgNode;
using Server.Log;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;

namespace Server.System.Scenario
{
    public partial class ScenarioDataUpdater
    {
        #region Semaphore

        /// <summary>
        /// To not overwrite our own data we use a lock
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> Semaphore = new ConcurrentDictionary<string, object>();

        /// <summary>
        /// [fix:BUG-033] Returns the per-scenario lock object used by every Write*DataToFile
        /// writer in this folder. Exposed to <see cref="ScenarioStoreSystem.BackupScenarios"/>
        /// so the backup-side serialization can acquire the same lock writers compete for and
        /// avoid racing <see cref="LunaConfigNode.CfgNode.ConfigNode.ToString"/> against an
        /// in-flight AddNode/RemoveNode/ReplaceNode on the same node instance.
        ///
        /// Re-entrant safe — C# lock acquisitions on the same object are per-thread; the
        /// existing <see cref="ScenarioPartPurchaseDataUpdater"/> path that calls into
        /// BackupScenarios from inside its own per-scenario lock works unchanged.
        /// </summary>
        internal static object GetSemaphore(string scenarioName) =>
            Semaphore.GetOrAdd(scenarioName, _ => new object());

        #endregion

        /// <summary>
        /// Creates a ConfigNode from raw bytes, stripping the outer { } braces that KSP's
        /// ConfigNode.WriteNode() adds. LunaConfigNode's parser wraps braced content in an
        /// unnamed child node, which causes GetValue() on the root to return null.
        ///
        /// [Stage 5.17e-4] Promoted private→internal so <see cref="Agency.AgencyTechRouter"/>
        /// can reuse the same brace-stripping + name-tagging without duplicating the
        /// parse logic (which would silently drift if KSP's ConfigNode serializer changes).
        /// </summary>
        internal static ConfigNode ParseClientConfigNode(byte[] data, int numBytes, string nodeName)
        {
            var raw = Encoding.UTF8.GetString(data, 0, numBytes);
            var trimmed = raw.Trim();

            // KSP serializes unnamed ConfigNodes as "{\n\tkey = val\n}" — strip the wrapper
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                trimmed = trimmed.Substring(1, trimmed.Length - 2);

            return new ConfigNode(trimmed) { Name = nodeName };
        }

        /// <summary>
        /// Raw updates a scenario in the dictionary, stripping outer { } braces
        /// that KSP's ConfigNode serializer adds (same fix as ParseClientConfigNode).
        /// </summary>
        public static void RawConfigNodeInsertOrUpdate(string scenarioModule, string scenarioAsConfigNode)
        {
            _ = Task.Run(() =>
            {
                var trimmed = scenarioAsConfigNode.Trim();
                if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                    trimmed = trimmed.Substring(1, trimmed.Length - 2);

                var scenario = new ConfigNode(trimmed) { Name = scenarioModule };
                lock (Semaphore.GetOrAdd(scenarioModule, new object()))
                {
                    ScenarioStoreSystem.CurrentScenarios.AddOrUpdate(scenarioModule, scenario, (key, existingVal) => scenario);
                }
            });
        }
    }
}
