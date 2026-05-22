using LmpClient.Systems.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;

namespace LmpClientTest
{
    /// <summary>
    /// Stage 5.18a / per-agency client mirror. Pins the defensive-filter helper used
    /// by <c>AgencyMessageHandler.HandleState</c> and <c>HandleContract</c>.
    ///
    /// The server is the primary contract for privacy (spec §10 Q1
    /// <c>PrivateAgencyResources=true</c> — owner-only sends are routed by the
    /// server's <c>AgencyCurrencyRouter</c> / <c>AgencyContractRouter</c>). This
    /// filter is defence-in-depth against three failure modes:
    /// <list type="bullet">
    ///   <item>Future server-side regression that misroutes an owner-only message.</item>
    ///   <item>Wire corruption (multi-byte AgencyId field) producing a foreign id.</item>
    ///   <item>A peer talking a future protocol version that ships agency wire
    ///         before the client's local handshake completes.</item>
    /// </list>
    /// Without the filter, applying a foreign agency's scalars to the local KSP
    /// singletons would corrupt the local player's career state. Without rejecting
    /// the empty/empty case, a State arriving before the local Handshake completed
    /// would zero out the local career.
    /// </summary>
    [TestClass]
    public class AgencyMembershipDecisionTest
    {
        [TestMethod]
        public void IsForLocalAgency_Matching_Ids_ReturnsTrue()
        {
            var id = Guid.NewGuid();
            Assert.IsTrue(AgencyMembership.IsForLocalAgency(id, id));
        }

        [TestMethod]
        public void IsForLocalAgency_DifferentIds_ReturnsFalse()
        {
            var local = Guid.NewGuid();
            var foreign = Guid.NewGuid();
            Assert.IsFalse(AgencyMembership.IsForLocalAgency(local, foreign));
        }

        [TestMethod]
        public void IsForLocalAgency_LocalEmpty_ReturnsFalse()
        {
            // Pre-Handshake state: LocalAgencyId stays at Guid.Empty until
            // AgencyHandshakeMsgData arrives. A State arriving in that window must
            // be dropped — accepting it would set DisplayName/OwningPlayerName from
            // the message but leave LocalAgencyId empty, so subsequent State messages
            // would also be dropped (loss of the canonical id source) and the
            // identity tracking would be permanently inconsistent.
            Assert.IsFalse(AgencyMembership.IsForLocalAgency(Guid.Empty, Guid.NewGuid()));
        }

        [TestMethod]
        public void IsForLocalAgency_IncomingEmpty_ReturnsFalse()
        {
            // Empty incoming id is the Unassigned-vessel sentinel (spec §10 Q3) —
            // it identifies a vessel with no owner, NOT an agency. A State message
            // with AgencyId=Empty cannot legitimately arrive (the server's router
            // always sends per-real-agency); if it does, drop.
            Assert.IsFalse(AgencyMembership.IsForLocalAgency(Guid.NewGuid(), Guid.Empty));
        }

        [TestMethod]
        public void IsForLocalAgency_BothEmpty_ReturnsFalse()
        {
            // The most common pre-handshake / dual-mode-disabled state. Must drop
            // so we don't treat "no agency yet" as a valid match.
            Assert.IsFalse(AgencyMembership.IsForLocalAgency(Guid.Empty, Guid.Empty));
        }

        // --- TryParseAgencyId — Stage 5.18b ----------------------------------
        //
        // Backs VesselProto.CreateProtoVessel's wire-side lmpOwningAgency extraction
        // into AgencySystem.VesselOwnership. Maps every failure mode to Guid.Empty
        // (the Unassigned sentinel — spec §10 Q3) so the registry never holds a
        // surprise non-Guid value; consumers (5.18c UI / 5.18d economy guards)
        // distinguish "unknown" from "Unassigned" via dict presence, NOT via the
        // parsed value. These cases pin the failure surface.

        [TestMethod]
        public void TryParseAgencyId_Null_ReturnsEmpty()
        {
            // ConfigNode.GetValue returns null when the field is absent — the
            // common case for pre-0.31 vessels and for server-side-unstamped new
            // vessels under PerAgencyCareer=false. Must not NRE.
            Assert.AreEqual(Guid.Empty, AgencyMembership.TryParseAgencyId(null));
        }

        [TestMethod]
        public void TryParseAgencyId_EmptyString_ReturnsEmpty()
        {
            // A literal empty string in the ConfigNode would mean the field was
            // serialized but with no value — wire corruption or a server bug.
            // Treat the same as absent.
            Assert.AreEqual(Guid.Empty, AgencyMembership.TryParseAgencyId(string.Empty));
        }

        [TestMethod]
        public void TryParseAgencyId_ValidGuid_ReturnsParsed()
        {
            var id = Guid.NewGuid();
            // Guid.ToString("D") is the canonical hyphenated form the server emits
            // via Guid.ToString() in AgencySystem persistence + wire serialization.
            Assert.AreEqual(id, AgencyMembership.TryParseAgencyId(id.ToString()));
        }

