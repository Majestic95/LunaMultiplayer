using System;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17e-4 — server-side per-agency tech node record. Stores the
    /// KSP <c>RDTech</c> id plus the decompressed wire payload exactly as
    /// <see cref="LmpCommon.Message.Data.ShareProgress.TechNodeInfo"/> serialised
    /// it. The decompressed form is held so an operator diffing
    /// <c>Universe/Agencies/{guid}.txt</c> can read the actual tech ConfigNode
    /// (compression is a wire-only concern). Same shape pattern as
    /// <see cref="AgencyContractEntry"/>, deliberately — both store raw
    /// KSP-internal payloads bracketed by an id and a length.
    /// </summary>
    public class AgencyTechNodeEntry
    {
        /// <summary>KSP <c>RDTech.techID</c>. Stable across sessions; canonical
        /// dedup key in <see cref="AgencyState.TechNodes"/>.</summary>
        public string TechId { get; set; } = string.Empty;

        /// <summary>Decompressed ConfigNode bytes for the tech entry (the format
        /// the wire's <c>TechNodeInfo.Data</c> + <c>NumBytes</c> hands the server
        /// after Lidgren receive). Persisted Base64-encoded in the agency file
        /// so operators can diff readable text without un-encoding by hand.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>Number of meaningful bytes in <see cref="Data"/>. May be
        /// less than <c>Data.Length</c> if the buffer was over-allocated by
        /// the wire path. Always clamped to <c>Data.Length</c> on read/write.
        /// </summary>
        public int NumBytes { get; set; }
    }
}
