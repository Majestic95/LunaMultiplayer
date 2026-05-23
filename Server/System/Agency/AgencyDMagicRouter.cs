using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Log;
using System;
using System.Globalization;
using System.Linq;

namespace Server.System.Agency
{
    /// <summary>
    /// [Mod-compat S4 — DMagic Orbital Science] Path B per-agency router for
    /// DMagic's asteroid-science diminishing-returns log + discovered-anomaly
    /// records. Sits behind <see cref="Scenario.ScenarioDataUpdater.RawConfigNodeInsertOrUpdate"/>
    /// for the <c>DMScienceScenario</c> module. Replaces the shared-store
    /// write under <see cref="AgencySystem.PerAgencyEnabled"/> with per-agency
    /// upserts into <see cref="AgencyState.DMagicAsteroidScience"/> +
    /// <see cref="AgencyState.DMagicAnomalies"/>.
    ///
    /// <para><b>Path B (implementation-spec D1).</b> Mirrors
    /// <see cref="AgencyScanRouter"/>: no client-side Harmony, no dedicated
    /// wire, no <c>IgnoredScenarios</c> entry, no owner echo. Mutation rides
    /// the existing periodic <c>DMScienceScenario</c> broadcast; catch-up
    /// rides the D2 <see cref="ScenarioSystem.SendScenariosToClient"/> helper
    /// at handshake completion.</para>
    ///
    /// <para><b>Decision §B — anomaly wire is 2-level nested per-body, flat in storage.</b>
    /// Inbound wire: <c>Anomaly_Records → DM_Anomaly_List → DM_Anomaly</c>
    /// where each <c>DM_Anomaly_List</c> wrapper carries a <c>Body</c> (int
    /// flightGlobalsIndex) field and contains 1-N <c>DM_Anomaly</c> children.
    /// Agency-state storage flattens to <c>Dictionary&lt;string,
    /// AgencyDMagicAnomalyEntry&gt;</c> keyed by composite
    /// <c>"$bodyIndex|$name"</c> for storage convenience (mirrors kolony's
    /// <c>"$vesselId|$bodyIndex"</c> shape).</para>
    ///
    /// <para><b>No cross-agency rejection.</b> Unlike <see cref="AgencyScanRouter"/>'s
    /// vessel-keyed scanner-record cross-agency check, DMagic entries are
    /// Title-keyed (asteroid) or BodyIndex+Name-keyed (anomaly) — no vessel
    /// ownership concept applies. Under the 1:1 player↔agency Operating Rule
    /// (implementation-spec.md), latest-wins upsert is correct within an
    /// agency; cross-agency wire arrivals don't happen at the router level
    /// because each client's broadcast lands at the router with the SENDER's
    /// agency mapping (<c>AgencySystem.AgencyByPlayerName[client.PlayerName]</c>).</para>
    ///
    /// <para><b>No transferagency migration.</b> Same reasoning — entries are
    /// not vessel-keyed, so vessel A→B transfers don't move asteroid science
    /// or anomaly records.</para>
    ///
    /// <para><b>Per-entry isolation at TWO levels for anomalies</b>
    /// (Invariant 4 + S2 multi-Sensor precedent). A malformed
    /// <c>DM_Anomaly_List</c> wrapper (unparseable Body) drops the wrapper
    /// + its children; sibling lists survive. A malformed <c>DM_Anomaly</c>
    /// child (missing Name) drops only that child; sibling anomalies inside
    /// the same body survive.</para>
    ///
    /// <para><b>Router upserts, never removes</b> (consumer-lens CONSIDER C1
    /// from the S4 review). Each <see cref="TryRoute"/> call adds or updates
    /// entries by Title (asteroid) or composite key (anomaly); it does NOT
    /// remove entries whose keys are absent from the inbound. Stock DMagic
    /// only grows these collections (asteroid science is a diminishing-
    /// returns accumulator; anomalies are baked-in PQS surface objects), so
    /// the upsert-only contract matches the source-of-truth shape. **Caveat
    /// for future consumers:** if a client-side data-loss recovery removes
    /// entries from DMagic's local instance, the next broadcast carries a
    /// smaller-than-stored blob, and the per-agency dicts retain stale
    /// entries until manual operator intervention. DMagic's own
    /// <c>OnLoad</c> calls <c>recoveredDMScience.Clear()</c> +
    /// <c>DMAnomalyList.clearAnomalies()</c> before reading (verified at
    /// <c>DMScienceScenario.cs:128 + :150</c>), but the broadcast wire shape
    /// is "everything I currently know about" — there's no delete-by-omission
    /// semantics that the router can detect.</para>
    /// </summary>
    public static class AgencyDMagicRouter
    {
        /// <summary>
        /// Attempts to route an inbound <c>DMScienceScenario</c> blob through
        /// the per-agency path. Returns <c>true</c> when this method handled
        /// the inbound (caller MUST suppress the shared-store AddOrUpdate);
        /// <c>false</c> on gate-off / null client / unregistered player /
        /// missing agency registry entry.
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
                acceptedAny |= UpsertAsteroidScienceEntries(scenario, agency);
                acceptedAny |= UpsertAnomalyEntries(scenario, agency, agencyId);
            }

