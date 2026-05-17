using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ServerTest
{
    /// <summary>
    /// Coverage for <see cref="FileHandler.WriteAtomic"/> + <see cref="FileHandler.ReadAtomic"/>
    /// added in Stage 5.14c. These pin the four contract points operators care about:
    /// 1. happy-path round-trip,
    /// 2. .bak rotation on second write so a prior generation is recoverable,
    /// 3. ReadAtomic falls back to .bak when the canonical path is missing (simulates a
    ///    crash between rotate and rename — the window WriteAtomic explicitly designs for),
    /// 4. ReadAtomic does NOT trust .tmp — leftover .tmp from a previous failed write must
    ///    not resurrect a potentially partial payload.
    /// </summary>
    [TestClass]
    public class FileHandlerAtomicWriteTest
    {
        private string _testDir;

        [TestInitialize]
        public void Setup()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "lmp-fh-atomic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        [TestCleanup]
        public void Teardown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }

        [TestMethod]
        public void WriteAtomic_FirstWrite_LeavesOnlyCanonicalFile()
        {
            var path = Path.Combine(_testDir, "first.txt");

            FileHandler.WriteAtomic(path, "alpha");

            Assert.AreEqual("alpha", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"), ".tmp must be renamed away after a successful write");
            Assert.IsFalse(File.Exists(path + ".bak"), "first write has no prior generation; .bak must not exist");
        }

        [TestMethod]
        public void WriteAtomic_SecondWrite_RotatesPriorToBak()
        {
            var path = Path.Combine(_testDir, "rotate.txt");

            FileHandler.WriteAtomic(path, "v1");
            FileHandler.WriteAtomic(path, "v2");

            Assert.AreEqual("v2", File.ReadAllText(path));
            Assert.AreEqual("v1", File.ReadAllText(path + ".bak"),
                ".bak must hold the previous generation so a crash mid-second-write leaves v1 recoverable");
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [TestMethod]
        public void WriteAtomic_ThirdWrite_OverwritesBakWithLatestPriorGeneration()
        {
            // Single-generation rotation per spec — .bak holds the *previous* good copy,
            // not an unbounded archive. The archive role is BackupSystem's RunArchiveBackup.
            var path = Path.Combine(_testDir, "rotate2.txt");

            FileHandler.WriteAtomic(path, "v1");
            FileHandler.WriteAtomic(path, "v2");
            FileHandler.WriteAtomic(path, "v3");

            Assert.AreEqual("v3", File.ReadAllText(path));
            Assert.AreEqual("v2", File.ReadAllText(path + ".bak"),
                "third write rotates the v2-from-second-write to .bak, discarding v1");
        }

        [TestMethod]
        public void ReadAtomic_HappyPath_ReadsCanonicalFile()
        {
            var path = Path.Combine(_testDir, "read.txt");

            FileHandler.WriteAtomic(path, "payload");

            Assert.AreEqual("payload", FileHandler.ReadAtomic(path));
        }

        [TestMethod]
        public void ReadAtomic_FallsBackToBak_WhenCanonicalPathIsMissing()
        {
            // Simulates a crash in WriteAtomic's window between path -> .bak rotation
            // and .tmp -> path rename. ReadAtomic must surface .bak so the previous-good
            // generation is recoverable.
            var path = Path.Combine(_testDir, "recover.txt");

            FileHandler.WriteAtomic(path, "v1");
            FileHandler.WriteAtomic(path, "v2");

            File.Delete(path);

            Assert.AreEqual("v1", FileHandler.ReadAtomic(path),
                "with canonical path deleted, ReadAtomic must surface .bak content");
        }

        [TestMethod]
        public void ReadAtomic_NeitherFileExists_ReturnsEmpty()
        {
            // First-ever read on an empty universe — no .bak yet, no canonical file yet.
            // Caller (AgencySystem) interprets empty as "no prior state" and creates fresh.
            var path = Path.Combine(_testDir, "absent.txt");

            Assert.AreEqual(string.Empty, FileHandler.ReadAtomic(path));
        }

        [TestMethod]
        public void ReadAtomic_IgnoresLeftoverTmp()
        {
            // A leftover .tmp from an interrupted write may be partial / corrupt — the
            // OS may have buffered the WriteAllText and crashed mid-flush. ReadAtomic
            // must NOT trust it; .bak (or the canonical path, if present) is the
            // contract.
            var path = Path.Combine(_testDir, "stale-tmp.txt");

            // Set up the unfortunate state: only .tmp exists, no canonical, no .bak.
            File.WriteAllText(path + ".tmp", "possibly-corrupt-payload");

            Assert.AreEqual(string.Empty, FileHandler.ReadAtomic(path),
                "leftover .tmp must not be surfaced; treat as 'no recoverable state'");
        }

        [TestMethod]
        public void WriteAtomic_ConcurrentWritesToSamePath_FinalContentIsOneOfTheWrites_NotCorrupt()
        {
            // Pins the load-bearing property that two threads racing WriteAtomic on the
            // same path produce last-writer-wins (serialized via the per-directory lock
            // from GetLockSemaphore), never an interleaved / corrupt payload. The whole
            // point of WriteAtomic is that callers don't need their own synchronisation.
            var path = Path.Combine(_testDir, "concurrent.txt");

            const int iterations = 200;
            Parallel.For(0, iterations, i =>
            {
                FileHandler.WriteAtomic(path, $"writer-{i}");
            });

            var final = File.ReadAllText(path);
            Assert.IsTrue(final.StartsWith("writer-"), $"final content should be exactly one writer's payload, got: {final}");
            Assert.IsFalse(File.Exists(path + ".tmp"), "no leftover .tmp after all writes settle");
        }

        [TestMethod]
        public void WriteAtomic_RoundTripsUnicodeViaUtf8()
        {
            // Stage 5 will store DisplayName which may contain non-ASCII characters
            // (Cyrillic player handles, emoji-y agency names, etc). Pin the encoding
            // contract here so a future "let's just use Default" regression doesn't
            // silently corrupt content on a non-UTF-8-default locale.
            var path = Path.Combine(_testDir, "utf8.txt");
            var unicode = "Майор95 🚀 Space Agency";

            FileHandler.WriteAtomic(path, unicode);

            Assert.AreEqual(unicode, FileHandler.ReadAtomic(path));
        }
    }
}
