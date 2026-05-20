using Server.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 4 Slice F (Stage 5) — migration helpers that run alongside the
    /// per-agency admin commands when WOLF (MKS Wireless Orbital Logistics
    /// Function) state is involved. Today the only non-trivial entry point is
    /// <see cref="CascadeOnDelete"/>, invoked by
    /// <see cref="Server.Command.Command.DeleteAgencyCommand"/> to restore
    /// kerbals who are mid-flight in the demoted agency's WOLF CrewRoutes
    /// back to the Astronaut Complex pool before the agency record is
    /// removed.
    ///
    /// <para><b>Phase 6.7 closer (Stage 6).</b> Under
    /// <see cref="AgencySystem.PerAgencyKerbalRosterEnabled"/>=true the
    /// cascade no longer writes to the legacy shared <c>Universe/Kerbals/</c>
    /// directory (which the per-agency request filter at
    /// <see cref="KerbalSystem.ResolveKerbalsPathForRequester"/> never
    /// enumerates under gate=on — restored files would be unreachable to
    /// clients of any surviving agency). Instead the caller MUST supply a
    /// <i>destination agency</i> (via the new <c>--restore-to</c> command
    /// flag) and the cascade reads kerbal files from the deleted agency's
    /// own per-agency subdir + writes the rewritten content to the
    /// destination agency's subdir. The <c>--restore-to-none</c> flag opts
    /// the operator out of the disk write entirely (CrewRoute audit still
    /// emits, kerbal files are abandoned on disk inside the source subdir
    /// that <see cref="AgencySystem.TryDeleteAgency"/> recursively deletes
    /// seconds later). The command refuses if neither flag is supplied
    /// and the deleted agency has in-flight CrewRoute passengers under
    /// gate=on — see
    /// <see cref="Server.Command.Command.DeleteAgencyCommandParser"/>
    /// for the operator-facing usage banner.</para>
    ///
    /// <para><b>Why this helper exists.</b> WOLF's <c>WOLF_CrewTransferScenario.Launch</c>
    /// (verified s42 against MKS SHA <c>ed0f6aa6</c> at
    /// <c>Source/WOLF/WOLF/Modules/WOLF_CrewTransferScenario.cs:586-590</c>)
    /// mutates each in-flight passenger's roster status to
    /// <c>ProtoCrewMember.RosterStatus.Missing</c> + calls
    /// <c>SetTimeForRespawn(double.MaxValue)</c>. Server-side this propagates
    /// to the on-disk kerbal file via <c>KerbalSystem.HandleKerbalProto</c>
    /// as <c>state = Missing</c> + <c>ToD = double.MaxValue</c>. Recovery
    /// for the kerbal happens client-side in <c>Disembark</c> (line 155) when
    /// the operator clicks the in-game Disembark button at destination —
    /// rosterStatus flips back to Available + the kerbal is added to the
    /// terminal vessel's crew. If the agency owning the CrewRoute is
    /// wholesale-deleted (via <c>/deleteagency --confirm</c>) BEFORE the
    /// operator clicks Disembark, that Disembark path can never run: the
    /// route's record vanishes with the agency, leaving the kerbal stuck in
    /// Missing + respawn-MaxValue forever with no in-band recovery. Manual
    /// recovery would require hand-editing every Missing kerbal file. Slice F
    /// restores them automatically.</para>
    ///
    /// <para><b>Restoration scope: {Enroute, Arrived} only.</b> Per the WOLF
    /// state-machine contract (<c>CrewRoute.cs:105-180</c>):
    /// <list type="bullet">
    ///   <item><b>Boarding</b> — Operator has added passengers via UI but
    ///         has NOT clicked Launch yet. <c>Embark</c> mutates only the
    ///         CrewRoute's passenger list; kerbal <c>rosterStatus</c> stays
    ///         Assigned (still on source vessel). Boarding passengers are
    ///         NOT in Missing — skip them, otherwise we'd gratuitously
    ///         stomp Assigned→Available for kerbals that don't need it.</item>
    ///   <item><b>Enroute</b> — Launch ran. Passengers ARE in Missing.
    ///         Restore.</item>
    ///   <item><b>Arrived</b> — <c>CheckArrived</c> transitioned
    ///         Enroute→Arrived but operator has NOT clicked Disembark yet.
    ///         <c>CheckArrived</c> only mutates <c>FlightStatus</c>, NOT
    ///         <c>rosterStatus</c>. Passengers are STILL in Missing.
    ///         Restore.</item>
    /// </list></para>
    ///
    /// <para><b>Lock-domain discipline (Phase 6.7 update).</b> The cascade
    /// runs in three phases now:
    /// <list type="number">
    ///   <item>Snapshot phase under
    ///         <c>lock (AgencySystem.GetAgencyLock(sourceAgencyId))</c> —
    ///         brief; reads <see cref="AgencyState.WolfCrewRoutes"/> and
    ///         copies passenger names into a HashSet.</item>
    ///   <item>Per-kerbal read phase WITHOUT any agency lock — reads the
    ///         on-disk kerbal file (source path depends on gate). Disk I/O
    ///         serializes via FileHandler's per-path lock.</item>
    ///   <item>Per-kerbal write phase under
    ///         <c>lock (AgencySystem.GetAgencyLock(destinationAgencyId))</c>
    ///         (Phase 6.7 add). Holding the destination lock during write
    ///         serialises against concurrent
    ///         <see cref="KerbalSystem.TryWriteKerbalProtoPerAgency"/> calls
    ///         from the destination agency's owner — without it, the
    ///         destination owner's client could write a different version
    ///         of the kerbal proto microseconds after our collision check
    ///         and the cascade would silently lose either write. Same race
    ///         model as Phase 6.5's
    ///         <see cref="KerbalSystem.TryWriteKerbalProtoPerAgency"/>
    ///         + Phase 6.3's TryDeleteAgency cascade.</item>
    /// </list>
    /// Each phase's lock is released before the next acquires; we never
    /// hold two agency locks simultaneously. Avoiding nested locks means
    /// no risk of AB-BA deadlock with any future per-agency mutation path.</para>
    ///
    /// <para><b>Cascade-race guard on destination.</b> Step 3 re-checks
    /// <see cref="AgencySystem.Agencies"/><c>.ContainsKey(destinationAgencyId)</c>
    /// under the destination lock. A concurrent
    /// <c>/deleteagency &lt;destination&gt; --confirm</c> against the
    /// destination agency (two operators racing on the GUI launcher, or a
    /// script firing both) would have raced past us here — we DROP the write
    /// with Warning rather than land a file in a subdir about to be deleted.
    /// Same posture as Phase 6.5's
    /// <see cref="KerbalSystem.TryWriteKerbalProtoPerAgency"/>.</para>
    ///
    /// <para><b>Race window — narrow, accepted.</b> Between snapshot lock
    /// release and <see cref="AgencySystem.TryDeleteAgency"/>'s own lock
    /// acquire (microseconds), a new <c>AgencyWolfCrewRouter.TryRoute</c>
    /// postfix from the demoted agency's owner could accept a new Launch,
    /// adding fresh passengers in Missing state via a subsequent
    /// <c>KerbalSystem.HandleKerbalProto</c>. Those new passengers would be
    /// orphaned post-delete. Mitigation: existing operator workflow advice
    /// at <see cref="Server.Command.Command.DeleteAgencyCommand"/>:226+
    /// already directs operators to <c>/kick</c> the prior owner — kicked
    /// players cannot initiate Launches. The race is operationally closed
    /// by following the documented workflow.</para>
    ///
    /// <para><b>No dict.Clear() of the 5 WOLF dicts.</b>
    /// <see cref="AgencySystem.TryDeleteAgency"/>'s precedent is to remove
    /// the AgencyState from <c>Agencies</c> + <c>AgencyByPlayerName</c> +
    /// delete the on-disk file + GC the agency lock + evict contract
    /// claims. It does NOT call <c>.Clear()</c> on
    /// <c>KolonyEntries</c> / <c>OrbitalTransfers</c> / <c>Contracts</c> /
    /// <c>TechNodes</c> / etc. — those dicts vanish with the unreachable
    /// AgencyState reference. The 5 WOLF dicts get the same treatment.</para>
    ///
    /// <para><b>Per-kerbal isolation</b> (mirrors
    /// <see cref="AgencyWolfCrewRouter"/> per-entry try/catch). A malformed
    /// or missing kerbal file never aborts sibling restorations. Each
    /// failure contributes a Warning log line + a name in
    /// <see cref="CascadeResult.FailedKerbalNames"/> (or
    /// <see cref="CascadeResult.CollidedKerbalNames"/> for the destination-
    /// already-has-this-name case); the cascade carries on with the
    /// remaining names.</para>
    ///
    /// <para><b>Intended call site.</b> Today the ONLY caller is
    /// <see cref="Server.Command.Command.DeleteAgencyCommand"/> immediately
    /// before <see cref="AgencySystem.TryDeleteAgency"/>. If a future
    /// Slice G+ command needs to restore kerbals WITHOUT deleting the
    /// agency, add a sibling method (e.g. <c>RestoreInFlightKerbalsForReset</c>)
    /// that ALSO clears or re-emits the affected WolfCrewRoutes entries —
    /// without that clear, the projector tick would broadcast the dissonant
    /// "Enroute route with Available kerbals" state to clients.</para>
    /// </summary>
    public static class AgencyWolfMigration
    {
        /// <summary>
        /// Summary of work the cascade performed. Feeds the operator-visible
        /// audit log emitted by the calling admin command.
        /// </summary>
        public sealed class CascadeResult
        {
            /// <summary>Count of CrewRoutes scanned in the restoration scope ({Enroute, Arrived}).</summary>
            public int InFlightRoutesScanned;
            /// <summary>Count of distinct kerbal files successfully restored to state=Available + ToD=0.</summary>
            public int RestoredKerbalCount;
            /// <summary>Names of kerbals restored (one per distinct name; HashSet-deduped).</summary>
            public List<string> RestoredKerbalNames = new List<string>();
            /// <summary>Names of kerbals where restoration failed (missing file, malformed, IO error). Per-kerbal Warning is logged at failure time.</summary>
            public List<string> FailedKerbalNames = new List<string>();
            /// <summary>
            /// [Phase 6.7] Names of kerbals where the destination agency
            /// already had a same-named kerbal on disk. Per spec §Q-Render +
            /// Phase 6.7 design decision (operator-confirmed 2026-05-20),
            /// destination's existing file is preserved and the source's
            /// version is dropped with a Warning. Separate from
            /// <see cref="FailedKerbalNames"/> so an audit consumer can
            /// distinguish "lost due to operator-chosen agency boundaries"
            /// from "lost due to malformed file" — different recovery
            /// recipes (the former requires a different <c>--restore-to</c>
            /// + a re-run on a recovered backup; the latter requires
            /// hand-editing the malformed file).
            /// </summary>
            public List<string> CollidedKerbalNames = new List<string>();
            /// <summary>
            /// [Phase 6.7] The agency that received the restored kerbal files
            /// for the gate=on case, or <see cref="Guid.Empty"/> for the
            /// gate=off legacy path / the <c>--restore-to-none</c> opt-out.
            /// Reflected in the cascade summary log line.
            /// </summary>
            public Guid DestinationAgencyId;
            /// <summary>
            /// [Phase 6.7] True when the caller passed a destination agency
            /// (<c>--restore-to</c> path) AND we wrote at least one file to
            /// its subdir. False for the <c>--restore-to-none</c> path, the
            /// gate=off path, and any gate=on case where every restoration
            /// failed before reaching the write step.
            /// </summary>
            public bool WroteToDestinationSubdir;
            /// <summary>
            /// [v8.1 audit cross-phase (h)] Count of passengers the cascade
            /// walked but deliberately did NOT restore because the operator
            /// passed <c>--restore-to-none</c>. Distinct from
            /// <see cref="FailedKerbalNames"/> (which counts failures during
            /// an attempted restoration). Non-zero only on the
            /// <c>--restore-to-none</c> path; zero everywhere else. Emitted
            /// in the cascade summary as <c>dropped-kerbals={n}</c> so a GUI
            /// launcher parsing the audit line by key=value gets an integer
            /// for "how many were dropped under operator-accepted loss"
            /// without needing to per-name grep the surrounding Normal
            /// lines.
            /// </summary>
            public int DroppedPassengerCount;
        }

        /// <summary>
        /// [Phase 6.7] Returns the count of WOLF CrewRoute passengers
        /// currently in restoration scope ({Enroute, Arrived}) on the supplied
        /// agency. Used by <see cref="Server.Command.Command.DeleteAgencyCommand"/>
        /// to decide whether <c>--restore-to</c> / <c>--restore-to-none</c>
        /// is required: under gate=on, a non-zero count forces the operator
        /// to choose a disposition before the cascade can proceed.
        ///
        /// <para>Pure read under the agency's per-agency lock. Cheap — the
        /// dict is keyed by route UniqueId and the scope check on each entry
        /// is a single string compare. Called once per <c>/deleteagency</c>
        /// invocation under gate=on.</para>
        /// </summary>
        public static int CountInFlightPassengersForRefusalCheck(AgencyState agency)
        {
            if (agency == null || agency.WolfCrewRoutes == null) return 0;
            var count = 0;
            lock (AgencySystem.GetAgencyLock(agency.AgencyId))
            {
                foreach (var kvp in agency.WolfCrewRoutes)
                {
                    var route = kvp.Value;
                    if (route == null) continue;
                    if (!IsInFlightForRestoration(route.FlightStatus)) continue;
                    if (route.Passengers == null) continue;
                    foreach (var passenger in route.Passengers)
                    {
                        if (passenger != null && !string.IsNullOrEmpty(passenger.Name))
                            count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Restores in-flight WOLF CrewRoute passengers on the supplied
        /// agency back to the AC pool (<c>state = Available</c>,
        /// <c>ToD = 0</c>). Caller invokes this BEFORE
        /// <see cref="AgencySystem.TryDeleteAgency"/> so the WolfCrewRoutes
        /// dict is still readable. Caller MUST NOT hold any agency lock —
        /// this method takes the per-agency locks internally for the
        /// snapshot + write phases.
        ///
        /// <para><b>Routing under
        /// <see cref="AgencySystem.PerAgencyKerbalRosterEnabled"/>:</b>
        /// <list type="bullet">
        ///   <item><b>Gate off</b> — <paramref name="destination"/> is
        ///         ignored (the command parser rejects <c>--restore-to</c>
        ///         under gate=off so production callers always pass null).
        ///         Read + rewrite + write happen against the legacy shared
        ///         <see cref="KerbalSystem.KerbalsPath"/> directory. This is
        ///         the v0-v7 behaviour preserved unchanged.</item>
        ///   <item><b>Gate on +
        ///         <paramref name="destination"/> non-null</b> — read kerbal
        ///         files from <c>Universe/Agencies/{source.AgencyId:N}/Kerbals/</c>,
        ///         rewrite, write to
        ///         <c>Universe/Agencies/{destination.AgencyId:N}/Kerbals/</c>.
        ///         Destination-side collisions skip with Warning. Source
        ///         files are NOT deleted here — TryDeleteAgency's cascade
        ///         removes the whole source subdir seconds later.</item>
        ///   <item><b>Gate on + <paramref name="destination"/> null
        ///         (<c>--restore-to-none</c>)</b> — walk routes, emit audit
        ///         counts, but write nothing. Operator chose to accept the
        ///         loss.</item>
        /// </list></para>
        /// </summary>
        /// <param name="source">The agency being deleted. Reads its
        ///   WolfCrewRoutes + (under gate=on) its kerbal subdir.</param>
        /// <param name="destination">The destination agency that receives
        ///   the restored kerbal files under gate=on. Null under gate=off
        ///   AND under <c>--restore-to-none</c>. Must not be the same
        ///   instance as <paramref name="source"/> — the caller's
        ///   destination-resolution step already enforces this; defensive
        ///   guard inside.</param>
        public static CascadeResult CascadeOnDelete(AgencyState source, AgencyState destination = null)
        {
            var result = new CascadeResult();
            if (source == null)
                return result;

            // Phase 6.7 routing decision. Snap once at the top so the body
            // below never re-reads the gate (changing gates mid-cascade is
            // not a supported operator workflow but defensive consistency
            // is cheap).
            var gateOn = AgencySystem.PerAgencyKerbalRosterEnabled;
            var hasDestination = gateOn && destination != null && destination.AgencyId != source.AgencyId;
            result.DestinationAgencyId = hasDestination ? destination.AgencyId : Guid.Empty;

            // Phase 1 — snapshot phase under the source agency lock. We
            // collect distinct passenger names into a HashSet so a kerbal
            // who somehow appears in two in-flight routes (defensive —
            // WOLF's wire contract doesn't permit this but a malformed
            // wire upsert could) is restored exactly once. HashSet uses
            // Ordinal compare to match KSP's kerbal-name semantics (file
            // paths on disk are case-sensitive on Linux; Ordinal is the
            // safe default).
            var passengersToRestore = new HashSet<string>(StringComparer.Ordinal);
            lock (AgencySystem.GetAgencyLock(source.AgencyId))
            {
                foreach (var kvp in source.WolfCrewRoutes)
                {
                    var route = kvp.Value;
                    if (route == null)
                        continue;
                    if (!IsInFlightForRestoration(route.FlightStatus))
                        continue;
                    result.InFlightRoutesScanned++;
                    if (route.Passengers == null)
                        continue;
                    foreach (var passenger in route.Passengers)
                    {
                        if (passenger == null || string.IsNullOrEmpty(passenger.Name))
                            continue;
                        passengersToRestore.Add(passenger.Name);
                    }
                }
            }

            var sourceAgencyId = source.AgencyId;

            // Phase 2/3 — read + write phase WITHOUT the source agency lock.
            // Per-kerbal try/catch isolates failures.
            //
            // Log grammar mirrors the per-vessel demote audit lines in
            // DeleteAgencyCommand (verb, agencyId, key='value'): a GUI
            // launcher's `[fix:WOLF-R4] deleteagency {agencyId:N}` grep can
            // pair these lines with the surrounding cascade-summary +
            // visibility-broadcast + per-vessel-demote audit trail for the
            // same /deleteagency invocation.
            foreach (var name in passengersToRestore)
            {
                try
                {
                    var outcome = TryRestoreKerbalForCascade(name, sourceAgencyId, gateOn, hasDestination, destination);
                    switch (outcome)
                    {
                        case RestoreOutcome.Restored:
                            result.RestoredKerbalCount++;
                            result.RestoredKerbalNames.Add(name);
                            result.WroteToDestinationSubdir |= hasDestination;
                            LunaLog.Normal(
                                $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} restored-kerbal " +
                                $"name='{name}' state=Available ToD=0" +
                                (hasDestination ? $" destination={destination.AgencyId:N}" : string.Empty));
                            break;
                        case RestoreOutcome.SkippedNoDestination:
                            // --restore-to-none path. Per-name visibility
                            // matters: operator wants to know which kerbals
                            // they accepted the loss of (so they can spot
                            // operator-time mistakes after the fact). v8.1
                            // (cross-phase audit h) — also bump the
                            // CascadeResult.DroppedPassengerCount aggregate
                            // so the cascade summary line emits a single
                            // integer for "how many dropped" matching the
                            // existing restored-kerbals / failed-kerbals /
                            // collided-kerbals counters.
                            result.DroppedPassengerCount++;
                            LunaLog.Normal(
                                $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} dropped-kerbal " +
                                $"name='{name}' reason='--restore-to-none' " +
                                "manual-recovery-path-source-side='Universe/Agencies/" +
                                $"{sourceAgencyId:N}/Kerbals/{name}.txt' note='source file is " +
                                "removed by the recursive subdir delete seconds later — copy " +
                                "it BEFORE running /deleteagency if you change your mind'");
                            break;
                        case RestoreOutcome.Collision:
                            result.CollidedKerbalNames.Add(name);
                            // Warning emitted inside TryRestoreKerbalForCascade.
                            break;
                        case RestoreOutcome.Failed:
                        default:
                            result.FailedKerbalNames.Add(name);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    result.FailedKerbalNames.Add(name);
                    LunaLog.Warning(
                        $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} failed-kerbal " +
                        $"name='{name}' cause='{ex.GetType().Name}: {ex.Message}' " +
                        $"manual-recovery-path='{ResolveKerbalSourcePath(name, sourceAgencyId, gateOn)}' " +
                        "manual-recovery-fields='state = Available; ToD = 0'");
                }
            }

            return result;
        }

        /// <summary>
        /// [Phase 6.7] Per-kerbal restoration outcome. Maps 1:1 onto the
        /// fields of <see cref="CascadeResult"/>. Internal so ServerTest can
        /// pin the routing decision without disk I/O.
        /// </summary>
        internal enum RestoreOutcome
        {
            Restored,
            SkippedNoDestination,
            Collision,
            Failed,
        }

        /// <summary>
        /// [Phase 6.7] Per-kerbal restoration core. Reads from source path,
        /// rewrites state/ToD, decides destination per the routing rules
        /// documented on <see cref="CascadeOnDelete"/>.
        /// </summary>
        internal static RestoreOutcome TryRestoreKerbalForCascade(
            string kerbalName,
            Guid sourceAgencyId,
            bool gateOn,
            bool hasDestination,
            AgencyState destination)
        {
            if (string.IsNullOrEmpty(kerbalName))
                return RestoreOutcome.Failed;

            // [Phase 6.7 consumer-lens v1 finding #5] Every per-kerbal Warning
            // line carries the {sourceAgencyId:N} prefix so a GUI launcher's
            // grep `[fix:WOLF-R4] deleteagency {agencyId:N}` gathers ALL
            // cascade-related output (summary + per-kerbal Normal lines +
            // per-kerbal Warnings) under the originating /deleteagency
            // invocation. Without the prefix, the Warnings would only match
            // the lazier `[fix:WOLF-R4]` grep, mixing them with other
            // invocations under concurrent operator workflows.
            var sourcePath = ResolveKerbalSourcePath(kerbalName, sourceAgencyId, gateOn);
            // [Phase 6.7 upgrade-lens v1 finding #1] Pre-Phase-6.5 / AllowEnable...
            // upgrade hazard. An operator who ran a pre-Phase-6.5 binary with
            // PerAgencyKerbalRoster=true (the half-shipped state Phase 6.4's
            // temporary boot-refusal was supposed to prevent, but a dev-build
            // operator could have hit) OR an operator who used the
            // AllowEnablePerAgencyKerbalsOnExistingUniverse=true override on
            // a populated v0-v7 universe could have kerbal files stranded at
            // legacy Universe/Kerbals/{name}.txt with state=Missing +
            // ToD=MaxValue. The gate=on cascade's first-choice source path
            // is the per-agency subdir; if absent there, probe legacy before
            // giving up — finding the file at legacy lets the cascade rescue
            // it AND emits an operator-visible Warning so the upgrade hazard
            // is audible (without the fallback, the operator sees the same
            // "source file missing" Warning as for a newly-recruited kerbal,
            // which is diagnostically wrong).
            if (gateOn && !FileHandler.FileExists(sourcePath))
            {
                var legacyFallback = Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt");
                if (FileHandler.FileExists(legacyFallback))
                {
                    LunaLog.Warning(
                        $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} cascade-warning kerbal='{kerbalName}' " +
                        $"reason='per-agency source missing at {sourcePath} BUT a legacy-stranded copy " +
                        $"exists at {legacyFallback}. Falling back to legacy read — this is the upgrade " +
                        "hazard for operators who ran pre-Phase-6.5 dev-builds with " +
                        "PerAgencyKerbalRoster=true, or who used AllowEnablePerAgencyKerbalsOnExistingUniverse=true " +
                        "on a populated universe. The rescued kerbal will land in the destination agency''s " +
                        "subdir correctly. The legacy file at Universe/Kerbals/ is NOT deleted by this " +
                        "cascade — operator should hand-remove it after the cascade completes if it''s " +
                        "no longer referenced by any agency.'");
                    sourcePath = legacyFallback;
                }
            }
            if (!FileHandler.FileExists(sourcePath))
            {
                LunaLog.Warning(
                    $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} cascade-warning kerbal='{kerbalName}' " +
                    $"sourcePath='{sourcePath}' reason='source file missing — likely a newly-recruited " +
                    "kerbal whose KerbalProto never made it to disk before /deleteagency, or operator " +
                    "hand-deleted the file." +
                    (gateOn
                        ? " Also checked legacy Universe/Kerbals/ as Phase 6.7 upgrade-fallback; absent there too."
                        : string.Empty) +
                    "'");
                return RestoreOutcome.Failed;
            }

            var original = FileHandler.ReadFileText(sourcePath);
            if (string.IsNullOrEmpty(original))
            {
                LunaLog.Warning(
                    $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} cascade-warning kerbal='{kerbalName}' " +
                    $"sourcePath='{sourcePath}' reason='source file is empty (zero bytes) — malformed, " +
                    "manual recovery required.'");
                return RestoreOutcome.Failed;
            }

            if (!TryRewriteKerbalText(original, out var rewritten, out var stateSeen, out var todSeen, sourceAgencyId))
                return RestoreOutcome.Failed;

            if (!stateSeen || !todSeen)
            {
                LunaLog.Warning(
                    $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} cascade-warning kerbal='{kerbalName}' " +
                    $"sourcePath='{sourcePath}' reason='missing top-level state= (seen={stateSeen}) " +
                    $"and/or ToD= (seen={todSeen}) — malformed for a KSP kerbal record; skipping " +
                    "restoration to avoid partial corruption. Manual recovery required.'");
                return RestoreOutcome.Failed;
            }

            // Gate=on + --restore-to-none branch: walk through the read +
            // rewrite (so the audit accurately counts the kerbal as "in
            // scope") but write nothing. Operator-confirmed acceptance of
            // the loss.
            if (gateOn && !hasDestination)
                return RestoreOutcome.SkippedNoDestination;

            var destPath = ResolveKerbalDestinationPath(kerbalName, sourceAgencyId, gateOn, hasDestination, destination);

            // Destination collision check + write under the destination
            // agency lock (gate=on) or unlocked (gate=off, single-anchor
            // semaphore handles the legacy path).
            //
            // Gate=on: the destination subdir's per-agency lock anchors all
            // concurrent KerbalProto writes from the destination owner. We
            // re-check Agencies.ContainsKey under the lock to catch the
            // narrow race where two /deleteagency commands race against
            // each other (source-delete cascade vs destination-delete
            // cascade); same posture as Phase 6.5's
            // KerbalSystem.TryWriteKerbalProtoPerAgency.
            //
            // Gate=off: no per-agency lock concept; FileHandler's per-path
            // lock covers concurrent writers.
            if (gateOn && hasDestination)
            {
                lock (AgencySystem.GetAgencyLock(destination.AgencyId))
                {
                    if (!AgencySystem.Agencies.ContainsKey(destination.AgencyId))
                    {
                        LunaLog.Warning(
                            $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} cascade-warning " +
                            $"kerbal='{kerbalName}' destination={destination.AgencyId:N} " +
                            $"owner='{destination.OwningPlayerName}' " +
                            "reason='destination no longer in registry (a concurrent /deleteagency on " +
                            "the destination raced the restoration). Dropping write. The source file " +
                            "will be removed seconds later by TryDeleteAgency''s subdir cascade — " +
                            "manual recovery is no longer possible.'");
                        return RestoreOutcome.Failed;
                    }
                    if (FileHandler.FileExists(destPath))
                    {
                        LunaLog.Warning(
                            $"[fix:WOLF-R4] deleteagency {sourceAgencyId:N} cascade-warning " +
                            $"kerbal='{kerbalName}' destination={destination.AgencyId:N} " +
                            $"owner='{destination.OwningPlayerName}' " +
                            $"reason='destination already has a kerbal file at '{destPath}'. " +
                            "Per-agency rosters can legitimately have same-named kerbals (Q-Seed: each " +
                            "agency''s stock 4 share the stock names). The destination''s " +
                            "existing kerbal is preserved; the source agency''s kerbal is dropped. " +
                            "Operator recovery: re-run with a different --restore-to destination, or " +
                            $"hand-copy from 'Universe/Agencies/{sourceAgencyId:N}/Kerbals/{kerbalName}.txt' " +
                            "BEFORE /deleteagency completes its subdir cascade.'");
                        return RestoreOutcome.Collision;
                    }
                    WriteAtomicViaFileHandler(destPath, rewritten);
                }
            }
            else
            {
                // Gate=off legacy path. No collision check needed — same-
                // path read+write means the only "collision" would be with
                // ourselves, which is the intended behaviour.
                WriteAtomicViaFileHandler(destPath, rewritten);
            }

            return RestoreOutcome.Restored;
        }

        /// <summary>
        /// [Phase 6.7] Resolves the kerbal source path for the cascade's
        /// read step. Under gate=on the source lives in the deleted
        /// agency's per-agency subdir; under gate=off it lives in the
        /// legacy shared <see cref="KerbalSystem.KerbalsPath"/>.
        ///
        /// <para>Internal visibility so ServerTest can exercise the resolver
        /// without spinning the cascade.</para>
        /// </summary>
        internal static string ResolveKerbalSourcePath(string kerbalName, Guid sourceAgencyId, bool gateOn)
        {
            return gateOn
                ? Path.Combine(AgencySystem.GetKerbalsPathForAgency(sourceAgencyId), kerbalName + ".txt")
                : Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt");
        }

        /// <summary>
        /// [Phase 6.7] Resolves the kerbal destination path for the cascade's
        /// write step. Mirrors the source path resolver but consults
        /// <paramref name="destination"/> when set (gate=on +
        /// <c>--restore-to</c>); otherwise returns the source path (gate=off
        /// legacy in-place rewrite) — callers must NOT invoke this in the
        /// gate=on + no-destination path (the routing logic in
        /// <see cref="TryRestoreKerbalForCascade"/> returns
        /// <see cref="RestoreOutcome.SkippedNoDestination"/> before reaching
        /// here).
        /// </summary>
        internal static string ResolveKerbalDestinationPath(
            string kerbalName,
            Guid sourceAgencyId,
            bool gateOn,
            bool hasDestination,
            AgencyState destination)
        {
            if (gateOn && hasDestination)
                return Path.Combine(AgencySystem.GetKerbalsPathForAgency(destination.AgencyId), kerbalName + ".txt");
            // Gate=off: same-path in-place rewrite. Caller-guarded; not
            // reachable in the gate=on + no-destination case.
            return Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt");
        }

        /// <summary>
        /// Atomic-write the rewritten kerbal text via
        /// <see cref="FileHandler.WriteAtomic(string,string)"/>. Centralised
        /// so the two write call-sites (gate=on under destination lock /
        /// gate=off legacy) share the same crash-tolerance behaviour
        /// (.tmp → rename rotation; .bak retained for one generation).
        ///
        /// <para>FolderCreate the parent on a brand-new destination subdir.
        /// This is mostly redundant under gate=on (Phase 6.3 lifecycle hook
        /// seeded the subdir at agency mint), but defensive against
        /// operator-deleted subdirs + the gate=off path running on a fresh
        /// universe where <see cref="KerbalSystem.KerbalsPath"/> exists.</para>
        /// </summary>
        private static void WriteAtomicViaFileHandler(string destPath, string rewritten)
        {
            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent) && !FileHandler.FolderExists(parent))
                FileHandler.FolderCreate(parent);
            FileHandler.WriteAtomic(destPath, rewritten);
        }

        /// <summary>
        /// True when the supplied FlightStatus string (as serialized by
        /// <see cref="AgencyWolfCrewRouter"/> from WOLF's
        /// <c>FlightStatus.ToString()</c>) indicates the route's passengers
        /// are in <c>RosterStatus.Missing</c> and need rescuing. See class
        /// XML "Restoration scope" paragraph for the per-status rationale.
        ///
        /// <para><b>Internal visibility</b> for ServerTest unit coverage of
        /// the per-status decision without bringing the disk-I/O path up.</para>
        /// </summary>
        internal static bool IsInFlightForRestoration(string flightStatus)
        {
            if (string.IsNullOrEmpty(flightStatus))
                return false;
            return string.Equals(flightStatus, "Enroute", StringComparison.Ordinal)
                || string.Equals(flightStatus, "Arrived", StringComparison.Ordinal);
        }

        /// <summary>
        /// Pure-text helper for the depth-aware kerbal-file line rewrite.
        /// Walks <paramref name="original"/> line-by-line tracking brace
        /// depth and rewrites top-level <c>state = ...</c> /
        /// <c>ToD = ...</c> lines.
        ///
        /// <para><paramref name="sourceAgencyIdForLog"/> is used ONLY for the
        /// brace-depth-negative Warning prefix (Phase 6.7 consumer-lens v1
        /// finding #5 — every per-kerbal Warning must carry
        /// <c>deleteagency {agencyId:N}</c> for the GUI launcher grep).
        /// Default <see cref="Guid.Empty"/> for callers (ServerTest) that
        /// don't have an agency-id context.</para>
        ///
        /// <para><b>Internal visibility</b> for ServerTest unit coverage of
        /// the rewrite logic without disk I/O.</para>
        /// </summary>
        internal static bool TryRewriteKerbalText(string original, out string rewritten, out bool stateSeen, out bool todSeen, Guid sourceAgencyIdForLog = default)
        {
            stateSeen = false;
            todSeen = false;
            rewritten = original;

            if (string.IsNullOrEmpty(original))
                return false;

            var lines = original.Split('\n');
            var sb = new StringBuilder(original.Length);
            var depth = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (depth == 0)
                {
                    if (IsTopLevelAssignment(trimmed, "state"))
                    {
                        sb.Append("state = Available");
                        stateSeen = true;
                    }
                    else if (IsTopLevelAssignment(trimmed, "ToD"))
                    {
                        sb.Append("ToD = 0");
                        todSeen = true;
                    }
                    else
                    {
                        sb.Append(line);
                    }
                }
                else
                {
                    sb.Append(line);
                }

                if (i < lines.Length - 1)
                    sb.Append('\n');

                foreach (var c in trimmed)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }

                if (depth < 0)
                {
                    LunaLog.Warning(
                        $"[fix:WOLF-R4] deleteagency {sourceAgencyIdForLog:N} cascade-warning " +
                        $"reason='kerbal text rewrite aborted — brace depth went negative at line " +
                        $"{i + 1}. File is malformed.'");
                    rewritten = original;
                    return false;
                }
            }

            rewritten = sb.ToString();
            return true;
        }

        private static bool IsTopLevelAssignment(string trimmedLine, string fieldName)
        {
            if (!trimmedLine.StartsWith(fieldName, StringComparison.Ordinal))
                return false;
            var rest = trimmedLine.Substring(fieldName.Length);
            var i = 0;
            while (i < rest.Length && (rest[i] == ' ' || rest[i] == '\t')) i++;
            return i < rest.Length && rest[i] == '=';
        }
    }
}
