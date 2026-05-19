using LmpCommon.Message.Data.Agency;
using Server.Client;
using Server.Log;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 3 Slice C — server-side per-agency router for MKS' planetary-logistics
    /// warehouse balances. Sits behind the <c>AgencyMsgReader</c> dispatch for
    /// <see cref="LmpCommon.Message.Types.AgencyMessageType.PlanetaryState"/> (slot
    /// 7). Replaces the legacy 30s SHA broadcast of <c>PlanetaryLogisticsScenario</c>
    /// with per-agency routing when <see cref="AgencySystem.PerAgencyEnabled"/> is
    /// true (gate=on AND Career mode). Owner-only echo + persistence; the projector
    /// (<see cref="AgencyScenarioProjector"/>) splices per-agency entries into
    /// outgoing <c>PlanetaryLogisticsScenario</c> blobs at <c>SendScenarioModules</c>
    /// time so each agency sees ONLY their own planetary entries — spec §10 Q1
    /// PrivateAgencyResources=true.
    ///
    /// <para><b>Structural template: Slice B's <see cref="AgencyKolonyRouter"/>.</b>
    /// Same single-try/catch isolation per entry, same dual-mode silence gate,
    /// same cross-agency rejection at Warning log level per the 5.17a soak
    /// Finding-2 precedent, same internal-visibility on <see cref="Upsert"/> for
    /// ServerTest reach-in. The DIFFERENCES from Slice B:</para>
    /// <list type="bullet">
    ///   <item><b>Partition key:</b> body-and-resource (<c>$"{bodyIndex}|{resourceName}"</c>),
    ///        NOT vessel-and-body. Multiple of an agency's vessels pumping the
    ///        same resource on the same body collapse into ONE entry.</item>
    ///   <item><b>VesselId is typed Guid not string:</b>
    ///        <see cref="AgencyPlanetaryEntry.OwningVesselId"/> arrives canonical.
    ///        No Guid.TryParse step, no string-form normalisation. The
    ///        cross-agency check still consults
    ///        <see cref="VesselStoreSystem.CurrentVessels"/> using the Guid
    ///        directly.</item>
    ///   <item><b>Migration policy on vessel-stamp change: NONE.</b> Per
    ///        pre-spec §4.e: planetary entries do NOT migrate when a vessel
    ///        changes agency — the entry represents a body's logistics pool,
    ///        not a vessel's contribution. The Slice E MKS-aware extension
    ///        to the (future) <c>setvesselagency</c> command will explicitly
    ///        skip the planetary dict during migration; the documented
    ///        operator recovery for "I want my planetary balances to move
    ///        with the vessel" is to hand-edit
    ///        <c>Universe/Agencies/{guid}.txt</c>. The 5.18d
    ///        <c>transferagency</c> command itself preserves agency identity
    ///        (renames the owner only), so no migration happens there
    ///        either. The only removal-producing path on planetary entries
    ///        is the EXISTING 5.18d <c>deleteagency</c> cascade (which
    ///        removes the agency file wholesale; per-entry removal echoes
    ///        are not needed since the client mirror simply forgets the
    ///        AgencyId).</item>
    /// </list>
    ///
    /// <para><b>Single-class wire contract (pre-spec §2.e).</b>
    /// <see cref="AgencyPlanetaryStateMsgData"/> is used both directions on slot
    /// 7; inbound from the client postfix carries wire-supplied <c>AgencyId</c>
    /// that the server IGNORES. Sender authority is derived authoritatively from
    /// <c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c> — same trust
    /// posture as <see cref="AgencyKolonyRouter"/> + 5.17d
    /// <see cref="AgencyContractRouter"/>. Spoofing which agency a mutation is
    /// attributed to is structurally impossible.</para>
    ///
    /// <para><b>Per-entry isolation (pre-spec §3.a).</b> Cross-agency check +
    /// vessel lookup + upsert are wrapped in a SINGLE try/catch per entry. A
    /// malformed vessel id, a missing-from-store vessel, or an upsert failure
    /// for one entry never aborts siblings. Matches
    /// <see cref="AgencyKolonyRouter.TryRoute"/>'s shape directly.</para>
    ///
    /// <para><b>Cross-agency rejection (pre-spec §2.b.ii).</b> If the inbound
    /// entry's resolved vessel has a non-Empty <c>OwningAgencyId</c> that does
    /// NOT match the sender's agency, the entry is dropped with a Warning log
    /// line (5.17a soak Finding-2 precedent, applied uniformly across Phase 3).
    /// Unassigned-sentinel (<c>OwningAgencyId == Guid.Empty</c>) bypasses the
    /// check per spec §10 Q3 — any agency may interact with pre-0.31 vessels
    /// until the admin stamps them via Stage 5.18d <c>transferagency</c>.</para>
    ///
    /// <para><b>Vessel-not-in-store decision.</b> Same pragmatic DROP as Slice B
    /// kolony — if the entry's <see cref="AgencyPlanetaryEntry.OwningVesselId"/>
    /// is not in <see cref="VesselStoreSystem.CurrentVessels"/>, the entry is
    /// dropped with a Debug log line. The pre-spec asymmetry vs 5.17a is
    /// intentional: the postfix-vs-proto-ingest race is benign for planetary
    /// entries because the projector reads ownership from the canonical vessel
    /// store at splice time, so a not-in-store id would never project anyway.</para>
    ///
    /// <para><b>No shared-pool slot freeing.</b> Unlike
    /// <see cref="AgencyContractRouter"/>'s Offered-slot removal, planetary
    /// entries have no peer-acceptance race — the shared
    /// <c>PlanetaryLogisticsScenario</c> still accumulates from any clients
    /// running with gate=off (in mixed-mode futures); under uniform gate=on the
    /// IgnoredScenarios Option B filter (Slice C item 13) prevents clients from
    /// broadcasting <c>PlanetaryLogisticsScenario</c> via the 30s SHA pass, so
    /// the shared scenario stays at the operator-supplied baseline.</para>
    ///
    /// <para><b>Dual-mode gate (spec §11).</b> With
    /// <see cref="AgencySystem.PerAgencyEnabled"/> false, <see cref="TryRoute"/>
    /// returns <c>false</c> immediately and the caller — <c>AgencyMsgReader</c>
    /// — drops the inbound silently. Under uniform gate=off the postfix is also
    /// a no-op so this branch shouldn't fire in practice. The early-return
    /// matches every other Agency* surface for consistency.</para>
    /// </summary>
    public static class AgencyPlanetaryRouter
    {
        /// <summary>
        /// Attempts to route the inbound planetary state batch through the
        /// per-agency path. Returns <c>true</c> if this method handled the
        /// inbound; returns <c>false</c> when the gate is off, the client
        /// lacks an agency mapping (defensive — under gate=on every
        /// authenticated client has one via the handshake auto-register), or
        /// the agency registry entry is missing (defensive — same).
        /// </summary>
        public static bool TryRoute(ClientStructure client, AgencyPlanetaryStateMsgData msg)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            // [Phase 3 Slice C / consumer-lens MUST FIX #1] Empty-batch
            // observability. An EntryCount=0 inbound is structurally possible
            // (buggy client, future Slice D/E author probing wire shape) and
            // would otherwise produce zero log lines AND zero echo —
            // operator's only signal is "did my catchup land?" Log once so
            // operators investigating per-agency MKS sync gaps have a grep
            // target. Caller contract on no echo: per the AgencyPlanetaryStateMsgData
            // XML "Echo vs catch-up distinction" para, an inbound with no
            // accepted entries produces no echo by design (the postfix never
            // emits empty batches in practice; only a buggy/probing client
            // would reach here with EntryCount=0).
            if (msg.EntryCount == 0)
            {
                LunaLog.Debug($"[fix:MKS-R2] empty planetary batch from {client.PlayerName} (agency {agencyId:N}) — no-op by design.");
                return true;
            }

            // Per-entry classify + upsert. The single-try/catch wraps the entire
            // per-entry pipeline (vessel lookup, cross-agency check, upsert) so
            // one malformed entry never derails siblings. Same shape as
            // AgencyKolonyRouter.TryRoute step 5.
            var accepted = new List<AgencyPlanetaryEntry>(msg.EntryCount);
            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                for (var i = 0; i < msg.EntryCount; i++)
                {
                    var entry = msg.Entries[i];
                    if (entry == null)
                        continue;

                    try
                    {
                        // ResourceName is the second half of the partition key
                        // and the projector emits it as a value-pair name on
                        // disk; empty / null would corrupt both the dict key
                        // and the projected scenario shape.
                        if (string.IsNullOrEmpty(entry.ResourceName))
                        {
                            LunaLog.Debug($"[fix:MKS-R2] planetary entry skipped: empty ResourceName (agency {agencyId:N})");
                            continue;
                        }

                        // OwningVesselId is already a Guid (typed on the wire,
                        // no string-form drift to normalise). Compare to the
                        // canonical store directly. An all-zeros Guid here is
                        // distinct from the vessel-store sentinel — it means
                        // the client postfix could not resolve a vessel id
                        // (e.g. KSP-side this.vessel was null mid-scene-load).
                        // Drop those silently; the next postfix tick on a
                        // stable vessel will produce a real id.
                        if (entry.OwningVesselId == Guid.Empty)
                        {
                            LunaLog.Debug($"[fix:MKS-R2] planetary entry skipped: empty OwningVesselId (agency {agencyId:N}, body {entry.BodyIndex})");
                            continue;
                        }

                        if (!VesselStoreSystem.CurrentVessels.TryGetValue(entry.OwningVesselId, out var v))
                        {
                            // Vessel-not-in-store — pragmatic drop (see class XML).
                            // Same posture as Slice B kolony.
                            LunaLog.Debug($"[fix:MKS-R2] planetary entry skipped: vessel {entry.OwningVesselId:N} not in store (agency {agencyId:N})");
                            continue;
                        }

                        // Cross-agency rejection. Unassigned-sentinel (Guid.Empty)
                        // on the vessel's stamp bypasses per spec §10 Q3 — any
                        // agency may interact with pre-0.31 vessels until admin
                        // transferagency stamps them.
                        //
                        // Warning level matches Slice B per the 5.17a soak
                        // Finding-2 precedent (operator-grep visibility for
                        // cross-agency claim attempts). Malformed VesselId +
                        // vessel-not-in-store stay at Debug (race-window cases
                        // on legitimate clients).
                        if (v.OwningAgencyId != Guid.Empty && v.OwningAgencyId != agencyId)
                        {
                            LunaLog.Warning($"[fix:MKS-R2] planetary entry rejected: vessel {entry.OwningVesselId:N} owning agency {v.OwningAgencyId:N} != requester {agencyId:N} (body {entry.BodyIndex}, resource {entry.ResourceName})");
                            continue;
                        }

                        Upsert(agency, entry);
                        // [Phase 3 Slice C / general-lens SHOULD FIX S1]
                        // Deep-copy the entry into `accepted` so a concurrent
                        // Upsert against the same key cannot tear the wire
                        // serialization that happens AFTER lock release. The
                        // 1-player-per-agency invariant (spec §10) makes the
                        // concurrent-mutate-against-same-key window vanishingly
                        // narrow in practice, but the invariant is documented
                        // not enforced and the wire-side serializer would
                        // observe a torn entry on race. 4 fields = cheap. (The
                        // same hazard applies to AgencyKolonyRouter; deferred
                        // to a Slice B refresh — same fix shape.)
                        accepted.Add(new AgencyPlanetaryEntry
                        {
                            OwningVesselId = entry.OwningVesselId,
                            BodyIndex = entry.BodyIndex,
                            ResourceName = entry.ResourceName,
                            StoredQuantity = entry.StoredQuantity,
                        });
                    }
                    catch (Exception ex)
                    {
                        LunaLog.Error($"[fix:MKS-R2] planetary entry skipped for vessel {entry.OwningVesselId:N} (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (accepted.Count > 0)
            {
                AgencySystem.SaveAgency(agencyId);
                AgencySystemSender.SendPlanetaryStateToOwner(client, agencyId, accepted);
            }

            return true;
        }

        /// <summary>
        /// Inserts or replaces the planetary entry keyed by
        /// <c>$"{bodyIndex}|{resourceName}"</c>. Caller MUST hold
        /// <c>AgencySystem.GetAgencyLock(agencyId)</c> per the
        /// <see cref="AgencyState.PlanetaryEntries"/> concurrency contract.
        ///
        /// <para><b>Key construction.</b> Body-and-resource keyed —
        /// <c>$"{<see cref="AgencyPlanetaryEntry.BodyIndex"/>}|{<see cref="AgencyPlanetaryEntry.ResourceName"/>}"</c>.
        /// <c>BodyIndex.ToString(CultureInfo.InvariantCulture)</c> is locale-safe
        /// for the integer case (defensive belt-and-braces, not required for
        /// correctness on an integer). DISTINCT from Slice B kolony's
        /// vessel-and-body key — the body-and-resource shape means multiple
        /// of an agency's vessels pumping the same resource on the same body
        /// collapse into one entry (the intended planetary-pool product).
        /// Slice D's <c>OrbitalTransfers</c> is keyed by <c>Guid TransferGuid</c>
        /// directly — three different key shapes for three different per-router
        /// partitions.</para>
        ///
        /// <para><b>Migration scan strategy</b> (for the Stage 5.18d MKS-aware
        /// <c>transferagency</c> extension in Slice E): per pre-spec §4.e,
        /// planetary entries do NOT migrate. Slice E's scan strategy for
        /// planetary explicitly SKIPS the dict — no key prefix scan, no value
        /// field scan. Pre-spec §4.e operator workflow is the documented
        /// recovery path. (Compare to Slice B kolony's prefix scan and Slice
        /// D orbital's value-field scan against OriginVesselId +
        /// DestinationVesselId.)</para>
        ///
        /// <para><b>Defensive copy.</b> The planetary entry has no mutable
        /// byte-array fields — all 4 fields are value types or an immutable
        /// string. Reference assignment is safe under the per-agency lock +
        /// same-message-single-thread Lidgren receive contract. Same shape as
        /// Slice B kolony. (Slice D's <c>AgencyOrbitalTransferEntry.PayloadBytes</c>
        /// will need <c>Buffer.BlockCopy</c> before upserting.)</para>
        ///
        /// <para><b>Internal visibility</b> matches
        /// <see cref="AgencyKolonyRouter.Upsert"/> — ServerTest reaches in to
        /// pin the upsert semantics without bringing the full
        /// <see cref="TryRoute"/> path up; the MockClientTest harness covers
        /// the wire-level integration.</para>
        /// </summary>
        internal static void Upsert(AgencyState agency, AgencyPlanetaryEntry entry)
        {
            if (agency == null)
                throw new ArgumentNullException(nameof(agency));
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            var key = $"{entry.BodyIndex.ToString(CultureInfo.InvariantCulture)}|{entry.ResourceName}";
            agency.PlanetaryEntries[key] = entry;
        }
    }
}
