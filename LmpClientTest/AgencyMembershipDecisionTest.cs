using LmpClient.Systems.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

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
    }
}
