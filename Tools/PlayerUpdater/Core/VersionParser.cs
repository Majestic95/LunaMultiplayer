using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Parses release tags into VersionMetadata. CANONICAL grammar lives in
    // Scripts/build-release.ps1's Get-LmpVersionMetadata function — if you
    // extend one (e.g. adding an 'rc' channel), extend the other in lockstep
    // or installed PlayerUpdaters will refuse to classify the new tags.
    //
    // Accepted shapes:
    //   v<MAJOR>.<MINOR>.<PATCH>                                  -> channel=stable, revision=null, hotfix=null
    //   v<MAJOR>.<MINOR>.<PATCH>-private-<N>                      -> channel=private, revision=N, hotfix=null
    //   v<MAJOR>.<MINOR>.<PATCH>-private-<N>.<H>                  -> channel=private, revision=N, hotfix=H
    //   v<MAJOR>.<MINOR>.<PATCH>-per-agency-private-<N>           -> channel=per-agency-private, revision=N, hotfix=null
    //   v<MAJOR>.<MINOR>.<PATCH>-per-agency-private-<N>.<H>       -> channel=per-agency-private, revision=N, hotfix=H
    //   v0.0.0-dev | null | empty                                  -> VersionMetadata.Dev sentinel
    //
    // Channel + revision are a SINGLE atomic group: either BOTH are present or
    // NEITHER. A bare '-private' without '-N' is a malformed tag, not a valid
    // pre-revision shape — Parse throws, TryParse returns false.
    //
    // Hotfix is an INNER optional dot-suffix on the revision. Only one hotfix
    // segment is permitted: 'v0.31.0-per-agency-private-8.1' is valid;
    // 'v0.31.0-per-agency-private-8.1.2' is rejected. A bare trailing dot
    // ('v0.31.0-per-agency-private-8.') with no digits is also rejected. The
    // hotfix segment requires a parent revision — there is no stable-release
    // hotfix path ('v0.31.0.1' is not a valid tag).
    //
    // Hotfix-zero ('v0.31.0-per-agency-private-8.0') is REJECTED on purpose:
    // it would tie with the bare '-8' under the planned coalesce-to-zero
    // ordering rule (see VersionMetadata), letting two distinct release tags
    // map to the same ordinal — a footgun for GitHubClient's "pick latest"
    // loop. Operators bumping from '-8' must use '-8.1' (the first hotfix)
    // or '-9' (a fresh revision).
    public static class VersionParser
    {
        private static readonly Regex TagPattern = new(
            @"^v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(-(?<channel>private|per-agency-private)-(?<rev>\d+)(\.(?<hotfix>[1-9]\d*))?)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Throws ArgumentException on a non-dev tag that does not match the grammar.
        // Returns VersionMetadata.Dev for null, empty, whitespace, or the explicit
        // 'v0.0.0-dev' sentinel — these all route to the same local-dev metadata.
        public static VersionMetadata Parse(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || string.Equals(tag, VersionMetadata.DevTag, StringComparison.Ordinal))
            {
                return VersionMetadata.Dev;
            }

            var match = TagPattern.Match(tag);
            if (!match.Success)
            {
                throw new ArgumentException(
                    $"Release tag '{tag}' does not match the expected grammar " +
                    "(v<MAJOR>.<MINOR>.<PATCH>[-private-N[.H]|-per-agency-private-N[.H]]). " +
                    "The grammar is shared with Scripts/build-release.ps1's Get-LmpVersionMetadata — " +
                    "extend both together or installed PlayerUpdaters will refuse to classify new tags.",
                    nameof(tag));
            }

            var hasChannel = match.Groups["channel"].Success;
            var channel = hasChannel ? match.Groups["channel"].Value : VersionMetadata.ChannelStable;
            int? revision = hasChannel
                ? int.Parse(match.Groups["rev"].Value, NumberStyles.None, CultureInvariant)
                : null;
            int? hotfix = match.Groups["hotfix"].Success
                ? int.Parse(match.Groups["hotfix"].Value, NumberStyles.None, CultureInvariant)
                : null;

            return new VersionMetadata(
                Tag: tag,
                Major: int.Parse(match.Groups["major"].Value, NumberStyles.None, CultureInvariant),
                Minor: int.Parse(match.Groups["minor"].Value, NumberStyles.None, CultureInvariant),
                Patch: int.Parse(match.Groups["patch"].Value, NumberStyles.None, CultureInvariant),
                Channel: channel,
                Revision: revision,
                Hotfix: hotfix);
        }

        // Non-throwing variant. Returns true + populated metadata for valid tags
        // (including the dev sentinels); returns false + null for malformed input.
        //
        // Catches widen beyond ArgumentException to cover the int.Parse paths:
        //   - FormatException: non-ASCII digits pass the regex's \d (Unicode-aware)
        //     but fail int.Parse(NumberStyles.None, Invariant). Symmetric with
        //     the PS1 mirror's [int] cast rejection.
        //   - OverflowException: e.g. "v99999999999.0.0" — regex passes, int.Parse
        //     overflows. PS1's [int] cast has the same overflow gap.
        public static bool TryParse(string? tag, out VersionMetadata? metadata)
        {
            try
            {
                metadata = Parse(tag);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                metadata = null;
                return false;
            }
        }

        private static readonly IFormatProvider CultureInvariant = CultureInfo.InvariantCulture;
    }
}
