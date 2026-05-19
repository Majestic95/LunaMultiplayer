using LmpCommon.Message.Data.ShareProgress;
using Server.Client;
using Server.Log;
using System;

namespace Server.System.Agency
{
    /// <summary>
    /// Stage 5.17e-3 — per-agency routing for the three career scalars
    /// (Funds / Science / Reputation). Sits between the
    /// <c>Share{Funds,Science,Reputation}System.*Received</c> handlers and the
    /// existing shared-scenario broadcast+write path. When
    /// <see cref="AgencySystem.PerAgencyEnabled"/> is true and the sender has a
    /// registered agency, the router:
    /// <list type="number">
    ///   <item>Resolves the sender's <see cref="AgencyState"/>.</item>
    ///   <item>Acquires <see cref="AgencySystem.GetAgencyLock"/> for that agency.</item>
    ///   <item>Writes the new scalar onto <see cref="AgencyState.Funds"/> /
    ///        <see cref="AgencyState.Science"/> / <see cref="AgencyState.Reputation"/>.</item>
    ///   <item>Persists via <see cref="AgencySystem.SaveAgency"/> under the same lock.</item>
    ///   <item>Echoes the full <see cref="AgencyState"/> snapshot back to the OWNER only
    ///        via <see cref="AgencySystemSender.SendStateTo"/> (privacy rule spec §10 Q1
    ///        — peers never receive another agency's scalars).</item>
    /// </list>
    /// Returns <c>true</c> when this method handled the inbound — caller must NOT then
    /// run the shared-scenario relay/write path. Returns <c>false</c> when the gate is
    /// off (gate-off OR non-Career game mode), the client lacks an agency mapping
    /// (defensive — should not happen post-HandshakeSystem auth under the gate, but
    /// surfacing a leak via the shared path is worse than running the legacy code
    /// behind a documented condition), or the registry entry is missing.
    ///
    /// **This is the write-path counterpart to <see cref="AgencyScenarioProjector"/>'s
    /// read-side scalar substitution.** Together they close the per-agency Funds /
    /// Science / Reputation loop:
    /// <list type="bullet">
    ///   <item>READ at handshake / scene-load: projector rewrites the wire bytes
    ///        with the requesting client's stored values.</item>
    ///   <item>WRITE mid-session: router intercepts the Share* update, applies to
    ///        the sender's <see cref="AgencyState"/>, persists, echoes owner-only.</item>
    /// </list>
    /// Before 5.17e-3, mid-session writes (`Funding.Instance.AddFunds`,
    /// `Reputation.Instance.AddReputation`, etc.) flowed through
    /// <see cref="ShareFundsSystem.FundsReceived"/> and friends to the SHARED
    /// scenario AND broadcast to every peer — cross-agency leak (peers' KSP totals
    /// silently clobbered) AND silent divergence (next scene-load projection
    /// rewrote the wire back to the seeded starting value since
    /// <see cref="AgencyState"/> was never updated).
    ///
    /// **Echo channel.** Reuses the existing <see cref="AgencyStateMsgData"/> wire
    /// type rather than introducing per-field delta messages (spec §4 listed
    /// `Agency{Funds,Science,Reputation}MutateMsgData` as deferred — they remain
    /// deferred). The full snapshot echo is two extra doubles vs a delta and the
    /// client-side handler shape is uniform across all three resources, which keeps
    /// the Stage 5.18a client mirror simpler. If telemetry shows the snapshot cost
    /// is non-trivial at scale, splitting to per-field deltas is a future refinement.
    ///
    /// **I/O cadence trade-off (round-1 upgrade-lens review).** Each routed
    /// mutation calls <see cref="AgencySystem.SaveAgency"/> synchronously, which
    /// performs an atomic <see cref="FileHandler.WriteAtomic"/> (rotate +
    /// rename). The shared-agency baseline writes to in-memory
    /// <see cref="Server.System.ScenarioStoreSystem.CurrentScenarios"/> and
    /// amortises the disk flush via <see cref="Server.System.BackupSystem.RunBackup"/>'s
    /// periodic pass, so the per-agency path is more I/O per mutation. Under a
    /// fleet-recovery burst (one Funds write per vessel recovered) this is N
    /// atomic writes back-to-back per active agency. Acceptable for the v1 small-
    /// cohort soak (single-digit clients, low-frequency mutations); revisit with
    /// an async or coalesced write path if telemetry shows the inbound-thread
    /// fsync wait becomes a bottleneck. The synchronous write is a deliberate
    /// safety choice — durability before performance — given the spec §3 atomic-
    /// write requirement and the player-visible cost of a lost mutation.
    ///
    /// **Client-side feedback-loop hazard (BUG-025 precedent, deferred to 5.18a).**
    /// The future Stage 5.18a client handler for <see cref="AgencyStateMsgData"/>
    /// will apply Funds / Science / Reputation to <c>Funding.Instance</c> /
    /// <c>ResearchAndDevelopment.Instance</c> / <c>Reputation.Instance</c>. KSP's
    /// <c>OnFundsChanged</c> et al. events will fire, triggering the local
    /// <c>Share*Sender</c> to send another <see cref="ShareProgressFundsMsgData"/> /
    /// etc. back to the server — feedback loop. The handler MUST bracket the apply
    /// with <c>Share*System.Singleton.StartIgnoringEvents() /
    /// StopIgnoringEvents()</c>, same shape as the BUG-025 v2 refund path. Until
    /// 5.18a lands, unmodified clients have no AgencyStateMsgData handler so the
    /// echo drops silently — no loop today.
    /// </summary>
    public static class AgencyCurrencyRouter
    {
        /// <summary>Per-agency Funds routing. See <see cref="AgencyCurrencyRouter"/>
        /// XML for the design + locking + privacy contract.</summary>
        public static bool TryRouteFunds(ClientStructure client, ShareProgressFundsMsgData msg)
        {
            if (!TryResolveAgency(client, msg, out var agency))
                return false;

            if (RejectIfNonFinite(client, agency, "Funds", msg.Funds))
                return true;

            // [Round-1 review] SendStateTo is INSIDE the lock so the snapshot it
            // builds (a re-read of agency.Funds/Science/Reputation) cannot race a
            // concurrent router invocation for the same agency. Both reviewers
            // caught this independently. MessageQueuer.SendToClient just enqueues
            // on a ConcurrentQueue — no network I/O blocks the lock window.
            lock (AgencySystem.GetAgencyLock(agency.AgencyId))
            {
                agency.Funds = msg.Funds;
                AgencySystem.SaveAgency(agency.AgencyId);
                AgencySystemSender.SendStateTo(client, agency);
            }

            LunaLog.Debug($"[fix:per-agency-career] Routed Funds={msg.Funds} (reason={msg.Reason}) to agency {agency.AgencyId:N} for {client.PlayerName}");
            return true;
        }