        [TestMethod]
        public void TryParseAgencyId_InvalidString_ReturnsEmpty()
        {
            // Wire corruption / future schema mismatch — must not throw. The
            // registry just records Unassigned and consumers handle that case
            // gracefully (vessel becomes interactable by any agency until the
            // operator runs transferagency, per spec §10 Q3).
            Assert.AreEqual(Guid.Empty, AgencyMembership.TryParseAgencyId("not-a-guid"));
            Assert.AreEqual(Guid.Empty, AgencyMembership.TryParseAgencyId("xyz"));
        }

        [TestMethod]
        public void TryParseAgencyId_EmptyGuidString_ReturnsEmpty()
        {
            // The Unassigned sentinel itself, emitted by the server's branch (c) of
            // VesselDataUpdater first-stamp logic (genuinely new vessel + sender
            // has no agency) and by pre-0.31 universes after the spec §10 Q3 sticky
            // sentinel rule. Round-trips cleanly through the parser.
            Assert.AreEqual(Guid.Empty, AgencyMembership.TryParseAgencyId(Guid.Empty.ToString()));
        }

        [TestMethod]
        public void TryParseAgencyId_NFormatString_ReturnsParsed()
        {
            // Server-side AgencyState persistence + wire serialization emits Guids
            // via Guid.ToString("N") (32-char hex, no hyphens) in places — see
            // Server/System/Vessel/Classes/Vessel.cs:108. Guid.TryParse accepts
            // both "D" and "N" forms; this case pins the actual wire shape so a
            // future change to TryParse's tolerance regresses loudly.
            var id = Guid.NewGuid();
            Assert.AreEqual(id, AgencyMembership.TryParseAgencyId(id.ToString("N")));
        }

        // --- RecordOwnership — Stage 5.18b relay-safety preservation rule ---
        //
        // The server-side comment at Server/Message/VesselMsgReader.cs:188-198
        // warns that the relay path forwards the original sender wire bytes, which
        // KSP's BackupVessel/protoVessel.Save strips of the unknown lmpOwningAgency
        // field on every local-owner resend. Without preservation, every periodic
        // drift-correction resend from a vessel's owner would clobber peer-side
        // ownership with Empty (Unassigned). These cases pin the preservation rule
        // that VesselProto.CreateProtoVessel relies on.

        [TestMethod]
        public void RecordOwnership_NonEmpty_NoPrior_Inserts()
        {
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var agencyId = Guid.NewGuid();

            AgencyMembership.RecordOwnership(registry, vesselId, agencyId);

            Assert.IsTrue(registry.TryGetValue(vesselId, out var stored));
            Assert.AreEqual(agencyId, stored);
        }

        [TestMethod]
        public void RecordOwnership_NonEmpty_OverwritesPriorReal()
        {
            // Authoritative wire (VesselSync reply, future AgencyVisibilityMsgData,
            // or genuinely-new vessel via relay) with a real agency id overwrites
            // any prior real agency id. The future Stage 5.18d transferagency
            // admin flow relies on this — it moves a vessel from agency A to
            // agency B, and the new claim must propagate to peer registries.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var oldAgency = Guid.NewGuid();
            var newAgency = Guid.NewGuid();
            registry[vesselId] = oldAgency;

            AgencyMembership.RecordOwnership(registry, vesselId, newAgency);

            Assert.AreEqual(newAgency, registry[vesselId]);
        }

        [TestMethod]
        public void RecordOwnership_Empty_NoPrior_InsertsEmpty()
        {
            // First-sight Unassigned vessel (pre-0.31 sentinel from spec §10 Q3,
            // or server's branch (c) for a genuinely-new vessel whose sender has
            // no agency). The Empty must be recorded so consumers can distinguish
            // "Unassigned" (dict hit with value Empty) from "unknown" (dict miss).
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();

            AgencyMembership.RecordOwnership(registry, vesselId, Guid.Empty);

            Assert.IsTrue(registry.TryGetValue(vesselId, out var stored));
            Assert.AreEqual(Guid.Empty, stored);
        }

        [TestMethod]
        public void RecordOwnership_Empty_WithPriorReal_PreservesPrior()
        {
            // THE BUG FIX. Every local-owner resend through the relay path arrives
            // at peer clients with no lmpOwningAgency (KSP stripped it). Parsing
            // returns Empty. Without preservation, this Empty would overwrite the
            // peer's previously-recorded real agency id, corrupting Stage 5.18c UI
            // labels and Stage 5.18d economy guards. The fix: never downgrade a
            // real id to Empty via this path — keep the prior value intact.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var realAgency = Guid.NewGuid();
            registry[vesselId] = realAgency;

            AgencyMembership.RecordOwnership(registry, vesselId, Guid.Empty);

            Assert.AreEqual(realAgency, registry[vesselId],
                "Empty incoming must NOT downgrade a prior real agency id (relay-safety preservation rule).");
        }

        [TestMethod]
        public void RecordOwnership_Empty_WithPriorEmpty_Idempotent()
        {
            // A pre-0.31 Unassigned vessel that keeps receiving relay-stripped
            // protos (which also parse to Empty). The registry stays at Empty
            // idempotently — no churn, no spurious "value changed" signal that
            // future consumers might subscribe to.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            registry[vesselId] = Guid.Empty;

            AgencyMembership.RecordOwnership(registry, vesselId, Guid.Empty);

            Assert.AreEqual(Guid.Empty, registry[vesselId]);
        }

