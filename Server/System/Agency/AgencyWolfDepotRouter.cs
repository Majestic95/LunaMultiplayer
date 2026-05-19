using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 4 Slice B — server-side per-agency router for MKS WOLF depots.
    /// Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.WolfDepotState"/>.
    /// Replaces the legacy 30s SHA broadcast of <c>WOLF_ScenarioModule</c>'s
    /// <c>DEPOTS</c> child for the depot surface when
    /// <see cref="AgencySystem.PerAgencyEnabled"/> is true (gate=on AND Career
    /// mode). Owner-only echo + persistence; the projector
    /// (<see cref="AgencyScenarioProjector"/>) splices per-agency entries into
    /// outgoing <c>WOLF_ScenarioModule</c> blobs at <c>SendScenarioModules</c>
    /// time so each agency sees ONLY their own depots — spec §10 Q1
    /// PrivateAgencyResources=true.
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyWolfDepotStateMsgData"/> is used both directions on
    /// slot 9; inbound from the client postfix carries wire-supplied
    /// <c>AgencyId</c> that the server IGNORES. Sender authority is derived
    /// authoritatively from <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>
    /// — same trust posture as <see cref="AgencyKolonyRouter.TryRoute"/>.
    /// Spoofing which agency a mutation is attributed to is structurally
    /// impossible.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Validation +
    /// upsert are wrapped in a SINGLE try/catch per entry. A malformed entry
    /// (empty Body / empty Biome) never aborts siblings. Matches the
    /// <see cref="AgencyKolonyRouter"/> precedent.</para>
    ///
    /// <para><b>No cross-agency vessel-proxy check.</b> Depots are
    /// <c>(Body, Biome)</c>-keyed, not vessel-keyed. Two agencies CAN each
    /// have a depot at the same body+biome — they live in separate per-agency
    /// dicts; the projector splice emits only the requesting agency's depots
    /// into outgoing scenario blobs. The cross-agency CrewRoute kerbal gate
    /// in Slice E is the only Phase 4 router with vessel-proxy authority;
    /// Depot/Route/Hopper/Terminal routers are pure per-agency partitions.</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false,
    /// <see cref="TryRoute"/> returns <c>false</c> immediately and the caller
    /// — <c>AgencyMsgReader</c> — drops the inbound silently (a client
    /// emitting <c>AgencyWolfDepotStateMsgData</c> under gate=off is already
    /// protocol-violating; the read path's gate-off branch in
    /// <c>AgencyMsgReader.HandleMessage</c> handles the same posture for
    /// sibling subtypes). Under uniform gate=off the postfix is also a no-op
    /// so this branch shouldn't fire in practice. The early-return matches
    /// every other Agency* surface for consistency.</para>
    ///
    /// <para><b>Per-agency lock contract (pre-spec §3.b).</b> The per-entry
    /// for-loop is wrapped in
    /// <c>lock (AgencySystem.GetAgencyLock(agencyId))</c>. Mirrors
    /// <see cref="AgencyKolonyRouter.TryRoute"/> at line 100. A concurrent
    /// <c>SaveAgency</c> or projector splice would otherwise observe a torn
    /// intermediate snapshot.</para>
    /// </summary>
    public static class AgencyWolfDepotRouter
    {
        /// <summary>
        /// Attempts to route the inbound depot state batch through the
        /// per-agency path. Returns <c>true</c> if this method handled the
        /// inbound; returns <c>false</c> when the gate is off, the client
        /// lacks an agency mapping (defensive — under gate=on every
        /// authenticated client has one via the handshake auto-register),
        /// or the agency registry entry is missing (defensive — same).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyWolfDepotStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            var accepted = new List<AgencyWolfDepotEntry>(msg.EntryCount);
            var removedKeys = new List<string>();

            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                // Per-entry classify + upsert. The single-try/catch wraps the
                // entire per-entry pipeline (Body/Biome validation, upsert) so
                // one malformed entry never derails siblings.
                for (var i = 0; i < msg.EntryCount; i++)
                {
                    var entry = msg.Entries[i];
                    if (entry == null)
                        continue;

                    try
                    {
                        if (string.IsNullOrEmpty(entry.Body) || string.IsNullOrEmpty(entry.Biome))
                        {
                            LunaLog.Debug($"[fix:WOLF-R4] depot entry skipped: empty Body or Biome (agency {agencyId:N})");
                            continue;
                        }

                        Upsert(agency, entry);
                        accepted.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:WOLF-R4] depot entry skipped for '{entry.Body}/{entry.Biome}' (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Removal tail. Pre-spec §2.b.i: depots have no normal-op
                // Remove API in WOLF (no ScenarioPersister.RemoveDepot
                // method exists), so RemovedKeys is reserved for migration /
                // admin paths. Validation: skip null/empty; drop any key
                // that's not in the dict (no-op semantics).
                if (msg.RemovedKeyCount > 0 && msg.RemovedKeys != null)
                {
                    for (var i = 0; i < msg.RemovedKeyCount; i++)
                    {
                        var key = msg.RemovedKeys[i];
                        if (string.IsNullOrEmpty(key))
                            continue;
                        if (agency.WolfDepots.Remove(key))
                            removedKeys.Add(key);
                    }
                }
            }

            if (accepted.Count > 0 || removedKeys.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendWolfDepotStateToOwner(client, agencyId, accepted, removedKeys);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the depot entry keyed by
        /// <c>$"{<see cref="AgencyWolfDepotEntry.Body"/>}|{<see cref="AgencyWolfDepotEntry.Biome"/>}"</c>.
        /// Caller MUST hold <c>AgencySystem.GetAgencyLock(agencyId)</c> per
        /// the <see cref="AgencyState.WolfDepots"/> concurrency contract.
        ///
        /// <para><b>Defensive copy of <see cref="AgencyWolfDepotEntry.ResourceStreams"/></b>
        /// (pre-spec §3.c). The nested list is a mutable
        /// <c>List&lt;AgencyWolfResourceStreamEntry&gt;</c> from the wire.
        /// Storing the reference directly would let a subsequent re-arrival
        /// mutate the stored entry in place — defensive shallow copy of the
        /// list + the wire-stream values preserves the at-ingest snapshot.
        /// Stream entries themselves are value-shape (no nested mutable
        /// state) so a shallow copy suffices.</para>
        ///
        /// <para><b>Internal visibility</b> matches
        /// <see cref="AgencyKolonyRouter.Upsert"/> — ServerTest reaches in
        /// to pin the upsert semantics without bringing the full
        /// <see cref="TryRoute"/> path up.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyWolfDepotEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            var copy = new AgencyWolfDepotEntry
            {
                Body = entry.Body,
                Biome = entry.Biome,
                IsEstablished = entry.IsEstablished,
                IsSurveyed = entry.IsSurveyed,
                ResourceStreams = new List<AgencyWolfResourceStreamEntry>(entry.ResourceStreams?.Count ?? 0),
            };
            if (entry.ResourceStreams != null)
            {
                foreach (var stream in entry.ResourceStreams)
                {
                    if (stream == null)
                        continue;
                    copy.ResourceStreams.Add(new AgencyWolfResourceStreamEntry
                    {
                        ResourceName = stream.ResourceName,
                        Incoming = stream.Incoming,
                        Outgoing = stream.Outgoing,
                    });
                }
            }

            var key = $"{entry.Body}|{entry.Biome}";
            agency.WolfDepots[key] = copy;
        }
    }
}
