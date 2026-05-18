using System;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// Pure decision helpers for per-agency client-side filtering. Lives outside
    /// <see cref="AgencySystem"/> so the call sites can be unit-tested without spinning
    /// up the KSP-bound singleton (KSP DLLs aren't loaded in <c>LmpClientTest</c>).
    /// Same pattern as <c>VesselPositionUpdate.ComputeMaxInterpolationDuration</c>
    /// (BUG-003/004) and <c>WarpSystem.ShouldSteadyStateRetry</c> (BUG-051b): the
    /// instance call site becomes a one-line delegate; the decision math lives here.
    /// </summary>
    public static class AgencyMembership
    {
        /// <summary>
        /// Defensive filter for owner-only S→C messages (<c>AgencyStateMsgData</c>,
        /// <c>AgencyContractMsgData</c>). Returns true only when the inbound agency id
        /// matches the local player's assigned agency. The server's per-agency router
        /// is the primary contract (it only ever <c>SendToClient</c>s these to the
        /// owner), so this filter is defence-in-depth against a misrouted send / wire
        /// corruption / future server-side regression.
        ///
        /// <para><b>Empty-sentinel handling.</b> <see cref="Guid.Empty"/> is the
        /// Unassigned-vessel sentinel (spec §10 Q3) — it MUST NOT be treated as a
        /// valid agency match. The empty/empty case (handler called before Handshake
        /// arrived) returns false so we drop instead of applying a State carrying
        /// zeroed scalars.</para>
        ///
        /// <para><b>Note: not gated on PerAgencyCareerEnabled.</b> Under the server's
        /// dual-mode silence contract, no Agency*MsgData will arrive when the gate is
        /// off. If one does arrive (e.g. peer talking a future protocol version), the
        /// localAgencyId will be <see cref="Guid.Empty"/> because the client never
        /// processed an <c>AgencyHandshakeMsgData</c>, and this method returns false.
        /// Same observable outcome as an explicit gate check, with less coupling.</para>
        /// </summary>
        public static bool IsForLocalAgency(Guid localAgencyId, Guid incomingAgencyId)
        {
            if (localAgencyId == Guid.Empty) return false;
            if (incomingAgencyId == Guid.Empty) return false;
            return localAgencyId == incomingAgencyId;
        }
    }
}