        [TestMethod]
        public void RecordOwnership_NullRegistry_NoThrow()
        {
            // Defensive: VesselProto.CreateProtoVessel reads AgencySystem.Singleton?.
            // VesselOwnership which is non-null in practice but the call site uses
            // null-conditional access for symmetry with other Singleton lookups.
            // RecordOwnership must tolerate the null to keep the call site simple.
            AgencyMembership.RecordOwnership(null, Guid.NewGuid(), Guid.NewGuid());
            // No assertion needed — passing without exception is the contract.
        }

        // --- ForceRecordOwnership — Stage 5.18d authoritative-write bypass ---
        //
        // Companion to RecordOwnership. Bypasses the relay-safety preservation rule
        // for authoritative server-pushed mutations (transferagency X→Y push;
        // deleteagency cascade demoting vessels to Unassigned). Callers MUST route
        // only authoritative values through this method — never relay-path values
        // — or peer registries will be corrupted by relay-stripped Empty values
        // overwriting real agency ids. The choice between RecordOwnership and
        // ForceRecordOwnership is an explicit call-site decision; these cases pin
        // the differentiated behaviour.

        [TestMethod]
        public void ForceRecordOwnership_NonEmpty_NoPrior_Inserts()
        {
            // Common authoritative path: 5.18c AgencyVisibilityMsgData arrives for
            // a vessel the client hasn't seen yet. Force-insert; same outcome as
            // RecordOwnership for this case.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var agencyId = Guid.NewGuid();

            AgencyMembership.ForceRecordOwnership(registry, vesselId, agencyId);

            Assert.IsTrue(registry.TryGetValue(vesselId, out var stored));
            Assert.AreEqual(agencyId, stored);
        }

        [TestMethod]
        public void ForceRecordOwnership_NonEmpty_OverwritesPriorReal()
        {
            // Transferagency X→Y mid-session: authoritative server push of the new
            // owner Y replaces the prior owner X in the local registry. Indexer
            // semantics handle the upsert.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var oldAgency = Guid.NewGuid();
            var newAgency = Guid.NewGuid();
            registry[vesselId] = oldAgency;

            AgencyMembership.ForceRecordOwnership(registry, vesselId, newAgency);

            Assert.AreEqual(newAgency, registry[vesselId]);
        }

        [TestMethod]
        public void ForceRecordOwnership_Empty_DowngradesPriorReal()
        {
            // THE BYPASS — differentiates ForceRecordOwnership from RecordOwnership.
            // Deleteagency cascade: the deleted agency's vessels are authoritatively
            // demoted to Guid.Empty (Unassigned sentinel) on the server, and the
            // 5.18c AgencyVisibilityMsgData push carries Empty as the new owner.
            // The peer client's registry MUST update — RecordOwnership's
            // preservation rule would absorb this write and leave the stale
            // "owned by deleted agency" stamp in place. ForceRecordOwnership
            // exists precisely to make this demotion land.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var realAgency = Guid.NewGuid();
            registry[vesselId] = realAgency;

            AgencyMembership.ForceRecordOwnership(registry, vesselId, Guid.Empty);

            Assert.AreEqual(Guid.Empty, registry[vesselId],
                "ForceRecordOwnership MUST overwrite a prior real agency id with Empty " +
                "(authoritative demotion to Unassigned — deleteagency cascade).");
        }

        [TestMethod]
        public void ForceRecordOwnership_Empty_NoPrior_InsertsEmpty()
        {
            // Authoritative push for a vessel the client hasn't seen yet, carrying
            // Empty as the canonical state (e.g. a freshly-spawned Unassigned vessel
            // announced via a future visibility push). Same outcome as
            // RecordOwnership for this case; included for symmetry.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();

            AgencyMembership.ForceRecordOwnership(registry, vesselId, Guid.Empty);

            Assert.IsTrue(registry.TryGetValue(vesselId, out var stored));
            Assert.AreEqual(Guid.Empty, stored);
        }

        [TestMethod]
        public void ForceRecordOwnership_NullRegistry_NoThrow()
        {
            // Symmetric defensive contract with RecordOwnership. The 5.18d call
            // sites (AgencyVisibilityMsgData handler) will use
            // AgencySystem.Singleton?.VesselOwnership; the helper tolerates the
            // null so the call site stays a one-liner.
            AgencyMembership.ForceRecordOwnership(null, Guid.NewGuid(), Guid.NewGuid());
        }

        // --- IsRecoveryBlockedByAgency — Stage 5.18d slice (h) economy guard ---
        //
        // Pure decision helper that backs VesselRemoveEvents.OnVesselRecovered +
        // OnVesselTerminated's cross-agency block. The bypass shape matches
        // 5.17a's cross-agency lock guard (gate off / local agency-less /
        // vessel unknown / Unassigned sentinel / same-agency); only the
        // gate-on-known-different-agency case blocks.

        [TestMethod]
        public void IsRecoveryBlockedByAgency_DifferentAgency_Blocks()
        {
            var local = Guid.NewGuid();
            var other = Guid.NewGuid();
            Assert.IsTrue(AgencyMembership.IsRecoveryBlockedByAgency(
                localAgencyId: local,
                vesselKnown: true,
                vesselOwningAgencyId: other,
                perAgencyEnabledClientGate: true),
                "gate on + known vessel + different non-Empty agency → BLOCK");
        }

