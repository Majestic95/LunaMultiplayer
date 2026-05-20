using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 4 Slice D — server-side per-agency router for MKS WOLF terminals.
    /// Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.WolfTerminalState"/>
    /// (slot 12). Owner-only echo + persistence; the projector
    /// (<see cref="AgencyScenarioProjector"/>) splices per-agency entries into
    /// outgoing <c>WOLF_ScenarioModule</c> blobs at <c>SendScenarioModules</c>
    /// time so each agency sees ONLY their own terminals — spec §10 Q1
    /// PrivateAgencyResources=true.
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyWolfTerminalStateMsgData"/> is used both directions on
    /// slot 12; inbound from the client postfix carries wire-supplied
    /// <c>AgencyId</c> that the server IGNORES. Sender authority is derived
    /// authoritatively from <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>
    /// — same trust posture as <see cref="AgencyWolfRouteRouter.TryRoute"/>
    /// and <see cref="AgencyWolfHopperRouter.TryRoute"/>.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Validation + upsert
    /// are wrapped in a SINGLE try/catch per entry.</para>
    ///
    /// <para><b>Key form preservation</b> (pre-spec §2.f.vi). Terminal.Id is
    /// a Guid in <c>ToString("N")</c> form WITHOUT hyphens per WOLF's
    /// <c>TerminalMetadata.cs:15</c>. Distinct from
    /// <see cref="AgencyState.WolfHoppers"/>' with-hyphens form — do NOT
    /// normalize at any boundary. Router uses the raw wire string as the
    /// dictionary key.</para>
    ///
    /// <para><b>No FK sweep (unlike Hoppers and Routes).</b> WOLF's
    /// <c>ScenarioPersister.OnLoad</c> at <c>ScenarioPersister.cs:343-353</c>
    /// loads terminals via <c>new TerminalMetadata()</c> + <c>OnLoad(...)</c>
    /// directly — NO depot-registry lookup, NO silent drop on missing depot.
    /// <c>TerminalMetadata</c> carries its own <c>Body</c>+<c>Biome</c>
    /// fields per <c>TerminalMetadata.cs:9-29</c>. Per source contract a
    /// terminal can persist independent of depot existence. The projector
    /// emits TERMINALS without an FK gate.</para>
    ///
    /// <para><b>No cross-agency vessel-proxy check.</b> Terminals are
    /// Guid-keyed, not vessel-keyed. Same posture as Routes / Hoppers.</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false,
    /// <see cref="TryRoute"/> returns <c>false</c> immediately and the caller
    /// drops the inbound silently.</para>
    ///
    /// <para><b>Per-agency lock contract (pre-spec §3.b).</b> Per-entry
    /// for-loop wrapped in <c>lock (AgencySystem.GetAgencyLock(agencyId))</c>.</para>
    ///
    /// <para><b>RemovedKeys is non-trivially used.</b> WOLF's
    /// <c>ScenarioPersister.RemoveTerminal(string id)</c> at
    /// <c>ScenarioPersister.cs:442-449</c> is a normal-operation API (the
    /// WOLF UI removes a terminal when the operator decommissions it). The
    /// matching Slice D client-side <c>ScenarioPersister_RemoveTerminalPostfix</c>
    /// ships the removed id in the
    /// <see cref="AgencyWolfTerminalStateMsgData.RemovedKeys"/> tail.</para>
    /// </summary>
    public static class AgencyWolfTerminalRouter
    {
        /// <summary>
        /// Attempts to route the inbound terminal state batch through the
        /// per-agency path. Returns <c>true</c> if this method handled the
        /// inbound; returns <c>false</c> when the gate is off, the client
        /// lacks an agency mapping (defensive), or the agency registry
        /// entry is missing (defensive).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyWolfTerminalStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            var accepted = new List<AgencyWolfTerminalEntry>(msg.EntryCount);
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
                            LunaLog.Debug($"[fix:WOLF-R4] terminal entry skipped: empty Id/Body/Biome (agency {agencyId:N})");
                            continue;
                        }

                        Upsert(agency, entry);
                        accepted.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:WOLF-R4] terminal entry skipped for '{entry.Id}' at '{entry.Body}/{entry.Biome}' (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (msg.RemovedKeyCount > 0 && msg.RemovedKeys != null)
                {
                    for (var i = 0; i < msg.RemovedKeyCount; i++)
                    {
                        var key = msg.RemovedKeys[i];
                        if (string.IsNullOrEmpty(key))
                            continue;
                        if (agency.WolfTerminals.Remove(key))
                            removedKeys.Add(key);
                    }
                }
            }

            if (accepted.Count > 0 || removedKeys.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendWolfTerminalStateToOwner(client, agencyId, accepted, removedKeys);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the terminal entry keyed by <c>entry.Id</c>
        /// (Guid string in <c>ToString("N")</c> form — no hyphens). Caller
        /// MUST hold <c>AgencySystem.GetAgencyLock(agencyId)</c>.
        ///
        /// <para><b>Defensive copy</b>: shallow copy of three scalar fields
        /// suffices (no nested mutable state in the entry).</para>
        ///
        /// <para><b>Internal visibility</b> matches sibling routers for
        /// ServerTest reach-in.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyWolfTerminalEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Id))
                throw new ArgumentException("Terminal entry Id must be non-empty", nameof(entry));

            var copy = new AgencyWolfTerminalEntry
            {
                Id = entry.Id,
                Body = entry.Body ?? string.Empty,
                Biome = entry.Biome ?? string.Empty,
            };

            agency.WolfTerminals[entry.Id] = copy;
        }
    }
}
