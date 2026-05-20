using Server.Log;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Server.System.Agency
{
    /// <summary>
    /// Phase 4 Slice F — migration helpers that run alongside the per-agency
    /// admin commands when WOLF (MKS Wireless Orbital Logistics Function)
    /// state is involved. Today the only non-trivial entry point is
    /// <see cref="CascadeOnDelete"/>, invoked by
    /// <see cref="Server.Command.Command.DeleteAgencyCommand"/> to restore
    /// kerbals who are mid-flight in the demoted agency's WOLF CrewRoutes
    /// back to the Astronaut Complex pool before the agency record is
    /// removed.
    ///
    /// <para><b>Why this helper exists.</b> WOLF's <c>WOLF_CrewTransferScenario.Launch</c>
    /// (verified s42 against MKS SHA <c>ed0f6aa6</c> at
    /// <c>Source/WOLF/WOLF/Modules/WOLF_CrewTransferScenario.cs:586-590</c>)
    /// mutates each in-flight passenger's roster status to
    /// <c>ProtoCrewMember.RosterStatus.Missing</c> + calls
    /// <c>SetTimeForRespawn(double.MaxValue)</c>. Server-side this propagates
    /// to <c>Universe/Kerbals/{name}.txt</c> via <c>KerbalSystem.HandleKerbalProto</c>
    /// as <c>state = Missing</c> + <c>ToD = double.MaxValue</c>. Recovery
    /// for the kerbal happens client-side in <c>Disembark</c> (line 155) when
    /// the operator clicks the in-game Disembark button at destination —
    /// rosterStatus flips back to Available + the kerbal is added to the
    /// terminal vessel's crew. If the agency owning the CrewRoute is
    /// wholesale-deleted (via <c>/deleteagency --confirm</c>) BEFORE the
    /// operator clicks Disembark, that Disembark path can never run: the
    /// route's record vanishes with the agency, leaving the kerbal stuck in
    /// Missing + respawn-MaxValue forever with no in-band recovery. Manual
    /// recovery would require hand-editing every Missing kerbal file in
    /// <c>Universe/Kerbals/</c>. Slice F restores them automatically.</para>
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
    /// </list>
    /// The s42 pickup memo cited {Boarding, Enroute} as the restoration
    /// scope; ground-truth source walk corrected to {Enroute, Arrived}
    /// before implementation.</para>
    ///
    /// <para><b>Lock-domain discipline.</b> The cascade runs in two phases:
    /// (1) snapshot phase under <c>lock (AgencySystem.GetAgencyLock(agencyId))</c>
    /// — brief; reads the WolfCrewRoutes dict and copies passenger names to
    /// a local HashSet. (2) Restoration phase WITHOUT the agency lock —
    /// kerbal-file disk I/O serializes via FileHandler's own per-path lock,
    /// not the agency lock. Holding the agency lock across multiple disk
    /// writes would extend the lock window unnecessarily and contradict the
    /// established split-lock pattern from <see cref="AgencyWolfCrewRouter.BuildKerbalAgencyMap"/>
    /// (which similarly runs the expensive vessel-text serialization
    /// OUTSIDE the per-agency lock).</para>
    ///
    /// <para><b>Race window — narrow, accepted.</b> Between cascade-lock-
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
    /// AgencyState reference. The 5 WOLF dicts get the same treatment.
    /// Clearing them explicitly would be pointless (no caller can resolve
    /// to a deleted AgencyId) and breaks precedent.</para>
    ///
    /// <para><b>Per-kerbal isolation</b> (mirrors
    /// <see cref="AgencyWolfCrewRouter"/> per-entry try/catch). A malformed
    /// or missing kerbal file never aborts sibling restorations. Each
    /// failure contributes a Warning log line + a name in
    /// <see cref="CascadeResult.FailedKerbalNames"/>; the cascade carries
    /// on with the remaining names.</para>
    ///
    /// <para><b>Intended call site.</b> Today the ONLY caller is
    /// <see cref="Server.Command.Command.DeleteAgencyCommand"/> immediately
    /// before <see cref="AgencySystem.TryDeleteAgency"/>. The cascade
    /// assumes the agency is about to be removed from the registry — it
    /// does NOT defensively reset the WolfCrewRoutes dict, so a caller that
    /// invokes the cascade WITHOUT then deleting the agency would leave
    /// the in-flight CrewRoutes pointing at kerbals who are now in
    /// <c>state=Available</c> on disk + ProtoCrewMember caches across the
    /// cluster — the next projector tick would broadcast the dissonant
    /// "Enroute route with Available kerbals" state to clients. If a future
    /// Slice G+ command needs to restore kerbals WITHOUT deleting the
    /// agency, add a sibling method (e.g. <c>RestoreInFlightKerbalsForReset</c>)
    /// that ALSO clears or re-emits the affected WolfCrewRoutes entries.
    /// Do NOT add new call sites for <see cref="CascadeOnDelete"/> without
    /// satisfying this invariant.</para>
    ///
    /// <para><b>Pre-Slice-A dev-build orphans are NOT recoverable by this
    /// cascade.</b> An operator who ran a pre-Slice-A intermediate
    /// <c>feature/per-agency</c> build against MKS WOLF may have kerbal
    /// files on disk with <c>state = Missing</c> + <c>ToD = MaxValue</c>
    /// from a WOLF Launch that ran BEFORE <see cref="AgencyState.WolfCrewRoutes"/>
    /// existed as a persisted dict. The cascade iterates per-agency
    /// WolfCrewRoutes; those pre-Slice-A orphans have no per-agency record
    /// to discover. Operators upgrading from pre-<c>95b65711</c> dev builds
    /// with such orphans should <c>grep -l 'state = Missing'</c> in
    /// <c>Universe/Kerbals/</c> BEFORE running <c>/deleteagency</c>, and
    /// hand-restore any matches via the documented "edit Universe/Kerbals/
    /// {name}.txt — set 'state = Available' + 'ToD = 0'" recipe.</para>
    ///
    /// <para><b>Concurrent-Launch race window — observed secondary
    /// consequence beyond the documented "kerbal stays Missing" outcome.</b>
    /// At t2 in the race window (between cascade-lock-release and
    /// <see cref="AgencySystem.TryDeleteAgency"/>'s lock-acquire), a
    /// concurrent <see cref="AgencyWolfCrewRouter.TryRoute"/> postfix from
    /// the demoted agency's owner can land a new CrewRoute, calling
    /// <see cref="AgencySystem.SaveAgency"/> and emitting
    /// <see cref="AgencySystemSender.SendWolfCrewRouteStateToOwner"/> to
    /// the owner. The owner's 5.18a client mirror records the new entry.
    /// At t3 the agency file is deleted. The owner's local mirror diverges
    /// silently from the (now-empty) server state. On reconnect, the
    /// handshake-driven <see cref="AgencySystemSender.SendWolfCrewRouteCatchupTo"/>
    /// runs against the FRESH agency the handshake mints (the prior
    /// <see cref="AgencySystem.AgencyByPlayerName"/> mapping was removed)
    /// — that fresh agency has no CrewRoutes, but the owner's local mirror
    /// was never told to forget the stale entries. Operator workflow:
    /// <c>/kick</c> the prior owner BEFORE <c>/deleteagency</c> to fully
    /// close the race (kicked players cannot initiate Launches AND their
    /// reconnect re-mints the agency-id + re-empties the local mirror in
    /// a single round-trip).</para>
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
        }

        /// <summary>
        /// Restores in-flight WOLF CrewRoute passengers on the supplied
        /// agency back to the AC pool (<c>state = Available</c>,
        /// <c>ToD = 0</c>). Caller invokes this BEFORE
        /// <see cref="AgencySystem.TryDeleteAgency"/> so the WolfCrewRoutes
        /// dict is still readable. Caller MUST NOT hold any agency lock —
        /// this method takes the per-agency lock internally for the snapshot
        /// phase.
        /// </summary>
        public static CascadeResult CascadeOnDelete(AgencyState agency)
        {
            var result = new CascadeResult();
            if (agency == null)
                return result;

            // [Stage 6 Phase 6.5 known limitation — Phase 6.7 scope]
            // The kerbal-file rewrite below targets KerbalSystem.KerbalsPath
            // (legacy Universe/Kerbals/) regardless of the per-agency gate. Under
            // Phase 6.5's PerAgencyKerbalRosterEnabled=true mode, the per-agency
            // request filter at KerbalSystem.ResolveKerbalsPathForRequester does
            // NOT enumerate legacy — restored kerbals would be unreachable to
            // clients of any agency. Phase 6.7 will rework this code path to
            // route the restoration to the surviving agency's
            // Universe/Agencies/{guid}/Kerbals/ subdir. For Phase 6.5 we surface
            // the limitation as an audible Warning so an operator running
            // /deleteagency on an agency with in-flight WOLF cross-agency
            // CrewRoutes sees the kerbal-loss risk before the deletion completes.
            // Operator-workflow mitigation per spec §Q-Migration: /kick the
            // owner BEFORE /deleteagency to ensure no Launch lands during the
            // cascade race window; AND manually restore any Missing passengers
            // via the documented hand-edit workflow at Universe/Kerbals/{name}.txt
            // (state = Available; ToD = 0) — they'll need to be hand-copied into
            // the surviving agency's subdir for the receiving client to see them.
            if (AgencySystem.PerAgencyKerbalRosterEnabled && agency.WolfCrewRoutes != null && agency.WolfCrewRoutes.Count > 0)
            {
                LunaLog.Warning(
                    $"[fix:per-agency-kerbal-roster-write-routing] CascadeOnDelete invoked on agency {agency.AgencyId:N} " +
                    "({agency.OwningPlayerName}) under PerAgencyKerbalRosterEnabled=true with in-flight WOLF CrewRoutes. " +
                    "Restored kerbal files will land in legacy Universe/Kerbals/ but the per-agency request filter " +
                    "(Phase 6.4) does not enumerate legacy under gate=on — restored kerbals will be unreachable to " +
                    "clients of any surviving agency until Phase 6.7 ships the per-agency-subdir routing for Slice F. " +
                    "Recovery: stop the server after this command completes, hand-copy any Universe/Kerbals/{name}.txt " +
                    "files to the destination agency's Universe/Agencies/{guid:N}/Kerbals/ subdir, restart.");
            }

            // Phase 1 — snapshot phase under the agency lock. We collect
            // distinct passenger names into a HashSet so a kerbal who somehow
            // appears in two in-flight routes (defensive — WOLF's wire
            // contract doesn't permit this but a malformed wire upsert
            // could) is restored exactly once. HashSet uses Ordinal compare
            // to match KSP's kerbal-name semantics (file paths on disk are
            // case-sensitive on Linux; Ordinal is the safe default).
            var passengersToRestore = new HashSet<string>(StringComparer.Ordinal);
            lock (AgencySystem.GetAgencyLock(agency.AgencyId))
            {
                foreach (var kvp in agency.WolfCrewRoutes)
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

            // Phase 2 — restoration phase WITHOUT the agency lock. Per-kerbal
            // file mutation serializes via FileHandler's per-path lock.
            // Per-kerbal try/catch isolates failures.
            //
            // Log grammar mirrors the per-vessel demote audit lines in
            // DeleteAgencyCommand (verb, agencyId, key='value'): a GUI
            // launcher's `[fix:WOLF-R4] deleteagency {agencyId:N}` grep can
            // pair these lines with the surrounding cascade-summary +
            // visibility-broadcast + per-vessel-demote audit trail for the
            // same /deleteagency invocation. (Consumer-lens SHOULD FIX
            // #1 — without the agencyId token the per-kerbal lines couldn't
            // be routed back to the deleted agency's audit panel under
            // concurrent /deleteagency operations.)
            var agencyId = agency.AgencyId;
            foreach (var name in passengersToRestore)
            {
                try
                {
                    if (TryRestoreKerbalToAcPool(name))
                    {
                        result.RestoredKerbalCount++;
                        result.RestoredKerbalNames.Add(name);
                        LunaLog.Normal(
                            $"[fix:WOLF-R4] deleteagency {agencyId:N} restored-kerbal " +
                            $"name='{name}' state=Available ToD=0");
                    }
                    else
                    {
                        result.FailedKerbalNames.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    result.FailedKerbalNames.Add(name);
                    LunaLog.Warning(
                        $"[fix:WOLF-R4] deleteagency {agencyId:N} failed-kerbal " +
                        $"name='{name}' cause='{ex.GetType().Name}: {ex.Message}' " +
                        $"manual-recovery-path='Universe/Kerbals/{name}.txt' " +
                        "manual-recovery-fields='state = Available; ToD = 0'");
                }
            }

            return result;
        }

        /// <summary>
        /// True when the supplied FlightStatus string (as serialized by
        /// <see cref="AgencyWolfCrewRouter"/> from WOLF's
        /// <c>FlightStatus.ToString()</c>) indicates the route's passengers
        /// are in <c>RosterStatus.Missing</c> and need rescuing. See class
        /// XML "Restoration scope" paragraph for the per-status rationale.
        ///
        /// <para><b>Case-sensitive Ordinal compare.</b> WOLF's
        /// <c>FlightStatus.ToString()</c> emits canonical PascalCase enum
        /// names (Arrived / Boarding / Enroute / Unknown — verified
        /// <c>FlightMetadata.cs:3</c>). The wire round-trip preserves the
        /// string verbatim per <see cref="AgencyWolfCrewRouter.Upsert"/>.
        /// Defensive against null/empty via the IsNullOrEmpty short-circuit.</para>
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
        /// Mutates the on-disk kerbal file at
        /// <c>Universe/Kerbals/{name}.txt</c>: rewrites the top-level
        /// <c>state = ...</c> line to <c>state = Available</c> and the
        /// top-level <c>ToD = ...</c> line to <c>ToD = 0</c>. Returns true
        /// on successful rewrite, false when the file is missing or
        /// malformed.
        ///
        /// <para><b>Depth-aware line rewrite.</b> Kerbal files have nested
        /// <c>CAREER_LOG { ... }</c> and <c>FLIGHT_LOG { ... }</c> blocks
        /// per the resource template at
        /// <c>Server/Resources/Kerbals/Jebediah Kerman.txt:18-25</c>. KSP
        /// could theoretically emit a <c>ToD</c> field inside a log block
        /// (none today, but defensive); we only rewrite at depth 0 to avoid
        /// stomping nested data.</para>
        ///
        /// <para><b>UTF-8 round-trip</b> via <see cref="FileHandler.ReadFileText"/>
        /// + <see cref="FileHandler.WriteAtomic"/> (which internally uses
        /// <c>Encoding.UTF8</c>). Matches KSP's kerbal-file on-disk
        /// encoding. <see cref="FileHandler.WriteAtomic"/> rather than
        /// <see cref="FileHandler.WriteToFile(string, string)"/> because
        /// the kerbal-restoration cascade runs as part of an operator-
        /// initiated destructive command (<c>/deleteagency --confirm</c>) —
        /// a crash mid-write of a kerbal file (truncate-then-rewrite) would
        /// leave the file at half-bytes ("state = AvAvailable" or similar)
        /// which KSP's ConfigNode parser would fail on, replacing the
        /// "kerbal in Missing state" problem the cascade was solving with
        /// a worse "kerbal file unparseable" problem. WriteAtomic's
        /// rotate-temp-rename gives crash-tolerance against the truncation
        /// window. (Integration-lens MUST FIX #1 / general-lens SHOULD FIX
        /// #3 on Slice F.) Note: WriteAtomic leaves a single-generation
        /// <c>{path}.bak</c> after rotation; operators see one .bak per
        /// restored kerbal in <c>Universe/Kerbals/</c> until KSP's next
        /// save cycle overwrites the canonical file — same pattern as
        /// <c>Universe/Agencies/{guid}.txt.bak</c> from Stage 5.14c.</para>
        ///
        /// <para><b>Line-ending behavior.</b> Split-on-LF + join-on-LF
        /// preserves \r\n on lines we pass through unchanged. Rewritten
        /// state/ToD lines emit unconditionally with \n (the \r from a
        /// CRLF-source line is dropped on the two rewritten lines only).
        /// KSP's ConfigNode parser tolerates the mixed line endings; any
        /// subsequent KSP-side save normalises the file to the platform
        /// default in one round-trip.</para>
        ///
        /// <para><b>Internal visibility</b> for ServerTest unit coverage.</para>
        /// </summary>
        internal static bool TryRestoreKerbalToAcPool(string kerbalName)
        {
            if (string.IsNullOrEmpty(kerbalName))
                return false;

            var path = Path.Combine(KerbalSystem.KerbalsPath, kerbalName + ".txt");
            if (!FileHandler.FileExists(path))
            {
                LunaLog.Warning(
                    $"[fix:WOLF-R4] deleteagency cascade: kerbal file '{path}' is missing — " +
                    "cannot restore. Likely a newly-recruited kerbal whose KerbalProto never " +
                    "made it to disk before /deleteagency, or operator hand-deleted the file.");
                return false;
            }

            var original = FileHandler.ReadFileText(path);
            if (string.IsNullOrEmpty(original))
            {
                LunaLog.Warning(
                    $"[fix:WOLF-R4] deleteagency cascade: kerbal file '{path}' is empty — " +
                    "cannot restore. File is malformed (zero bytes); manual recovery required.");
                return false;
            }

            if (!TryRewriteKerbalText(original, out var rewritten, out var stateSeen, out var todSeen))
            {
                return false;
            }
            if (!stateSeen || !todSeen)
            {
                LunaLog.Warning(
                    $"[fix:WOLF-R4] deleteagency cascade: kerbal file '{path}' missing top-level " +
                    $"'state =' (seen={stateSeen}) and/or 'ToD =' (seen={todSeen}). File is " +
                    "malformed for a KSP kerbal record; skipping restoration to avoid corrupting " +
                    "the file. Manual recovery required.");
                return false;
            }

            // WriteAtomic gives crash-tolerance against the truncate window
            // — see XML "UTF-8 round-trip" paragraph above for rationale.
            // No-op short-circuit: if the file already has state=Available +
            // ToD=0 (e.g. operator manually pre-restored the kerbal before
            // /deleteagency), WriteAtomic still rotates + writes the
            // identical content. A trivial waste of one .bak file vs a
            // ContentChecker pre-compare; acceptable for an operator-
            // initiated destructive command.
            FileHandler.WriteAtomic(path, rewritten);
            return true;
        }

        /// <summary>
        /// Pure-text helper for the depth-aware kerbal-file line rewrite.
        /// Extracted from <see cref="TryRestoreKerbalToAcPool"/> so the
        /// regex-free line-walk decision logic gets ServerTest coverage
        /// without disk I/O.
        ///
        /// <para>Walks <paramref name="original"/> line-by-line tracking
        /// brace depth (<c>{</c> increments, <c>}</c> decrements). At depth
        /// 0, rewrites <c>^\s*state\s*=\s*...</c> to <c>state = Available</c>
        /// and <c>^\s*ToD\s*=\s*...</c> to <c>ToD = 0</c>. Other lines pass
        /// through verbatim. Returns false only on catastrophic parse
        /// failure (depth goes negative — indicates malformed file with an
        /// unbalanced <c>}</c> at top scope).</para>
        ///
        /// <para><b>Internal visibility</b> for ServerTest unit coverage of
        /// the rewrite logic without disk I/O.</para>
        /// </summary>
        internal static bool TryRewriteKerbalText(string original, out string rewritten, out bool stateSeen, out bool todSeen)
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

                // Detect a top-level (depth 0) field assignment for state or ToD
                // BEFORE bumping depth on this line — KSP's kerbal files always
                // open a brace on its own line, so per-line "trim and check
                // prefix" is sufficient. If a future KSP version inlines
                // `key = value { nested }` on one line this heuristic would
                // mis-classify; today no such shape exists in stock kerbal files.
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

                // Re-join with LF; the trailing \r (if Windows-style line endings
                // were present on the input) is preserved as the last char of
                // each pre-LF line via Split('\n'), so the joined output keeps
                // CRLF when the input had CRLF.
                if (i < lines.Length - 1)
                    sb.Append('\n');

                // Update depth from THIS line's brace count. A line containing
                // both `{` and `}` (one-line nested block — KSP doesn't emit
                // these for kerbal files, defensive) nets to zero. Trim before
                // counting so leading-tab `}` lines are caught.
                foreach (var c in trimmed)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }

                if (depth < 0)
                {
                    LunaLog.Warning(
                        $"[fix:WOLF-R4] deleteagency cascade: kerbal text rewrite aborted — " +
                        $"brace depth went negative at line {i + 1}. File is malformed.");
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
            // Tolerate `key = value`, `key=value`, `key   =   value`. Reject
            // `keyExtra = value` (e.g. a future field literally named "stateMachine").
            var i = 0;
            while (i < rest.Length && (rest[i] == ' ' || rest[i] == '\t')) i++;
            return i < rest.Length && rest[i] == '=';
        }
    }
}
