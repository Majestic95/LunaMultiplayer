using System;
using System.IO;
using System.Security.Cryptography;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Verifies a downloaded release asset's bytes against the SHA-256 digest
    // GitHub publishes alongside the asset metadata. We do this BEFORE
    // backing up and overlaying so a corrupted download (truncated transfer,
    // MITM, disk error) never reaches the player's KSP install.
    //
    // Digest source: GitHub's release-asset 'digest' field, present on every
    // Majestic95 release since v0.30.0-private-1 (rolled out by GitHub in
    // October 2024). Older releases have a null digest — VerifyFile reports
    // Skipped in that case and the Forms layer surfaces a warning instead of
    // refusing the install, so players can still update from a pre-digest
    // release if they accept the risk.
    //
    // Computation: SHA-256 over the file bytes, streamed in chunks. We do
    // NOT load the whole file into memory — release zips can be 70+ MB
    // (selfcontained AdminGui flavour) and we want the verifier callable
    // from the Forms thread without blocking.
    //
    // Comparison: lowercase hex string equality. The hex output of
    // SHA256.ComputeHash + BitConverter is uppercase by default, so we
    // normalise via ToLowerInvariant. GitHub's digest is already lowercase
    // post-prefix-strip in GitHubClient.
    public static class HashVerifier
    {
        public enum Outcome
        {
            // File bytes hashed to the expected digest. Safe to install.
            Verified,

            // File bytes hashed to a different digest. The download is
            // corrupt or tampered — refuse the install.
            Mismatch,

            // No expected digest was provided (release predates GitHub's
            // digest rollout, or asset has no digest field). Caller decides
            // whether to warn-and-proceed or refuse.
            Skipped,
        }

        // Computes SHA-256 of the file at filePath and compares against the
        // expected digest. expectedDigest may be:
        //   - null or whitespace          -> returns Skipped
        //   - "sha256:<64-hex>"           -> prefix stripped, compared
        //   - "<64-hex>"                  -> compared directly
        //   - anything else               -> returns Skipped (we don't trust
        //     unknown hash algorithms — better to warn than to falsely
        //     verify against a hash we can't compute).
        //
        // The Result struct carries the actual computed hex (for diagnostic
        // log lines) and the expected hex (normalized) so the Forms layer
        // can show both halves to the player on Mismatch.
        public static Result VerifyFile(string filePath, string? expectedDigest)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("filePath must be non-empty.", nameof(filePath));
            }

            if (!TryNormaliseExpected(expectedDigest, out var expectedHex))
            {
                return new Result(Outcome.Skipped, ComputedHex: null, ExpectedHex: null);
            }

            string computedHex;
            using (var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: false))
            using (var sha = SHA256.Create())
            {
                var hashBytes = sha.ComputeHash(stream);
                computedHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            var outcome = string.Equals(computedHex, expectedHex, StringComparison.Ordinal)
                ? Outcome.Verified
                : Outcome.Mismatch;
            return new Result(outcome, computedHex, expectedHex);
        }

        // Strips an optional 'sha256:' prefix, trims, lowercases, and
        // validates the remaining string is a 64-char hex digit run. Anything
        // that fails validation is treated as Skipped — we won't compute and
        // compare against garbage.
        internal static bool TryNormaliseExpected(string? expectedDigest, out string normalised)
        {
            normalised = string.Empty;
            if (string.IsNullOrWhiteSpace(expectedDigest)) return false;

            var trimmed = expectedDigest.Trim();
            const string prefix = "sha256:";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(prefix.Length);
            }

            trimmed = trimmed.ToLowerInvariant();
            if (trimmed.Length != 64) return false;

            foreach (var c in trimmed)
            {
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex) return false;
            }

            normalised = trimmed;
            return true;
        }

        public readonly record struct Result(
            Outcome Outcome,
            string? ComputedHex,
            string? ExpectedHex);
    }
}
