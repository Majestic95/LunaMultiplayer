using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 4 Slice C — server-side per-agency router for MKS WOLF cargo
    /// routes. Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.WolfRouteState"/>.
    /// Replaces the legacy 30s SHA broadcast of <c>WOLF_ScenarioModule</c>'s
    /// <c>ROUTES</c> child for the route surface when
    /// <see cref="AgencySystem.PerAgencyEnabled"/> is true (gate=on AND Career
    /// mode). Owner-only echo + persistence; the projector
    /// (<see cref="AgencyScenarioProjector"/>) splices per-agency entries into
    /// outgoing <c>WOLF_ScenarioModule</c> blobs at <c>SendScenarioModules</c>
    /// time so each agency sees ONLY their own routes — spec §10 Q1
    /// PrivateAgencyResources=true.
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyWolfRouteStateMsgData"/> is used both directions on
    /// slot 10; inbound from the client postfix carries wire-supplied
    /// <c>AgencyId</c> that the server IGNORES. Sender authority is derived
    /// authoritatively from <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>
    /// — same trust posture as <see cref="AgencyWolfDepotRouter.TryRoute"/>.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Validation + upsert
    /// are wrapped in a SINGLE try/catch per entry. A malformed entry (empty
    /// Origin/Destination Body or Biome) never aborts siblings. Matches the
    /// <see cref="AgencyWolfDepotRouter"/> precedent.</para>
    ///
    /// <para><b>No cross-agency vessel-proxy check.</b> Routes are 4-string-
    /// composite-keyed (<c>OriginBody|OriginBiome|DestinationBody|DestinationBiome</c>),
    /// not vessel-keyed. Two agencies CAN each have a route between the same
    /// body+biome endpoints — they live in separate per-agency dicts; the
    /// projector splice emits only the requesting agency's routes into
    /// outgoing scenario blobs. The cross-agency CrewRoute kerbal gate in
    /// Slice E is the only Phase 4 router with vessel-proxy authority.</para>
    ///
    /// <para><b>FK-integrity decoupling (pre-spec §2.f.iii).</b> WOLF's
    /// <c>Route.OnLoad</c> at <c>Route.cs:172-173</c> calls
    /// <c>_depotRegistry.GetDepot(OriginBody, OriginBiome)</c> which throws
    /// <c>DepotDoesNotExistException</c> when the depot doesn't exist —
    /// killing the whole scenario load. The router does NOT enforce
    /// origin/destination depots being present in <c>WolfDepots</c> at
    /// upsert time: the wire batch may arrive before the parent depot's
    /// postfix-driven upsert (different Harmony patches, different messages,
    /// no ordering guarantee). FK integrity is enforced LATER in the
    /// projector splice: any Route whose origin or destination key is
    /// missing from the just-emitted depot pool is dropped from the
    /// outgoing scenario blob — see <c>AgencyScenarioProjector.SpliceAgencyWolfState</c>.
    /// This decouples disk-side persistence (loose) from wire-side projection
    /// (strict) and lets normal-operation message arrival ordering self-heal.</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false,
    /// <see cref="TryRoute"/> returns <c>false</c> immediately and the caller
    /// — <c>AgencyMsgReader</c> — drops the inbound silently. Same posture
    /// as <see cref="AgencyWolfDepotRouter"/>.</para>
    ///
    /// <para><b>Per-agency lock contract (pre-spec §3.b).</b> The per-entry
    /// for-loop is wrapped in
    /// <c>lock (AgencySystem.GetAgencyLock(agencyId))</c>. Mirrors
    /// <see cref="AgencyWolfDepotRouter.TryRoute"/>.</para>
    /// </summary>
    public static class AgencyWolfRouteRouter
    {
        /// <summary>
        /// Attempts to route the inbound route state batch through the
        /// per-agency path. Returns <c>true</c> if this method handled the
        /// inbound; returns <c>false</c> when the gate is off, the client
        /// lacks an agency mapping (defensive), or the agency registry
        /// entry is missing (defensive).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyWolfRouteStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            var accepted = new List<AgencyWolfRouteEntry>(msg.EntryCount);
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
                        if (string.IsNullOrEmpty(entry.OriginBody) || string.IsNullOrEmpty(entry.OriginBiome)
                            || string.IsNullOrEmpty(entry.DestinationBody) || string.IsNullOrEmpty(entry.DestinationBiome))
                        {
                            LunaLog.Debug($"[fix:WOLF-R4] route entry skipped: empty Origin/Destination Body or Biome (agency {agencyId:N})");
                            continue;
                        }

                        Upsert(agency, entry);
                        accepted.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:WOLF-R4] route entry skipped for '{entry.OriginBody}/{entry.OriginBiome}→{entry.DestinationBody}/{entry.DestinationBiome}' (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Removal tail. Routes have no normal-op Remove API in WOLF
                // (no ScenarioPersister.RemoveRoute method exists in MKS),
                // so RemovedKeys is reserved for migration / admin paths
                // (Slice F deleteagency cleanup). Validation: skip
                // null/empty; drop any key that's not in the dict (no-op).
                if (msg.RemovedKeyCount > 0 && msg.RemovedKeys != null)
                {
                    for (var i = 0; i < msg.RemovedKeyCount; i++)
                    {
                        var key = msg.RemovedKeys[i];
                        if (string.IsNullOrEmpty(key))
                            continue;
                        if (agency.WolfRoutes.Remove(key))
                            removedKeys.Add(key);
                    }
                }
            }

            if (accepted.Count > 0 || removedKeys.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendWolfRouteStateToOwner(client, agencyId, accepted, removedKeys);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the route entry keyed by
        /// <c>$"{OriginBody}|{OriginBiome}|{DestinationBody}|{DestinationBiome}"</c>.
        /// Caller MUST hold <c>AgencySystem.GetAgencyLock(agencyId)</c> per
        /// the <see cref="AgencyState.WolfRoutes"/> concurrency contract.
        ///
        /// <para><b>Defensive copy of <see cref="AgencyWolfRouteEntry.Resources"/></b>
        /// (pre-spec §3.c). The nested list is a mutable
        /// <c>List&lt;AgencyWolfRouteResourceEntry&gt;</c> from the wire.
        /// Storing the reference directly would let a subsequent re-arrival
        /// mutate the stored entry in place — defensive shallow copy of the
        /// list + the wire values preserves the at-ingest snapshot. Resource
        /// entries themselves are value-shape (no nested mutable state) so a
        /// shallow copy suffices.</para>
        ///
        /// <para><b>Internal visibility</b> matches
        /// <see cref="AgencyWolfDepotRouter.Upsert"/> — ServerTest reaches in
        /// to pin the upsert semantics without bringing the full
        /// <see cref="TryRoute"/> path up.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyWolfRouteEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            var copy = new AgencyWolfRouteEntry
            {
                OriginBody = entry.OriginBody,
                OriginBiome = entry.OriginBiome,
                DestinationBody = entry.DestinationBody,
                DestinationBiome = entry.DestinationBiome,
                Payload = entry.Payload,
                Resources = new List<AgencyWolfRouteResourceEntry>(entry.Resources?.Count ?? 0),
            };
            if (entry.Resources != null)
            {
                foreach (var resource in entry.Resources)
                {
                    if (resource == null)
                        continue;
                    copy.Resources.Add(new AgencyWolfRouteResourceEntry
                    {
                        ResourceName = resource.ResourceName,
                        Quantity = resource.Quantity,
                    });
                }
            }

            var key = $"{entry.OriginBody}|{entry.OriginBiome}|{entry.DestinationBody}|{entry.DestinationBiome}";
            agency.WolfRoutes[key] = copy;
        }
    }
}
