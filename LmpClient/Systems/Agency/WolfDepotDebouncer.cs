using LmpCommon.Message.Data.Agency;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// [Phase 4 Slice B-3] Per-depot debounce buffer for Negotiate-driven
    /// resource-stream mutations. Pre-spec §3.e: <c>Depot.NegotiateProvider</c>
    /// + <c>NegotiateConsumer</c> fire at MKS resource-conversion cadence
    /// (every <c>FixedUpdate</c> on a busy depot). Emitting per-tick would
    /// flood the wire — instead, the postfix path enqueues the depot's
    /// latest state here, and at most one batch per
    /// <see cref="FlushIntervalMs"/> milliseconds reaches
    /// <see cref="AgencyWolfDepotSender.SendBatch"/>.
    ///
    /// <para><b>Latest-wins semantics.</b> The dict is keyed by
    /// <c>$"{Body}|{Biome}"</c>; an Enqueue replaces any pending entry under
    /// the same key. The resulting flush carries one entry per touched depot
    /// regardless of how many Negotiate calls fired between flushes —
    /// perfect for the steady-state-rate-update use case.</para>
    ///
    /// <para><b>Where the flush ticks come from.</b> Negotiate fires on
    /// every <c>FixedUpdate</c> on an active depot, so the postfix path is
    /// the natural pulse — <see cref="EnqueueAndMaybeFlush"/> checks the
    /// stopwatch and flushes inline if the interval has elapsed. No
    /// background timer needed; no MonoBehaviour subscription. If Negotiate
    /// stops firing (depot idle), the buffered state stays in the dict
    /// until next Negotiate — acceptable because depot state is steady
    /// when Negotiate isn't firing. Convergence on the trailing-edge of a
    /// converter cycle: a subsequent Establish/Survey postfix (Slice B-2)
    /// reads the same live depot, so its direct SendMutation carries the
    /// final post-Negotiate stream values to the server. The pending
    /// snapshot becomes dominated and the server's idempotent upsert on
    /// <c>(Body, Biome)</c> converges.</para>

    ///
    /// <para><b>2× fan-in per <c>Negotiate(Dictionary, Dictionary)</c> call.</b>
    /// WOLF's high-level <c>Depot.Negotiate</c> entry point (used by
    /// converters) internally calls <c>NegotiateConsumer</c> then
    /// <c>NegotiateProvider</c>, so each high-level negotiation produces
    /// TWO postfix invocations → TWO Enqueues for the same depot. Both
    /// build the same snapshot (the full live depot state including both
    /// Incoming + Outgoing), latest-wins on the dict absorbs the
    /// duplication. Acceptable — the per-call reflection budget is the
    /// same as a single-Negotiate-firing depot.</para>
    ///
    /// <para><b>Threading.</b> Harmony postfixes fire on Unity's main thread
    /// (FixedUpdate). The dict + stopwatch are accessed single-threaded from
    /// the postfix side. <see cref="AgencyWolfDepotSender.SendBatch"/> uses
    /// TaskFactory.StartNew to offload Lidgren serialisation — the offload
    /// reads the snapshot list, not the live dict. <see cref="ConcurrentDictionary"/>
    /// + <see cref="Stopwatch"/> are defensive belt-and-braces.</para>
    ///
    /// <para><b>Scope.</b> Slice B-3 only covers Negotiate-driven mutations.
    /// Create / Establish / Survey postfixes (Slice B-2) bypass the debouncer
    /// and send immediately — they're player-driven low-frequency events
    /// where latency matters more than bandwidth. The debouncer's
    /// latest-wins semantic means a Negotiate-debounced state that arrives
    /// after a direct Establish emit still converges correctly at the
    /// server-side router (idempotent upsert by <c>(Body, Biome)</c>).</para>
    ///
    /// <para><b>Lost-on-disconnect.</b> If the client disconnects between
    /// Enqueue and the next flush, the buffered state is lost — and the
    /// server-side catch-up path (<c>SendWolfDepotCatchupTo</c>) ships
    /// server-authoritative state TO the client on reconnect, not
    /// client-buffered state to the server. So any client-side mutation
    /// that didn't reach the server before disconnect is discarded; on
    /// reconnect the next Negotiate fires the postfix and re-emits the
    /// live depot state, converging the server to current truth.</para>

    ///
    /// <para><b>Agency attribution is also latest-wins at the server side.</b>
    /// The enqueued <see cref="AgencyWolfDepotEntry"/> carries no
    /// AgencyId; <see cref="AgencyWolfDepotSender"/> sets it to
    /// <c>Guid.Empty</c> on the wire, and the server router resolves the
    /// owning agency from <c>AgencySystem.AgencyByPlayerName[PlayerName]</c>
    /// at receive time. If an admin runs <c>/setagency</c> or
    /// <c>/deleteagency</c> for the connected player during a 1s debounce
    /// window, a flush that lands after the admin action attributes the
    /// snapshot to the player's CURRENT agency (or drops it if the agency
    /// is gone), not the agency that was current at Enqueue time. Depots
    /// are <c>(Body, Biome)</c>-keyed (not vessel-keyed), so
    /// <c>setvesselagency</c> does not interact.</para>
    ///
    /// <para><b>Gate.</b> The postfix gate (<c>PerAgencyCareerEnabled</c>
    /// check at the entry point of each Negotiate postfix) prevents
    /// Enqueue from being called under gate=off — the debouncer stays
    /// empty + idle, no wire traffic.
    ///
    /// <b>Mid-session gate-flip + future Flush callers.</b> If an operator
    /// flips the setting <c>true → false</c> mid-session, the postfix gate
    /// stops feeding the dict but the pending snapshots survive. A future
    /// direct Flush caller (e.g. the graceful-disconnect cleanup hinted at
    /// on <see cref="Flush"/>) would emit them under gate=off, violating
    /// strict dual-mode silence — and on a subsequent on-flip, those
    /// pre-flip snapshots would be combined with current entries and
    /// emitted as fresh state. <see cref="Flush"/> defends against both by
    /// re-checking the gate at entry and discarding pending state when it
    /// is off.</para>
    /// </summary>
    public static class WolfDepotDebouncer
    {
        /// <summary>
        /// Flush interval in milliseconds. Pre-spec §3.e calls for "the
        /// simple 'emit on next 1s timer' suffices"; soak can bump the
        /// interval if cohort cadence requires.
        /// </summary>
        public const int FlushIntervalMs = 1000;

        /// <summary>
        /// Pending depot snapshots keyed by <c>$"{Body}|{Biome}"</c>. Latest
        /// Enqueue wins. Cleared on Flush.
        /// </summary>
        private static readonly ConcurrentDictionary<string, AgencyWolfDepotEntry> _pending =
            new ConcurrentDictionary<string, AgencyWolfDepotEntry>();

        private static readonly Stopwatch _sinceLastFlush = Stopwatch.StartNew();

        /// <summary>
        /// Wraps SendBatch for testability — tests inject a no-op sender
        /// so they can pin debounce behaviour without bringing up the
        /// LmpClient network stack.
        /// </summary>
        public static System.Action<IReadOnlyList<AgencyWolfDepotEntry>> SendOverride;

        /// <summary>
        /// Test seam for the gate-state read. Production sets this to a
        /// closure reading <c>SettingsSystem.ServerSettings.PerAgencyCareerEnabled</c>
        /// at <see cref="LmpClient.Base.HarmonyPatcher.Awake"/> via
        /// <see cref="InstallProductionGateResolver"/>; the indirection
        /// exists because <c>LmpClientTest</c> targets <c>net472</c> and
        /// the SettingsSystem type initializer transitively loads
        /// <c>UnityEngine.CoreModule</c>, which the test reference set
        /// cannot resolve. Tests override directly (see
        /// <c>WolfDepotDebouncerTest.ResetBetweenCases</c>). Null means
        /// "treat as gate=off" — defensive default if a future code path
        /// invokes Flush before HarmonyPatcher.Awake runs.
        /// </summary>
        public static System.Func<bool> GateResolver;

        /// <summary>
        /// Stores the latest <paramref name="entry"/> under its
        /// <c>(Body, Biome)</c> key, then flushes if the interval has
        /// elapsed.
        /// </summary>
        public static void EnqueueAndMaybeFlush(AgencyWolfDepotEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Body) || string.IsNullOrEmpty(entry.Biome))
                return;

            var key = entry.Body + "|" + entry.Biome;
            _pending[key] = entry;

            if (_sinceLastFlush.ElapsedMilliseconds >= FlushIntervalMs)
                Flush();
        }

        /// <summary>
        /// Drains the pending dict into a snapshot list, resets the
        /// stopwatch, and dispatches one batch send. Idempotent on empty.
        /// Exposed for test-side immediate-flush + future code that wants
        /// to force-flush (e.g. graceful-disconnect cleanup).
        ///
        /// <para><b>Gate re-check.</b> The postfix call sites already gate
        /// on <see cref="SettingsSystem.ServerSettings.PerAgencyCareerEnabled"/>
        /// before they Enqueue, but direct callers (tests, future
        /// graceful-disconnect cleanup, manual operator-triggered flush)
        /// could land under gate=off with snapshots that were buffered
        /// before a mid-session flip. Re-check here, discard pending state,
        /// and short-circuit — keeps strict dual-mode silence.</para>
        /// </summary>
        public static void Flush()
        {
            var resolver = GateResolver;
            // Defensive: null resolver = "treat as gate=off" (production
            // installs the resolver at HarmonyPatcher.Awake before any
            // postfix can fire; this branch protects a hypothetical future
            // direct-Flush caller that races boot).
            var gateOn = resolver != null && resolver();
            if (!gateOn)
            {
                if (!_pending.IsEmpty)
                    _pending.Clear();
                _sinceLastFlush.Restart();
                return;
            }

            if (_pending.IsEmpty)
            {
                _sinceLastFlush.Restart();
                return;
            }

            // Snapshot + clear under no extra lock — the postfix thread is
            // single-writer, the send is offloaded via TaskFactory and reads
            // the captured snapshot.
            var snapshot = new List<AgencyWolfDepotEntry>(_pending.Count);
            foreach (var kvp in _pending)
            {
                if (kvp.Value != null)
                    snapshot.Add(kvp.Value);
            }
            _pending.Clear();
            _sinceLastFlush.Restart();

            if (snapshot.Count == 0)
                return;

            var send = SendOverride;
            if (send != null)
            {
                send(snapshot);
            }
            else
            {
                AgencyWolfDepotSender.SendBatch(snapshot);
            }
        }

        /// <summary>
        /// Test-only — reset internal state between cases. Called by
        /// LmpClientTest setup hooks. Not part of the production runtime
        /// contract; production state is reset by KSP restart. Public for
        /// LmpClientTest cross-assembly access — LmpClient does not have
        /// <c>InternalsVisibleTo</c> set up.
        /// </summary>
        public static void ResetForTests()
        {
            _pending.Clear();
            _sinceLastFlush.Restart();
            SendOverride = null;
            GateResolver = null;
        }

        /// <summary>
        /// Test-only — count of pending entries. For LmpClientTest
        /// assertions. Not part of the production runtime contract. Public
        /// for LmpClientTest cross-assembly access.
        /// </summary>
        public static int PendingCount => _pending.Count;
    }
}
