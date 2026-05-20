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
        }

        [TestMethod]
        public void Parse_PerAgencyPrivateRevisionTag_ReturnsPerAgencyPrivateChannelWithRevision()
        {
            var meta = VersionParser.Parse("v0.31.0-per-agency-private-7");

            Assert.AreEqual("v0.31.0-per-agency-private-7", meta.Tag);
            Assert.AreEqual(VersionMetadata.ChannelPerAgencyPrivate, meta.Channel);
            Assert.AreEqual(7, meta.Revision);
        }

        [TestMethod]
        public void Parse_LargeRevision_ParsesAsInt()
        {
            var meta = VersionParser.Parse("v1.20.5-private-999");

            Assert.AreEqual(999, meta.Revision);
            Assert.AreEqual(VersionMetadata.ChannelPrivate, meta.Channel);
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
    }
}
