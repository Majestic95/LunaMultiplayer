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
    }
}