            // Missing both containers is M8 no-op — operator running with no
            // DMagic activity yet, or pre-first-experiment client. Still
            // return true so the caller suppresses the shared-store write
            // (a pre-first-experiment blob shouldn't flip populated agency
            // state back to empty just because the broadcast arrived empty).
            //
            // SaveAgency gated on acceptedAny — matches S2's general-lens
            // SHOULD-FIX (avoids per-30s-per-agency disk churn on quiet
            // DMagic-installed servers).
            if (acceptedAny)
                AgencySystem.SaveAgency(agencyId);

            // [Catch-up-baseline fix 2026-05-22] Same shape as
            // AgencyScanRouter.SeedBaselineIfMissing — without this, the
            // catch-up path SendScenariosToClient finds no DMScienceScenario
            // key in CurrentScenarios under gate=on (router suppresses every
            // shared-store insert) and silently sends nothing on reconnect.
            // Per-agency DMagic state would be correctly persisted on disk in
            // AgencyState.DMagicAsteroidScience + DMagicAnomalies but never
            // projected back to the client. Same root cause as S2: DMagic's
            // DMScienceScenario is a mod scenario, never seeded by
            // ScenarioSystem.GenerateDefaultScenarios at lines 28-38, so no
            // boot-time path populates CurrentScenarios["DMScienceScenario"].
            // See SeedBaselineIfMissing XML for the full rationale; same
            // ordering + lock-discipline + consumer-note as the S2 call site
            // (see AgencyScanRouter.TryRoute for the canonical comment block).
            SeedBaselineIfMissing(scenario);

