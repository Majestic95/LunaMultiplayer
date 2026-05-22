using System;
using System.IO;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class HashVerifierTest
    {
        // Known-answer SHA-256 of the byte sequence "Luna Multiplayer\n"
        // (UTF-8, trailing LF). Confirmed via PowerShell:
        //   $b = [Text.Encoding]::UTF8.GetBytes("Luna Multiplayer`n")
        //   [BitConverter]::ToString((New-Object Security.Cryptography.SHA256Managed).ComputeHash($b)) -replace '-'
        // The cohort-local pinning means a future SHA-256 impl swap would
        // break this test if it ever produced a wrong digest.
        private const string KnownContent = "Luna Multiplayer\n";
        private const string KnownDigest = "668b1d1f9531386b254064e65289012d79d44cb88dda49d0715a8e1c4b469dd2";

        private string _tempPath = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _tempPath = Path.Combine(
                Path.GetTempPath(),
                $"HashVerifierTest-{Guid.NewGuid():N}.bin");
        }

        [TestCleanup]
        public void Teardown()
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }

        // --- Happy path ---

        [TestMethod]
        public void VerifyFile_MatchingDigest_ReturnsVerified()
        {
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, KnownDigest);

            Assert.AreEqual(HashVerifier.Outcome.Verified, result.Outcome);
            Assert.AreEqual(KnownDigest, result.ComputedHex);
            Assert.AreEqual(KnownDigest, result.ExpectedHex);
        }

        [TestMethod]
        public void VerifyFile_MatchingDigestWithSha256Prefix_ReturnsVerified()
        {
            // GitHub's digest field carries the prefix. The verifier strips
            // it transparently so callers can pass the raw field value.
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, "sha256:" + KnownDigest);

            Assert.AreEqual(HashVerifier.Outcome.Verified, result.Outcome);
        }

        [TestMethod]
        public void VerifyFile_UppercaseExpectedHex_NormalisesAndVerifies()
        {
            // Hex case is operator-uploader-dependent; we accept either case
            // by normalising the expected to lowercase before comparing.
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, KnownDigest.ToUpperInvariant());

            Assert.AreEqual(HashVerifier.Outcome.Verified, result.Outcome);
        }

        // --- Mismatch ---

        [TestMethod]
        public void VerifyFile_WrongDigest_ReturnsMismatch()
        {
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));
            const string wrongDigest = "0000000000000000000000000000000000000000000000000000000000000000";

            var result = HashVerifier.VerifyFile(_tempPath, wrongDigest);

            Assert.AreEqual(HashVerifier.Outcome.Mismatch, result.Outcome);
            Assert.AreEqual(KnownDigest, result.ComputedHex);
            Assert.AreEqual(wrongDigest, result.ExpectedHex);
        }

        [TestMethod]
        public void VerifyFile_CorruptedFile_ReturnsMismatch()
        {
            // Simulate a corrupted download: one byte flipped from the known
            // content. SHA-256 is a cryptographic hash so even a single bit
            // produces a completely different digest.
            var bytes = System.Text.Encoding.UTF8.GetBytes(KnownContent);
            bytes[0] ^= 0x01;
            File.WriteAllBytes(_tempPath, bytes);

            var result = HashVerifier.VerifyFile(_tempPath, KnownDigest);

            Assert.AreEqual(HashVerifier.Outcome.Mismatch, result.Outcome);
        }

        // --- Skipped: no expected digest provided ---

        [TestMethod]
        public void VerifyFile_NullExpected_ReturnsSkipped()
        {
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, null);

            Assert.AreEqual(HashVerifier.Outcome.Skipped, result.Outcome);
            Assert.IsNull(result.ComputedHex);
            Assert.IsNull(result.ExpectedHex);
        }

        [TestMethod]
        public void VerifyFile_EmptyExpected_ReturnsSkipped()
        {
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, "");

            Assert.AreEqual(HashVerifier.Outcome.Skipped, result.Outcome);
        }

        [TestMethod]
        public void VerifyFile_WhitespaceExpected_ReturnsSkipped()
        {
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, "   ");

            Assert.AreEqual(HashVerifier.Outcome.Skipped, result.Outcome);
        }

        // --- Skipped: malformed expected ---

        [TestMethod]
        public void VerifyFile_NonSha256Hex_ReturnsSkipped()
        {
            // Anything that isn't 64 hex chars (after optional prefix strip)
            // is treated as Skipped — better than falsely-verifying against
            // a hash algorithm we can't compute.
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, "sha512:abc");

            Assert.AreEqual(HashVerifier.Outcome.Skipped, result.Outcome);
        }

        [TestMethod]
        public void VerifyFile_TruncatedHex_ReturnsSkipped()
        {
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            var result = HashVerifier.VerifyFile(_tempPath, "abcdef");

            Assert.AreEqual(HashVerifier.Outcome.Skipped, result.Outcome);
        }

        [TestMethod]
        public void VerifyFile_HexWithNonHexChars_ReturnsSkipped()
        {
            File.WriteAllBytes(_tempPath, System.Text.Encoding.UTF8.GetBytes(KnownContent));

            // 64 chars but with a 'g' in the middle — not valid hex.
            var bad = new string('a', 31) + "g" + new string('a', 32);
            var result = HashVerifier.VerifyFile(_tempPath, bad);

            Assert.AreEqual(HashVerifier.Outcome.Skipped, result.Outcome);
        }

        // --- I/O paths ---

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void VerifyFile_EmptyPath_Throws()
        {
            HashVerifier.VerifyFile("", KnownDigest);
        }

        [TestMethod]
        [ExpectedException(typeof(FileNotFoundException))]
        public void VerifyFile_MissingFile_Throws()
        {
            // The caller is the one who downloaded the file; a missing file
            // here represents a bug, not a recoverable state.
            HashVerifier.VerifyFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".does-not-exist"),
                KnownDigest);
        }

        // --- Streamed hash sanity (large file) ---

        [TestMethod]
        public void VerifyFile_LargerFile_HashedCorrectly()
        {
            // ~1 MB to exercise the streaming buffer path (65 KB chunks).
            // We don't pin a specific digest here because we control the
            // bytes and just verify the round-trip: hash the file, pass that
            // hash as 'expected', expect Verified.
            var random = new Random(12345);
            var bytes = new byte[1024 * 1024];
            random.NextBytes(bytes);
            File.WriteAllBytes(_tempPath, bytes);

            // First call: pass a sentinel "wrong" digest to capture the
            // computed hash from the Mismatch result.
            const string zero = "0000000000000000000000000000000000000000000000000000000000000000";
            var first = HashVerifier.VerifyFile(_tempPath, zero);
            Assert.AreEqual(HashVerifier.Outcome.Mismatch, first.Outcome);
            Assert.IsNotNull(first.ComputedHex);

            // Second call: pass the computed hash back — expect Verified.
            var second = HashVerifier.VerifyFile(_tempPath, first.ComputedHex);
            Assert.AreEqual(HashVerifier.Outcome.Verified, second.Outcome);
        }
    }
}
