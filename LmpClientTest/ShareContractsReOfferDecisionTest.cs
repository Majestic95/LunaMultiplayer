using LmpClient.Systems.ShareContracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace LmpClientTest
{
    /// <summary>
    /// Pins the decision helper used by
    /// <c>ShareContractsEvents.ContractOffered</c> to withdraw a re-Offer of a
    /// contract the local user has already taken a terminal action on this session.
    ///
    /// <para><b>Bug it guards against.</b> KSP's <c>ContractSystem.Update</c> runs
    /// <c>GenerateContracts</c> on the next tick after the user clicks Cancel to fill
    /// the now-empty slot. KSPCF's <c>ContractPreLoader</c> does not always evict the
    /// cancelled contract from its persistent cache before that tick, so CC's patched
    /// generator restores the just-cancelled contract from cache and KSP fires
    /// <c>onOffered</c> for it ~1 second later. Without this guard the contract pops
    /// back into the Available list immediately after the cancel click. The session-
    /// scoped <see cref="ShareContractsSystem.LocallyActedOnContractGuids"/> set is
    /// populated by the per-action event handlers (Accept / Cancel / Decline /
    /// Complete / Fail); this helper turns "is this GUID in the set" into the
    /// withdraw decision so the call site can stay a single line.</para>
    ///
    /// <para><b>Case-insensitive match.</b> The set is constructed with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>; the helper just delegates to
    /// <see cref="ICollection{T}.Contains"/> which honours the set's comparer. KSP
    /// emits contract GUIDs in their canonical lowercase form; CC's preloader
    /// occasionally round-trips through paths that uppercase the hex digits. The
    /// case-insensitive comparison closes that mismatch source.</para>
    /// </summary>
    [TestClass]
    public class ShareContractsReOfferDecisionTest
    {
        [TestMethod]
        public void ShouldWithdrawReOffer_GuidIsInActedOnSet_ReturnsTrue()
        {
            var guid = Guid.NewGuid().ToString();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { guid };

            Assert.IsTrue(ShareContractsSystem.ShouldWithdrawReOffer(guid, set));
        }

        [TestMethod]
        public void ShouldWithdrawReOffer_GuidNotInSet_ReturnsFalse()
        {
            var guid = Guid.NewGuid().ToString();
            var otherGuid = Guid.NewGuid().ToString();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { otherGuid };

            Assert.IsFalse(ShareContractsSystem.ShouldWithdrawReOffer(guid, set));
        }

        [TestMethod]
        public void ShouldWithdrawReOffer_EmptySet_ReturnsFalse()
        {
            var guid = Guid.NewGuid().ToString();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Assert.IsFalse(ShareContractsSystem.ShouldWithdrawReOffer(guid, set));
        }

        [TestMethod]
        public void ShouldWithdrawReOffer_NullSet_ReturnsFalse()
        {
            var guid = Guid.NewGuid().ToString();

            Assert.IsFalse(ShareContractsSystem.ShouldWithdrawReOffer(guid, null));
        }

        [TestMethod]
        public void ShouldWithdrawReOffer_NullGuid_ReturnsFalse()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "any" };

            Assert.IsFalse(ShareContractsSystem.ShouldWithdrawReOffer(null, set));
        }

        [TestMethod]
        public void ShouldWithdrawReOffer_EmptyGuid_ReturnsFalse()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "any" };

            Assert.IsFalse(ShareContractsSystem.ShouldWithdrawReOffer(string.Empty, set));
        }

        [TestMethod]
        public void ShouldWithdrawReOffer_CaseInsensitive_ReturnsTrue()
        {
            // Set is constructed with OrdinalIgnoreCase per ShareContractsSystem.
            // KSP emits lowercase GUIDs but a stray round-trip through an upper-case
            // path (occasionally seen from CC's ContractPreLoader byte-level
            // restoration) shouldn't bypass the guard.
            var lowerGuid = "11111111-2222-3333-4444-555555555555";
            var upperGuid = lowerGuid.ToUpperInvariant();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lowerGuid };

            Assert.IsTrue(ShareContractsSystem.ShouldWithdrawReOffer(upperGuid, set));
        }
    }
}
