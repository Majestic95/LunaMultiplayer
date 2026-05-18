using System;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17e-5 — server-side per-agency science-subject record.
    /// One entry per KSP <c>ScienceSubject</c> (an experiment outcome the player
    /// has completed: "transmitted Crew Report from Kerbin's surface", etc.).
    /// Same shape as <see cref="AgencyTechNodeEntry"/> and
    /// <see cref="AgencyContractEntry"/>: stable Id + decompressed wire payload
    /// + NumBytes for buffer-clamp safety. Stored under SUBJECTS/SUBJECT child
    /// nodes in the per-agency file; spliced into the outgoing R&amp;D scenario
    /// as <c>Science { ... }</c> child entries by <see cref="AgencyScenarioProjector"/>.
    /// </summary>
    public class AgencyScienceSubjectEntry
    {
        /// <summary>KSP <c>ScienceSubject.id</c> (e.g. <c>crewReport@KerbinSrfLandedShores</c>).
        /// Canonical dedup key in <see cref="AgencyState.ScienceSubjects"/>.</summary>
        public string SubjectId { get; set; } = string.Empty;

        /// <summary>Decompressed ConfigNode bytes for the Science entry — the
        /// format <c>ScienceSubjectInfo.Data</c> + <c>NumBytes</c> hands the
        /// server after Lidgren receive. Stored verbatim and re-parsed in the
        /// projector to splice into the R&amp;D scenario.</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>Number of meaningful bytes in <see cref="Data"/>. Always
        /// clamped to <c>Data.Length</c> on read/write.</summary>
        public int NumBytes { get; set; }
    }
}