        [TestMethod]
        public void IsRecoveryBlockedByAgency_SameAgency_Permits()
        {
            var same = Guid.NewGuid();
            Assert.IsFalse(AgencyMembership.IsRecoveryBlockedByAgency(
                localAgencyId: same,
                vesselKnown: true,
                vesselOwningAgencyId: same,
                perAgencyEnabledClientGate: true),
                "same agency → permit");
        }

        [TestMethod]
        public void IsRecoveryBlockedByAgency_GateOff_PermitsEvenWhenDifferentAgency()
        {
            // Dual-mode silence (spec §11): under gate-off the per-agency
            // surface is invisible; recovery behaviour matches the legacy
            // shared-agency UX (any player can recover any vessel).
            Assert.IsFalse(AgencyMembership.IsRecoveryBlockedByAgency(
                localAgencyId: Guid.NewGuid(),
                vesselKnown: true,
                vesselOwningAgencyId: Guid.NewGuid(),
                perAgencyEnabledClientGate: false),
                "gate off → permit regardless of agency match");
        }

        [TestMethod]
        public void IsRecoveryBlockedByAgency_LocalEmpty_Permits()
        {
            // Pre-handshake / post-transferagency / post-deleteagency where
            // the local player has no agency mapping. Matches the 5.17a
            // "requester has no agency mapping" bypass — recovering as
            // agency-less is permitted to keep the player interactable.
            Assert.IsFalse(AgencyMembership.IsRecoveryBlockedByAgency(
                localAgencyId: Guid.Empty,
                vesselKnown: true,
                vesselOwningAgencyId: Guid.NewGuid(),
                perAgencyEnabledClientGate: true),
                "local agency-less → permit");
        }

        [TestMethod]
        public void IsRecoveryBlockedByAgency_VesselUnknown_Permits()
        {
            // Vessel not in the client's VesselOwnership registry (relay
            // hasn't supplied a stamp yet). Permit locally; server-side
            // 5.17a write-path counterpart catches any real cross-agency
            // attempt downstream.
            Assert.IsFalse(AgencyMembership.IsRecoveryBlockedByAgency(
                localAgencyId: Guid.NewGuid(),
                vesselKnown: false,
                vesselOwningAgencyId: Guid.Empty,
                perAgencyEnabledClientGate: true),
                "vessel unknown to client → permit");
        }

        [TestMethod]
        public void IsRecoveryBlockedByAgency_UnassignedSentinel_Permits()
        {
            // Spec §10 Q3 — Unassigned-sentinel vessels (Empty agency id) are
            // interactable by ANY agency. /deleteagency cascade lands every
            // demoted vessel in this state; the prior owner's reconnect should
            // be able to re-recover them, AND any other agency should be able
            // to recover them.
            Assert.IsFalse(AgencyMembership.IsRecoveryBlockedByAgency(
                localAgencyId: Guid.NewGuid(),
                vesselKnown: true,
                vesselOwningAgencyId: Guid.Empty,
                perAgencyEnabledClientGate: true),
                "Unassigned-sentinel vessel → permit per spec §10 Q3");
        }

        // --- IsAutoAcquireBlockedByAgency — passive Update/UnloadedUpdate
        // lock auto-acquire suppression. Backs the
        // LockSystem.AcquireUpdateLock + AcquireUnloadedUpdateLock client-
        // side suppression that prevents stray "Cannot interact with this
        // vessel: it belongs to ..." toasts from four KSP-driven passive
        // paths: (1) VesselLockEvents.LockReleased on other-player vessel-
        // switch (the most frequent path), (2) VesselLockEvents.LevelLoaded
        // blanket pass on Flight/TrackStation entry, (3) VesselLockEvents.
        // VesselLoaded on physics-range entry, (4) VesselLockSystem.
        // StopSpectating blanket pass after a spectator session ends. Same
        // 6-branch bypass shape as IsRecoveryBlockedByAgency — only the
        // gate-on / known / non-Empty-different-agency case blocks. Call
        // sites at LockSystem.AcquireUpdateLock + AcquireUnloadedUpdateLock
        // additionally short-circuit on force=true (chain-fired acquires
        // from Decouple/Undock/Control-grant have established local
        // authority upstream); IsAutoAcquireBlockedByAgency_ParameterShape
        // pins the helper's 4-param signature so a refactor lifting force
        // into the helper would have to deliberately break that test.

        [TestMethod]
        public void IsAutoAcquireBlockedByAgency_DifferentAgency_Blocks()
        {
            // The motivating case: foreign player B (agency Y) switches
            // craft → server fans out LockRelease for vessel V_B's prior
            // Update lock → every client's LockReleased handler re-fires
            // AcquireUpdateLock(V_B). Local player A (agency X) was about
            // to send a useless cross-agency acquire that the server would
            // reject + toast. Helper blocks the send.
            var local = Guid.NewGuid();
            var other = Guid.NewGuid();
            Assert.IsTrue(AgencyMembership.IsAutoAcquireBlockedByAgency(
                localAgencyId: local,
                vesselKnown: true,
                vesselOwningAgencyId: other,
                perAgencyEnabledClientGate: true),
                "gate on + known vessel + different non-Empty agency → BLOCK");
        }