            return true;
        }

        /// <summary>
        /// [Catch-up-baseline fix 2026-05-22] If
        /// <see cref="ScenarioStoreSystem.CurrentScenarios"/> has no
        /// <c>DMScienceScenario</c> entry, insert a stripped baseline derived
        /// from <paramref name="inbound"/>. The baseline preserves any root
        /// scalars but strips the player-progress containers' children
        /// (<c>Asteroid_Science → DM_Science</c>,
        /// <c>Anomaly_Records → DM_Anomaly_List</c>) so:
        /// <list type="bullet">
        ///   <item>The projector at
        ///        <see cref="AgencyScenarioProjector.SpliceDMagicScienceIntoScenario"/>
        ///        has a baseline to splice each agency's
        ///        <see cref="AgencyState.DMagicAsteroidScience"/> +
        ///        <see cref="AgencyState.DMagicAnomalies"/> onto at outbound
        ///        time.</item>
        ///   <item>The on-disk backup at
        ///        <see cref="ScenarioStoreSystem.BackupScenarios"/> carries the
        ///        baseline only — never per-agency player progress.</item>
        ///   <item>Cross-leak is impossible — the baseline by construction has
        ///        no DM_Science / DM_Anomaly_List children.</item>
        /// </list>
        ///
        /// <para>Mirror of
        /// <see cref="AgencyScanRouter.SeedBaselineIfMissing"/>. Same Path B
        /// catch-up problem, same fix shape. See that helper's XML for the
        /// full rationale + GetOrAdd semantics.</para>
        ///
        /// <para><b>Internal visibility</b> for direct ServerTest pinning.</para>
        /// </summary>
        internal static void SeedBaselineIfMissing(ConfigNode inbound)
        {
            if (inbound == null) return;
            ScenarioStoreSystem.CurrentScenarios.GetOrAdd("DMScienceScenario", _ => BuildStrippedBaseline(inbound));
        }

        /// <summary>
        /// Builds a baseline <see cref="ConfigNode"/> from
        /// <paramref name="inbound"/> with all player-progress children
        /// removed. Round-trips via <see cref="ConfigNode.ToString"/> to fully
        /// isolate the result from the inbound's tree. Internal visibility for
        /// ServerTest pinning.
        /// </summary>
        internal static ConfigNode BuildStrippedBaseline(ConfigNode inbound)
        {
            var baseline = new ConfigNode(inbound.ToString()) { Name = "DMScienceScenario" };

            var asteroid = baseline.GetNode("Asteroid_Science")?.Value;
            if (asteroid != null)
            {
                foreach (var entry in asteroid.GetNodes("DM_Science").ToArray())
                    asteroid.RemoveNode(entry.Value);
            }
            var anomaly = baseline.GetNode("Anomaly_Records")?.Value;
            if (anomaly != null)
            {
                foreach (var wrapper in anomaly.GetNodes("DM_Anomaly_List").ToArray())
                    anomaly.RemoveNode(wrapper.Value);
            }

            return baseline;
        }

        /// <summary>
        /// [Mod-compat S4] Per-asteroid-Title upsert. Internal-visibility so
        /// ServerTest can pin the per-entry isolation + field shape semantics
        /// directly without constructing a live <see cref="ClientStructure"/>
        /// (which requires the full server runtime). Caller MUST hold
        /// <see cref="AgencySystem.GetAgencyLock"/>.
        /// </summary>
        internal static bool UpsertAsteroidScienceEntries(ConfigNode scenario, AgencyState agency)
        {
            var asteroidContainer = scenario.GetNode("Asteroid_Science")?.Value;
            if (asteroidContainer == null)
                return false;

            var acceptedAny = false;
            // [Review SHOULD-FIX general#1] Outer try/catch around the whole
            // GetNodes-iteration mirrors AgencyScanRouter.UpsertScannerEntries
            // (S2 precedent). Defensive — if GetNodes itself throws (very
            // unlikely on LunaConfigNode but possible on a future API change),
            // the per-agency state stays whatever was upserted so far AND the
            // caller can still flush acceptedAny correctly. Without this wrapper,
            // an escape exits TryRoute mid-batch via the Task.Run swallow.
            try
            {
            foreach (var aEntry in asteroidContainer.GetNodes("DM_Science"))
            {
                var asteroidNode = aEntry.Value;
                try
                {
                    var title = asteroidNode.GetValue("title")?.Value;
                    if (string.IsNullOrEmpty(title))
                    {
                        LunaLog.Debug("[fix:S4-DMagic] DM_Science entry skipped: missing title");
                        continue;
                    }

                    agency.DMagicAsteroidScience[title] = new AgencyDMagicAsteroidEntry
                    {
                        Title = title,
                        BaseValue = ParseFloat(asteroidNode.GetValue("bsv")?.Value),
                        SciVal = ParseFloat(asteroidNode.GetValue("scv")?.Value),
                        Science = ParseFloat(asteroidNode.GetValue("sci")?.Value),
                        Cap = ParseFloat(asteroidNode.GetValue("cap")?.Value),
                    };
                    acceptedAny = true;
                }
                catch (Exception ex)
                {
                    LunaLog.Error($"[fix:S4-DMagic] DM_Science entry skipped: {ex.GetType().Name}: {ex.Message}");
                }
            }
            }
            catch (Exception ex)
            {
                LunaLog.Error($"[fix:S4-DMagic] DM_Science outer-loop aborted: {ex.GetType().Name}: {ex.Message}");
            }
            return acceptedAny;
        }

        /// <summary>
        /// [Mod-compat S4] Per-anomaly upsert with TWO-level per-entry isolation
        /// (Decision §B nested wire shape). Outer try/catch per
        /// <c>DM_Anomaly_List</c> wrapper, inner try/catch per
        /// <c>DM_Anomaly</c> child. Internal-visibility for ServerTest pin.
        /// Caller MUST hold <see cref="AgencySystem.GetAgencyLock(agencyId)"/>.
        /// </summary>
        internal static bool UpsertAnomalyEntries(ConfigNode scenario, AgencyState agency, Guid agencyId)
        {
            var anomaliesContainer = scenario.GetNode("Anomaly_Records")?.Value;
            if (anomaliesContainer == null)
                return false;

            var acceptedAny = false;
            // [Review SHOULD-FIX general#1] Outer try/catch — same defensive
            // wrapper as UpsertAsteroidScienceEntries; documented there.
            try
            {
            foreach (var listEntry in anomaliesContainer.GetNodes("DM_Anomaly_List"))
            {
                var listNode = listEntry.Value;
                try
                {
                    var rawBody = listNode.GetValue("Body")?.Value;
                    if (!int.TryParse(rawBody, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bodyIndex))
                    {
                        LunaLog.Debug($"[fix:S4-DMagic] DM_Anomaly_List skipped: malformed Body '{rawBody ?? "<null>"}' (agency {agencyId:N})");
                        continue;
                    }

                    foreach (var anomEntry in listNode.GetNodes("DM_Anomaly"))
                    {
                        var anomNode = anomEntry.Value;
                        try
                        {
                            var name = anomNode.GetValue("Name")?.Value;
                            if (string.IsNullOrEmpty(name))
                            {
                                LunaLog.Debug($"[fix:S4-DMagic] DM_Anomaly skipped on body {bodyIndex}: missing Name");
                                continue;
                            }

                            var key = $"{bodyIndex.ToString(CultureInfo.InvariantCulture)}|{name}";
                            agency.DMagicAnomalies[key] = new AgencyDMagicAnomalyEntry
                            {
                                BodyIndex = bodyIndex,
                                Name = name,
                                Latitude = ParseDouble(anomNode.GetValue("Lat")?.Value),
                                Longitude = ParseDouble(anomNode.GetValue("Lon")?.Value),
                                Altitude = ParseDouble(anomNode.GetValue("Alt")?.Value),
                            };
                            acceptedAny = true;
                        }
                        catch (Exception ex)
                        {
                            LunaLog.Error($"[fix:S4-DMagic] DM_Anomaly on body {bodyIndex} skipped: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LunaLog.Error($"[fix:S4-DMagic] DM_Anomaly_List wrapper skipped (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
                }
            }
            }
            catch (Exception ex)
            {
                LunaLog.Error($"[fix:S4-DMagic] DM_Anomaly_List outer-loop aborted (agency {agencyId:N}): {ex.GetType().Name}: {ex.Message}");
            }
            return acceptedAny;
        }

        // Local parse helpers — same shape as AgencyScanRouter's tolerant
        // defaults. Private to keep the router's helper surface independent
        // of AgencyState's public helpers (mirrors S2 precedent).

        private static double ParseDouble(string raw) =>
            !string.IsNullOrEmpty(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d;

        private static float ParseFloat(string raw) =>
            !string.IsNullOrEmpty(raw) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }
}
