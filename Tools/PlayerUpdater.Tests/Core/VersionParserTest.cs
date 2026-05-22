using System;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class VersionParserTest
    {
        // --- Stable releases (no channel suffix) ---

        [TestMethod]
        public void Parse_StableTag_ReturnsStableChannelNullRevision()
        {
            var meta = VersionParser.Parse("v0.31.0");

            Assert.AreEqual("v0.31.0", meta.Tag);
            Assert.AreEqual(0, meta.Major);
            Assert.AreEqual(31, meta.Minor);
            Assert.AreEqual(0, meta.Patch);
            Assert.AreEqual(VersionMetadata.ChannelStable, meta.Channel);
            Assert.IsNull(meta.Revision);
            Assert.IsNull(meta.Hotfix);
        }

        [TestMethod]
        public void Parse_LargeVersionComponents_ParsesCorrectly()
        {
            var meta = VersionParser.Parse("v123.456.789");

            Assert.AreEqual(123, meta.Major);
            Assert.AreEqual(456, meta.Minor);
            Assert.AreEqual(789, meta.Patch);
            Assert.AreEqual(VersionMetadata.ChannelStable, meta.Channel);
        }

        // --- Private cohort releases ---

        [TestMethod]
        public void Parse_PrivateRevisionTag_ReturnsPrivateChannelWithRevision()
        {
            var meta = VersionParser.Parse("v0.30.0-private-1");

            Assert.AreEqual("v0.30.0-private-1", meta.Tag);
            Assert.AreEqual(0, meta.Major);
            Assert.AreEqual(30, meta.Minor);
            Assert.AreEqual(0, meta.Patch);
            Assert.AreEqual(VersionMetadata.ChannelPrivate, meta.Channel);
            Assert.AreEqual(1, meta.Revision);
            Assert.IsNull(meta.Hotfix);
        }

        [TestMethod]
        public void Parse_PerAgencyPrivateRevisionTag_ReturnsPerAgencyPrivateChannelWithRevision()
        {
            var meta = VersionParser.Parse("v0.31.0-per-agency-private-7");

            Assert.AreEqual("v0.31.0-per-agency-private-7", meta.Tag);
            Assert.AreEqual(VersionMetadata.ChannelPerAgencyPrivate, meta.Channel);
            Assert.AreEqual(7, meta.Revision);
            Assert.IsNull(meta.Hotfix);
        }

        [TestMethod]
        public void Parse_LargeRevision_ParsesAsInt()
        {
            var meta = VersionParser.Parse("v1.20.5-private-999");

            Assert.AreEqual(999, meta.Revision);
            Assert.AreEqual(VersionMetadata.ChannelPrivate, meta.Channel);
        }

        // --- Hotfix dot-suffix on the revision ---
        //
        // Tagged BLOCKER fix for v0.31.0-per-agency-private-8.1 (Majestic95's
        // first hotfix-suffix release). The PS1 grammar in
        // Scripts/build-release.ps1's Get-LmpVersionMetadata is extended in
        // lockstep — keep these test grammars in sync if either side changes.

        [TestMethod]
        public void Parse_HotfixSuffixOnPerAgencyPrivate_ReturnsRevisionAndHotfix()
        {
            // The exact live tag that motivated this fix.
            var meta = VersionParser.Parse("v0.31.0-per-agency-private-8.1");

            Assert.AreEqual("v0.31.0-per-agency-private-8.1", meta.Tag);
            Assert.AreEqual(0, meta.Major);
            Assert.AreEqual(31, meta.Minor);
            Assert.AreEqual(0, meta.Patch);
            Assert.AreEqual(VersionMetadata.ChannelPerAgencyPrivate, meta.Channel);
            Assert.AreEqual(8, meta.Revision);
            Assert.AreEqual(1, meta.Hotfix);
        }

        [TestMethod]
        public void Parse_MultiDigitHotfix_ParsesAsInt()
        {
            // Distinguishes '8.10' from '8.1' — string-sort would order
            // them backwards. The ordering rule (deferred to GitHubClient
            // in Core sub-slice 3) treats Hotfix as an int.
            var meta = VersionParser.Parse("v0.31.0-per-agency-private-8.10");

            Assert.AreEqual(8, meta.Revision);
            Assert.AreEqual(10, meta.Hotfix);
        }

        [TestMethod]
        public void Parse_HotfixOnPrivateChannel_ReturnsHotfix()
        {
            // The stability channel also supports hotfix suffixes; the grammar
            // is uniform across both private channels.
            var meta = VersionParser.Parse("v0.30.0-private-3.2");

            Assert.AreEqual(VersionMetadata.ChannelPrivate, meta.Channel);
            Assert.AreEqual(3, meta.Revision);
            Assert.AreEqual(2, meta.Hotfix);
        }

        [TestMethod]
        public void Parse_NonHotfixTag_HotfixIsNull()
        {
            // Pre-existing tag shape — Hotfix is explicitly null (NOT zero)
            // so a re-emitted tag round-trips byte-equal without a synthetic
            // '.0' suffix appearing.
            var meta = VersionParser.Parse("v0.31.0-per-agency-private-7");

            Assert.AreEqual(7, meta.Revision);
            Assert.IsNull(meta.Hotfix);
        }

        [TestMethod]
        public void Parse_StableTag_HotfixIsNull()
        {
            // Stable releases never have a hotfix segment ('v0.31.0.1' is
            // rejected — see Parse_StableHotfixShape_Throws below).
            var meta = VersionParser.Parse("v0.31.0");

            Assert.IsNull(meta.Revision);
            Assert.IsNull(meta.Hotfix);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_TrailingDotWithoutHotfixDigit_Throws()
        {
            // '-private-8.' with no digit after the dot is a typo, not a
            // valid hotfix-zero shape.
            VersionParser.Parse("v0.31.0-per-agency-private-8.");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_MultiSegmentHotfix_Throws()
        {
            // Only one hotfix segment is permitted. A two-segment shape
            // ('-private-8.1.2') would require a separate sub-hotfix concept
            // which we explicitly decided against — bump revision instead.
            VersionParser.Parse("v0.31.0-per-agency-private-8.1.2");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_StableHotfixShape_Throws()
        {
            // There is no stable-release hotfix path — 'v0.31.0.1' is invalid;
            // stable hotfixes go out as a patch bump ('v0.31.1').
            VersionParser.Parse("v0.31.0.1");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_HotfixWithoutRevision_Throws()
        {
            // Hotfix requires a parent revision; '-private.1' has no revision
            // to attach the hotfix to.
            VersionParser.Parse("v0.31.0-private.1");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_HotfixZero_Throws()
        {
            // Hotfix-zero is rejected on purpose — '-8' and '-8.0' would map
            // to the same release ordinal under the planned coalesce-to-zero
            // ordering rule, a footgun for GitHubClient's "pick latest" loop.
            // Operators bumping from '-8' must use '-8.1' or '-9'.
            VersionParser.Parse("v0.31.0-per-agency-private-8.0");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_HotfixWithLeadingZero_Throws()
        {
            // '8.01' is rejected because the hotfix group is [1-9]\d* — no
            // leading zeros allowed. Operators must write '8.1', not '8.01'.
            VersionParser.Parse("v0.31.0-per-agency-private-8.01");
        }

        // --- Dev sentinel routing ---

        [TestMethod]
        public void Parse_NullTag_ReturnsDevSentinel()
        {
            var meta = VersionParser.Parse(null);

            Assert.AreSame(VersionMetadata.Dev, meta);
        }

        [TestMethod]
        public void Parse_EmptyTag_ReturnsDevSentinel()
        {
            var meta = VersionParser.Parse("");

            Assert.AreSame(VersionMetadata.Dev, meta);
        }

        [TestMethod]
        public void Parse_WhitespaceTag_ReturnsDevSentinel()
        {
            var meta = VersionParser.Parse("   ");

            Assert.AreSame(VersionMetadata.Dev, meta);
        }

        [TestMethod]
        public void Parse_ExplicitDevTag_ReturnsDevSentinel()
        {
            var meta = VersionParser.Parse("v0.0.0-dev");

            Assert.AreSame(VersionMetadata.Dev, meta);
            Assert.AreEqual(VersionMetadata.ChannelDev, meta.Channel);
            Assert.IsNull(meta.Revision);
        }

        // --- Malformed tags must throw ---

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_BarePrivateWithoutRevision_Throws()
        {
            // This is the exact shape the PS1 grammar rejects: a typo where
            // the operator forgot the '-N' revision suffix.
            VersionParser.Parse("v0.31.0-private");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_BarePerAgencyPrivateWithoutRevision_Throws()
        {
            VersionParser.Parse("v0.31.0-per-agency-private");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_MissingVPrefix_Throws()
        {
            VersionParser.Parse("0.31.0");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_UnknownChannel_Throws()
        {
            // 'rc' is not in the current grammar — if added, both this test
            // and Scripts/build-release.ps1's Get-LmpVersionMetadata must be
            // extended in lockstep.
            VersionParser.Parse("v0.31.0-rc-1");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_NonIntegerRevision_Throws()
        {
            VersionParser.Parse("v0.31.0-private-x");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_ExtraSuffix_Throws()
        {
            VersionParser.Parse("v0.31.0-private-7-extra");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_GibberishTag_Throws()
        {
            VersionParser.Parse("not-a-version");
        }

        // --- TryParse semantics ---

        [TestMethod]
        public void TryParse_ValidTag_ReturnsTrueAndMetadata()
        {
            var ok = VersionParser.TryParse("v0.31.0-private-2", out var meta);

            Assert.IsTrue(ok);
            Assert.IsNotNull(meta);
            Assert.AreEqual(2, meta!.Revision);
        }

        [TestMethod]
        public void TryParse_DevSentinel_ReturnsTrueAndDevMetadata()
        {
            var ok = VersionParser.TryParse("", out var meta);

            Assert.IsTrue(ok);
            Assert.AreSame(VersionMetadata.Dev, meta);
        }

        [TestMethod]
        public void TryParse_MalformedTag_ReturnsFalseAndNull()
        {
            var ok = VersionParser.TryParse("v0.31.0-private", out var meta);

            Assert.IsFalse(ok);
            Assert.IsNull(meta);
        }

        [TestMethod]
        public void TryParse_HotfixTag_ReturnsTrueAndHotfixMetadata()
        {
            // Live tag from the v0.31.0-per-agency-private-8.1 release.
            var ok = VersionParser.TryParse("v0.31.0-per-agency-private-8.1", out var meta);

            Assert.IsTrue(ok);
            Assert.IsNotNull(meta);
            Assert.AreEqual(8, meta!.Revision);
            Assert.AreEqual(1, meta.Hotfix);
        }

        [TestMethod]
        public void TryParse_MalformedHotfix_ReturnsFalseAndNull()
        {
            var ok = VersionParser.TryParse("v0.31.0-per-agency-private-8.1.2", out var meta);

            Assert.IsFalse(ok);
            Assert.IsNull(meta);
        }
    }
}
