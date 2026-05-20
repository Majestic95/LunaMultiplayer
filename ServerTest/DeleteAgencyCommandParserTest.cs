using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.18d slice (g) <see cref="DeleteAgencyCommandParser"/>
    /// + Phase 6.7 grammar extension. Three-token grammar with order-insensitive
    /// <c>--confirm</c> (required downstream), <c>--restore-to &lt;agency-token&gt;</c>
    /// and <c>--restore-to-none</c> (mutually exclusive optionals). The parser
    /// surfaces flag state to the command and accepts inputs with/without
    /// optional flags — the command body decides refusal posture per gate +
    /// in-flight-passenger conditions.
    /// </summary>
    [TestClass]
    public class DeleteAgencyCommandParserTest
    {
        // -------------------------------------------------------------------
        // Stage 5.18d original grammar (--confirm + agency token)
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryParse_TokenThenFlag_BothCaptured()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm",
                out var token, out var confirmed, out var restoreTo, out var restoreNone, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
            Assert.AreEqual(string.Empty, restoreTo);
            Assert.IsFalse(restoreNone);
        }

        [TestMethod]
        public void TryParse_FlagThenToken_OrderInsensitive()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "--confirm Alice",
                out var token, out var confirmed, out _, out _, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void TryParse_NoFlag_ParsesButConfirmedFalse()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice",
                out var token, out var confirmed, out _, out _, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsFalse(confirmed);
        }

        [TestMethod]
        public void TryParse_GuidToken_PassesThrough()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92 --confirm",
                out var token, out var confirmed, out _, out _, out _));
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void TryParse_DuplicateConfirmFlag_Tolerated()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --confirm",
                out var token, out var confirmed, out _, out _, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void TryParse_Empty_FailsWithReason()
        {
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                string.Empty, out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "no arguments");
        }

        [TestMethod]
        public void TryParse_OnlyConfirmFlag_FailsWithReason()
        {
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "--confirm", out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "missing agency token");
        }

        [TestMethod]
        public void TryParse_TooManyNonFlagTokens_FailsWithReason()
        {
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "Alice Bob --confirm", out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "too many tokens");
        }

        [TestMethod]
        public void TryParse_DoubleSpaces_Tolerated()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice   --confirm",
                out var token, out var confirmed, out _, out _, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void UsageBanner_AdvertisesDestructiveSemantics()
        {
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "/deleteagency");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "--confirm");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "Destructive");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "LOST");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "No undo".ToLower(),
                "banner mentions the no-undo property in some form");
        }

        // -------------------------------------------------------------------
        // Phase 6.7 grammar extension — --restore-to / --restore-to-none
        // -------------------------------------------------------------------

        [TestMethod]
        public void TryParse_RestoreTo_CapturesDestinationToken()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --restore-to Bob",
                out var token, out var confirmed, out var restoreTo, out var restoreNone, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
            Assert.AreEqual("Bob", restoreTo);
            Assert.IsFalse(restoreNone);
        }

        [TestMethod]
        public void TryParse_RestoreToNone_FlagSet()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --restore-to-none",
                out var token, out var confirmed, out var restoreTo, out var restoreNone, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
            Assert.AreEqual(string.Empty, restoreTo);
            Assert.IsTrue(restoreNone);
        }

        [TestMethod]
        public void TryParse_RestoreToInterleavedOrder_StillParses()
        {
            // GUI launchers may emit flags in any order; the parser is
            // position-agnostic for the flag tokens (the value following
            // --restore-to is always the next token).
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "--restore-to Bob --confirm Alice",
                out var token, out var confirmed, out var restoreTo, out var restoreNone, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
            Assert.AreEqual("Bob", restoreTo);
            Assert.IsFalse(restoreNone);
        }

        [TestMethod]
        public void TryParse_RestoreToGuidToken_PassesThrough()
        {
            // The destination token can be a Guid id; the parser only checks
            // non-emptiness, the command resolves it via TryResolveAgencyToken.
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --restore-to f0ea9ad24719407ab370342d7effae92",
                out _, out _, out var restoreTo, out _, out _));
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", restoreTo);
        }

        [TestMethod]
        public void TryParse_RestoreToWithoutValue_FailsWithReason()
        {
            // --restore-to at end-of-args with no following token.
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --restore-to",
                out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "--restore-to");
            StringAssert.Contains(err, "destination");
        }

        [TestMethod]
        public void TryParse_RestoreToFollowedByFlag_FailsWithReason()
        {
            // --restore-to immediately followed by another flag — operator
            // forgot the destination. We treat this as a missing destination
            // (better than greedy-consuming a flag as the value).
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "Alice --restore-to --confirm",
                out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "--restore-to");
            StringAssert.Contains(err, "destination");
        }

        [TestMethod]
        public void TryParse_RestoreToAndRestoreToNone_MutuallyExclusive()
        {
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --restore-to Bob --restore-to-none",
                out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "mutually exclusive");
        }

        [TestMethod]
        public void TryParse_DuplicateRestoreTo_FailsWithReason()
        {
            // Specifying --restore-to twice is ambiguous — refuse rather than
            // last-wins or first-wins.
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --restore-to Bob --restore-to Carol",
                out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "--restore-to");
        }

        [TestMethod]
        public void TryParse_DuplicateRestoreToNone_FailsWithReason()
        {
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --restore-to-none --restore-to-none",
                out _, out _, out _, out _, out var err));
            StringAssert.Contains(err, "--restore-to-none");
        }

        [TestMethod]
        public void UsageBanner_AdvertisesRestoreToOptions()
        {
            // Operators who forget --confirm see this banner; it must explain
            // the WOLF-restoration disposition flags so they understand both
            // the destructive nature AND the kerbal-rescue obligation.
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "--restore-to");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "--restore-to-none");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "PerAgencyKerbalRoster");
        }
    }
}
