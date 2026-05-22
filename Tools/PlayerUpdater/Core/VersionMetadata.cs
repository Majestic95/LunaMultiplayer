namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Parsed view of a release tag. Mirrors the hashtable returned by
    // Get-LmpVersionMetadata in Scripts/build-release.ps1 — channel + revision
    // are a single optional group, both present together or neither present.
    // Hotfix is an OPTIONAL dot-suffix on the revision ('-private-8.1') and
    // is null when absent — persisted as null on the wire so a re-emitted tag
    // round-trips byte-equal.
    //
    // Stable releases: Channel == ChannelStable, Revision == null, Hotfix == null.
    // Private cohort releases: Channel == ChannelPrivate || ChannelPerAgencyPrivate,
    //                          Revision is a positive int, Hotfix is null or a
    //                          positive int (hotfix-zero is rejected at parse
    //                          time — see VersionParser).
    // Local-dev / unreleased: Channel == ChannelDev, Revision == null, Hotfix == null,
    //                        Tag == "v0.0.0-dev".
    //
    // Ordering contract (to be implemented in Core sub-slice 3's GitHubClient
    // when it picks the "latest" release): lexicographic on
    //   (Major, Minor, Patch, ChannelRank(Channel), Revision ?? 0, Hotfix ?? 0)
    // where ChannelRank is a deterministic ordering across channels and the
    // ?? 0 coalesce treats null-Hotfix as equivalent to hotfix-zero. This is
    // SAFE only because the parser rejects an explicit '.0' hotfix segment —
    // otherwise '-8' and '-8.0' would map to the same ordinal.
    public sealed record VersionMetadata(
        string Tag,
        int Major,
        int Minor,
        int Patch,
        string Channel,
        int? Revision,
        int? Hotfix)
    {
        public const string ChannelStable = "stable";
        public const string ChannelPrivate = "private";
        public const string ChannelPerAgencyPrivate = "per-agency-private";
        public const string ChannelDev = "dev";

        public const string DevTag = "v0.0.0-dev";

        public static VersionMetadata Dev { get; } =
            new(DevTag, 0, 0, 0, ChannelDev, Revision: null, Hotfix: null);
    }
}
