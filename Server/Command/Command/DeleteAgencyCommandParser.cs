using System;
using System.Collections.Generic;

namespace Server.Command.Command
{
    /// <summary>
    /// Pure parser for <see cref="DeleteAgencyCommand"/>. Stage 5.18d slice (g).
    /// Three-token grammar (Phase 6.7): the agency token + the literal
    /// <c>--confirm</c> flag (required) + optionally one of the mutually
    /// exclusive WOLF-cascade restoration directives — <c>--restore-to &lt;agency-token&gt;</c>
    /// or <c>--restore-to-none</c>. Position-agnostic.
    ///
    /// <para><b>--confirm is REQUIRED.</b> Deletion is destructive: the
    /// AgencyState file + its .bak are removed; per-agency contracts / tech /
    /// etc. inside it are gone; vessels are demoted to the Unassigned sentinel.
    /// Operator must opt in explicitly.</para>
    ///
    /// <para><b>--restore-to + --restore-to-none — Phase 6.7 cascade-routing
    /// flags (gate=on only).</b> Under
    /// <see cref="Server.System.Agency.AgencySystem.PerAgencyKerbalRosterEnabled"/>=true,
    /// WOLF CrewRoute passengers in-flight on the deleted agency CANNOT be
    /// restored to the agency's own per-agency kerbal subdir (the subdir is
    /// cascade-deleted by <see cref="Server.System.Agency.AgencySystem.TryDeleteAgency"/>
    /// seconds later). The operator MUST explicitly choose either:
    /// <list type="bullet">
    ///   <item><c>--restore-to &lt;agency-token&gt;</c> — restored kerbal files
    ///         land in the named agency's <c>Universe/Agencies/{guid:N}/Kerbals/</c>
    ///         subdir. The named agency must exist + must not be the agency
    ///         being deleted.</item>
    ///   <item><c>--restore-to-none</c> — operator explicitly accepts that
    ///         in-flight kerbals are lost on disk. Cascade still walks the
    ///         routes + emits the audit summary but writes no kerbal files
    ///         anywhere. Mirrors the <c>--confirm</c> "yes I really mean it"
    ///         posture.</item>
    /// </list>
    /// If the deleted agency has in-flight CrewRoute passengers under gate=on
    /// AND neither flag is supplied, the command refuses with an error
    /// pointing at this banner. Under gate=off the legacy shared
    /// <c>Universe/Kerbals/</c> rewrite path runs and both flags are
    /// REJECTED (no semantic meaning when the kerbal roster is shared).</para>
    ///
    /// <para><b>Why both flags vs single optional.</b> Without an explicit
    /// "I accept the loss" knob, an operator running on a server with no
    /// in-flight CrewRoutes would have to type a destination they don't care
    /// about. Without an explicit destination knob, the silent legacy-fallback
    /// IS the bug Phase 6.7 closes. The two-flag-mutex design forces a
    /// deliberate choice in the in-flight case and stays out of the way in
    /// the no-in-flight case.</para>
    /// </summary>
    internal static class DeleteAgencyCommandParser
    {
        public const string ConfirmFlag = "--confirm";
        public const string RestoreToFlag = "--restore-to";
        public const string RestoreToNoneFlag = "--restore-to-none";