        [TestMethod]
        public void IsAutoAcquireBlockedByAgency_SameAgency_Permits()
        {
            // Forward-compat with multi-player-per-agency: a same-agency
            // peer SHOULD be able to take an Update lock on a fellow
            // agency-mate's vessel — the 5.17a server-side guard already
            // permits this case. The 1:1-today design doesn't trigger it
            // in practice (each agency has one owner) but the contract
            // must hold for any future relaxation.
            var same = Guid.NewGuid();
            Assert.IsFalse(AgencyMembership.IsAutoAcquireBlockedByAgency(
                localAgencyId: same,
                vesselKnown: true,
                vesselOwningAgencyId: same,
                perAgencyEnabledClientGate: true),
                "same agency → permit");
        }

        [TestMethod]
        public void IsAutoAcquireBlockedByAgency_GateOff_PermitsEvenWhenDifferentAgency()
        {
            // Dual-mode silence (spec §11): under gate-off the per-agency
            // surface is invisible; lock-acquire behaviour matches the
            // legacy shared-agency UX (any player can grab any unowned
            // Update / UnloadedUpdate lock). Without this branch, a
            // gate-off cohort would never auto-acquire after a peer
            // released — the entire passive lock-acquire chain would
            // break.
            Assert.IsFalse(AgencyMembership.IsAutoAcquireBlockedByAgency(
                localAgencyId: Guid.NewGuid(),
                vesselKnown: true,
                vesselOwningAgencyId: Guid.NewGuid(),
                perAgencyEnabledClientGate: false),
                "gate off → permit regardless of agency match");
        }

        [TestMethod]
        public void IsAutoAcquireBlockedByAgency_LocalEmpty_Permits()
        {
            // Pre-handshake (Handshake hasn't arrived yet) / post-
            // deleteagency-of-own-agency. Player has no agency mapping —
            // matches the 5.17a "requester has no agency mapping" server-
            // side bypass. Permit locally; the server-side guard catches
            // anything actually problematic.
            Assert.IsFalse(AgencyMembership.IsAutoAcquireBlockedByAgency(
                localAgencyId: Guid.Empty,
                vesselKnown: true,
                vesselOwningAgencyId: Guid.NewGuid(),
                perAgencyEnabledClientGate: true),
                "local agency-less → permit (let server decide)");
        }

        [TestMethod]
        public void IsAutoAcquireBlockedByAgency_VesselUnknown_Permits()
        {
            // Registry MISS — vessel hasn't been seen via VesselProto relay
            // or VesselSync yet (initial-connect window, or vessel just
            // spawned and the local mirror hasn't caught up). Permit; the
            // server will reject if it's actually cross-agency, and the
            // 5s per-(player, vessel, type) debounce at the server end
            // means one toast at most for the brief race window. Matches
            // IsRecoveryBlockedByAgency's same bypass — the helper does
            // not pessimistically block on unknowns.
            Assert.IsFalse(AgencyMembership.IsAutoAcquireBlockedByAgency(
                localAgencyId: Guid.NewGuid(),
                vesselKnown: false,
                vesselOwningAgencyId: Guid.Empty,
                perAgencyEnabledClientGate: true),
                "vessel unknown to client → permit");
        }

        [TestMethod]
        public void IsAutoAcquireBlockedByAgency_UnassignedSentinel_Permits()
        {
            // Spec §10 Q3 — Unassigned-sentinel vessels (Empty agency id)
            // are interactable by ANY agency. Pre-0.31 vessels persisted
            // without lmpOwningAgency carry this sentinel; /deleteagency
            // cascade lands every demoted vessel here. The passive auto-
            // acquire MUST be permitted, otherwise no agency could take
            // over an orphaned vessel's update simulation.
            Assert.IsFalse(AgencyMembership.IsAutoAcquireBlockedByAgency(
                localAgencyId: Guid.NewGuid(),
                vesselKnown: true,
                vesselOwningAgencyId: Guid.Empty,
                perAgencyEnabledClientGate: true),
                "Unassigned-sentinel vessel → permit per spec §10 Q3");
        }

