using System;

namespace Server.System.Agency
{
    /// <summary>
    /// Server-side per-agency contract record. Lives inside <see cref="AgencyState.Contracts"/>
    /// and is the persistent counterpart to the wire <see cref="LmpCommon.Message.Data.ShareProgress.ContractInfo"/>
    /// that Stage 5.17d's <c>AgencyContractRouter</c> emits.
    ///
    /// **Persisted form (in <c>AgencyState.txt</c>):** one <c>CONTRACT</c> sub-node per
    /// entry under the parent <c>CONTRACTS</c> node, each carrying:
    /// <list type="bullet">
    ///   <item><c>Guid</c> — stable contract id used for upsert.</item>
    ///   <item><c>State</c> — KSP <c>Contract.State</c> serialized name (e.g. "Active",
    ///        "Completed", "Failed", "Cancelled", "DeadlineExpired", "Withdrawn").</item>
    ///   <item><c>Data</c> — Base64-encoded decompressed contract ConfigNode bytes. The
    ///        decompressed form is stored so the persisted file can be inspected /
    ///        diffed; the wire echo path re-compresses at serialize time.</item>
    /// </list>
    ///
    /// **Spec §2 Q6 commitments enforced via the router, not this class:**
    /// <list type="bullet">
    ///   <item>(a) No Offered persistence — the router classifies Offered out before
    ///        reaching this storage; only post-Accept states land here.</item>
    ///   <item>(b) Per-contract exception isolation — the router try/catches per entry
    ///        so a malformed payload doesn't abort the batch.</item>
    ///   <item>(c) <c>ContractPreLoader</c> untouched — this storage is orthogonal to
    ///        the shared scenario; CC's parallel ScenarioModule path is undisturbed.</item>
    /// </list>
    /// </summary>
    public class AgencyContractEntry
    {
        public Guid ContractGuid { get; set; }
        public string State { get; set; } = string.Empty;

        /// <summary>
        /// Decompressed contract ConfigNode bytes. Length invariant: after
        /// <see cref="AgencyContractRouter.Upsert"/> stores an entry, <c>Data.Length ==
        /// NumBytes</c> (the router defensive-copies into a freshly-sized array; the
        /// wire <see cref="LmpCommon.Message.Data.ShareProgress.ContractInfo"/> buffer
        /// is never aliased into <see cref="AgencyState.Contracts"/>). Same invariant
        /// after <see cref="AgencyState.FromConfigNode"/> reads the entry from disk
        /// (<see cref="Convert.FromBase64String"/> returns exactly-sized output).
        ///
        /// Stored as DECOMPRESSED bytes. The wire layer
        /// (<c>LmpCommon.Message.Data.ShareProgress.ContractInfo.Serialize</c>) compresses
        /// at send time and decompresses at receive time via
        /// <c>Common.ThreadSafeCompress</c> / <c>ThreadSafeDecompress</c>; the server-side
        /// store holds the inbound (decompressed) form, so on echo the wire layer will
        /// re-compress (idempotent on a freshly-decompressed payload). Stage 5.18a client
        /// authors: by the time your <c>AgencyContractMsgData</c> handler runs, Lidgren's
        /// <c>ContractInfo.Deserialize</c> has already decompressed — read
        /// <c>Data[0..NumBytes]</c> as raw ConfigNode bytes.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int NumBytes { get; set; }
    }
}
