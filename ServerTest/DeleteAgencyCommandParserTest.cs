using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.18d slice (g) <see cref="DeleteAgencyCommandParser"/>.
    /// Two-token grammar with order-insensitive <c>--confirm</c> flag. The flag is
    /// REQUIRED downstream (the command refuses without it); the parser surfaces
    /// the flag's presence to the command and accepts both with-flag and
    /// without-flag inputs (the command body decides the refusal).
    /// </summary>
    [TestClass]
    public class DeleteAgencyCommandParserTest
    {
        [TestMethod]
        public void TryParse_TokenThenFlag_BothCaptured()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm",
                out var token, out var confirmed, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void TryParse_FlagThenToken_OrderInsensitive()
        {
            // GUI launchers and operator shells may emit the flag first; the
            // parser tolerates either order.
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "--confirm Alice",
                out var token, out var confirmed, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void TryParse_NoFlag_ParsesButConfirmedFalse()
        {
            // The parser captures the token and flag-state cleanly; the command
            // body decides whether to refuse on missing --confirm so the
            // refusal can carry agency-specific destructive-action context.
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice",
                out var token, out var confirmed, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsFalse(confirmed);
        }

        [TestMethod]
        public void TryParse_GuidToken_PassesThrough()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92 --confirm",
                out var token, out var confirmed, out _));
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void TryParse_DuplicateConfirmFlag_Tolerated()
        {
            // Defensive: an operator who double-typed --confirm shouldn't get a
            // grammar error. The flag-presence test is set-membership; duplicates
            // are idempotent.
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice --confirm --confirm",
                out var token, out var confirmed, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void TryParse_Empty_FailsWithReason()
        {
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                string.Empty, out _, out _, out var err));
            StringAssert.Contains(err, "no arguments");
        }

        [TestMethod]
        public void TryParse_OnlyConfirmFlag_FailsWithReason()
        {
            // Operator forgot to type the agency token.
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "--confirm", out _, out _, out var err));
            StringAssert.Contains(err, "missing agency token");
        }

        [TestMethod]
        public void TryParse_TooManyNonFlagTokens_FailsWithReason()
        {
            // Two agency tokens (or one with embedded spaces) — operator typo.
            Assert.IsFalse(DeleteAgencyCommandParser.TryParse(
                "Alice Bob --confirm", out _, out _, out var err));
            StringAssert.Contains(err, "too many tokens");
        }

        [TestMethod]
        public void TryParse_DoubleSpaces_Tolerated()
        {
            Assert.IsTrue(DeleteAgencyCommandParser.TryParse(
                "Alice   --confirm",
                out var token, out var confirmed, out _));
            Assert.AreEqual("Alice", token);
            Assert.IsTrue(confirmed);
        }

        [TestMethod]
        public void UsageBanner_AdvertisesDestructiveSemantics()
        {
            // The banner is what an operator sees when they forget --confirm; it
            // must make the destructive nature LOUD enough that operators read
            // the consequences before re-running.
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "/deleteagency");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "--confirm");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "Destructive");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "LOST");
            StringAssert.Contains(DeleteAgencyCommandParser.UsageBanner, "No undo".ToLower(),
                "banner mentions the no-undo property in some form");
        }
    }
}
