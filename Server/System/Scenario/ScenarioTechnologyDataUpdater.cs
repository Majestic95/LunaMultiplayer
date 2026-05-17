using LmpCommon.Message.Data.ShareProgress;
using Server.Log;
using System;
using System.Globalization;
using System.Linq;

namespace Server.System.Scenario
{
    public partial class ScenarioDataUpdater
    {
        /// <summary>
        /// [fix:BUG-025] Synchronously check whether the incoming tech node is already
        /// in the canonical ResearchAndDevelopment scenario, and add it if not.
        ///
        /// Returns a tuple:
        /// <list type="bullet">
        /// <item><c>Added == true</c>, <c>CostInPayload == 0f</c> — caller should relay
        /// the message to all other clients as normal.</item>
        /// <item><c>Added == false</c>, <c>CostInPayload &gt; 0f</c> — duplicate purchase
        /// race; caller should send a <see cref="ShareProgressTechnologyRejectedMsgData"/>
        /// back to the sender with this refund amount and NOT relay to others.</item>
        /// </list>
        ///
        /// The whole check-and-add runs under the per-scenario writer lock
        /// (<see cref="GetSemaphore"/> for <c>"ResearchAndDevelopment"</c>) so two
        /// near-simultaneous purchase broadcasts cannot both see "absent" and both
        /// proceed. Replaces the previous async <c>WriteTechnologyDataToFile</c>
        /// Task.Run pattern: the lock window is short (in-memory ConfigNode mutation)
        /// and the message receive thread can absorb it.
        ///
        /// Persistence-to-disk is unchanged — the scenario dict mutation here is
        /// flushed via <see cref="ScenarioStoreSystem.BackupScenarios"/> on the
        /// existing periodic cadence.
        /// </summary>
        public static (bool Added, float CostInPayload) TryAddTechnologyAtomic(ShareProgressTechnologyMsgData techMsg)
        {
            // Per-scenario writer lock. Acquired here via the same surface every
            // other Scenario/Write*DataToFile writer uses — Semaphore.GetOrAdd
            // is idempotent on the key, so this resolves to the SAME object as
            // ScenarioDataUpdater.GetSemaphore("ResearchAndDevelopment") and
            // shares mutual exclusion with the BUG-033 backup-serialization path.
            lock (Semaphore.GetOrAdd("ResearchAndDevelopment", new object()))
            {
                if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ResearchAndDevelopment", out var scenario))
                {
                    // Scenario doesn't exist yet (fresh server, no progression history).
                    // Match the pre-fix WriteTechnologyDataToFile behaviour: return without
                    // persisting and let the caller relay. The first-ever tech that lands
                    // BEFORE any client has uploaded a ResearchAndDevelopment scenario via
                    // ScenarioSystem.ParseReceivedScenarioData will be relayed to peers but
                    // not persisted; subsequent purchases of the same tech are unprotected
                    // until the scenario shows up. In practice the scenario is uploaded on
                    // first handshake so the window is tiny.
                    return (true, 0f);
                }

                LunaConfigNode.CfgNode.ConfigNode receivedNode;
                float costInPayload;
                try
                {
                    receivedNode = ParseClientConfigNode(techMsg.TechNode.Data, techMsg.TechNode.NumBytes, "Tech");
                    if (receivedNode.IsEmpty())
                        return (true, 0f); // malformed payload — degrade to relay; not our race

                    costInPayload = ParseScienceCost(receivedNode);
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Error parsing technology payload: {e}");
                    return (true, 0f);
                }

                var techNodes = scenario.GetNodes("Tech").Select(v => v.Value);
                var existing = techNodes.FirstOrDefault(n =>
                {
                    var id = n.GetValue("id");
                    return id != null && id.Value == techMsg.TechNode.Id;
                });
                if (existing != null)
                    return (false, costInPayload); // duplicate purchase race — caller sends rejection

                scenario.AddNode(receivedNode);

                // Flush to disk inside the lock so a crash between this AddNode and the
                // next periodic BackupScenarios tick doesn't lose the purchase. Matches
                // the persistence pattern in ScenarioPartPurchaseDataUpdater.
                ScenarioStoreSystem.BackupScenarios();
                return (true, 0f);
            }
        }

        private static float ParseScienceCost(LunaConfigNode.CfgNode.ConfigNode techNode)
        {
            var costValue = techNode.GetValue("cost");
            if (costValue == null) return 0f;
            return float.TryParse(costValue.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
        }
    }
}
