using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Pure formatter for <see cref="ListAgenciesCommand"/>. Extracted as a standalone
    /// static so ServerTest can pin every line against synthetic rows — the console output
    /// IS the API contract for the Stage 5.18+ GUI launcher (which parses server stdout
    /// line-by-line), so format drift caught here saves a downstream parser regression.
    ///
    /// <para><b>Output framing.</b> Every line emitted by this formatter is prefixed with
    /// <see cref="Tag"/> (<c>[fix:per-agency-career]</c>). This is load-bearing for two
    /// reasons: (a) <see cref="Server.Log.LunaLog.Normal"/> further prepends
    /// <c>[HH:mm:ss][LMP]: </c> to every line on stdout, so the GUI parser cannot rely on
    /// raw indentation (timestamp width is 19 chars); (b) other commands and periodic
    /// background events (BackupSystem, [fix:BUG-XXX] runtime markers) interleave on the
    /// same stdout stream, so the GUI needs a stable per-line block-id. <see cref="Tag"/>
    /// is that block-id. A registry enumeration is bracketed by
    /// <c>{Tag} registry start ...</c> and <c>{Tag} registry end ...</c> so the GUI can
    /// match block boundaries unambiguously. <b>Important: the same tag is also emitted
    /// by boot-time helpers</b> (<c>WarnAboutOrphanedVessels</c>, the inactive-mode
    /// warning, etc.) so consumers MUST use the registry start/end pair to scope a block
    /// — see the parsing contract below.</para>
    ///
    /// <para><b>Concurrency model.</b> <see cref="Server.Command.CommandHandler"/>'s
    /// <c>ThreadMainAsync</c> processes stdin commands strictly serially (a single
    /// <c>await Task.Delay(500)</c> between reads), and every <see cref="Format"/> call's
    /// lines are emitted contiguously through <see cref="Server.Log.LunaLog.Normal"/>'s
    /// underlying <c>Console.WriteLine</c>. Consumers can therefore assume a
    /// <c>registry start</c> line is followed by its matching <c>registry end</c> with
    /// no overlapping <c>start</c>/<c>end</c> pair from a concurrent invocation in
    /// between. If a future code path adds a second admin-command source (web admin,
    /// remote trigger, etc.) that can fire <c>/listagencies</c> concurrently, this
    /// guarantee will need re-evaluation — likely via a per-invocation correlation id
    /// on the start/end lines.</para>
    ///
    /// <para><b>Line types emitted.</b></para>
    /// <list type="bullet">
    ///   <item><description><c>{Tag} disabled: ...</c> — PerAgencyCareer=false (gate off).</description></item>
    ///   <item><description><c>{Tag} stranded-stamps vessels=N</c> — gate off + N vessels carry an lmpOwningAgency stamp from a prior session.</description></item>
    ///   <item><description><c>{Tag} disabled-recovery: ...</c> — operator-facing recovery options when stranded stamps were surfaced.</description></item>
    ///   <item><description><c>{Tag} inactive: ...</c> — PerAgencyCareer=true but GameMode≠Career; registry still loaded for diagnostics.</description></item>
    ///   <item><description><c>{Tag} registry start state={live|inactive} agencies=N</c> — start of an enumeration block.</description></item>
    ///   <item><description><c>{Tag} row id=... owner=... display=... funds=... sci=... rep=... vessels=...</c> — one per registered agency.</description></item>
    ///   <item><description><c>{Tag} orphan id=... vessels=N</c> — one per agency-id referenced by a vessel but not present in the registry (mirror of <c>WarnAboutOrphanedVessels</c>).</description></item>
    ///   <item><description><c>{Tag} orphan-consequence: ...</c> — emitted once per block when orphans &gt; 0; restates the action consequence (lock-acquire refused + relayed writes dropped) so the GUI consumer doesn't need to scrape boot logs.</description></item>
    ///   <item><description><c>{Tag} unassigned vessels=N</c> — count of vessels carrying <c>Guid.Empty</c> (spec §10 Q3 Unassigned-sentinel).</description></item>
    ///   <item><description><c>{Tag} first-connect-pending hazard=N override={true|false}</c> — emitted in the gate-active block when <c>rows=0</c> AND <c>(orphans+unassigned)&gt;0</c>. Signals an upgrade-in-place universe before its first per-agency client connect, or one where the boot-refusal override was set explicitly. The <c>override</c> field distinguishes the two states.</description></item>
    ///   <item><description><c>{Tag} registry end agencies=N rows=N orphans=M unassigned=K</c> — terminator with summary counts.</description></item>
    /// </list>
    ///
    /// <para><b>Parsing contract for downstream consumers (GUI launcher etc.).</b></para>
    /// <list type="number">
    ///   <item><description><b>Filter by tag first.</b> Greedy-match
    ///   <c>\[fix:per-agency-career\]</c> in each stdout line; ignore lines without it.
    ///   Lines may be preceded by <c>LunaLog</c>'s <c>[HH:mm:ss][LMP]: </c> prefix.</description></item>
    ///   <item><description><b>Block detection — MUST require start/end framing.</b>
    ///   A tagged line is part of a registry enumeration block <i>only</i> when it
    ///   arrives between a <c>registry start</c> line and the matching <c>registry end</c>.
    ///   Tagged lines that arrive outside any open block are status events — emitted by
    ///   boot-time helpers (<c>WarnAboutOrphanedVessels</c> et al.), other admin
    ///   commands, or the pre-block <c>disabled</c> / <c>inactive</c> /
    ///   <c>stranded-stamps</c> / <c>disabled-recovery</c> lines this command emits.
    ///   Consumers MUST classify line kind by matching the literal substring after the
    ///   tag (<c>" registry start"</c>, <c>" row "</c>, <c>" orphan "</c>,
    ///   <c>" orphan-consequence:"</c>, <c>" unassigned "</c>,
    ///   <c>" first-connect-pending "</c>, <c>" registry end"</c>, <c>" disabled"</c>,
    ///   <c>" inactive:"</c>, <c>" stranded-stamps "</c>, <c>" disabled-recovery:"</c>),
    ///   not by structural inference.</description></item>
    ///   <item><description><b>Tokenization.</b> Within a line after the tag, split into
    ///   <c>key=value</c> tokens with a <i>quote-aware</i> parser: a value beginning with
    ///   <c>"</c> consumes characters until an unescaped <c>"</c>. Inside a quoted string,
    ///   <c>\\</c> represents a literal backslash and <c>\"</c> represents a literal
    ///   double quote (unescape in this order: <c>\"</c> → <c>"</c>, then <c>\\</c> → <c>\</c>).
    ///   The unit-test file ships a reference tokenizer named <c>RowTokenizer</c> that
    ///   implements this rule. <b>Unclosed-quote behaviour is undefined</b> — well-formed
    ///   output from this formatter cannot produce one (the <c>QuoteEscape</c> helper
    ///   always closes its quotes), so any unclosed quote a consumer encounters is
    ///   corrupt input from outside this code path.</description></item>
    ///   <item><description><b>Field-order stability — MUST NOT depend on position.</b>
    ///   The keys within a given line type appear in a canonical order pinned by the
    ///   test suite. The pinned order exists for human readability and diff-stability
    ///   only; consumers MUST parse by key name (not by index or column position). A
    ///   future slice WILL append fields (e.g. <c>flag=</c>, <c>callsign=</c>) and
    ///   consumers MUST tolerate unknown keys without breaking. Positional parsing is
    ///   not a supported access pattern under any version of this contract.</description></item>
    ///   <item><description><b>Guid format.</b> All <c>id=</c> values use the
    ///   <c>Guid.ToString("N")</c> 32-character hex format (no hyphens), matching the
    ///   <c>Universe/Agencies/{guid}.txt</c> filenames on disk. Cross-references between
    ///   <c>/listagencies</c>, future <c>/listclients</c> agency-aware extensions, and
    ///   on-disk artifacts will use the same form.</description></item>
    ///   <item><description><b>Numeric format.</b> <c>funds</c> / <c>sci</c> / <c>rep</c>
    ///   use the <c>"R"</c> round-trip specifier under InvariantCulture. <c>Infinity</c>,
    ///   <c>-Infinity</c>, and <c>NaN</c> are possible for hand-edited or corrupt agency
    ///   files (per <c>AgencyState.Parse</c>'s permissive ParseDoubleOrZero); consumers
    ///   should handle them via <c>double.Parse(value, NumberStyles.Float, InvariantCulture)</c>
    ///   which recognises all three. A GUI dashboard should treat non-finite values as
    ///   "corrupt" and surface them to the operator instead of silently rendering "0".</description></item>
    ///   <item><description><b>Line endings.</b> Stdout uses the platform's native line
    ///   ending — CRLF on the Windows server-host, LF on Linux. Consumers MUST accept
    ///   both (do not hard-code <c>\r\n</c>).</description></item>
    ///   <item><description><b>Long-form recovery messages.</b> The <c>disabled-recovery</c>
    ///   and <c>inactive:</c> lines run 200-350 chars and list multiple paths in
    ///   preference order. GUI consumers showing them as status banners SHOULD soft-wrap
    ///   the text rather than truncate — the operator needs all paths visible to make
    ///   an informed recovery choice. A future slice may break these into per-option
    ///   sub-keyed lines if a GUI demands strict atomic banners.</description></item>
    ///   <item><description><b>Field semantics.</b> <c>owner</c> is the LMP player handle
    ///   (the join key for <c>/listclients</c>); <c>display</c> is the user-chosen agency
    ///   name (free-form, may contain spaces / quotes / backslashes); <c>id</c> is the
    ///   durable <see cref="System.Guid"/> identity (join key for the vessel-level
    ///   <c>lmpOwningAgency</c> stamp). <c>owner</c> is captured at agency-registration
    ///   time and is NOT updated mid-session; if a player reconnects under a different
    ///   LMP name, <c>owner</c> shows the registration-time name until the Stage 5.18d
    ///   <c>transferagency</c> command lands.</description></item>
    /// </list>
    /// </summary>
    internal static class ListAgenciesFormatter
    {
        /// <summary>
        /// Tag prefix every emitted line carries. Exposed for tests that need to assert
        /// presence without hard-coding the string.
        /// </summary>
        public const string Tag = "[fix:per-agency-career]";

        public struct AgencyRow
        {
            public Guid AgencyId;
            public string OwningPlayerName;
            public string DisplayName;
            public double Funds;
            public double Science;
            public double Reputation;
            public int VesselCount;
        }

        public struct OrphanRow
        {
            public Guid OrphanAgencyId;
            public int VesselCount;
        }

        public static IEnumerable<string> Format(
            IReadOnlyList<AgencyRow> rows,
            IReadOnlyList<OrphanRow> orphans,
            int unassignedVessels,
            bool perAgencyConfigured,
            bool gateActive,
            bool acceptedLossOverrideSet)
        {
            // (1) Gate off — disk registry isn't loaded per AgencySystem.LoadExistingAgencies,
            // so no enumeration block. Stranded-stamps and recovery hint are emitted as
            // standalone tagged lines so the GUI parser sees them as block-less status.
            if (!perAgencyConfigured)
            {
                yield return $"{Tag} disabled: PerAgencyCareer=false. No registry to display.";
                if (unassignedVessels > 0)
                {
                    yield return $"{Tag} stranded-stamps vessels={unassignedVessels.ToString(CultureInfo.InvariantCulture)}";
                    // Three recovery paths in preference order — matches the boot helper
                    // WarnAboutStrandedAgencyStampsIfGateOff in AgencySystem.cs:741-749.
                    // Least-destructive first: keep Universe/Agencies/ intact so the
                    // stamps round-trip on re-enable. Mid: restore .bak when the
                    // canonical file was deleted but the rotation copy survives. Last:
                    // admin transferagency (Stage 5.18d) — destructive, loses identity.
                    yield return $"{Tag} disabled-recovery: To re-enable safely, keep Universe/Agencies/ intact (the stamps round-trip), or restore Universe/Agencies/{{guid}}.txt from its .bak rotation copy if the canonical file was deleted, or use the Stage 5.18d transferagency admin command to re-own affected vessels under fresh agencies (loses original agency identity).";
                }
                yield break;
            }

            // (2) Configured but inactive (PerAgencyCareer=true, mode≠Career).
            // LoadExistingAgencies DOES populate the registry for diagnostics; we surface
            // the same recovery information as the boot warning at AgencySystem.cs:165-173
            // — both routes (set GameMode=Career to activate, OR set PerAgencyCareer=false
            // to opt out cleanly). Operator chooses based on intent.
            var state = gateActive ? "live" : "inactive";
            if (!gateActive)
            {
                yield return $"{Tag} inactive: PerAgencyCareer=true but GameMode is not Career. Registry loaded from disk for diagnostics; runtime per-agency routing is OFF. To activate, set GameMode=Career in Settings/GeneralSettings.xml; to disable per-agency cleanly, set PerAgencyCareer=false in Settings/GameplaySettings.xml (may flip GameDifficulty to Custom — see CLAUDE.md Settings caveat).";
            }

            // (3) Enumeration block: start → rows → orphans → orphan-consequence (when N>0)
            // → first-connect-pending (when applicable) → unassigned → end.
            yield return $"{Tag} registry start state={state} agencies={rows.Count.ToString(CultureInfo.InvariantCulture)}";

            var ordered = rows
                .OrderBy(r => r.DisplayName ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(r => r.AgencyId);
            foreach (var row in ordered)
                yield return FormatAgencyRow(row);

            // Orphan rows mirror WarnAboutOrphanedVessels. Stable order by AgencyId so
            // GUI diff-refresh logic doesn't churn between identical invocations.
            var orderedOrphans = orphans.OrderBy(o => o.OrphanAgencyId);
            foreach (var orphan in orderedOrphans)
                yield return FormatOrphanRow(orphan);

            // [Upgrade-lens v2 N4] Action-consequence reminder. The boot helper carries it
            // (AgencySystem.cs:768); operators reading /listagencies via the GUI dashboard
            // may not have boot-log access by then (logs rotate, GUI buffers scroll past).
            // One reminder line per block keeps parsing stable while surfacing the
            // consequence at the dashboard surface.
            if (orphans.Count > 0)
            {
                yield return $"{Tag} orphan-consequence: Vessels owned by an orphan agency are server-side rejected for vessel-scoped lock acquires (Stage 5.17a) AND have their relayed position/flightstate broadcasts silently dropped (Stage 5.17a write-path counterpart). The owning player is locked out of their own vessels until either Universe/Agencies/{{guid}}.txt is restored or the Stage 5.18d transferagency command re-owns them.";
            }

            // [Upgrade-lens v2 N1+N3] First-connect-pending / accepted-loss signal. The
            // gate-active universe with zero registered agencies but pending stamps
            // (orphans + unassigned) is the upgrade-in-place signature. Boot-time
            // RefuseStartupIfUpgradeHazardWithoutOverride enforces the override gate at
            // startup; mid-session this command is the operator's verification surface.
            // override=true means the operator deliberately opted into the projection-strip
            // path; override=false at this point means the boot refusal was bypassed
            // (e.g. someone toggled the setting at runtime). Both states are observable;
            // the override field disambiguates them.
            if (rows.Count == 0 && (orphans.Count + unassignedVessels) > 0)
            {
                var hazardCount = unassignedVessels + orphans.Sum(o => o.VesselCount);
                var overrideToken = acceptedLossOverrideSet ? "true" : "false";
                yield return $"{Tag} first-connect-pending hazard={hazardCount.ToString(CultureInfo.InvariantCulture)} override={overrideToken}";
            }

            if (unassignedVessels > 0)
                yield return $"{Tag} unassigned vessels={unassignedVessels.ToString(CultureInfo.InvariantCulture)}";

            yield return
                $"{Tag} registry end " +
                $"agencies={rows.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"rows={rows.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"orphans={orphans.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"unassigned={unassignedVessels.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string FormatAgencyRow(AgencyRow r) =>
            $"{Tag} row " +
            $"id={r.AgencyId.ToString("N", CultureInfo.InvariantCulture)} " +
            $"owner={QuoteEscape(r.OwningPlayerName)} " +
            $"display={QuoteEscape(r.DisplayName)} " +
            $"funds={r.Funds.ToString("R", CultureInfo.InvariantCulture)} " +
            $"sci={r.Science.ToString("R", CultureInfo.InvariantCulture)} " +
            $"rep={r.Reputation.ToString("R", CultureInfo.InvariantCulture)} " +
            $"vessels={r.VesselCount.ToString(CultureInfo.InvariantCulture)}";

        private static string FormatOrphanRow(OrphanRow o) =>
            $"{Tag} orphan " +
            $"id={o.OrphanAgencyId.ToString("N", CultureInfo.InvariantCulture)} " +
            $"vessels={o.VesselCount.ToString(CultureInfo.InvariantCulture)}";

        private static string QuoteEscape(string value)
        {
            var safe = (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + safe + "\"";
        }
    }
}
