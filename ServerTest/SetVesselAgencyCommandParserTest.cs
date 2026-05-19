using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Command.Command;

namespace ServerTest
{
    /// <summary>
    /// Coverage for the Phase 3 Slice E-2 <see cref="SetVesselAgencyCommandParser"/>.
    /// Pure parser grammar; the resolver + lock + mutation paths live in
    /// <see cref="SetVesselAgencyCommandTest"/> (direct Execute()) and
    /// <c>MockClientTest/CrossRouterVesselTransferTest</c> (end-to-end wire).
    /// </summary>
    [TestClass]
    public class SetVesselAgencyCommandParserTest
    {
        [TestMethod]
        public void TryParse_TwoArgs_ValidInputs()
        {
            Assert.IsTrue(SetVesselAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92 Bob",
                out var vesselToken, out var agencyToken, out var err));
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", vesselToken);
            Assert.AreEqual("Bob", agencyToken);
            Assert.AreEqual(string.Empty, err);
        }

        [TestMethod]
        public void TryParse_HyphenatedGuid_PassesThrough()
        {
            // "D" form (hyphenated) is what Guid.ToString() defaults to and what
            // /listclients output uses. Parser doesn't normalize — the command's
            // Guid.TryParse call accepts both N and D forms.
            Assert.IsTrue(SetVesselAgencyCommandParser.TryParse(
                "f0ea9ad2-4719-407a-b370-342d7effae92 Bob",
                out var vesselToken, out var agencyToken, out _));
            Assert.AreEqual("f0ea9ad2-4719-407a-b370-342d7effae92", vesselToken);
            Assert.AreEqual("Bob", agencyToken);
        }

        [TestMethod]
        public void TryParse_TwoGuidArgs_PassesThrough()
        {
            // Agency token can also be a Guid (operator using /listagencies id=).
            // Parser is just split-on-whitespace; the command's TryResolveAgencyToken
            // decides whether the token is an id or a name.
            Assert.IsTrue(SetVesselAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92 abcd1234567890abcdef1234567890ab",
                out var vesselToken, out var agencyToken, out _));
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", vesselToken);
            Assert.AreEqual("abcd1234567890abcdef1234567890ab", agencyToken);
        }

        [TestMethod]
        public void TryParse_Empty_FailsWithReason()
        {
            Assert.IsFalse(SetVesselAgencyCommandParser.TryParse(
                string.Empty, out _, out _, out var err));
            StringAssert.Contains(err, "no arguments");

            Assert.IsFalse(SetVesselAgencyCommandParser.TryParse(
                "   ", out _, out _, out err));
            StringAssert.Contains(err, "no arguments");
        }

        [TestMethod]
        public void TryParse_OneArg_FailsWithReason()
        {
            Assert.IsFalse(SetVesselAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92", out _, out _, out var err));
            StringAssert.Contains(err, "expected 2 arguments");
            StringAssert.Contains(err, "got 1");
        }

        [TestMethod]
        public void TryParse_ThreeArgs_FailsWithReasonCallingOutSpaces()
        {
            // Neither vessel guids nor player handles contain spaces; a 3-token
            // input means the operator typo'd a space somewhere.
            Assert.IsFalse(SetVesselAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92 Bob Charlie", out _, out _, out var err));
            StringAssert.Contains(err, "got 3");
            StringAssert.Contains(err, "spaces");
        }

        [TestMethod]
        public void TryParse_DoubleSpaces_Tolerated()
        {
            Assert.IsTrue(SetVesselAgencyCommandParser.TryParse(
                "f0ea9ad24719407ab370342d7effae92   Bob",
                out var vesselToken, out var agencyToken, out _));
            Assert.AreEqual("f0ea9ad24719407ab370342d7effae92", vesselToken);
            Assert.AreEqual("Bob", agencyToken);
        }

        [TestMethod]
        public void UsageBanner_DocumentsBothTokensAndMigrationPolicy()
        {
            // GUI launcher displays this; verify the contract surfaces:
            // - command name, both required arguments,
            // - per-router migration policy hint (so operators reading the help
            //   know kolony moves, orbital depends on Destination, planetary
            //   doesn't migrate),
            // - reversibility note (so operators know there's no --confirm and
            //   they can re-issue to undo).
            StringAssert.Contains(SetVesselAgencyCommandParser.UsageBanner, "/setvesselagency");
            StringAssert.Contains(SetVesselAgencyCommandParser.UsageBanner, "<vessel-guid>");
            StringAssert.Contains(SetVesselAgencyCommandParser.UsageBanner, "<agency-id|owner>");
            StringAssert.Contains(SetVesselAgencyCommandParser.UsageBanner, "kolony");
            StringAssert.Contains(SetVesselAgencyCommandParser.UsageBanner, "orbital");
            StringAssert.Contains(SetVesselAgencyCommandParser.UsageBanner, "planetary");
            StringAssert.Contains(SetVesselAgencyCommandParser.UsageBanner, "Reversible");
        }
    }
}
