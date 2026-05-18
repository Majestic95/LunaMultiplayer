using System;

namespace Server.Command.Command
{
    /// <summary>
    /// Pure parser for <see cref="TransferAgencyCommand"/>'s input string. Extracted
    /// so ServerTest can pin the argument grammar without spinning up
    /// <c>AgencySystem</c>.
    ///
    /// <para><b>Shape:</b> two tokens — agency-id-or-owner, new-player-name. NO
    /// subcommands (one verb = one shape). The vessel-level X→Y reassignment
    /// shape mentioned in <see cref="LmpCommon.Message.Data.Agency.AgencyVisibilityMsgData"/>'s
    /// XML is implemented exclusively by the Stage 5.18d slice (g)
    /// <c>/deleteagency</c> cascade, not by <c>/transferagency</c>.</para>
    /// </summary>
    internal static class TransferAgencyCommandParser
    {
        public const string UsageBanner =
            "Usage:\n" +
            "  /transferagency <agency-id|owner> <new-player-name>\n" +
            "Tokens match the id= and owner= columns of /listagencies output.\n" +
            "Transfers ownership of an existing agency to a different LMP player handle.\n" +
            "Vessels keep their AgencyId stamp; the agency's identity is preserved.";

        public static bool TryParse(string commandArgs, out string sourceToken, out string newOwnerName, out string error)
        {
            sourceToken = string.Empty;
            newOwnerName = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                error = "transferagency: no arguments supplied.";
                return false;
            }

            var parts = commandArgs.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                error = $"transferagency: expected 2 arguments (source agency-token, new player name); got {parts.Length}.";
                return false;
            }
            if (parts.Length > 2)
            {
                // New player names with embedded spaces aren't supported — LMP player
                // handles are space-free per HandshakeSystemValidator. A 3+ token
                // input is almost certainly an operator typo.
                error = $"transferagency: expected 2 arguments (source agency-token, new player name); got {parts.Length}. Player handles cannot contain spaces.";
                return false;
            }

            sourceToken = parts[0];
            newOwnerName = parts[1];

            if (string.IsNullOrEmpty(sourceToken))
            {
                error = "transferagency: source agency token must be non-empty.";
                return false;
            }
            if (string.IsNullOrEmpty(newOwnerName))
            {
                error = "transferagency: new player name must be non-empty.";
                return false;
            }

            return true;
        }
    }
}