        [TestMethod]
        public void IsAutoAcquireBlockedByAgency_ParameterShape()
        {
            // Consumer-lens review [SHOULD FIX] #3: pin the helper's 4-param
            // signature so a "refactor for clarity" lifting `force` into the
            // helper would trip loudly. The force-bypass contract lives at
            // the call site (LockSystem.AcquireUpdateLock +
            // AcquireUnloadedUpdateLock via `!force && IsAutoAcquireBlockedForVessel(...)`),
            // NOT in the helper itself — the helper is pure decision math
            // over agency-state inputs. Adding a `force` parameter to the
            // helper would invert that contract and make the call-site
            // gate ambiguous.
            var method = typeof(AgencyMembership).GetMethod(nameof(AgencyMembership.IsAutoAcquireBlockedByAgency));
            Assert.IsNotNull(method, "IsAutoAcquireBlockedByAgency must exist as a public static helper");
            var parameters = method.GetParameters();
            Assert.AreEqual(4, parameters.Length, "Helper must take exactly 4 parameters; force lives at the call site");
            Assert.AreEqual("localAgencyId", parameters[0].Name);
            Assert.AreEqual("vesselKnown", parameters[1].Name);
            Assert.AreEqual("vesselOwningAgencyId", parameters[2].Name);
            Assert.AreEqual("perAgencyEnabledClientGate", parameters[3].Name);
            foreach (var p in parameters)
            {
                Assert.IsFalse(p.Name.IndexOf("force", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Helper parameter '{p.Name}' contains 'force' — force-bypass MUST stay at the call site");
            }
        }

        [TestMethod]
        public void ForceRecordOwnership_Idempotent_WhenSameValueWrittenTwice()
        {
            // Transferagency / deleteagency visibility pushes may be redelivered
            // after a transient disconnect (the 5.18d catch-up flow ships a full
            // ownership snapshot on reconnect). Repeated ForceRecordOwnership
            // with the same value MUST be a no-op — same final state, no spurious
            // change-signal that a future event-emitting refactor could leak.
            // Pinning this here forecloses such a refactor breaking the contract
            // silently.
            var registry = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var agencyId = Guid.NewGuid();

            AgencyMembership.ForceRecordOwnership(registry, vesselId, agencyId);
            AgencyMembership.ForceRecordOwnership(registry, vesselId, agencyId);
            AgencyMembership.ForceRecordOwnership(registry, vesselId, agencyId);

            Assert.AreEqual(1, registry.Count, "no extra entries materialised");
            Assert.AreEqual(agencyId, registry[vesselId]);
        }

        // --- Mod-compat S6: DetermineOutboundStamp ---
        //
        // Pins VesselSerializer.PreSerializationChecks's outbound-proto stamping
        // decision. The helper is consulted on every outbound vessel proto wire
        // serialization to re-inject lmpOwningAgency stripped by KSP's
        // ProtoVessel.Save. Server-side first-stamp / preserve protects against
        // any divergence; the value of the stamp matters for peer-side
        // AgencySystem.VesselOwnership mirrors which read directly from the
        // relayed bytes.

        [TestMethod]
        public void DetermineOutboundStamp_GateOff_ReturnsEmpty()
        {
            // Dual-mode silence: PerAgencyCareer=false → never stamp. The
            // existing-relay-path contract is "no Agency wire under gate=off"
            // (Stack Notes 2026-05-17 5.17d closure) and this helper preserves
            // it by writing nothing into the outbound ConfigNode.
            var mirror = Guid.NewGuid();
            Assert.AreEqual(Guid.Empty, AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: true,
                mirrorStampedAgencyId: mirror,
                perAgencyEnabledClientGate: false));
        }

        [TestMethod]
        public void DetermineOutboundStamp_KnownVesselWithRealId_ReassertsMirror()
        {
            // The common case: vessel was stamped server-side via 5.16b first-stamp,
            // VesselSync round-tripped the value into our mirror, now we're sending
            // a periodic drift-correction. KSP's Save stripped lmpOwningAgency;
            // we re-inject the mirror's known value so peer-side mirrors don't
            // see "field absent → preservation rule → stale" for a vessel
            // whose canonical agency is known.
            var ownerOnMirror = Guid.NewGuid();
            Assert.AreEqual(ownerOnMirror, AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: true,
                mirrorStampedAgencyId: ownerOnMirror,
                perAgencyEnabledClientGate: true));
        }

        [TestMethod]
        public void DetermineOutboundStamp_KnownVesselAsUnassigned_DoesNotUpgrade()
        {
            // 2026-05-19 upgrade-lens fix. The pre-0.31 sticky-Unassigned
            // contract (Stack Notes 2026-05-17 Round-5: "pre-existing vessels
            // including the Unassigned sentinel are sticky") forbids the
            // relay path from converting an Unassigned vessel to any agency
            // — admin-mediated transferagency is the only legitimate path.
            // A LocalAgencyId fallback here would silently de-facto transfer
            // every pre-0.31 vessel to whatever local agency happens to
            // serialise it. Server-side branch (a) preserves Empty correctly,
            // but the relayed bytes would carry LocalAgencyId and clobber
            // peer mirrors via RecordOwnership's write-through-non-Empty rule.
            // Pin Guid.Empty (no stamp) to forbid the upgrade at this layer.
            Assert.AreEqual(Guid.Empty, AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: true,
                mirrorStampedAgencyId: Guid.Empty,
                perAgencyEnabledClientGate: true));
        }

