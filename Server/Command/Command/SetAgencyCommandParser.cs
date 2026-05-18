using System;
using System.Globalization;

namespace Server.Command.Command
{
    /// <summary>
    /// Pure parser for <see cref="SetAgencyCommand"/>'s input string. Extracted so
    /// ServerTest can pin the argument grammar — usage banner format,
    /// case-insensitive subcommand matching, invariant-culture number parsing,
    /// per-error rejection messages — without spinning up the
    /// <c>AgencySystem</c> singleton.
    /// </summary>
    internal static class SetAgencyCommandParser
    {
        /// <summary>
        /// Three-line usage banner the command prints on parse failure. Same shape
        /// as <see cref="BackupCommand"/>'s usage lines so an operator who already
        /// knows the backup command's style recognises this one.
        /// </summary>
        public const string UsageBanner =
            "Usage:\n" +
            "  /setagency funds            <agency-id|owner> <amount>\n" +
            "  /setagency science          <agency-id|owner> <amount>\n" +
            "  /setagency reputation|rep   <agency-id|owner> <amount>\n" +
            "Tokens match the id= and owner= columns of /listagencies output.";

        /// <summary>
        /// Which scalar field the subcommand targets. Maps 1:1 to
        /// <see cref="System.Agency.AgencyState"/> field names.
        /// </summary>
        public enum Scalar
        {
            Funds,
            Science,
            Reputation,
        }

        /// <summary>
        /// Parses <paramref name="commandArgs"/> (the raw substring after
        /// <c>/setagency </c>) into the four output components. Returns false on
        /// any parse failure; <paramref name="error"/> carries the operator-facing
        /// reason and is non-empty on failure, empty on success.
        ///
        /// <para>Splits on single spaces (matches <see cref="Common.CommandSystemHelperMethods"/>
        /// behaviour for the existing admin command surface). Three tokens
        /// expected — subcommand, agency-token, amount. Extra tokens are tolerated
        /// (passed through to <paramref name="token"/> if a multi-word token was
        /// intended, but the agency-token resolver in
        /// <see cref="System.Agency.AgencySystem.TryResolveAgencyToken"/> rejects
        /// strings with embedded spaces — operators with weird-named display names
        /// must use the agency id form instead).</para>
        /// </summary>
        public static bool TryParse(string commandArgs, out Scalar sub, out string token, out double value, out string error)
        {
            sub = default;
            token = string.Empty;
            value = 0d;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                error = "setagency: no arguments supplied.";
                return false;
            }

            // Split on whitespace, drop empties (operator-typed double-spaces are
            // benign). Three tokens required.
            var parts = commandArgs.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                error = $"setagency: expected 3 arguments (subcommand, agency-token, amount); got {parts.Length}.";
                return false;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "funds":      sub = Scalar.Funds; break;
                case "science":    sub = Scalar.Science; break;
                case "reputation": sub = Scalar.Reputation; break;
                case "rep":        sub = Scalar.Reputation; break; // operator-friendly alias
                default:
                    error = $"setagency: unknown subcommand '{parts[0]}'. Expected funds | science | reputation.";
                    return false;
            }

            token = parts[1];
            if (string.IsNullOrEmpty(token))
            {
                error = "setagency: agency token must be non-empty.";
                return false;
            }

            // InvariantCulture parse — operators on de-DE / fr-FR locales should
            // type "25000.5" not "25000,5". The shared-agency setfunds /setscience
            // commands use double.TryParse with default culture which is a latent
            // bug; we don't propagate it here.
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                error = $"setagency: '{parts[2]}' is not a valid number (expected invariant-culture form, e.g. 25000.5).";
                return false;
            }

            return true;
        }
    }
}
