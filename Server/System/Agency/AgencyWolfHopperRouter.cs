using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 4 Slice D — server-side per-agency router for MKS WOLF hoppers.
    /// Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.WolfHopperState"/>
    /// (slot 11). Owner-only echo + persistence; the projector
    /// (<see cref="AgencyScenarioProjector"/>) splices per-agency entries into
    /// outgoing <c>WOLF_ScenarioModule</c> blobs at <c>SendScenarioModules</c>
    /// time so each agency sees ONLY their own hoppers — spec §10 Q1
    /// PrivateAgencyResources=true.
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyWolfHopperStateMsgData"/> is used both directions on
    /// slot 11; inbound from the client postfix carries wire-supplied
    /// <c>AgencyId</c> that the server IGNORES. Sender authority is derived
    /// authoritatively from <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>
    /// — same trust posture as <see cref="AgencyWolfRouteRouter.TryRoute"/>.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Validation + upsert
    /// are wrapped in a SINGLE try/catch per entry. A malformed entry (empty
    /// Id or Body or Biome) never aborts siblings. Matches the
    /// <see cref="AgencyWolfRouteRouter"/> precedent.</para>
    ///
    /// <para><b>Key form preservation</b> (pre-spec §2.f.v). Hopper.Id is a
    /// Guid in <c>ToString()</c> form WITH hyphens per WOLF's
    /// <c>HopperMetadata.cs:18</c>. Distinct from
    /// <see cref="AgencyState.WolfTerminals"/>' "N" form (without hyphens) —
    /// do NOT normalize at any boundary. Router uses the raw wire string as
    /// the dictionary key.</para>
    ///
    /// <para><b>No cross-agency vessel-proxy check.</b> Hoppers are
    /// Guid-keyed, not vessel-keyed. Two agencies CAN each have a hopper at
    /// the same body+biome (their depot pools are also separate) — they live
    /// in separate per-agency dicts; the projector splice emits only the
    /// requesting agency's hoppers into outgoing scenario blobs. The
    /// cross-agency CrewRoute kerbal gate in Slice E is the only Phase 4
    /// router with vessel-proxy authority.</para>
    ///
    /// <para><b>FK-integrity decoupling (mirrors Slice C Routes).</b> WOLF's
    /// <c>ScenarioPersister.OnLoad</c> at <c>ScenarioPersister.cs:320-329</c>
    /// looks up each hopper's depot by Body+Biome via
    /// <c>Depots.FirstOrDefault(...)</c> and SILENTLY DROPS hoppers whose
    /// depot isn't present. The router does NOT enforce depot existence at
    /// upsert time: the wire batch may arrive before the parent depot's
    /// postfix-driven upsert (different Harmony patches, different messages,
    /// no ordering guarantee). FK integrity is enforced LATER in the
    /// projector splice: any Hopper whose Body+Biome key is missing from
    /// the just-emitted depot pool is dropped from the outgoing scenario
    /// blob — see <see cref="AgencyScenarioProjector"/>'s
    /// <c>SpliceAgencyWolfState</c> Hoppers block. This decouples disk-side
    /// persistence (loose) from wire-side projection (strict) and lets
    /// normal-operation message arrival ordering self-heal.</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false,
    /// <see cref="TryRoute"/> returns <c>false</c> immediately and the caller
    /// — <c>AgencyMsgReader</c> — drops the inbound silently. Same posture
    /// as <see cref="AgencyWolfRouteRouter"/>.</para>
    ///
    /// <para><b>Per-agency lock contract (pre-spec §3.b).</b> The per-entry
    /// for-loop is wrapped in
    /// <c>lock (AgencySystem.GetAgencyLock(agencyId))</c>. Mirrors
    /// <see cref="AgencyWolfRouteRouter.TryRoute"/>.</para>
    ///
    /// <para><b>RemovedKeys is non-trivially used (unlike Routes).</b> WOLF's
    /// <c>ScenarioPersister.RemoveHopper(string id)</c> at
    /// <c>ScenarioPersister.cs:432-440</c> is a normal-operation API (the
    /// WOLF UI recipe-change flow calls Remove + Create as a pair when the
    /// operator picks a different recipe). The matching Slice D client-side
    /// <c>ScenarioPersister_RemoveHopperPostfix</c> ships the removed id in
    /// the <see cref="AgencyWolfHopperStateMsgData.RemovedKeys"/> tail.</para>
    /// </summary>
    public static class AgencyWolfHopperRouter
    {
        /// <summary>
        /// Attempts to route the inbound hopper state batch through the
        /// per-agency path. Returns <c>true</c> if this method handled the
        /// inbound; returns <c>false</c> when the gate is off, the client
        /// lacks an agency mapping (defensive), or the agency registry
        /// entry is missing (defensive).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyWolfHopperStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            var accepted = new List<AgencyWolfHopperEntry>(msg.EntryCount);
            var removedKeys = new List<string>();

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                for (var i = 0; i < msg.EntryCount; i++)
                {
                    var entry = msg.Entries[i];
                    if (entry == null)
                        continue;

                    try
                    {
                        if (string.IsNullOrEmpty(entry.Id)
                            || string.IsNullOrEmpty(entry.Body)
                            || string.IsNullOrEmpty(entry.Biome))
                        {
                            LunaLog.Debug($"[fix:WOLF-R4] hopper entry skipped: empty Id/Body/Biome (agency {agencyId:N})");
                            continue;
                        }

                        Upsert(agency, entry);
                        accepted.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:WOLF-R4] hopper entry skipped for '{entry.Id}' at '{entry.Body}/{entry.Biome}' (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Removal tail. Hoppers DO have a normal-op WOLF API
                // (ScenarioPersister.RemoveHopper at
                // ScenarioPersister.cs:432-440) — the UI recipe-change flow
                // removes the old hopper before creating a new one. Skip
                // null/empty; drop any key that's not in the dict (no-op).
                if (msg.RemovedKeyCount > 0 && msg.RemovedKeys != null)
                {
                    for (var i = 0; i < msg.RemovedKeyCount; i++)
                    {
                        var key = msg.RemovedKeys[i];
                        if (string.IsNullOrEmpty(key))
                            continue;
                        if (agency.WolfHoppers.Remove(key))
                            removedKeys.Add(key);
                    }
                }
            }

            if (accepted.Count > 0 || removedKeys.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendWolfHopperStateToOwner(client, agencyId, accepted, removedKeys);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the hopper entry keyed by <c>entry.Id</c>
        /// (Guid string in <c>ToString()</c> form WITH hyphens — preserve at
        /// the wire boundary). Caller MUST hold
        /// <c>AgencySystem.GetAgencyLock(agencyId)</c> per the
        /// <see cref="AgencyState.WolfHoppers"/> concurrency contract.
        ///
        /// <para><b>Defensive copy.</b> The entry's <c>Recipe</c> field is a
        /// flat comma-joined string per WOLF's
        /// <c>HopperMetadata.cs:44-48</c> persistence format — value-shape,
        /// no nested mutable state. A shallow copy of the four scalar fields
        /// suffices to insulate the stored snapshot from any future wire
        /// re-arrival.</para>
        ///
        /// <para><b>Internal visibility</b> matches
        /// <see cref="AgencyWolfRouteRouter.Upsert"/> — ServerTest reaches in
        /// to pin the upsert semantics without bringing the full
        /// <see cref="TryRoute"/> path up.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyWolfHopperEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Id))
                throw new ArgumentException("Hopper entry Id must be non-empty", nameof(entry));

            var copy = new AgencyWolfHopperEntry
            {
                Id = entry.Id,
                Body = entry.Body ?? string.Empty,
                Biome = entry.Biome ?? string.Empty,
                Recipe = entry.Recipe ?? string.Empty,
            };

            agency.WolfHoppers[entry.Id] = copy;
        }
    }
}
