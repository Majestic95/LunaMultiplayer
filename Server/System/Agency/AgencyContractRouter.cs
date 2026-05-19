using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Scenario;
using System;
using System.Collections.Generic;
using System.Text;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17d — Q6 hybrid contract routing. Replaces the
    /// <see cref="ShareContractsSystem.ContractsReceived"/> broadcast+scenario-write
    /// path with per-agency routing when <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>
    /// is on. Sits between <see cref="ShareContractsSystem"/> and the storage layer:
    /// classifies each incoming contract by KSP state name, routes Offered entries to
    /// the shared scenario (so Contract Configurator's <c>ContractPreLoader</c> module
    /// sees the world it expects), and persists Active / Completed / Failed / Cancelled
    /// / DeadlineExpired / Withdrawn entries to the sender's
    /// <see cref="AgencyState.Contracts"/>. The latter set echoes via owner-only
    /// <c>AgencyContractMsgData</c>; peers never learn another agency's per-agency
    /// contracts (spec §10 Q1 PrivateAgencyResources=true).
    ///
    /// **Q6 commitments (spec §2 Contracts row + audit
    /// <c>docs/research/05a-plaguenz-audit.md</c>):**
    /// <list type="number">
    ///   <item>(a) <b>No Offered persistence per-agency.</b> The Offered split is
    ///        carved out before storage; only post-Accept states reach
    ///        <see cref="AgencyState.Contracts"/>. PlagueNZ's full-isolation
    ///        approach bloated their per-agency JSON to 1,727 entries; we don't repeat.</item>
    ///   <item>(b) <b>Per-contract exception isolation.</b> Classification + storage
    ///        wrap each contract in its own try/catch so one malformed payload doesn't
    ///        abort the batch. CC's stricter <c>Register()</c>-throwing contracts
    ///        triggered the same retreat in PlagueNZ; the isolation contract is
    ///        load-bearing.</item>
    ///   <item>(c) <b><c>ContractPreLoader</c> ScenarioModule untouched.</b> The
    ///        shared <c>ContractSystem</c> scenario continues to receive Offered
    ///        updates via the existing
    ///        <see cref="ScenarioDataUpdater.WriteContractDataToFile"/> path; CC's
    ///        parallel module path is uninstrumented.</item>
    /// </list>
    ///
    /// **Dual-mode gate (spec §11).** With <see cref="GameplaySettingsDefinition.PerAgencyCareer"/>
    /// off, <see cref="TryRoute"/> returns <c>false</c> immediately and the caller
    /// runs the unchanged shared-agency path. Zero observable behaviour change in
    /// dual-mode-off, mirroring the lifecycle, sender, and projector contracts.
    ///
    /// **Persistence path (vs Stage 5.18a client mirror).** Stage 5.17d ships the
    /// router + storage; the wire-side <c>AgencyContractMsgData</c> is consumed by
    /// the Stage 5.18a client <c>AgencySystem</c> mirror to populate the local
    /// player's contract list. Until 5.18a lands, the message reaches the wire but
    /// the client has no handler — silent drop. The server-side architecture is
    /// architecturally complete after 5.17d; the visible end-to-end contract flow
    /// activates with the client mirror.
    /// </summary>
    public static class AgencyContractRouter
    {
        /// <summary>
        /// Contract states that route to the shared scenario pool (Q6 commitment a).
        /// "Offered" is the canonical pre-Accept state. "Generated" is used by Contract
        /// Configurator for contracts that have been instantiated but not yet promoted
        /// to Offered; treating it as shared keeps CC's pre-loader cache consistent.
        /// Any other state (Active / Completed / Failed / Cancelled / DeadlineExpired /
        /// Withdrawn) is per-agency-owned.
        /// </summary>
        private static readonly HashSet<string> SharedScenarioStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Offered",
            "Generated",
        };

        /// <summary>
        /// Attempts to route the inbound contracts batch through the per-agency path.
        /// Returns <c>true</c> if this method handled the inbound — caller must NOT
        /// then run the shared-scenario relay/write path. Returns <c>false</c> when
        /// the gate is off, the client lacks an agency mapping (defensive), or the
        /// agency registry entry is missing — caller continues with the existing
        /// shared-agency path unchanged.
        /// </summary>
        public static bool TryRoute(ClientStructure client, ShareProgressContractsMsgData msg)
        {
            // [Stage 5.17e-1] Combined gate (PerAgencyCareer && GameMode==Career). Career-
            // only product decision (spec §10 Q-Mode): per-agency contract routing requires
            // a real Career singleton on the client; in Science/Sandbox the inbound is the
            // shared-agency path's problem.
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            // Per-contract classify. Exceptions in classification (malformed bytes,
            // unparseable state) drop the contract — they cannot derail the batch.
            // Round-1 server-systems review hardening: even building two empty lists
            // is observable so a downstream null-dereference on empty path is excluded.
            var perAgencyEntries = new List<ContractInfo>(msg.ContractCount);
            var sharedEntries = new List<ContractInfo>(msg.ContractCount);

            for (var i = 0; i < msg.ContractCount; i++)
            {
                var contract = msg.Contracts[i];
                if (contract == null)
                    continue;

                try
                {
                    var state = ReadContractState(contract);
                    if (SharedScenarioStates.Contains(state))
                        sharedEntries.Add(contract);
                    else
                        perAgencyEntries.Add(contract);
                }
                catch (Exception ex)
                {
                    LunaLog.Error($"[fix:per-agency-career] Skipped per-contract classification for {contract.ContractGuid:N} (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (perAgencyEntries.Count > 0)
                ApplyPerAgencyBatch(client, agencyId, agency, perAgencyEntries);

            if (sharedEntries.Count > 0)
                ApplySharedBatch(client, sharedEntries);

            return true;
        }

        /// <summary>
        /// Upserts per-agency entries into <see cref="AgencyState.Contracts"/> under
        /// <see cref="AgencySystem.GetAgencyLock"/>, persists, and echoes
        /// <c>AgencyContractMsgData</c> to the owning client only. Per-contract
        /// exception isolation: a single upsert failure does not abort siblings or
        /// the echo for the rest of the batch.
        /// </summary>
        private static void ApplyPerAgencyBatch(ClientStructure client, Guid agencyId, AgencyState agency, List<ContractInfo> entries)
        {
            var echoEntries = new List<ContractInfo>(entries.Count);

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                foreach (var contract in entries)
                {
                    try
                    {
                        Upsert(agency, contract);
                        echoEntries.Add(contract);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:per-agency-career] Failed to upsert per-agency contract {contract.ContractGuid:N} (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            AgencySystem.SaveAgency(agencyId);

            // [Stage 5.17d upgrade-lens review — MUST FIX] Free the matching Offered slot
            // in the shared ContractSystem scenario. The shared CONTRACTS node still holds
            // the pre-Accept Offered entry that CC's pool seeded; without this removal,
            // peer agencies on their next handshake/scene-load would see the contract as
            // Offered, "accept" it independently, and produce duplicate per-agency claims
            // for the same Offered slot. Per-contract isolation — failure to remove one
            // shared-pool entry does NOT abort the rest of the batch.
            foreach (var contract in echoEntries)
            {
                try
                {
                    ScenarioDataUpdater.RemoveContractFromSharedOfferedPool(contract.ContractGuid);
                }
                catch (Exception ex)
                {
                    LunaLog.Error($"[fix:per-agency-career] Failed to remove guid {contract.ContractGuid:N} from shared Offered pool: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (echoEntries.Count > 0)
                AgencySystemSender.SendContractsToOwner(client, agencyId, echoEntries);
        }

        /// <summary>
        /// Routes Offered / Generated contracts to the shared scenario AND relays the
        /// shared subset to other connected clients via the legacy
        /// <see cref="ShareProgressSrvMsg"/> path. We rebuild a fresh
        /// <see cref="ShareProgressContractsMsgData"/> rather than mutating the inbound —
        /// the existing writer iterates by <c>ContractCount</c> and our split would
        /// otherwise leave per-agency entries in the array with a smaller count
        /// (silent index mismatch), and the relay payload would carry another agency's
        /// post-Accept state (spec §10 Q1 privacy violation).
        ///
        /// <para><b>Why peer relay is required.</b> Under <c>PerAgencyCareer=on</c>,
        /// only the contract-lock holder generates contracts locally — every other
        /// client has <c>ContractSystem.generateContractIterations=0</c> set by
        /// <c>LmpClient/Harmony/ContractSystem_OnAwake.cs</c>. Without this relay,
        /// non-lock-holding peers would never see live Offered/Generated updates and
        /// would have to reconnect to pick them up from <c>SendScenarioModules</c>'s
        /// projection. The relay payload contains ONLY shared-pool states; per-agency
        /// entries route through <see cref="ApplyPerAgencyBatch"/>'s owner-only echo
        /// (<see cref="AgencySystemSender.SendContractsToOwner"/>) so spec §10 Q1 is
        /// not violated.</para>
        /// </summary>
        private static void ApplySharedBatch(ClientStructure sender, List<ContractInfo> entries)
        {
            // ShareProgressContractsMsgData has an internal ctor — it's an
            // IMessageData and must come from the production factory so the
            // base-class plumbing (TypeName, recycling) is initialised the
            // same way the wire path produces.
            var sharedMsg = ServerContext.ServerMessageFactory.CreateNewMessageData<ShareProgressContractsMsgData>();
            sharedMsg.ContractCount = entries.Count;
            sharedMsg.Contracts = entries.ToArray();

            // Relay to all other connected clients. Mirror order of the legacy
            // shared-agency path in ShareContractsSystem.ContractsReceived (relay first,
            // then persist) — the relay enqueues onto Lidgren's send thread immediately
            // while WriteContractDataToFile's Task.Run defers the disk write.
            //
            // INHERITED RACE NOTE: ContractInfo.Serialize calls Common.ThreadSafeCompress
            // which reassigns Data to compressed bytes in place. If Lidgren's send-thread
            // serialize wins against WriteContractDataToFile's Task.Run, the disk write
            // parses compressed bytes as UTF-8 and silently drops the entry. This race
            // is pre-existing in the legacy ShareContractsSystem ordering (relay then
            // write) and has shipped without observed corruption — out of scope here.
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(sender, sharedMsg);

            ScenarioDataUpdater.WriteContractDataToFile(sharedMsg);
        }

        /// <summary>
        /// Upserts (insert or replace) the entry keyed by <see cref="ContractInfo.ContractGuid"/>.
        /// Matches the shared-scenario writer's upsert semantics in
        /// <see cref="ScenarioDataUpdater.WriteContractDataToFile"/>: contracts are
        /// identified by their stable guid and a re-arrival overwrites the prior
        /// snapshot. Stores decompressed bytes (the form the network layer hands us).
        /// </summary>
        internal static void Upsert(AgencyState agency, ContractInfo contract)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (contract == null)
                throw new ArgumentNullException(nameof(contract));

            var state = ReadContractState(contract);
            var dataLen = Math.Min(contract.NumBytes, contract.Data?.Length ?? 0);

            // Defensive copy: the wire ContractInfo's Data array is mutated on
            // serialize (Common.ThreadSafeCompress works in-place). Storing the
            // reference directly would let a subsequent wire echo re-compress
            // bytes already living in AgencyState — a hard-to-diagnose corruption
            // path if the same router instance ever holds a reference across
            // multiple sends. Copy on store.
            var dataCopy = dataLen > 0 ? new byte[dataLen] : Array.Empty<byte>();
            if (dataLen > 0)
                Buffer.BlockCopy(contract.Data, 0, dataCopy, 0, dataLen);

            for (var i = 0; i < agency.Contracts.Count; i++)
            {
                if (agency.Contracts[i] != null && agency.Contracts[i].ContractGuid == contract.ContractGuid)
                {
                    agency.Contracts[i].State = state;
                    agency.Contracts[i].Data = dataCopy;
                    agency.Contracts[i].NumBytes = dataLen;
                    return;
                }
            }

            agency.Contracts.Add(new AgencyContractEntry
            {
                ContractGuid = contract.ContractGuid,
                State = state,
                Data = dataCopy,
                NumBytes = dataLen,
            });
        }

        /// <summary>
        /// Reads the <c>state = ...</c> root-level value out of a contract's decompressed
        /// ConfigNode bytes. Mirrors <see cref="ScenarioDataUpdater"/>'s
        /// <c>ParseClientConfigNode</c> private helper (KSP wraps unnamed nodes in
        /// braces; LunaConfigNode would parse those as a single unnamed child).
        /// Returns the empty string when the bytes are malformed or the state field is
        /// absent — caller treats empty as non-Offered (per-agency) so a contract
        /// missing its state field doesn't accidentally pollute the shared pool.
        /// </summary>
        internal static string ReadContractState(ContractInfo contract)
        {
            if (contract == null || contract.NumBytes <= 0 || contract.Data == null)
                return string.Empty;

            var raw = Encoding.UTF8.GetString(contract.Data, 0, Math.Min(contract.NumBytes, contract.Data.Length)).Trim();
            if (raw.StartsWith("{") && raw.EndsWith("}"))
                raw = raw.Substring(1, raw.Length - 2);

            var node = new ConfigNode(raw) { Name = "CONTRACT" };
            return node.GetValue("state")?.Value ?? string.Empty;
        }
    }
}