        /// <summary>Per-agency Science routing. See <see cref="AgencyCurrencyRouter"/>
        /// XML for the design + locking + privacy contract.</summary>
        public static bool TryRouteScience(ClientStructure client, ShareProgressScienceMsgData msg)
        {
            if (!TryResolveAgency(client, msg, out var agency))
                return false;

            if (RejectIfNonFinite(client, agency, "Science", msg.Science))
                return true;

            lock (AgencySystem.GetAgencyLock(agency.AgencyId))
            {
                agency.Science = msg.Science;
                AgencySystem.SaveAgency(agency.AgencyId);
                AgencySystemSender.SendStateTo(client, agency);
            }

            LunaLog.Debug($"[fix:per-agency-career] Routed Science={msg.Science} (reason={msg.Reason}) to agency {agency.AgencyId:N} for {client.PlayerName}");
            return true;
        }

        /// <summary>Per-agency Reputation routing. See <see cref="AgencyCurrencyRouter"/>
        /// XML for the design + locking + privacy contract.</summary>
        public static bool TryRouteReputation(ClientStructure client, ShareProgressReputationMsgData msg)
        {
            if (!TryResolveAgency(client, msg, out var agency))
                return false;

            if (RejectIfNonFinite(client, agency, "Reputation", msg.Reputation))
                return true;

            lock (AgencySystem.GetAgencyLock(agency.AgencyId))
            {
                agency.Reputation = msg.Reputation;
                AgencySystem.SaveAgency(agency.AgencyId);
                AgencySystemSender.SendStateTo(client, agency);
            }

            LunaLog.Debug($"[fix:per-agency-career] Routed Reputation={msg.Reputation} (reason={msg.Reason}) to agency {agency.AgencyId:N} for {client.PlayerName}");
            return true;
        }

