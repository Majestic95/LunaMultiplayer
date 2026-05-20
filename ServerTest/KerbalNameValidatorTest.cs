using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;

namespace ServerTest
{
    /// <summary>
    /// [Stage 6 Phase 6.9-hardening] Pins every branch of
    /// <see cref="KerbalNameValidator.IsValid"/>. Names that flow into per-
    /// agency kerbal-file path construction MUST be validated at the wire
    /// boundary; this test family is the regression fence against future
    /// changes that might weaken the validator.
    /// </summary>
    [TestClass]
    public class KerbalNameValidatorTest
    {
        // -------------------------------------------------------------------
        // Happy paths — legitimate KSP-stock + modded kerbal names
        // -------------------------------------------------------------------

        [TestMethod]
        public void IsValid_StockJebediah_True()
        {
            Assert.IsTrue(KerbalNameValidator.IsValid("Jebediah Kerman", out var reason));
            Assert.IsNull(reason);
        }

        [TestMethod]
        public void IsValid_NameWithApostrophe_True()
        {
            // Real-Names mod can produce names like "O'Brian Kerman".
            Assert.IsTrue(KerbalNameValidator.IsValid("O'Brian Kerman", out _));
        }

        [TestMethod]
        public void IsValid_NameWithHyphen_True()
        {
            Assert.IsTrue(KerbalNameValidator.IsValid("Anne-Marie Kerman", out _));
        }

        [TestMethod]
        public void IsValid_NameWithPeriod_True()
        {
            // Period in the middle of a name is fine — only "." and ".." as
            // FULL names are rejected.
            Assert.IsTrue(KerbalNameValidator.IsValid("Dr. Wernher Kerman", out _));
        }

        [TestMethod]
        public void IsValid_NameAtMaxLength_True()
        {
            // 64-char name should be accepted at the boundary.
            var name = new string('A', KerbalNameValidator.MaxLength);
            Assert.IsTrue(KerbalNameValidator.IsValid(name, out _));
        }

        // -------------------------------------------------------------------
        // Empty / whitespace
        // -------------------------------------------------------------------

        [TestMethod]
        public void IsValid_Null_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid(null, out var reason));
            StringAssert.Contains(reason, "empty");
        }

        [TestMethod]
        public void IsValid_Empty_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("", out _));
        }

        [TestMethod]
        public void IsValid_WhitespaceOnly_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("   ", out var reason));
            StringAssert.Contains(reason, "whitespace");
        }

        // -------------------------------------------------------------------
        // Length cap
        // -------------------------------------------------------------------

        [TestMethod]
        public void IsValid_NameOverMaxLength_False()
        {
            var name = new string('A', KerbalNameValidator.MaxLength + 1);
            Assert.IsFalse(KerbalNameValidator.IsValid(name, out var reason));
            StringAssert.Contains(reason, "length");
            StringAssert.Contains(reason, (KerbalNameValidator.MaxLength + 1).ToString());
        }

        [TestMethod]
        public void IsValid_NameAtOneHundredMegabytes_FastReject()
        {
            // Reject MUST fire on length check before any per-char scan to
            // prevent log-amplification on hostile input. The reason text
            // carries the length integer but NOT the name itself.
            var name = new string('A', 100_000_000);
            Assert.IsFalse(KerbalNameValidator.IsValid(name, out var reason));
            StringAssert.Contains(reason, "length");
            // Reason MUST NOT echo the name (recursive log amplification).
            Assert.IsFalse(reason.Contains(name.Substring(0, 100)),
                "Reason text must not echo the rejected name body.");
        }

        // -------------------------------------------------------------------
        // Path-traversal vectors
        // -------------------------------------------------------------------

        [TestMethod]
        public void IsValid_DotDot_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("..", out var reason));
            StringAssert.Contains(reason, "reserved");
        }

        [TestMethod]
        public void IsValid_SingleDot_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid(".", out _));
        }

        [TestMethod]
        public void IsValid_PathTraversalUnixStyle_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("../../etc/passwd", out var reason));
            StringAssert.Contains(reason, "path-separator");
            StringAssert.Contains(reason, "/");
        }

        [TestMethod]
        public void IsValid_PathTraversalWindowsStyle_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid(@"..\..\Settings\GeneralSettings", out var reason));
            StringAssert.Contains(reason, "path-separator");
            StringAssert.Contains(reason, "\\");
        }

        [TestMethod]
        public void IsValid_RootedPathWindowsAbsolute_False()
        {
            // Path.Combine drops the first arg if the second is rooted — the
            // most dangerous variant. Reject closes the arbitrary-file-write
            // sink. The drive separator ':' is caught BEFORE Path.IsPathRooted.
            Assert.IsFalse(KerbalNameValidator.IsValid(@"C:\Windows\Temp\pwn", out var reason));
            StringAssert.Contains(reason, "drive-separator");
        }

        [TestMethod]
        public void IsValid_RootedPathUnixAbsolute_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("/tmp/pwn", out var reason));
            StringAssert.Contains(reason, "path-separator");
        }

        [TestMethod]
        public void IsValid_NameContainingForwardSlash_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("Foo/Bar Kerman", out var reason));
            StringAssert.Contains(reason, "/");
        }

        [TestMethod]
        public void IsValid_NameContainingBackslash_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid(@"Foo\Bar Kerman", out var reason));
            StringAssert.Contains(reason, "\\");
        }

        [TestMethod]
        public void IsValid_NameContainingColon_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("Foo:Bar Kerman", out var reason));
            StringAssert.Contains(reason, "drive-separator");
        }

        // -------------------------------------------------------------------
        // Control characters
        // -------------------------------------------------------------------

        [TestMethod]
        public void IsValid_NameWithNullChar_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("Jeb\0Kerman", out var reason));
            StringAssert.Contains(reason, "control char");
        }

        [TestMethod]
        public void IsValid_NameWithNewline_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("Jeb\nKerman", out var reason));
            StringAssert.Contains(reason, "control char");
        }

        [TestMethod]
        public void IsValid_NameWithTab_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("Jeb\tKerman", out var reason));
            StringAssert.Contains(reason, "control char");
        }

        [TestMethod]
        public void IsValid_NameWithEscape_False()
        {
            Assert.IsFalse(KerbalNameValidator.IsValid("Jeb\x1bKerman", out _));
        }

        // -------------------------------------------------------------------
        // Reason-text safety — never echo full malicious input
        // -------------------------------------------------------------------

        [TestMethod]
        public void IsValid_RejectionReason_DoesNotEchoFullMaliciousLongName()
        {
            // Length-overflow reason carries the integer length, NOT the name
            // body. Important: a malicious 100MB name's reason text should be
            // O(constant), not O(name.Length).
            var name = new string('A', 1_000_000);
            Assert.IsFalse(KerbalNameValidator.IsValid(name, out var reason));
            Assert.IsTrue(reason.Length < 200,
                $"Reason text must be O(constant) length; was {reason.Length} chars for a 1MB input.");
        }
    }
}