        public const string UsageBanner =
            "Usage:\n" +
            "  /deleteagency <agency-id|owner> --confirm [--restore-to <agency-id|owner> | --restore-to-none]\n" +
            "Removes the AgencyState (in memory + disk + .bak), demotes vessels stamped with this agency\n" +
            "to the Unassigned sentinel (broadcast via AgencyVisibilityMsgData), and releases the prior\n" +
            "owner's vessel-scoped locks. The prior owner mints a fresh agency on next reconnect.\n" +
            "\n" +
            "Vessels survive as UNASSIGNED — they are not deleted. Per spec §10 Q3 any agency may\n" +
            "interact with Unassigned vessels (acquire control locks, recover, etc.). The prior owner\n" +
            "can re-claim them after their reconnect mints a fresh agency. If the operator intent is\n" +
            "FULL purge, run /clearvessels for the affected vessels BEFORE /deleteagency.\n" +
            "\n" +
            "Destructive: per-agency contracts, tech, science, reputation, funds, facility levels, and\n" +
            "strategies stored in the AgencyState file are LOST. There is no undo. The --confirm flag\n" +
            "is required; without it, the command prints this banner and refuses.\n" +
            "\n" +
            "WOLF in-flight kerbal restoration (PerAgencyKerbalRoster=true only):\n" +
            "  If the agency has WOLF CrewRoute passengers in {Enroute,Arrived} state, the cascade\n" +
            "  CANNOT write their restored kerbal files to the deleted agency's own subdir (that\n" +
            "  subdir is removed seconds later). Operator must pick a disposition:\n" +
            "    --restore-to <agency-id|owner>  Land restored kerbals in the named agency's subdir.\n" +
            "                                    Destination must exist and must not be the agency\n" +
            "                                    being deleted. Per-kerbal name collisions in the\n" +
            "                                    destination subdir are SKIPPED with a Warning; the\n" +
            "                                    destination's existing kerbal is preserved.\n" +
            "    --restore-to-none               Operator explicitly accepts in-flight kerbal loss.\n" +
            "                                    No disk writes; CrewRoute audit summary still emits.\n" +
            "  Under PerAgencyKerbalRoster=false (shared-roster mode), both flags are rejected\n" +
            "  with an error. The legacy Universe/Kerbals/ rewrite path runs unchanged.";

        public static bool TryParse(
            string commandArgs,
            out string sourceToken,
            out bool confirmed,
            out string restoreToToken,
            out bool restoreToNone,
            out string error)
        {
            sourceToken = string.Empty;
            confirmed = false;
            restoreToToken = string.Empty;
            restoreToNone = false;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                error = "deleteagency: no arguments supplied.";
                return false;
            }

            var parts = commandArgs.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Walk tokens once and categorise. Position-agnostic per the
            // existing --confirm precedent. The --restore-to flag consumes the
            // NEXT non-flag token as its destination value; that value gets
            // marked-consumed so it doesn't end up in the source-token bucket.
            var nonFlagTokens = new List<string>();
            var restoreToCount = 0;
            var restoreToNoneCount = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var token = parts[i];
                if (string.Equals(token, ConfirmFlag, StringComparison.Ordinal))
                {
                    confirmed = true;
                    continue;
                }
                if (string.Equals(token, RestoreToNoneFlag, StringComparison.Ordinal))
                {
                    restoreToNoneCount++;
                    continue;
                }
                if (string.Equals(token, RestoreToFlag, StringComparison.Ordinal))
                {
                    restoreToCount++;
                    // Consume the next token as the destination value. If
                    // absent or itself a flag, that's an error — surfaced
                    // below.
                    if (i + 1 >= parts.Length || LooksLikeFlag(parts[i + 1]))
                    {
                        error = $"deleteagency: {RestoreToFlag} requires a destination agency token (id or owner name). Run /listagencies to see valid tokens.";
                        return false;
                    }
                    restoreToToken = parts[i + 1];
                    i++; // skip the consumed value
                    continue;
                }
                nonFlagTokens.Add(token);
            }

            if (restoreToCount > 1)
            {
                error = $"deleteagency: {RestoreToFlag} specified {restoreToCount} times. Specify it at most once.";
                return false;
            }
            if (restoreToNoneCount > 1)
            {
                error = $"deleteagency: {RestoreToNoneFlag} specified {restoreToNoneCount} times. Specify it at most once.";
                return false;
            }
            if (restoreToCount > 0 && restoreToNoneCount > 0)
            {
                error = $"deleteagency: {RestoreToFlag} and {RestoreToNoneFlag} are mutually exclusive. Pick one.";
                return false;
            }
            restoreToNone = restoreToNoneCount > 0;

            if (nonFlagTokens.Count == 0)
            {
                error = "deleteagency: missing agency token (expected an agency id or owner name).";
                return false;
            }
            if (nonFlagTokens.Count > 1)
            {
                error = $"deleteagency: too many tokens. Expected one agency id or owner name (plus --confirm, optional --restore-to/--restore-to-none); got '{string.Join(" ", nonFlagTokens)}'.";
                return false;
            }

            sourceToken = nonFlagTokens[0];
            return true;
        }

        private static bool LooksLikeFlag(string token) =>
            !string.IsNullOrEmpty(token) && token.StartsWith("--", StringComparison.Ordinal);
    }
}