        /// <summary>
        /// Stage 5.18g — defends per-agency persisted scalars (Funds / Science /
        /// Reputation) against wire corruption + cheat clients that send NaN or
        /// ±Infinity. Returns <c>true</c> when the value is non-finite — caller
        /// should return <c>true</c> from <c>TryRoute*</c> so the legacy
        /// shared-agency path doesn't run (otherwise the same NaN flows to
        /// <c>Funding.Instance</c> et al). The unchanged authoritative state is
        /// echoed back to the sender via <see cref="AgencySystemSender.SendStateTo"/>
        /// so their local <c>Funding.Instance</c> snaps back on receipt — the
        /// 5.18a client handler applies the echo with the
        /// <c>StartIgnoringEvents</c> bracket so no feedback loop fires.
        ///
        /// <para><b>Minimum-scope policy (operator-confirmed 2026-05-19).</b>
        /// Finite values pass through unchanged. KSP careers + CC + KCT can
        /// legitimately mutate scalars in wide ranges (vessel-recovery bounties,
        /// large CC rewards, strategy commits), so no upper cap is applied —
        /// <c>double.MaxValue</c> is a known limitation. A future hardening slice
        /// could add a configurable <c>MaxAbsAgencyValue</c> setting; not in 5.18g.</para>
        ///
        /// <para><b>Per-agency design.</b> Runs only inside the
        /// <see cref="AgencySystem.PerAgencyEnabled"/>-gated path
        /// (<see cref="TryResolveAgency"/> returns false otherwise). Under gate=off
        /// the legacy shared-agency path is untouched — no observable behaviour
        /// change.</para>
        /// </summary>
        private static bool RejectIfNonFinite(ClientStructure client, AgencyState agency, string fieldName, double value)
        {
            if (!IsNonFinite(value))
                return false;

            LunaLog.Warning(
                $"[fix:per-agency-career] Rejected non-finite {fieldName}={value} from {client.PlayerName} (agency {agency.AgencyId:N}); echoing authoritative state");
            lock (AgencySystem.GetAgencyLock(agency.AgencyId))
            {
                AgencySystemSender.SendStateTo(client, agency);
            }
            return true;
        }

        /// <summary>
        /// Stage 5.18g — pure check exposing the NaN/±Infinity decision. Returns
        /// <c>true</c> iff <paramref name="value"/> is <see cref="double.IsNaN"/> or
        /// <see cref="double.IsInfinity"/>. Internal — exposed to <c>ServerTest</c>
        /// via <c>InternalsVisibleTo</c> so the validation surface can be pinned
        /// without bringing up the wire harness. <c>double.MaxValue</c>,
        /// <c>double.MinValue</c>, and finite negative values all return
        /// <c>false</c> (no upper cap per the minimum-scope policy).
        /// </summary>
        internal static bool IsNonFinite(double value) =>
            double.IsNaN(value) || double.IsInfinity(value);

        /// <summary>
        /// Common entry-validation for all three routers. Returns <c>true</c> with
        /// <paramref name="agency"/> set when the inbound is eligible for per-agency
        /// routing; <c>false</c> when the caller must fall through to the existing
        /// shared-agency path. Bail conditions:
        /// <list type="bullet">
        ///   <item><see cref="AgencySystem.PerAgencyEnabled"/> is false (dual-mode
        ///        silence — gate off OR non-Career game mode).</item>
        ///   <item>Client / message null, or empty player name (defensive).</item>
        ///   <item>No agency mapped for the sender (HandshakeSystem auto-registers under
        ///        the gate, so this is unexpected; we fall through to the shared path
        ///        rather than NRE on the registry miss, which preserves at-least-some
        ///        behaviour for the affected player at the cost of a one-shot leak —
        ///        operator sees the path via the existing missing-agency log lines).</item>
        ///   <item>Agency Guid resolved but not in <see cref="AgencySystem.Agencies"/>
        ///        (mid-session disconnect/cleanup race — same fall-through semantics).</item>
        /// </list>
        /// </summary>
        private static bool TryResolveAgency(ClientStructure client, object msg, out AgencyState agency)
        {
            agency = null;
            if (!AgencySystem.PerAgencyEnabled)
                return false;
            if (client == null || string.IsNullOrEmpty(client.PlayerName) || msg == null)
                return false;
            if (!AgencySystem.AgencyByPlayerName.TryGetValue(client.PlayerName, out var agencyId))
                return false;
            return AgencySystem.Agencies.TryGetValue(agencyId, out agency);
        }
    }
}
