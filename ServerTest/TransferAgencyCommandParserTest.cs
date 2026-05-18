using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Stage 5.18d slice (e) <see cref="TransferAgencyCommandParser"/>.
    /// Pure parser grammar; the resolver + lock + mutation paths live in
    /// AgencySystemTest (TryRenameAgencyOwner) and TransferAgencyCommandTest (end-to-end).
    /// </summary>
    [TestClass]
    public class TransferAgencyCommandParserTest
    {
        [TestMethod]
        public void TryParse_TwoArgs_ValidInputs()
        {
            Assert.IsTrue(TransferAgencyCommandParser.TryParse(
                "Alice Bob",
                out var src, out var newName, out var err));
            Assert.AreEqual("Alice", src);
            Assert.AreEqual("Bob", newName);
            Assert.AreEqual(string.Empty, err);
        }

        [TestMethod]
        public void TryParse_GuidArg_PassesThrough()
        {
            // Resolver decides whether the token is a Guid or a name; the parser
            // is just split-on-whitespace.
            Assert.IsTrue(TransferAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92 Bob",
                out var src, out var newName, out _));
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", src);
            Assert.AreEqual("Bob", newName);
        }

        [TestMethod]
        public void TryParse_Empty_FailsWithReason()
        {
            Assert.IsFalse(TransferAgencyCommandParser.TryParse(
                string.Empty, out _, out _, out var err));
            StringAssert.Contains(err, "no arguments");

            Assert.IsFalse(TransferAgencyCommandParser.TryParse(
                "   ", out _, out _, out err));
            StringAssert.Contains(err, "no arguments");
        }

        [TestMethod]
        public void TryParse_OneArg_FailsWithReason()
        {
            Assert.IsFalse(TransferAgencyCommandParser.TryParse(
                "Alice", out _, out _, out var err));
            StringAssert.Contains(err, "expected 2 arguments");
            StringAssert.Contains(err, "got 1");
        }

        [TestMethod]
        public void TryParse_ThreeArgs_FailsWithReasonCallingOutSpaces()
        {
            // LMP player handles cannot contain spaces (HandshakeSystemValidator).
            // A 3-token input means the operator typed a space-containing name —
            // most likely a typo. Fail with a hint.
            Assert.IsFalse(TransferAgencyCommandParser.TryParse(
                "Alice Bob Charlie", out _, out _, out var err));
            StringAssert.Contains(err, "got 3");
            StringAssert.Contains(err, "spaces");
        }

        [TestMethod]
        public void TryParse_DoubleSpaces_Tolerated()
        {
            Assert.IsTrue(TransferAgencyCommandParser.TryParse(
                "Alice   Bob",
                out var src, out var newName, out _));
            Assert.AreEqual("Alice", src);
            Assert.AreEqual("Bob", newName);
        }

        [TestMethod]
        public void UsageBanner_DocumentsBothTokensAndIntent()
        {
            // GUI launcher displays this; verify the contract surfaces:
            // - command name, both required arguments,
            // - cross-reference to /listagencies columns,
            // - explanation that vessel ids are preserved (so operators don't
            //   think transferagency moves vessels between agencies — that's
            //   /deleteagency cascade).
            StringAssert.Contains(TransferAgencyCommandParser.UsageBanner, "/transferagency");
            StringAssert.Contains(TransferAgencyCommandParser.UsageBanner, "<agency-id|owner>");
            StringAssert.Contains(TransferAgencyCommandParser.UsageBanner, "<new-player-name>");
            StringAssert.Contains(TransferAgencyCommandParser.UsageBanner, "/listagencies");
            StringAssert.Contains(TransferAgencyCommandParser.UsageBanner, "Vessels keep their AgencyId");
        }
    }
}
