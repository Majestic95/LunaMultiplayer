using System;

namespace Server.System.Agency
{
    /// <summary>Stage 5.17e-6 — per-agency achievement / progress-tracking
    /// record. Id is the canonical KSP progress node name (e.g.
    /// <c>Kerbin/RocketLaunch</c>, <c>FirstLaunch</c>) — used as both the
    /// dedup key in <see cref="AgencyState.Achievements"/> AND the ConfigNode
    /// name when the projector splices the entry back into the
    /// <c>ProgressTracking</c> scenario's <c>Progress</c> child block.</summary>
    public class AgencyAchievementEntry
    {
        public string Id { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int NumBytes { get; set; }
    }
}
