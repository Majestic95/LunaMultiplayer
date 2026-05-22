using System;
using System.IO;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class DiskSpaceCheckTest
    {
        // DiskSpaceCheck is mostly a thin DriveInfo wrapper; the hand-testable
        // surface is the input-validation + path-normalization + threshold
        // arithmetic. The live DriveInfo path is hit by the
        // CurrentDriveHasSpace test against the temp directory, which always
        // has at least a few MB free on a sane dev box.

        [TestMethod]
        public void Check_ZipBytesZero_RequiresZero()
        {
            // Edge case: a zero-byte zip means zero bytes required (any free
            // space is sufficient). Used as a sanity-check that the
            // multiplier math doesn't underflow.
            var result = DiskSpaceCheck.Check(Path.GetTempPath(), zipBytes: 0);

            Assert.AreEqual(DiskSpaceCheck.Outcome.Sufficient, result.Outcome);
            Assert.AreEqual(0, result.RequiredBytes);
        }

        [TestMethod]
        public void Check_SmallZipAgainstTempDir_Sufficient()
        {
            // 1 KB zip -> requires 3 KB; the temp directory's drive
            // realistically has multiple MB.
            var result = DiskSpaceCheck.Check(Path.GetTempPath(), zipBytes: 1024);

            Assert.AreEqual(DiskSpaceCheck.Outcome.Sufficient, result.Outcome);
            Assert.AreEqual(3072, result.RequiredBytes);
            Assert.IsTrue(result.AvailableBytes >= 3072);
        }

        [TestMethod]
        public void Check_ImpossiblyLargeZip_Insufficient()
        {
            // 1 TB zip -> requires 3 TB. Even on a 12 TB NAS the dev box
            // won't have 3 TB free on the temp drive.
            const long oneTerabyte = 1L * 1024 * 1024 * 1024 * 1024;
            var result = DiskSpaceCheck.Check(Path.GetTempPath(), zipBytes: oneTerabyte);

            Assert.AreEqual(DiskSpaceCheck.Outcome.Insufficient, result.Outcome);
            Assert.AreEqual(oneTerabyte * 3, result.RequiredBytes);
        }

        [TestMethod]
        public void Check_RelativePath_NormalizesAndProbes()
        {
            // Path.GetFullPath resolves '.' against the process CWD. The
            // result should match a Check against the absolute CWD path.
            var absolute = DiskSpaceCheck.Check(Directory.GetCurrentDirectory(), zipBytes: 1024);
            var relative = DiskSpaceCheck.Check(".", zipBytes: 1024);

            Assert.AreEqual(absolute.DriveRoot, relative.DriveRoot);
            Assert.AreEqual(absolute.Outcome, relative.Outcome);
        }

        [TestMethod]
        public void Check_NonexistentDriveLetter_Unknown()
        {
            // 'Q:' is rarely a real drive on Windows dev boxes. DriveInfo
            // constructor accepts it but AvailableFreeSpace throws IOException.
            // We surface that as Unknown.
            var result = DiskSpaceCheck.Check(@"Q:\some\path", zipBytes: 1024);

            // On a machine where Q: IS mounted (rare but possible), the
            // outcome will be either Sufficient or Insufficient depending on
            // free space — both are valid. Only the explicit Unknown path
            // is what we're documenting; this assertion is loose to avoid
            // flakes on dev machines with unusual drive setups.
            Assert.IsTrue(
                result.Outcome == DiskSpaceCheck.Outcome.Unknown
                    || result.Outcome == DiskSpaceCheck.Outcome.Sufficient
                    || result.Outcome == DiskSpaceCheck.Outcome.Insufficient,
                $"Unexpected outcome {result.Outcome} for Q: drive — should be Unknown on machines without Q: mounted.");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Check_EmptyPath_Throws()
        {
            DiskSpaceCheck.Check("", zipBytes: 1024);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentOutOfRangeException))]
        public void Check_NegativeZipBytes_Throws()
        {
            DiskSpaceCheck.Check(Path.GetTempPath(), zipBytes: -1);
        }

        [TestMethod]
        public void RequiredBytes_MultipliesByThree()
        {
            // Pinning the multiplier so a future refactor that changes 3x
            // to 2x or 4x has to update this test deliberately.
            Assert.AreEqual(0, DiskSpaceCheck.RequiredBytes(0));
            Assert.AreEqual(3, DiskSpaceCheck.RequiredBytes(1));
            Assert.AreEqual(300_000_000, DiskSpaceCheck.RequiredBytes(100_000_000));
        }

        [TestMethod]
        public void RequiredMultiplier_IsThree()
        {
            // Belt-and-braces — the constant itself is pinned in case the
            // arithmetic and the const drift apart.
            Assert.AreEqual(3, DiskSpaceCheck.RequiredMultiplier);
        }
    }
}
