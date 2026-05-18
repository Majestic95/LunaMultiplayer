using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;
using System.Globalization;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.18d slice (f) <see cref="SetAgencyCommandParser"/>. Pins
    /// every branch of the input grammar — subcommand parsing (case-insensitive +
    /// alias), invariant-culture amount parsing, error messages, banner format. The
    /// command is the operator's most-likely typo surface; clear error feedback +
    /// stable usage banner are both load-bearing for the Stage 5.18+ GUI launcher
    /// (which surfaces errors and uses the banner for inline help).
    /// </summary>
    [TestClass]
    public class SetAgencyCommandParserTest
    {
        private CultureInfo _originalCulture;

        [TestInitialize]
        public void Setup()
        {
            // Force a comma-decimal culture so a regression that drops the explicit
            // CultureInfo.InvariantCulture pass in the parser shows up as a "valid
            // number" string failing to parse.
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        }

        [TestCleanup]
        public void Teardown()
        {
            Thread.CurrentThread.CurrentCulture = _originalCulture;
        }

        [TestMethod]
        public void TryParse_Funds_ValidInputs()
        {
            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "funds Alice 25000",
                out var sub, out var token, out var value, out var error));

            Assert.AreEqual(SetAgencyCommandParser.Scalar.Funds, sub);
            Assert.AreEqual("Alice", token);
            Assert.AreEqual(25000d, value);
            Assert.AreEqual(string.Empty, error);
        }

        [TestMethod]
        public void TryParse_Science_ValidInputs()
        {
            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "science f0ea9ad24719407ab370342d7effae92 1234.5",
                out var sub, out var token, out var value, out _));

            Assert.AreEqual(SetAgencyCommandParser.Scalar.Science, sub);
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", token);
            Assert.AreEqual(1234.5d, value);
        }

        [TestMethod]
        public void TryParse_Reputation_ValidInputs()
        {
            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "reputation Bob -50",
                out var sub, out _, out var value, out _));

            Assert.AreEqual(SetAgencyCommandParser.Scalar.Reputation, sub);
            Assert.AreEqual(-50d, value);
        }

        [TestMethod]
        public void TryParse_RepAlias_ResolvesToReputation()
        {
            // Operator-friendly short form. "Rep" is the natural KSP / ksp.log abbreviation
            // and operators typing the full word repeatedly is friction-inducing.
            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "rep Bob 12.5",
                out var sub, out _, out _, out _));

            Assert.AreEqual(SetAgencyCommandParser.Scalar.Reputation, sub);
        }

        [TestMethod]
        public void TryParse_SubcommandCaseInsensitive()
        {
            // Operator typing across multiple sessions inevitably mixes cases.
            // Subcommand matching is case-insensitive (ToLowerInvariant); the agency
            // token and the amount are NOT — the token might be a case-sensitive
            // player name. Pin the case-insensitivity here and the case-sensitivity
            // of the token in a separate test below.
            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "FUNDS Alice 100",
                out var sub, out _, out _, out _));
            Assert.AreEqual(SetAgencyCommandParser.Scalar.Funds, sub);

            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "Science Alice 100",
                out sub, out _, out _, out _));
            Assert.AreEqual(SetAgencyCommandParser.Scalar.Science, sub);
        }

        [TestMethod]
        public void TryParse_TokenCaseSensitive()
        {
            // Player names in LMP are case-sensitive — the AgencyByPlayerName index
            // uses StringComparer.Ordinal. Verify the parser preserves case so the
            // resolver gets the exact string the operator typed.
            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "funds AlIcE 100",
                out _, out var token, out _, out _));
            Assert.AreEqual("AlIcE", token);
        }

        [TestMethod]
        public void TryParse_EmptyInput_FailsWithReason()
        {
            Assert.IsFalse(SetAgencyCommandParser.TryParse(
                string.Empty,
                out _, out _, out _, out var error));
            StringAssert.Contains(error, "no arguments");

            Assert.IsFalse(SetAgencyCommandParser.TryParse(
                "   ",
                out _, out _, out _, out error));
            StringAssert.Contains(error, "no arguments");
        }

        [TestMethod]
        public void TryParse_TooFewArguments_FailsWithReason()
        {
            Assert.IsFalse(SetAgencyCommandParser.TryParse(
                "funds Alice",
                out _, out _, out _, out var error));
            StringAssert.Contains(error, "expected 3 arguments");
            StringAssert.Contains(error, "got 2");
        }

        [TestMethod]
        public void TryParse_UnknownSubcommand_FailsWithReason()
        {
            // Typo: setagency reputation → setagency reput
            Assert.IsFalse(SetAgencyCommandParser.TryParse(
                "reput Alice 50",
                out _, out _, out _, out var error));
            StringAssert.Contains(error, "unknown subcommand");
            StringAssert.Contains(error, "'reput'");
            StringAssert.Contains(error, "funds | science | reputation");
        }

        [TestMethod]
        public void TryParse_InvalidNumber_FailsWithReason()
        {
            Assert.IsFalse(SetAgencyCommandParser.TryParse(
                "funds Alice not-a-number",
                out _, out _, out _, out var error));
            StringAssert.Contains(error, "not a valid number");
        }

        [TestMethod]
        public void TryParse_GermanCommaDecimal_RejectedUnderInvariantCulture()
        {
            // Setup forced CurrentCulture to de-DE. The parser MUST pin
            // InvariantCulture so an operator on a German server doesn't end up
            // setting Funds to 25 when they typed "25,000". The shared-agency
            // SetFundsCommand uses default-culture parse — this is the bug we're
            // not propagating.
            Assert.IsFalse(SetAgencyCommandParser.TryParse(
                "funds Alice 25000,5",
                out _, out _, out _, out var error));
            StringAssert.Contains(error, "not a valid number");
        }

        [TestMethod]
        public void TryParse_DoubleSpaces_Tolerated()
        {
            // Operator copy-pasting from documentation often introduces double-spaces.
            // The split-and-drop-empties pattern handles them silently rather than
            // failing with an unhelpful "expected 3, got 5" error.
            Assert.IsTrue(SetAgencyCommandParser.TryParse(
                "funds   Alice   100",
                out var sub, out var token, out var value, out _));
            Assert.AreEqual(SetAgencyCommandParser.Scalar.Funds, sub);
            Assert.AreEqual("Alice", token);
            Assert.AreEqual(100d, value);
        }

        [TestMethod]
        public void UsageBanner_ListsAllThreeSubcommands()
        {
            // GUI launcher will likely include this banner in its inline help; pin
            // the format so a regression that drops the alphabetical ordering or
            // omits a subcommand surfaces here.
            StringAssert.Contains(SetAgencyCommandParser.UsageBanner, "/setagency funds");
            StringAssert.Contains(SetAgencyCommandParser.UsageBanner, "/setagency science");
            StringAssert.Contains(SetAgencyCommandParser.UsageBanner, "/setagency reputation");
            // Banner mirrors /listagencies output columns (id= / owner=) so a GUI
            // launcher's form-field labels stay consistent across the two surfaces.
            StringAssert.Contains(SetAgencyCommandParser.UsageBanner, "<agency-id|owner>");
            StringAssert.Contains(SetAgencyCommandParser.UsageBanner, "<amount>");
            // Reputation alias surfaced in the banner so operators see it without
            // reading source / docs (consumer-lens v1 C2).
            StringAssert.Contains(SetAgencyCommandParser.UsageBanner, "reputation|rep");
            // Cross-surface vocabulary pointer for operators switching between
            // /listagencies and /setagency.
            StringAssert.Contains(SetAgencyCommandParser.UsageBanner, "/listagencies");
        }
    }
}