        [TestMethod]
        public void DetermineOutboundStamp_UnknownVessel_DoesNotStamp()
        {
            // 2026-05-19 reconnect-window fix. AgencySystem.OnDisabled clears
            // VesselOwnership on disconnect. Between reconnect and the
            // server's VesselSync repopulating the mirror, every locally-
            // serialised vessel (including peer-owned vessels KSP happens
            // to physics-load nearby) appears here as "vesselKnownToMirror
            // false". A LocalAgencyId stamp would relay our stamp onto
            // someone else's vessel, corrupting peer mirrors. Server-side
            // branch (a) catches it (preserves canonical owner), but
            // relayed bytes are the corruption vector. Pin Guid.Empty.
            //
            // The KIS-attach / fresh-launch acceptance case the original
            // spec cited is covered by the server's branch (b) which
            // resolves canonical owner from sender-PlayerName regardless
            // of the wire field. Peer mirrors see "Unassigned" briefly
            // until the next VesselSync — cosmetic, not corruption.
            Assert.AreEqual(Guid.Empty, AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: false,
                mirrorStampedAgencyId: Guid.Empty,
                perAgencyEnabledClientGate: true));
        }

        [TestMethod]
        public void DetermineOutboundStamp_UnknownVessel_WithMirrorStampedAgency_StillNoStamp()
        {
            // Edge case: vesselKnownToMirror=false but mirrorStampedAgencyId
            // happens to be non-Empty. This shouldn't be possible from
            // TryGetOwningAgency's contract (false ⇒ out param = default,
            // which is Guid.Empty for Guid), but pin the contract anyway
            // so a future refactor that loosens the helper's invariants
            // doesn't silently open the "unknown + caller-supplied real id"
            // path back into the LocalAgencyId-fallback shape we deleted.
            var phantom = Guid.NewGuid();
            Assert.AreEqual(Guid.Empty, AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: false,
                mirrorStampedAgencyId: phantom,
                perAgencyEnabledClientGate: true));
        }

        [TestMethod]
        public void DetermineOutboundStamp_GateOff_KnownVessel_StillNoStamp()
        {
            // Reconfirm dual-mode silence under the "known vessel" condition
            // (gate-off path runs first in the helper). Under gate=off the
            // server's relay path doesn't emit any Agency wire either, so
            // outbound proto stamping would create a one-sided wire that
            // peer clients ignore at best, mis-handle at worst.
            var ownerOnMirror = Guid.NewGuid();
            Assert.AreEqual(Guid.Empty, AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: true,
                mirrorStampedAgencyId: ownerOnMirror,
                perAgencyEnabledClientGate: false));
        }

        [TestMethod]
        public void DetermineOutboundStamp_KnownVesselWithRealId_ReassertsRegardlessOfLocalAgency()
        {
            // Documents the contract that the helper does not consult
            // LocalAgencyId. The "transferagency lag" mitigation depends
            // on this: peer P's mirror says V is owned by A; P serialises
            // V; P's stamp = A regardless of P's own (possibly-Empty,
            // possibly-something-else) agency. The helper does not accept
            // a localAgencyId parameter so we can't even pass one in by
            // accident; that's the documentation that survives a refactor.
            var ownerOnMirror = Guid.NewGuid();
            Assert.AreEqual(ownerOnMirror, AgencyMembership.DetermineOutboundStamp(
                vesselKnownToMirror: true,
                mirrorStampedAgencyId: ownerOnMirror,
                perAgencyEnabledClientGate: true));
        }

        // -------------------------------------------------------------------
        // [v8.1 audit cross-phase (g)] ShouldRecordForeignCrewCount — pins
        // the combined-gate contract for ForeignCrewCount population. The
        // gate MUST be PerAgencyKerbalRosterEnabled (composite) not
        // PerAgencyCareerEnabled alone — Phase 6.6 review's MUST FIX caught
        // a regression where the gate was the latter, letting BUG-023 race
        // transients under the intermediate Stage 5 → 6 ramp seed misleading
        // foreign-vessel labels for shared kerbals.
        // -------------------------------------------------------------------

        [TestMethod]
        public void ShouldRecordForeignCrewCount_True_WhenPerAgencyKerbalRosterEnabled()
        {
            Assert.IsTrue(AgencyMembership.ShouldRecordForeignCrewCount(perAgencyKerbalRosterEnabled: true));
        }

        [TestMethod]
        public void ShouldRecordForeignCrewCount_False_WhenGateOff()
        {
            Assert.IsFalse(AgencyMembership.ShouldRecordForeignCrewCount(perAgencyKerbalRosterEnabled: false));
        }

        [TestMethod]
        public void ShouldRecordForeignCrewCount_ParameterNamedForCombinedGate()
        {
            // Compile-time + reflection contract assertion: helper signature
            // requires the combined gate name. A future refactor passing
            // PerAgencyCareerEnabled alone without renaming the parameter
            // would trip this test. The Phase 6.6 review's MUST FIX would
            // re-surface as a regression.
            var method = typeof(AgencyMembership).GetMethod(nameof(AgencyMembership.ShouldRecordForeignCrewCount));
            Assert.IsNotNull(method, "ShouldRecordForeignCrewCount must exist as a public static helper");
            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length, "Helper must take exactly one parameter");
            Assert.AreEqual("perAgencyKerbalRosterEnabled", parameters[0].Name,
                "Parameter must be named for the COMBINED gate, not PerAgencyCareerEnabled alone");
        }

        // -------------------------------------------------------------------
        // [Phase 6 follow-up — foreign-vessel crew strip decision gate]
        // Pins the four-state decision matrix for
        // AgencyMembership.ShouldStripForeignCrew. Integration-lens review
        // SHOULD FIX #6: extract a pure helper so LmpClientTest covers the
        // gate without needing to construct a KSP ConfigNode. The truth
        // table maps real connect-race scenarios from the live v8.1 soak.
        // -------------------------------------------------------------------

        [TestMethod]
        public void ShouldStripForeignCrew_False_WhenGateOff()
        {
            var ownership = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var localAgency = Guid.NewGuid();
            ownership[vesselId] = Guid.NewGuid();
            Assert.IsFalse(AgencyMembership.ShouldStripForeignCrew(
                perAgencyKerbalRosterEnabled: false,
                vesselId, localAgency, ownership));
        }

        [TestMethod]
        public void ShouldStripForeignCrew_False_WhenLocalAgencyEmpty()
        {
            // Pre-handshake state — per-agency mode isn't active for this
            // client. Treat foreign-ness as unknown; don't disrupt the
            // shared-roster behaviour.
            var ownership = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            ownership[vesselId] = Guid.NewGuid();
            Assert.IsFalse(AgencyMembership.ShouldStripForeignCrew(
                perAgencyKerbalRosterEnabled: true,
                vesselId, localAgencyId: Guid.Empty, ownership));
        }

        [TestMethod]
        public void ShouldStripForeignCrew_False_WhenOwnershipConfirmedLocal()
        {
            var ownership = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var localAgency = Guid.NewGuid();
            ownership[vesselId] = localAgency;  // confirmed-local
            Assert.IsFalse(AgencyMembership.ShouldStripForeignCrew(
                perAgencyKerbalRosterEnabled: true,
                vesselId, localAgency, ownership));
        }

        [TestMethod]
        public void ShouldStripForeignCrew_True_WhenOwnershipConfirmedForeign()
        {
            var ownership = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var localAgency = Guid.NewGuid();
            var foreignAgency = Guid.NewGuid();
            ownership[vesselId] = foreignAgency;
            Assert.IsTrue(AgencyMembership.ShouldStripForeignCrew(
                perAgencyKerbalRosterEnabled: true,
                vesselId, localAgency, ownership));
        }

        [TestMethod]
        public void ShouldStripForeignCrew_True_WhenOwnershipMiss_PessimisticInitialConnectRace()
        {
            // Integration-lens Finding 1 — relay-stripped VesselProto arrives
            // before VesselSync stamps the owning agency. Without pessimistic
            // strip, A's CrewRoster would bind to the foreign vessel's seats
            // via KSP's name-keyed lookup before the authoritative stamp
            // ever arrived. After VesselSync arrives + RecordOwnership writes
            // the real id, subsequent passes hit the confirmed-foreign or
            // confirmed-local branch.
            var ownership = new ConcurrentDictionary<Guid, Guid>();  // empty — registry MISS
            var vesselId = Guid.NewGuid();
            var localAgency = Guid.NewGuid();
            Assert.IsTrue(AgencyMembership.ShouldStripForeignCrew(
                perAgencyKerbalRosterEnabled: true,
                vesselId, localAgency, ownership));
        }

        [TestMethod]
        public void ShouldStripForeignCrew_True_WhenOwnershipEmpty_UnassignedSentinelOrRelayStripped()
        {
            // Spec §10 Q3 Unassigned-sentinel vessels (pre-0.31 carry-over)
            // AND the relay-stripped-then-Empty-inserted race window both
            // resolve to ownership[V] = Empty. Either way, no agency
            // legitimately "owns" the vessel's crew, so pessimistic strip
            // is correct — binding to anyone's local roster would let one
            // client claim a kerbal name across the cohort.
            var ownership = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var localAgency = Guid.NewGuid();
            ownership[vesselId] = Guid.Empty;
            Assert.IsTrue(AgencyMembership.ShouldStripForeignCrew(
                perAgencyKerbalRosterEnabled: true,
                vesselId, localAgency, ownership));
        }

        [TestMethod]
        public void ShouldStripForeignCrew_True_WhenVesselOwnershipDictNull_DefensivePessimism()
        {
            // AgencySystem.Singleton?.VesselOwnership being null implies the
            // agency singleton isn't initialised. Under combined gate=on
            // with a known LocalAgencyId, we shouldn't trust any vessel's
            // crew names — strip pessimistically. This branch is unreachable
            // in steady state but defensive against a null-vessel-ownership
            // failure during system init.
            var vesselId = Guid.NewGuid();
            var localAgency = Guid.NewGuid();
            Assert.IsTrue(AgencyMembership.ShouldStripForeignCrew(
                perAgencyKerbalRosterEnabled: true,
                vesselId, localAgency, vesselOwnership: null));
        }

        [TestMethod]
        public void ShouldStripForeignCrew_OwnershipStableAcrossRepeatedCalls()
        {
            // Pinning idempotency — the strip predicate must not mutate the
            // ownership registry. Repeated calls yield the same answer.
            var ownership = new ConcurrentDictionary<Guid, Guid>();
            var vesselId = Guid.NewGuid();
            var localAgency = Guid.NewGuid();
            var foreignAgency = Guid.NewGuid();
            ownership[vesselId] = foreignAgency;

            for (var i = 0; i < 5; i++)
            {
                Assert.IsTrue(AgencyMembership.ShouldStripForeignCrew(
                    perAgencyKerbalRosterEnabled: true,
                    vesselId, localAgency, ownership));
            }
            Assert.AreEqual(foreignAgency, ownership[vesselId], "Helper must not mutate ownership");
        }
    }
}
