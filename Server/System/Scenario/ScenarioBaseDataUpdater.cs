using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Log;
using Server.System.Agency;
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
        ///
        /// <para>[Mod-compat] <paramref name="client"/> is threaded through so
        /// Path B per-agency routers can derive sender authority from
        /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>. When a router
        /// claims the inbound (<c>TryRoute</c> returns <c>true</c>) the shared-store
        /// AddOrUpdate is SUPPRESSED — per-agency state owns the authoritative
        /// copy and the projector splices it back into outbound scenario blobs at
        /// <c>SendScenarioModules</c> time. Pass <paramref name="client"/> as
        /// <c>null</c> for boot-time / non-client-driven scenario loads — the
        /// routers all short-circuit on a null client and fall through to the
        /// legacy AddOrUpdate.</para>
        ///
        /// <para><b>Currently dispatched:</b> <see cref="AgencyScanRouter"/> for
        /// <c>SCANcontroller</c> (S2, commit <c>9fddb7fd</c>) and
        /// <see cref="AgencyDMagicRouter"/> for <c>DMScienceScenario</c> (S4).
        /// Future S5/S6 (Luna Compat sidecar Harmony) add their dispatch
        /// alongside these — single extra `if (scenarioModule == "X" &amp;&amp;
        /// AgencyXRouter.TryRoute(client, scenario)) return;` line per slice.
        /// S3 (FFT) was retired 2026-05-19 (orphan source file not in compiled
        /// FFT.dll; see commit <c>9404bfae</c>).</para>
        /// </summary>
        public static void RawConfigNodeInsertOrUpdate(ClientStructure client, string scenarioModule, string scenarioAsConfigNode)
        {
            _ = Task.Run(() =>
            {
                var trimmed = scenarioAsConfigNode.Trim();
                if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                    trimmed = trimmed.Substring(1, trimmed.Length - 2);

                var scenario = new ConfigNode(trimmed) { Name = scenarioModule };

                // [Mod-compat S2 Path B dispatch] Route SCANcontroller through
                // AgencyScanRouter under gate=on. TryRoute returns false when
                // PerAgencyEnabled is false, when the client lacks an agency
                // mapping, or when client is null — falls through to the
                // shared-store AddOrUpdate in those cases, preserving dual-mode
                // silence.
                if (scenarioModule == "SCANcontroller" &&
                    AgencyScanRouter.TryRoute(client, scenario))
                {
                    return;
                }

                // [Mod-compat S4 Path B dispatch] Same shape for DMScienceScenario
                // (DMagic asteroid science + anomaly records). Same suppression
                // semantics as S2 — under gate=on the router upserts per-agency
                // state and we skip the shared-store write.
                if (scenarioModule == "DMScienceScenario" &&
                    AgencyDMagicRouter.TryRoute(client, scenario))
                {
                    return;
                }

                lock (Semaphore.GetOrAdd(scenarioModule, new object()))
                {
                    ScenarioStoreSystem.CurrentScenarios.AddOrUpdate(scenarioModule, scenario, (key, existingVal) => scenario);
                }
            });
        }
    }
}
