using System;

namespace Server.Command.Command
{
    /// <summary>
    /// Pure parser for <see cref="SetVesselAgencyCommand"/>'s input string. Phase 3
    /// Slice E-2 (the MKS-compat Phase 3 closer). Extracted so ServerTest can pin
    /// the argument grammar without spinning up <c>AgencySystem</c>.
    ///
    /// <para><b>Shape:</b> two tokens — vessel-guid, agency-id-or-owner. NO
    /// subcommands (one verb = one shape). Mirrors
    /// <see cref="TransferAgencyCommandParser"/>'s two-token shape; the leading
    /// token here is the vessel identity (a Guid string in "N" or "D" form),
    /// distinct from <c>/transferagency</c>'s leading agency-token.</para>
    ///
    /// <para><b>Why a NEW command, not extending /transferagency.</b> Stage 5.18d
    /// slice (e) <c>/transferagency</c> is an owner-RENAME command — it preserves
    /// the agency's <c>AgencyId</c>, vessels keep their <c>lmpOwningAgency</c>
    /// stamp, only the player handle attached to the agency changes. The
    /// substantive A→B vessel-reassignment shape does NOT exist there; this
    /// command ships it as a separate operator-facing surface (operator
    /// confirmed session 30 / 2026-05-18 via AskUserQuestion).</para>
    ///
    /// <para><b>Reversibility (no --confirm flag).</b> Unlike
    /// <c>/deleteagency</c> (which destroys an AgencyState file), this command
    /// mutates a single vessel's stamp + per-router migration partitions. To
    /// undo, the operator re-runs with the original agency token. No
    /// destructive opt-in is required.</para>
    ///
    /// <para><b>Vessel-guid form tolerance.</b> Accepts both Guid "N" form
    /// (32 hex digits, no separators — matches <c>lmpOwningAgency</c> on-disk
    /// shape) and Guid "D" form (8-4-4-4-12 with hyphens — matches
    /// <c>/listclients</c> + <c>VesselStoreSystem</c> dict-key string format
    /// per default <see cref="Guid.ToString()"/>). Resolution to canonical
    /// <see cref="Guid"/> happens in <see cref="SetVesselAgencyCommand"/>;
    /// this parser only confirms parseability.</para>
    /// </summary>
    internal static class SetVesselAgencyCommandParser
    {
        public const string UsageBanner =
            "Usage:\n" +
            "  /setvesselagency <vessel-guid> <agency-id|owner>\n" +
            "Reassigns the given vessel to the named agency. The agency token matches the\n" +
            "id= or owner= column of /listagencies output. Per-agency MKS partitions migrate\n" +
            "with the vessel per pre-spec §4.e (kolony entries move; orbital transfers move\n" +
            "if the vessel is Destination and stay-in-source if Origin-only; planetary entries\n" +
            "do NOT migrate — they represent body-pool state, not vessel state).\n" +
            "\n" +
            "Reversible: re-run with the original agency token to undo. Source owner's vessel-\n" +
            "scoped locks (Control/Update/UnloadedUpdate) are released so a stale grip can't\n" +
            "freeze the vessel from her perspective.";

        public static bool TryParse(string commandArgs, out string vesselToken, out string agencyToken, out string error)
        {
            vesselToken = string.Empty;
            agencyToken = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                error = "setvesselagency: no arguments supplied.";
                return false;
            }

            var parts = commandArgs.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                error = $"setvesselagency: expected 2 arguments (vessel-guid, agency-token); got {parts.Length}.";
                return false;
            }
            if (parts.Length > 2)
            {
                // Neither LMP player handles nor Guid strings contain spaces — a
                // 3+ token input is almost certainly an operator typo.
                error = $"setvesselagency: expected 2 arguments (vessel-guid, agency-token); got {parts.Length}. Neither vessel guids nor player handles contain spaces.";
                return false;
            }

            vesselToken = parts[0];
            agencyToken = parts[1];

            if (string.IsNullOrEmpty(vesselToken))
            {
                error = "setvesselagency: vessel-guid must be non-empty.";
                return false;
            }
            if (string.IsNullOrEmpty(agencyToken))
            {
                error = "setvesselagency: agency token must be non-empty.";
                return false;
            }

            return true;
        }
    }
}
