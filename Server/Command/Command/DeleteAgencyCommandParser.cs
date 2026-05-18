using System;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Pure parser for <see cref="DeleteAgencyCommand"/>. Stage 5.18d slice (g).
    /// Two-token grammar: the agency token + the literal <c>--confirm</c> flag,
    /// in either order. The flag is REQUIRED — deletion is destructive (the
    /// AgencyState file + its .bak are removed; vessels are demoted to the
    /// Unassigned sentinel; per-agency contracts / tech / etc. inside the
    /// AgencyState file are gone). Operator must opt in explicitly.
    /// </summary>
    internal static class DeleteAgencyCommandParser
    {
        public const string ConfirmFlag = "--confirm";

        public const string UsageBanner =
            "Usage:\n" +
            "  /deleteagency <agency-id|owner> --confirm\n" +
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
            "is required; without it, the command prints this banner and refuses.";

        public static bool TryParse(string commandArgs, out string sourceToken, out bool confirmed, out string error)
        {
            sourceToken = string.Empty;
            confirmed = false;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                error = "deleteagency: no arguments supplied.";
                return false;
            }

            var parts = commandArgs.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Walk tokens once and partition into "confirm flags" + "other"; the
            // order on the command line doesn't matter. Defensive: a duplicate
            // --confirm is harmless (operator double-typed); a non-flag, non-token
            // surplus is an error.
            var flagSeen = parts.Any(p => string.Equals(p, ConfirmFlag, StringComparison.Ordinal));
            var nonFlagTokens = parts.Where(p => !string.Equals(p, ConfirmFlag, StringComparison.Ordinal)).ToList();

            if (nonFlagTokens.Count == 0)
            {
                error = "deleteagency: missing agency token (expected an agency id or owner name).";
                return false;
            }
            if (nonFlagTokens.Count > 1)
            {
                error = $"deleteagency: too many tokens. Expected one agency id or owner name (plus --confirm); got '{string.Join(" ", nonFlagTokens)}'.";
                return false;
            }

            sourceToken = nonFlagTokens[0];
            confirmed = flagSeen;
            return true;
        }
    }
}
