using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Log;
using Server.System.Vessel;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S2 — SCANsat] Path B per-agency router for SCANsat coverage +
    /// scanner state. Sits behind <see cref="Scenario.ScenarioDataUpdater.RawConfigNodeInsertOrUpdate"/>
    /// for the <c>SCANcontroller</c> scenario module. Replaces the shared-store
    /// write under <see cref="AgencySystem.PerAgencyEnabled"/> with a per-agency
    /// upsert into <see cref="AgencyState.Coverage"/> +
    /// <see cref="AgencyState.Scanners"/>. The shared
    /// <c>ScenarioStoreSystem.CurrentScenarios["SCANcontroller"]</c> entry stays
    /// at the operator-seed baseline forever under gate=on; the projector
    /// (<see cref="AgencyScenarioProjector.SpliceSCANsatCoverageIntoScenario"/>)
    /// splices per-agency state on top of that baseline at every
    /// <c>SendScenarioModules</c> tick + handshake catch-up.
    ///
    /// <para><b>Path B (implementation-spec D1).</b> No client-side Harmony, no
    /// dedicated wire, no <c>IgnoredScenarios</c> entry, no owner echo. Mutation
    /// rides the existing 30s SCANsat SHA broadcast; catch-up rides the new D2
    /// <see cref="ScenarioSystem.SendScenariosToClient"/> helper at handshake
    /// completion.</para>
    ///
    /// <para><b>Suppression model.</b> Under gate=on, <see cref="TryRoute"/>
    /// returns <c>true</c> and the caller skips <c>CurrentScenarios.AddOrUpdate</c>.
    /// Per-agency state accumulates separately in <see cref="AgencyState.Coverage"/>
    /// + <see cref="AgencyState.Scanners"/>. SCANResources + the ~30 root-level
    /// KSPField UI scalars stay at the operator-seed baseline (Decision §6 + §7).
    /// Under gate=off the router early-returns <c>false</c> and the legacy
    /// AddOrUpdate flow runs unchanged — dual-mode silence.</para>
    ///
    /// <para><b>Cross-agency rejection (Decision §3).</b> Only the vessel's owning
    /// agency may upsert a <c>VESSEL</c> record for that vessel. A blob from
    /// agency B carrying agency A's vessel id is rejected per-Vessel with
    /// <see cref="LunaLog.Warning"/> (Invariant 8 — cross-agency claim is
    /// operator-visible). Unassigned-sentinel vessels (<c>OwningAgencyId == Guid.Empty</c>,
    /// pre-0.31 upgrade-in-place per spec §10 Q3) bypass the check — any agency
    /// may interact with them until <c>setvesselagency</c> stamps them.</para>
    ///
    /// <para><b>Vessel-not-in-store (Decision matches kolony precedent).</b>
    /// Same pragmatic DROP semantics as <see cref="AgencyKolonyRouter.TryRoute"/>:
    /// if the vessel id has no entry in <see cref="VesselStoreSystem.CurrentVessels"/>,
    /// the per-Vessel slot is silently skipped (Debug log). The projector reads
    /// from authoritative <see cref="VesselStoreSystem"/> at splice time, so a
    /// not-in-store entry would never project anyway — drop early to avoid
    /// dead state.</para>
    ///
    /// <para><b>Coverage entries have no vessel-id key</b> — they're body-keyed
    /// (the SCANsat <c>Progress → Body</c> shape per Decision §1). There is no
    /// per-body ownership concept; every agency tracks coverage of every body
    /// independently. The router applies only per-entry isolation + latest-wins
    /// upsert (correct under the 1 player ↔ 1 agency rule).</para>
    /// </summary>
    public static class AgencyScanRouter
    {
        /// <summary>
        /// Attempts to route an inbound <c>SCANcontroller</c> blob through the
        /// per-agency path. Returns <c>true</c> when this method handled the
        /// inbound (caller MUST suppress the shared-store AddOrUpdate). Returns
        /// <c>false</c> when the gate is off, the client lacks an agency mapping
        /// (defensive — every authenticated client has one via handshake
        /// auto-register under gate=on), or the agency registry entry is missing
        /// (defensive — same).
        /// </summary>
        public static bool TryRoute(ClientStructure client, ConfigNode scenario)
        {
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || scenario == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            if (!AgencySystem.Agencies.TryGetValue(agencyId, out var agency))
                return false;

            var acceptedAny = false;
            lock (AgencySystem.GetAgencyLock(agencyId))
            {
                acceptedAny |= UpsertCoverageEntries(scenario, agency);
                acceptedAny |= UpsertScannerEntries(scenario, agency, agencyId);
            }

            // Missing both containers is a no-op (M8 — operator running with
            // empty SCANsat install state, or pre-first-scan client). Whole
            // SCANcontroller blob is still consumed (return true) so the caller
            // suppresses the shared-store write — we don't want a pre-first-scan
            // blob to flip a populated SCAN_COVERAGE on disk back to empty just
            // because the broadcast arrived empty.
            //
            // SaveAgency only on actual mutation — mirrors AgencyKolonyRouter's
            // `if (accepted.Count > 0) AgencySystem.SaveAgency(...)` gate
            // (general-lens review SHOULD-FIX). Avoids a `FileHandler.WriteAtomic`
            // per agency per 30s SHA pass when nothing changed (operator running
            // a quiet server, or every client running SCANsat with no live
            // scans). The 30s broadcast cadence × N agencies multiplies fast.
            if (acceptedAny)
                AgencySystem.SaveAgency(agencyId);
            return true;
        }

        /// <summary>
        /// [Mod-compat S2] Per-Body upsert under the per-agency lock. Internal-
        /// visibility so <see cref="ServerTest.AgencyScanRouterTest"/> can pin
        /// the per-entry isolation + field shape semantics directly without
        /// constructing a live <see cref="ClientStructure"/> + <see cref="NetConnection"/>
        /// (which would require the full server runtime). Mirrors the
        /// <see cref="AgencyKolonyRouter.Upsert"/> internal-visibility pattern.
        /// Caller MUST hold <see cref="AgencySystem.GetAgencyLock"/>.
        /// </summary>
        internal static bool UpsertCoverageEntries(ConfigNode scenario, AgencyState agency)
        {
            var progressContainer = scenario.GetNode("Progress")?.Value;
            if (progressContainer == null)
                return false;

            var acceptedAny = false;
            foreach (var bEntry in progressContainer.GetNodes("Body"))
            {
                var bodyNode = bEntry.Value;
                try
                {
                    var bodyName = bodyNode.GetValue("Name")?.Value;
                    if (string.IsNullOrEmpty(bodyName))
                    {
                        LunaLog.Debug("[fix:S2-SCANsat] Body entry skipped: missing Name");
                        continue;
                    }

                    var entry = new AgencyCoverageBodyEntry
                    {
                        BodyName = bodyName,
                        Disabled = ParseBool(bodyNode.GetValue("Disabled")?.Value),
                        MinHeightRange = ParseFloat(bodyNode.GetValue("MinHeightRange")?.Value),
                        MaxHeightRange = ParseFloat(bodyNode.GetValue("MaxHeightRange")?.Value),
                        ClampHeight = ParseNullableFloat(bodyNode.GetValue("ClampHeight")?.Value),
                        PaletteName = bodyNode.GetValue("PaletteName")?.Value ?? string.Empty,
                        PaletteSize = ParseInt(bodyNode.GetValue("PaletteSize")?.Value),
                        PaletteReverse = ParseBool(bodyNode.GetValue("PaletteReverse")?.Value),
                        PaletteDiscrete = ParseBool(bodyNode.GetValue("PaletteDiscrete")?.Value),
                        Map = bodyNode.GetValue("Map")?.Value ?? string.Empty,
                        LandingTarget = bodyNode.GetValue("LandingTarget")?.Value, // null when absent
                    };

                    agency.Coverage[bodyName] = entry;
                    acceptedAny = true;
                }
                catch (Exception ex)
                {
                    LunaLog.Error($"[fix:S2-SCANsat] Body entry skipped: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return acceptedAny;
        }

        /// <summary>
        /// [Mod-compat S2] Per-Vessel upsert with cross-agency rejection +
        /// vessel-not-in-store DROP + nested per-Sensor isolation. Internal-
        /// visibility for the same reason as
        /// <see cref="UpsertCoverageEntries"/>. Caller MUST hold
        /// <see cref="AgencySystem.GetAgencyLock(agencyId)"/>.
        /// </summary>
        internal static bool UpsertScannerEntries(ConfigNode scenario, AgencyState agency, Guid agencyId)
        {
            var scannersContainer = scenario.GetNode("Scanners")?.Value;
            if (scannersContainer == null)
                return false;

            var acceptedAny = false;
            foreach (var vEntry in scannersContainer.GetNodes("Vessel"))
            {
                var vesselNode = vEntry.Value;
                try
                {
                    var rawGuid = vesselNode.GetValue("guid")?.Value;
                    if (!Guid.TryParse(rawGuid, out var vesselGuid))
                    {
                        LunaLog.Debug($"[fix:S2-SCANsat] Vessel entry skipped: malformed guid '{rawGuid ?? "<null>"}' (agency {agencyId:N})");
                        continue;
                    }

                    if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselGuid, out var v))
                    {
                        // Vessel-not-in-store DROP (matches kolony precedent — see class XML).
                        LunaLog.Debug($"[fix:S2-SCANsat] Vessel entry skipped: vessel {vesselGuid:N} not in store (agency {agencyId:N})");
                        continue;
                    }

                    if (v.OwningAgencyId != Guid.Empty && v.OwningAgencyId != agencyId)
                    {
                        // Cross-agency claim rejection — Warning per Invariant 8.
                        // Unassigned-sentinel (Empty) bypasses per spec §10 Q3.
                        LunaLog.Warning($"[fix:S2-SCANsat] Vessel entry rejected: vessel {vesselGuid:N} owning agency {v.OwningAgencyId:N} != requester {agencyId:N}");
                        continue;
                    }

                    var entry = new AgencyScannerEntry
                    {
                        VesselId = vesselGuid,
                        VesselName = vesselNode.GetValue("name")?.Value ?? string.Empty,
                        Sensors = new List<AgencyScannerSensorRecord>(),
                    };

                    foreach (var sEntry in vesselNode.GetNodes("Sensor"))
                    {
                        var sensorNode = sEntry.Value;
                        try
                        {
                            var rawType = sensorNode.GetValue("type")?.Value;
                            if (!int.TryParse(rawType, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sensorType))
                            {
                                LunaLog.Debug($"[fix:S2-SCANsat] Sensor skipped on vessel {vesselGuid:N}: malformed type '{rawType ?? "<null>"}'");
                                continue;
                            }

                            entry.Sensors.Add(new AgencyScannerSensorRecord
                            {
                                SensorType = sensorType,
                                Fov = ParseDouble(sensorNode.GetValue("fov")?.Value),
                                MinAlt = ParseDouble(sensorNode.GetValue("min_alt")?.Value),
                                MaxAlt = ParseDouble(sensorNode.GetValue("max_alt")?.Value),
                                BestAlt = ParseDouble(sensorNode.GetValue("best_alt")?.Value),
                                RequireLight = ParseBool(sensorNode.GetValue("require_light")?.Value),
                            });
                        }
                        catch (Exception ex)
                        {
                            LunaLog.Error($"[fix:S2-SCANsat] Sensor on vessel {vesselGuid:N} skipped: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    agency.Scanners[vesselGuid] = entry;
                    acceptedAny = true;
                }
                catch (Exception ex)
                {
                    LunaLog.Error($"[fix:S2-SCANsat] Vessel entry skipped (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                }
            }
            return acceptedAny;
        }

        /// <summary>
        /// [Mod-compat S2 — Decision §3 + D3] Vessel-keyed migration for the
        /// <see cref="AgencyState.Scanners"/> partition. Moves the entry whose
        /// key matches <paramref name="movedVesselId"/> from
        /// <paramref name="source"/> to <paramref name="destination"/> (with the
        /// nested <see cref="AgencyScannerEntry.Sensors"/> list intact). Mirrors
        /// <see cref="AgencyKolonyRouter.MigrateForVesselTransfer"/> in shape +
        /// dual-lock contract.
        ///
        /// <para><b>Caller contract</b>: caller MUST hold BOTH
        /// <c>AgencySystem.GetAgencyLock(source.AgencyId)</c> AND
        /// <c>AgencySystem.GetAgencyLock(destination.AgencyId)</c> in
        /// <see cref="Guid.CompareTo"/> order. The
        /// <see cref="Server.Command.Command.SetVesselAgencyCommand"/>
        /// orchestrates this — same contract as the kolony / orbital migration
        /// helpers it already invokes.</para>
        ///
        /// <para><b>Coverage does NOT migrate.</b> Coverage is body-keyed,
        /// agency-scoped — A's discoveries on Eve stay A's; B retains B's. Only
        /// Scanners (vessel-keyed) follow the vessel. Decision §3.</para>
        ///
        /// <para><b>Destination-collision policy</b>: matches kolony precedent.
        /// In normal operation a vessel only belongs to one agency at a time so
        /// destination cannot already hold the same key. Defensively, source's
        /// entry wins on collision (more recent; source held the vessel until
        /// now) with a Warning under <c>[fix:S2-SCANsat-Mig]</c>.</para>
        ///
        /// <para><b>Defensive guards</b>: returns an empty result without
        /// mutation when source/destination are the same instance, or when
        /// <paramref name="movedVesselId"/> is <see cref="Guid.Empty"/>
        /// (Unassigned sentinel — by construction no Scanners entry maps to
        /// Empty since the router's TryRoute rejects malformed Guids before
        /// upserting).</para>
        ///
        /// <para><b>Internal visibility</b> matches the kolony / orbital
        /// migration helpers — ServerTest reaches in to pin migration semantics.</para>
        /// </summary>
        internal static ScanMigrationResult MigrateForVesselTransfer(
            AgencyState source, AgencyState destination, Guid movedVesselId)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            var result = new ScanMigrationResult();
            if (ReferenceEquals(source, destination)) return result;
            if (movedVesselId == Guid.Empty) return result;

            if (!source.Scanners.TryGetValue(movedVesselId, out var entry))
                return result;

            source.Scanners.Remove(movedVesselId);
            if (destination.Scanners.ContainsKey(movedVesselId))
            {
                // Collision Warning — see class XML.
                LunaLog.Warning(
                    $"[fix:S2-SCANsat-Mig] scanner migration collision: dest agency {destination.AgencyId:N} " +
                    $"already had VesselId {movedVesselId:N}; source agency {source.AgencyId:N} value wins per Decision §3.");
            }
            destination.Scanners[movedVesselId] = entry;
            result.RemovedVesselId = movedVesselId;
            result.AddedEntry = entry;
            return result;
        }

        // Local parse helpers — mirror AgencyState's tolerant defaults but stay
        // private to the router so the public AgencyState helpers don't grow a
        // dependent surface. Per-entry try/catch in the loops above swallows
        // any parse miss, but the helpers also default-on-failure for belt-and-
        // braces.

        private static double ParseDouble(string raw) =>
            !string.IsNullOrEmpty(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d;

        private static float ParseFloat(string raw) =>
            !string.IsNullOrEmpty(raw) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        private static float? ParseNullableFloat(string raw) =>
            !string.IsNullOrEmpty(raw) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (float?)null;

        private static int ParseInt(string raw) =>
            !string.IsNullOrEmpty(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static bool ParseBool(string raw) =>
            !string.IsNullOrEmpty(raw) && bool.TryParse(raw, out var v) && v;
    }

    /// <summary>
    /// [Mod-compat S2 — Decision §3 + D3] Result of
    /// <see cref="AgencyScanRouter.MigrateForVesselTransfer"/>. Wire-only
    /// transient — neither <see cref="RemovedVesselId"/> nor
    /// <see cref="AddedEntry"/> is persisted to disk; the migration helper
    /// already mutated the canonical <see cref="AgencyState.Scanners"/> dicts
    /// in-place. Fields stay populated when migration occurred so the
    /// <c>SetVesselAgencyCommand</c> caller can write a precise
    /// <c>[fix:S2-SCANsat-Mig]</c> log line + (future) wire echo.
    ///
    /// Single-entry shape rather than the kolony helper's
    /// <c>RemovedKeys</c>+<c>AddedEntries</c> lists: a vessel has at most one
    /// scanner record per agency (vessel-keyed dict), so the migration
    /// produces at most one moved entry.
    /// </summary>
    internal class ScanMigrationResult
    {
        /// <summary>The moved vessel's id, or <see cref="Guid.Empty"/> if no migration occurred (source had no entry for this vessel).</summary>
        public Guid RemovedVesselId { get; set; }

        /// <summary>The moved entry, or <c>null</c> if no migration occurred.</summary>
        public AgencyScannerEntry AddedEntry { get; set; }
    }
}
