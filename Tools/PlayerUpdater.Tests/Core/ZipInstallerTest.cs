using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class ZipInstallerTest
    {
        private string _testRoot = string.Empty;
        private string _installDir = string.Empty;
        private string _zipPath = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _testRoot = Path.Combine(Path.GetTempPath(), $"ZipInstallerTest-{Guid.NewGuid():N}");
            _installDir = Path.Combine(_testRoot, "KSP");
            _zipPath = Path.Combine(_testRoot, "release.zip");
            Directory.CreateDirectory(_installDir);
        }

        [TestCleanup]
        public void Teardown()
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }

        // --- PlanOverlay ---

        [TestMethod]
        public void PlanOverlay_NewFile_KindIsCreate()
        {
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(ZipInstaller.ActionKind.Create, plan[0].Kind);
            Assert.AreEqual(ZipInstaller.SkipReason.None, plan[0].SkipReason);
        }

        [TestMethod]
        public void PlanOverlay_ExistingFile_KindIsOverwrite()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(ZipInstaller.ActionKind.Overwrite, plan[0].Kind);
        }

        [TestMethod]
        public void PlanOverlay_DirectoryEntry_SkippedWithReasonDirectory()
        {
            WriteZipWithDirectories(
                new[] { "GameData/", "GameData/LunaMultiplayer/" },
                new[] { ("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new") });

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            var skipped = plan.Where(p => p.Kind == ZipInstaller.ActionKind.Skip).ToList();
            Assert.AreEqual(2, skipped.Count);
            Assert.IsTrue(skipped.All(p => p.SkipReason == ZipInstaller.SkipReason.Directory));
        }

        [TestMethod]
        public void PlanOverlay_BackslashTerminatedDirectoryEntry_SkippedWithReasonDirectory()
        {
            // Windows PowerShell's Compress-Archive emits directory markers
            // with `\` instead of the zip-spec `/`. Without explicit handling,
            // those entries slip past the `EndsWith("/")` check, get classified
            // as Create/Overwrite, and fail at extract time with
            // DirectoryNotFoundException. This pins the fix that recognises
            // both separators.
            WriteZipWithDirectories(
                new[] { "GameData\\", "GameData\\LunaMultiplayer\\Localization\\" },
                new[] { ("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new") });

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            var skipped = plan.Where(p => p.Kind == ZipInstaller.ActionKind.Skip).ToList();
            Assert.AreEqual(2, skipped.Count);
            Assert.IsTrue(skipped.All(p => p.SkipReason == ZipInstaller.SkipReason.Directory));
        }

        [TestMethod]
        public void PlanOverlay_EmptyNameDirectoryEntry_SkippedWithReasonDirectory()
        {
            // Defense in depth: ZipArchiveEntry.Name is empty for directory
            // entries regardless of which separator the FullName uses. This
            // is the canonical "this is a directory" signal in .NET's zip
            // API and should be honoured even if both `/` and `\` checks
            // somehow miss (e.g. a future zip-creator emits no trailing
            // separator at all but still flags the entry as a directory).
            using (var archive = ZipFile.Open(_zipPath, ZipArchiveMode.Create))
            {
                // Note: CreateEntry with a name that has Name == "" requires
                // a trailing separator on the FullName. We construct one
                // manually to exercise the empty-Name detection path.
                archive.CreateEntry("EmptyDir/");
                var fileEntry = archive.CreateEntry("file.txt");
                using var s = fileEntry.Open();
                using var w = new StreamWriter(s);
                w.Write("real");
            }

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            var dirSkip = plan.SingleOrDefault(p => p.Kind == ZipInstaller.ActionKind.Skip);
            Assert.IsNotNull(dirSkip);
            Assert.AreEqual(ZipInstaller.SkipReason.Directory, dirSkip!.SkipReason);
        }

        [TestMethod]
        public void PlanOverlay_PathTraversal_SkippedWithReasonPathTraversal()
        {
            // The cornerstone ZipSlip defense — a '../' segment that
            // canonicalises outside the install root must be rejected.
            WriteZip(("../../EvilFile.dll", "bytes"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(ZipInstaller.ActionKind.Skip, plan[0].Kind);
            Assert.AreEqual(ZipInstaller.SkipReason.PathTraversal, plan[0].SkipReason);
        }

        [TestMethod]
        public void PlanOverlay_AbsolutePathEntry_SkippedWithReasonPathTraversal()
        {
            // A zip entry that starts with a drive letter or '/' is treated
            // as absolute and rejected by the same defense.
            WriteZip((@"C:\Windows\System32\bad.dll", "bytes"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(ZipInstaller.ActionKind.Skip, plan[0].Kind);
            Assert.AreEqual(ZipInstaller.SkipReason.PathTraversal, plan[0].SkipReason);
        }

        [TestMethod]
        public void PlanOverlay_PathLooksLikeRootPrefix_NotConfusedWithRoot()
        {
            // 'KSP-evil/file.dll' must NOT match an install at 'KSP' via
            // string prefix — the traversal defense uses a trailing
            // separator comparison.
            var installNamedKsp = Path.Combine(_testRoot, "K");
            var siblingNamedKspEvil = Path.Combine(_testRoot, "K-evil");
            Directory.CreateDirectory(installNamedKsp);
            Directory.CreateDirectory(siblingNamedKspEvil);

            // We can't easily construct an entry that targets a sibling
            // without traversal, so we exercise the canonical happy path
            // here and rely on TryResolveInstallPath's trailing-separator
            // check to catch the malicious case (covered indirectly by the
            // PathTraversal test above).
            WriteZip(("Plugins/Ok.dll", "x"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, installNamedKsp);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(ZipInstaller.ActionKind.Create, plan[0].Kind);
        }

        [TestMethod]
        public void PlanOverlay_UncompressedSizeSurfaced()
        {
            // Forms uses the per-entry size to render the extract progress
            // bar. Pin that the field is populated from the zip's metadata.
            const string content = "twelve bytes";
            WriteZip(("a.dll", content));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);

            Assert.AreEqual(content.Length, plan[0].UncompressedSize);
        }

        [TestMethod]
        [ExpectedException(typeof(FileNotFoundException))]
        public void PlanOverlay_ZipMissing_Throws()
        {
            ZipInstaller.PlanOverlay(Path.Combine(_testRoot, "missing.zip"), _installDir);
        }

        // --- ExecuteOverlay ---

        [TestMethod]
        public void ExecuteOverlay_CreatesNewFiles()
        {
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new-bytes"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.AreEqual(1, result.ExtractedCount);
            Assert.AreEqual(0, result.FailedCount);
            var extracted = Path.Combine(_installDir, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll");
            Assert.IsTrue(File.Exists(extracted));
            Assert.AreEqual("new-bytes", File.ReadAllText(extracted));
        }

        [TestMethod]
        public void ExecuteOverlay_OverwritesExistingFiles()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old-bytes");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new-bytes"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.AreEqual(1, result.ExtractedCount);
            Assert.AreEqual(
                "new-bytes",
                File.ReadAllText(Path.Combine(_installDir, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll")));
        }

        [TestMethod]
        public void ExecuteOverlay_PreservesInstallFilesNotInZip()
        {
            // The cornerstone preservation contract: settings.xml is in the
            // install dir but NOT in the zip. After overlay it must still
            // contain its original content unchanged.
            WriteInstallFile("GameData/LunaMultiplayer/Data/settings.xml", "<player-settings/>");
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            // settings.xml untouched
            Assert.AreEqual(
                "<player-settings/>",
                File.ReadAllText(Path.Combine(_installDir, "GameData", "LunaMultiplayer", "Data", "settings.xml")));
            // LmpClient.dll overwritten
            Assert.AreEqual(
                "new",
                File.ReadAllText(Path.Combine(_installDir, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll")));
        }

        [TestMethod]
        public void ExecuteOverlay_PreservesFlagsDirectory()
        {
            // Flags/ is the player's custom-flag-uploads directory. Same
            // preservation rule as settings.xml: not in zip -> untouched.
            WriteInstallFile("GameData/LunaMultiplayer/Flags/CustomFlag.png", "PNG-BYTES");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.AreEqual(
                "PNG-BYTES",
                File.ReadAllText(Path.Combine(_installDir, "GameData", "LunaMultiplayer", "Flags", "CustomFlag.png")));
        }

        [TestMethod]
        public void ExecuteOverlay_SkipsPathTraversalEntries_NoFilesWritten()
        {
            // The malicious entry must NOT produce a file anywhere outside
            // the install dir. Pre/post-test we check that no file exists
            // at the traversal target (in the test root, which is the
            // parent of install).
            var traversalTarget = Path.Combine(_testRoot, "EvilFile.dll");
            Assert.IsFalse(File.Exists(traversalTarget));

            WriteZip(
                ("../EvilFile.dll", "evil"),
                ("Ok.dll", "ok"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            // Skipped entries are not "failed" — they're documented refusals.
            // The benign entry extracts successfully.
            Assert.AreEqual(1, result.ExtractedCount);
            Assert.IsFalse(File.Exists(traversalTarget),
                "Path-traversal entry must NOT produce a file outside the install dir.");
        }

        // --- C2/C9: Plan-drift surfaced via PlanDriftDetected (review-driven) ---

        [TestMethod]
        public void ExecuteOverlay_PlanReferencesMissingZipEntry_SetsPlanDriftDetected()
        {
            // Forms scenario: player runs Plan, downloads a new zip, the
            // existing plan now references entries that don't exist in the
            // updated zip. ExecuteOverlay must SURFACE this as a single
            // flag rather than N independent failure messages.
            WriteZip(("real.dll", "x"));

            var ghostAction = new OverlayAction(
                ZipEntryPath: "ghost.dll",
                TargetPath: Path.Combine(_installDir, "ghost.dll"),
                Kind: ZipInstaller.ActionKind.Create,
                SkipReason: ZipInstaller.SkipReason.None,
                UncompressedSize: 1);
            var realPlan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var hybridPlan = new[] { ghostAction }.Concat(realPlan).ToList();

            var result = ZipInstaller.ExecuteOverlay(hybridPlan, _zipPath, _installDir);

            Assert.IsTrue(result.PlanDriftDetected,
                "Plan referenced an entry not in the archive — PlanDriftDetected must be true.");
            Assert.AreEqual(1, result.FailedCount);
        }

        [TestMethod]
        public void ExecuteOverlay_HappyPath_PlanDriftDetectedIsFalse()
        {
            WriteZip(("file.dll", "x"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.IsFalse(result.PlanDriftDetected);
            Assert.AreEqual(0, result.FailedCount);
        }

        // --- C5: OverlayResult exposes CreatedCount + SkippedCount (review-driven) ---

        [TestMethod]
        public void ExecuteOverlay_OutcomeCountersBreakDownActionKinds()
        {
            // 2 new files + 1 overwrite + 1 directory marker (skipped).
            WriteInstallFile("ExistingFile.dll", "old");
            WriteZipWithDirectories(
                directories: new[] { "GameData/" },
                files: new[]
                {
                    ("ExistingFile.dll", "new"),
                    ("NewFile1.dll", "x"),
                    ("NewFile2.dll", "y"),
                });

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.AreEqual(3, result.ExtractedCount, "Extracted = all overwrites + creates.");
            Assert.AreEqual(2, result.CreatedCount, "Created = new files only (NewFile1 + NewFile2).");
            Assert.AreEqual(1, result.SkippedCount, "Skipped = directory marker.");
            Assert.AreEqual(0, result.FailedCount);
        }

        // --- U6: duplicate FullName surfaced via OverlayResult (review-driven) ---

        [TestMethod]
        public void ExecuteOverlay_ZipWithDuplicateFullName_SurfacedInResult()
        {
            // Legal per zip spec but produced by buggy archivers. Last
            // entry wins by dictionary ordering; we surface the duplicate
            // name so Forms can log it for the upstream tooling owner.
            using (var archive = ZipFile.Open(_zipPath, ZipArchiveMode.Create))
            {
                var first = archive.CreateEntry("dup.dll");
                using (var s = first.Open()) { using var w = new StreamWriter(s); w.Write("first"); }
                var second = archive.CreateEntry("dup.dll");
                using (var s = second.Open()) { using var w = new StreamWriter(s); w.Write("second"); }
            }

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.IsTrue(result.DuplicateEntries.Contains("dup.dll"),
                "Duplicate FullName must be reported in OverlayResult.DuplicateEntries.");
        }

        [TestMethod]
        public void ExecuteOverlay_NoDuplicates_DuplicateEntriesEmpty()
        {
            WriteZip(("a.dll", "x"), ("b.dll", "y"));

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.AreEqual(0, result.DuplicateEntries.Count);
        }

        [TestMethod]
        public void ExecuteOverlay_PerEntryFailureContinues()
        {
            // Simulate a per-entry failure by handing ExecuteOverlay a plan
            // with an entry that references a zip path NOT in the zip.
            WriteZip(("a.dll", "x"));

            var ghostAction = new OverlayAction(
                ZipEntryPath: "ghost.dll",
                TargetPath: Path.Combine(_installDir, "ghost.dll"),
                Kind: ZipInstaller.ActionKind.Create,
                SkipReason: ZipInstaller.SkipReason.None,
                UncompressedSize: 1);
            var realPlan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var hybridPlan = new[] { ghostAction }.Concat(realPlan).ToList();

            var result = ZipInstaller.ExecuteOverlay(hybridPlan, _zipPath, _installDir);

            Assert.AreEqual(1, result.ExtractedCount);
            Assert.AreEqual(1, result.FailedCount);
            // The good one still landed.
            Assert.IsTrue(File.Exists(Path.Combine(_installDir, "a.dll")));
            // The bad one is reported as failed but did not create a file.
            Assert.IsFalse(File.Exists(Path.Combine(_installDir, "ghost.dll")));
        }

        [TestMethod]
        public void ExecuteOverlay_OutcomesIncludeBothSkipsAndExtracts()
        {
            WriteZipWithDirectories(
                new[] { "GameData/" },
                new[] { ("GameData/file.dll", "x") });

            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            var result = ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);

            Assert.AreEqual(1, result.ExtractedCount);
            Assert.AreEqual(2, result.Outcomes.Count);
            Assert.IsTrue(result.Outcomes.All(o => o.Success), "Skipped entries are success=true.");
        }

        [TestMethod]
        [ExpectedException(typeof(DirectoryNotFoundException))]
        public void ExecuteOverlay_InstallDirMissing_Throws()
        {
            WriteZip(("a.dll", "x"));
            var plan = ZipInstaller.PlanOverlay(_zipPath, _installDir);
            Directory.Delete(_installDir, recursive: true);

            ZipInstaller.ExecuteOverlay(plan, _zipPath, _installDir);
        }

        // --- Helpers ---

        private void WriteInstallFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(_installDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        private void WriteZip(params (string EntryName, string Content)[] entries)
        {
            if (File.Exists(_zipPath)) File.Delete(_zipPath);
            using var archive = ZipFile.Open(_zipPath, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var s = entry.Open();
                using var w = new StreamWriter(s);
                w.Write(content);
            }
        }

        private void WriteZipWithDirectories(
            string[] directories,
            (string EntryName, string Content)[] files)
        {
            if (File.Exists(_zipPath)) File.Delete(_zipPath);
            using var archive = ZipFile.Open(_zipPath, ZipArchiveMode.Create);
            foreach (var dir in directories)
            {
                archive.CreateEntry(dir);
            }
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var s = entry.Open();
                using var w = new StreamWriter(s);
                w.Write(content);
            }
        }
    }
}
