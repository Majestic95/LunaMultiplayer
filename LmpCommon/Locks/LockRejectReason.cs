namespace LmpCommon.Locks
{
    /// <summary>
    /// Wire-protocol enum carrying the reason a server-side lock-acquire was
    /// refused. Stage 5.18d slice (c). Today only the cross-agency reason is
    /// emitted (the Stage 5.17a guard); other rejection paths in
    /// <c>LockSystem.AcquireLock</c> (cross-subspace past per BUG-005/006,
    /// existing-holder conflict, etc.) stay silent as they did pre-5.18d.
    ///
    /// <para><b>Wire-stability rule.</b> Values are part of the on-wire
    /// protocol; never reorder existing entries. New reject reasons APPEND
    /// at the end of this enum and the corresponding handler arm in
    /// <c>LmpClient.Systems.Lock.LockMessageHandler</c> gets a new case (or
    /// is silently dropped if the client predates the addition).</para>
    /// </summary>
    public enum LockRejectReason : byte
    {
        /// <summary>
        /// Sentinel — never sent on the wire. Future hot-path readers can
        /// short-circuit on this value if they want to treat the message as
        /// "no rejection reason supplied" (e.g. a future broadcast on
        /// success).
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Stage 5.17a — the requester's agency does not match the vessel's
        /// <c>OwningAgencyId</c>. The client surfaces a toast (and the owning
        /// agency's display name, when its <c>AgencyInfo</c> is in the
        /// <c>OtherAgencies</c> snapshot) so the player understands the
        /// refusal without needing operator intervention.
        /// </summary>
        CrossAgency = 1,
    }
}
