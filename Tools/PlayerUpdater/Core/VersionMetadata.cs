namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Parsed view of a release tag. Mirrors the hashtable returned by
    // Get-LmpVersionMetadata in Scripts/build-release.ps1 — channel + revision
    // are a single optional group, both present together or neither present.
    //
    // Stable releases: Channel == ChannelStable, Revision == null.
    // Private cohort releases: Channel == ChannelPrivate || ChannelPerAgencyPrivate,
    //                          Revision is a positive int.
    // Local-dev / unreleased: Channel == ChannelDev, Revision == null, Tag == "v0.0.0-dev".
    public sealed record VersionMetadata(
        string Tag,
        int Major,
        int Minor,
        int Patch,
        string Channel,
        int? Revision)
    {
        public const string ChannelStable = "stable";
        public const string ChannelPrivate = "private";
        public const string ChannelPerAgencyPrivate = "per-agency-private";
        public const string ChannelDev = "dev";

        public const string DevTag = "v0.0.0-dev";

        public static VersionMetadata Dev { get; } =
            new(DevTag, 0, 0, 0, ChannelDev, Revision: null);
    }
}
