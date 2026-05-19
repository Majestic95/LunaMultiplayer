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
using System.Collections.Concurrent;
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
        /// Stage 5.18g — per-<see cref="ContractInfo.ContractGuid"/> claim map. Closes
        /// the simultaneous-Accept race exposed by the v3 hotfix (<c>042d2cb5</c>),
        /// which made shared Offered/Generated contracts visible to peer agencies in
        /// real time. Without this guard, two agencies receiving the same Offered
        /// contract could both click Accept inside one server tick, both promote it
        /// to a per-agency Active record, and both collect the reward on completion.
        ///
        /// <para><b>Semantics.</b> First Accept wins via
        /// <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, TValue)"/>'s
        /// atomic insert. Subsequent <see cref="ApplyPerAgencyBatch"/> entries for the
        /// same guid see the stored claimant != the current agency and silently drop
        /// the contract from the batch (Warning log). Same-agency re-Accept is
        /// idempotent (claim already held by the same agency → proceed with Upsert).</para>
        ///
        /// <para><b>Lifetime.</b> Entries persist for the server process lifetime;
        /// no eviction in 5.18g (operator-confirmed minimum scope). Memory cost ~32
        /// bytes per Accepted contract guid; bounded by total Accepts across the
        /// universe. For long-running servers with heavy CC grinding, consider
        /// eviction on Completed/Failed/Cancelled — deferred to a future hardening
        /// slice, mirroring the BUG-025 v2 tech-purchase claim's same shape.</para>
        ///
        /// <para><b>Persistence.</b> The map is in-memory only. After a server restart,
        /// <see cref="PreSeedClaimsFromAgencyState"/> is called once per loaded
        /// <see cref="AgencyState"/> from <see cref="AgencySystem.LoadExistingAgencies"/>,
        /// rebuilding the claim set from the persisted per-agency
        /// <see cref="AgencyState.Contracts"/> list. This closes the post-restart
        /// double-claim window where Agency A's claim is on disk but Agency B
        /// connects first and could Accept the same guid before A reconnects.</para>
        ///
        /// <para><b>Per-agency design.</b> Only populated by the gated per-agency
        /// path (<see cref="TryRoute"/> guards on
        /// <see cref="AgencySystem.PerAgencyEnabled"/>). Under gate=off the
        /// dictionary stays empty and the legacy shared-agency path is untouched —
        /// no observable behaviour change in dual-mode-off (Stage 5 spec §11).</para>
        ///
        /// <para><b>Mod-compat impact.</b> Contract Configurator's
        /// <c>ContractPreLoader</c> drives the Offered/Generated pool and that path
        /// runs through <see cref="ApplySharedBatch"/> unchanged — CC's preloader is
        /// not touched by the claim guard. The guard intervenes only when a contract
        /// crosses from Offered/Generated to a post-Accept state (Active / Completed /
        /// Failed / Cancelled / DeadlineExpired / Withdrawn).</para>
        /// </summary>
        private static readonly ConcurrentDictionary<Guid, Guid> _claimedContracts =
            new ConcurrentDictionary<Guid, Guid>();

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
                    // Stage 5.18g — claim guard. See TryClaimContract XML for the
                    // atomic-first-wins contract. Cross-agency collision drops the
                    // contract from the batch (no Upsert, no echo, no shared-pool
                    // removal). Same-agency re-Accept stays idempotent.
                    var claimant = TryClaimContract(contract.ContractGuid, agencyId);
                    if (claimant != agencyId)
                    {
                        LunaLog.Warning(
                            $"[fix:per-agency-career] Dropped duplicate Accept for contract {contract.ContractGuid:N} from agency {agencyId:N} (player {client.PlayerName}); already claimed by agency {claimant:N}");
                        continue;
                    }

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

        /// <summary>
        /// Stage 5.18g — atomic claim-guard primitive. Returns the agency id that holds
        /// the claim for the given <paramref name="contractGuid"/> after this call. If
        /// no prior claimant exists, <paramref name="agencyId"/> is recorded and
        /// returned (first-wins). If a different agency already holds the claim, that
        /// agency's id is returned and the caller drops the contract. Same-agency
        /// repeat returns <paramref name="agencyId"/> idempotently (legitimate re-
        /// Accept after a state-machine update on the owning client).
        ///
        /// <para>Internal — exposed to <c>ServerTest</c> via <c>InternalsVisibleTo</c>
        /// so the claim guard's atomic semantics can be pinned at unit level without
        /// bringing up a wire harness.</para>
        /// </summary>
        internal static Guid TryClaimContract(Guid contractGuid, Guid agencyId)
        {
            return _claimedContracts.GetOrAdd(contractGuid, agencyId);
        }

        /// <summary>
        /// Stage 5.18g — read-only probe for tests + future admin tooling. Returns
        /// <c>true</c> with <paramref name="claimant"/> set when the contract guid has
        /// a recorded claim; <c>false</c> otherwise.
        /// </summary>
        internal static bool TryGetContractClaimant(Guid contractGuid, out Guid claimant)
        {
            return _claimedContracts.TryGetValue(contractGuid, out claimant);
        }

        /// <summary>
        /// Stage 5.18g — count of recorded claims. For tests that need to verify the
        /// pre-seed populated N entries, or that <see cref="ResetClaimedContracts"/>
        /// emptied the map. Not for production use.
        /// </summary>
        internal static int ClaimedContractsCount => _claimedContracts.Count;

        /// <summary>
        /// Stage 5.18g — boot-time pre-seed of <see cref="_claimedContracts"/> from the
        /// persisted per-agency Contracts list. Called once per loaded
        /// <see cref="AgencyState"/> from <see cref="AgencySystem.LoadExistingAgencies"/>.
        /// Closes the post-restart double-claim window: Agency A persisted a claim before
        /// the restart; Agency B is the first to reconnect after the restart; without
        /// pre-seed, B could Accept the same guid and win the in-memory race despite A's
        /// disk-persisted claim. Uses <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>
        /// — if the same guid somehow appears across two agencies' persisted state (an
        /// already-broken pre-5.18g universe), the first call wins and subsequent calls
        /// silently fail, leaving the data on disk untouched. Operator must clean up
        /// duplicated guids via <c>setvesselagency</c> / <c>transferagency</c>; this method
        /// preserves the pre-5.18g state rather than overwriting it.
        /// </summary>
        internal static void PreSeedClaimsFromAgencyState(Guid agencyId, AgencyState agency)
        {
            if (agency?.Contracts == null) return;
            foreach (var contract in agency.Contracts)
            {
                if (contract == null) continue;
                if (!_claimedContracts.TryAdd(contract.ContractGuid, agencyId)
                    && _claimedContracts.TryGetValue(contract.ContractGuid, out var existing)
                    && existing != agencyId)
                {
                    // [Stage 5.18g upgrade-lens follow-up] Operator-visible diagnostic
                    // for a pre-broken (pre-5.18g) universe where the same contract
                    // guid persisted under two different agencies. We preserve the
                    // first-seed (prior loop iteration on a different agency); the
                    // duplicate Contracts entry on the second agency's disk is now a
                    // zombie that can never re-Accept/complete. Recovery is operator
                    // hand-edit of Universe/Agencies/{guid}.txt — no admin command
                    // currently expunges per-agency contract entries.
                    LunaLog.Warning(
                        $"[fix:per-agency-career] Pre-seed collision on contract {contract.ContractGuid:N}: agency {existing:N} already claims it but agency {agencyId:N} also has it persisted. First-wins kept agency {existing:N}; the duplicate entry in agency {agencyId:N}'s state is now a zombie. Operator may need to hand-edit Universe/Agencies/{agencyId:N}.txt to remove the duplicate CONTRACT child.");
                }
            }
        }

        /// <summary>
        /// Stage 5.18g — clears the claim map. Intended for test isolation in
        /// <c>ServerHarness.ResetPerTestState</c> (matches the existing
        /// <c>ScenarioStoreSystem.CurrentScenarios.Clear()</c> precedent). Not for
        /// production use — clearing during normal server operation re-opens the
        /// simultaneous-Accept race.
        /// </summary>
        internal static void ResetClaimedContracts()
        {
            _claimedContracts.Clear();
        }

        /// <summary>
        /// Stage 5.18g multi-lens review follow-up — evicts every claim keyed by the
        /// supplied <paramref name="agencyId"/>. Called from
        /// <c>DeleteAgencyCommand</c> after <see cref="AgencySystem.Agencies"/> removal:
        /// without this, the deleted agency's id continues to "win" every contract
        /// guid it once held. The original owner reconnecting + minting a fresh
        /// agency would then see silent drops with a Warning naming a guid that no
        /// longer exists in the registry.
        ///
        /// <para><b>Race-free.</b> Iterates a snapshot of <see cref="_claimedContracts"/>
        /// (Keys enumeration is concurrent-safe) and uses
        /// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>
        /// per entry. A concurrent ApplyPerAgencyBatch on a different agency cannot
        /// be affected (foreign keys aren't touched). A concurrent ApplyPerAgencyBatch
        /// from the deleted agency's old player is structurally impossible — by the
        /// time this method runs, <c>AgencyByPlayerName</c> no longer maps to the
        /// deleted agency and any in-flight Share* message will fall through to the
        /// legacy path because <see cref="AgencyCurrencyRouter.TryResolveAgency"/>
        /// returns false.</para>
        /// </summary>
        /// <returns>The number of claims evicted.</returns>
        internal static int EvictClaimsForAgency(Guid agencyId)
        {
            var evicted = 0;
            foreach (var kvp in _claimedContracts)
            {
                if (kvp.Value == agencyId && _claimedContracts.TryRemove(kvp.Key, out _))
                    evicted++;
            }
            return evicted;
        }
    }
}
