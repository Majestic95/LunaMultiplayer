using LmpCommon.Message.Data.ShareProgress;
using LunaConfigNode.CfgNode;
using Server.Log;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Server.System.Scenario
{
    public partial class ScenarioDataUpdater
    {
        // States that mean a contract is done and should live in CONTRACTS_FINISHED, not CONTRACTS.
        // KSP serialises Contract.State enum values by name (e.g. "Completed", not "3").
        private static readonly IReadOnlyCollection<string> FinishedContractStates = new HashSet<string>
        {
            "Completed", "Failed", "Cancelled", "DeadlineExpired", "Withdrawn"
        };

        /// <summary>
        /// We received a contract message so update the scenario file accordingly.
        /// Finished contracts are moved from CONTRACTS to CONTRACTS_FINISHED so that
        /// they no longer occupy an offered-contract slot on the server.
        /// </summary>
        public static void WriteContractDataToFile(ShareProgressContractsMsgData contractsMsg)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    lock (Semaphore.GetOrAdd("ContractSystem", new object()))
                    {
                        if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ContractSystem", out var scenario)) return;

                        var contractsNode = scenario.GetNode("CONTRACTS")?.Value;
                        if (contractsNode == null) return;

                        // Get CONTRACTS_FINISHED, creating it if the scenario pre-dates the node.
                        var finishedNodeEntry = scenario.GetNode("CONTRACTS_FINISHED");
                        ConfigNode finishedNode;
                        if (finishedNodeEntry == null)
                        {
                            finishedNode = new ConfigNode("") { Name = "CONTRACTS_FINISHED" };
                            scenario.AddNode(finishedNode);
                        }
                        else
                        {
                            finishedNode = finishedNodeEntry.Value;
                        }

                        var existingActive   = contractsNode.GetNodes("CONTRACT").Select(c => c.Value).ToArray();
                        var existingFinished = finishedNode.GetNodes("CONTRACT").Select(c => c.Value).ToArray();

                        foreach (var contract in contractsMsg.Contracts.Select(v => ParseClientConfigNode(v.Data, v.NumBytes, "CONTRACT")))
                        {
                            var guid  = contract.GetValue("guid")?.Value;
                            var state = contract.GetValue("state")?.Value ?? string.Empty;

                            var inActive   = existingActive.FirstOrDefault(n => n.GetValue("guid")?.Value == guid);
                            var inFinished = existingFinished.FirstOrDefault(n => n.GetValue("guid")?.Value == guid);

                            if (FinishedContractStates.Contains(state))
                            {
                                // Remove from active list so it no longer blocks an offered-contract slot.
                                if (inActive != null)
                                    contractsNode.RemoveNode(inActive);

                                // Upsert into CONTRACTS_FINISHED.
                                if (inFinished != null)
                                    finishedNode.ReplaceNode(inFinished, contract);
                                else
                                    finishedNode.AddNode(contract);
                            }
                            else
                            {
                                // Not finished — update in place within CONTRACTS.
                                if (inActive != null)
                                    contractsNode.ReplaceNode(inActive, contract);
                                else
                                    contractsNode.AddNode(contract);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Error updating contract scenario data: {e}");
                }
            });
        }

        /// <summary>
        /// Stage 5.17d — under PerAgencyCareer=on the <see cref="Server.System.Agency.AgencyContractRouter"/>
        /// routes Active/Completed/etc. contracts per-agency, but the SHARED <c>ContractSystem</c>
        /// scenario's <c>CONTRACTS</c> node still holds the original Offered entry for the same
        /// guid. Without removing it, peer agencies see the contract as Offered on their next
        /// <c>SendScenarioModules</c> and could "accept" it — producing duplicate per-agency
        /// claims of the same Offered slot. This helper frees the slot by deleting the matching
        /// guid from the shared CONTRACTS node only; <c>CONTRACTS_FINISHED</c> is left alone
        /// (the per-agency archival lives in <see cref="Server.System.Agency.AgencyState.Contracts"/>
        /// — the shared scenario does NOT mirror per-agency finished state).
        ///
        /// Same per-scenario writer-lock contract as <see cref="WriteContractDataToFile"/> so the
        /// <c>ScenarioStoreSystem.BackupScenarios</c> serialization doesn't race the
        /// <c>RemoveNode</c> here (BUG-033 precedent).
        /// </summary>
        public static void RemoveContractFromSharedOfferedPool(Guid contractGuid)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    lock (Semaphore.GetOrAdd("ContractSystem", new object()))
                    {
                        if (!ScenarioStoreSystem.CurrentScenarios.TryGetValue("ContractSystem", out var scenario)) return;

                        var contractsNode = scenario.GetNode("CONTRACTS")?.Value;
                        if (contractsNode == null) return;

                        var guidString = contractGuid.ToString();
                        foreach (var entry in contractsNode.GetNodes("CONTRACT").ToArray())
                        {
                            var node = entry.Value;
                            var raw = node.GetValue("guid")?.Value;
                            if (string.IsNullOrEmpty(raw)) continue;
                            if (!Guid.TryParse(raw, out var parsed)) continue;
                            if (parsed == contractGuid)
                            {
                                contractsNode.RemoveNode(node);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[fix:per-agency-career] Error removing contract {contractGuid:N} from shared Offered pool: {e}");
                }
            });
        }
    }
}
