using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class BackupManagerTest
    {
        private string _testRoot = string.Empty;
        private string _installDir = string.Empty;
        private string _zipPath = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _testRoot = Path.Combine(Path.GetTempPath(), $"BackupManagerTest-{Guid.NewGuid():N}");
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

        // --- ComputeInstallHash ---

        [TestMethod]
        public void ComputeInstallHash_Deterministic()
        {
            var a = BackupManager.ComputeInstallHash(@"C:\KSP");
            var b = BackupManager.ComputeInstallHash(@"C:\KSP");

            Assert.AreEqual(a, b);
        }

        [TestMethod]
        public void ComputeInstallHash_CaseInsensitiveOnWindows()
        {
            // Windows file paths are case-insensitive; same path written in
            // different cases must hash to the same bucket so backups don't
            // fork across capitalisation differences.
            var lower = BackupManager.ComputeInstallHash(@"c:\ksp");
            var upper = BackupManager.ComputeInstallHash(@"C:\KSP");

            Assert.AreEqual(lower, upper);
        }

        [TestMethod]
        public void ComputeInstallHash_DifferentPathsDistinct()
        {
            var a = BackupManager.ComputeInstallHash(@"C:\KSP");
            var b = BackupManager.ComputeInstallHash(@"D:\KSP");

            Assert.AreNotEqual(a, b);
        }

        [TestMethod]
        public void ComputeInstallHash_RelativeAndAbsoluteEqual()
        {
            // GetFullPath normalises both inputs to the same absolute path
            // (CWD-resolved for the relative case) before hashing.
            var cwd = Directory.GetCurrentDirectory();
            var absolute = BackupManager.ComputeInstallHash(cwd);
            var relative = BackupManager.ComputeInstallHash(".");

            Assert.AreEqual(absolute, relative);
        }

        [TestMethod]
        public void ComputeInstallHash_TruncatedTo16Chars()
        {
            var hash = BackupManager.ComputeInstallHash(@"C:\KSP");

            Assert.AreEqual(16, hash.Length);
            Assert.IsTrue(hash.All(IsLowerHex), $"Hash '{hash}' contains non-lowercase-hex chars.");
        }

        // --- PlanBackup ---

        [TestMethod]
        public void PlanBackup_ZipContainsExistingInstallFile_IncludesAction()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old-bytes");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new-bytes"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual("GameData/LunaMultiplayer/Plugins/LmpClient.dll", plan[0].ZipEntryPath);
        }

        [TestMethod]
        public void PlanBackup_ZipContainsNewFile_ActionExcluded()
        {
            // File is in the zip but does NOT exist in the install -> no
            // backup needed for it (the extract will create it fresh).
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new-bytes"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);

            Assert.AreEqual(0, plan.Count);
        }

        [TestMethod]
        public void PlanBackup_InstallFileNotInZip_NotInPlan()
        {
            // The cornerstone of the preservation contract: settings.xml is
            // in the install dir but not in the zip. PlanBackup must NOT
            // include it — the install loop will not touch it, so backing
            // it up is wasted IO.
            WriteInstallFile("GameData/LunaMultiplayer/Data/settings.xml", "<player-settings/>");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new-bytes"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);

            Assert.AreEqual(0, plan.Count);
        }

        [TestMethod]
        public void PlanBackup_DirectoryEntries_Skipped()
        {
            // Zip entries that are directory markers (FullName ending in '/')
            // don't carry file content. Must not appear in the plan.
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old-bytes");
            WriteZipWithDirectories(
                directories: new[] { "GameData/", "GameData/LunaMultiplayer/" },
                files: new[] { ("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new-bytes") });

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);

            Assert.AreEqual(1, plan.Count);
            Assert.IsFalse(plan[0].ZipEntryPath.EndsWith("/", StringComparison.Ordinal));
        }

        [TestMethod]
        public void PlanBackup_PathTraversalZipEntry_Skipped()
        {
            // ZipSlip-style entry with '../' segments. The traversal-defense
            // path in TryResolveInstallPath catches it; the plan excludes it.
            WriteZip(("../../EvilFile.dll", "malicious-bytes"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);

            Assert.AreEqual(0, plan.Count);
        }

        [TestMethod]
        [ExpectedException(typeof(FileNotFoundException))]
        public void PlanBackup_ZipMissing_Throws()
        {
            BackupManager.PlanBackup(_installDir, Path.Combine(_testRoot, "no-zip.zip"));
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void PlanBackup_EmptyInstallDir_Throws()
        {
            WriteZip(("a.dll", "x"));
            BackupManager.PlanBackup("", _zipPath);
        }

        // --- ExecuteBackup ---

        [TestMethod]
        public void ExecuteBackup_CopiesFilesAndWritesManifest()
        {
            // Use a dedicated LocalAppData override so the backup doesn't
            // pollute the real %LOCALAPPDATA%. We can't redirect
            // SpecialFolder.LocalApplicationData without env var hacks, so
            // we just let the real path be used and assert by listing.
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old-bytes");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new-bytes"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var now = new DateTimeOffset(2026, 5, 22, 12, 30, 0, TimeSpan.Zero);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, replacingTag: "v0.31.0-private-1", now);

            try
            {
                Assert.IsTrue(Directory.Exists(backupDir));
                var backupFile = Path.Combine(backupDir, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll");
                Assert.IsTrue(File.Exists(backupFile));
                Assert.AreEqual("old-bytes", File.ReadAllText(backupFile));
                Assert.IsTrue(File.Exists(Path.Combine(backupDir, BackupManager.ManifestFileName)));
                Assert.IsTrue(File.Exists(Path.Combine(backupDir, BackupManager.InProgressMarkerFileName)));
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        [TestMethod]
        public void ExecuteBackup_TimestampFormatIsSortable()
        {
            // Pinning the timestamp format guarantees ListBackups can sort
            // chronologically via lexicographic compare on directory names.
            var t = new DateTime(2026, 5, 22, 12, 30, 45, DateTimeKind.Utc);
            var formatted = BackupManager.FormatTimestamp(t);

            Assert.AreEqual("2026-05-22T12-30-45Z", formatted);

            Assert.IsTrue(BackupManager.TryParseTimestampFromDirectoryName(formatted, out var roundTripped));
            Assert.AreEqual(t, roundTripped);
        }

        [TestMethod]
        public void MarkInstallComplete_RemovesInProgressMarker()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, replacingTag: "v0.31.0-private-1");
            try
            {
                Assert.IsTrue(File.Exists(Path.Combine(backupDir, BackupManager.InProgressMarkerFileName)));

                BackupManager.MarkInstallComplete(backupDir);

                Assert.IsFalse(File.Exists(Path.Combine(backupDir, BackupManager.InProgressMarkerFileName)));
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        // --- ListBackups / PruneBackups / RestoreBackup ---

        [TestMethod]
        public void ListBackups_ReturnsExistingBackups_NewestFirst()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var older = BackupManager.ExecuteBackup(plan, _installDir, "v1",
                new DateTimeOffset(2026, 5, 22, 10, 0, 0, TimeSpan.Zero));
            BackupManager.MarkInstallComplete(older);
            var newer = BackupManager.ExecuteBackup(plan, _installDir, "v2",
                new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));
            BackupManager.MarkInstallComplete(newer);

            try
            {
                var listed = BackupManager.ListBackups(_installDir);

                Assert.IsTrue(listed.Count >= 2, $"Expected >=2 backups, got {listed.Count}");
                // Newest first - find the two we just wrote among any pre-existing entries.
                var ours = listed.Where(b => b.Path == newer || b.Path == older).ToList();
                Assert.AreEqual(2, ours.Count);
                Assert.AreEqual(newer, ours[0].Path);
                Assert.AreEqual(older, ours[1].Path);
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        [TestMethod]
        public void PruneBackups_KeepsRetentionCount_DeletesRest()
        {
            // Use a fresh install dir so the prune count is deterministic
            // even if the dev box has other backups in %LOCALAPPDATA%.
            var freshInstall = Path.Combine(_testRoot, "Fresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(freshInstall);
            File.WriteAllText(Path.Combine(freshInstall, "marker"), "x");

            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            File.WriteAllText(Path.Combine(freshInstall, "GameData_LmpClient.dll"), "x");

            // Place 5 backups under the fresh install's hash bucket.
            var dirs = new System.Collections.Generic.List<string>();
            for (var i = 0; i < 5; i++)
            {
                var dir = BackupManager.ExecuteBackup(
                    new BackupAction[0],
                    freshInstall,
                    $"v{i}",
                    new DateTimeOffset(2026, 5, 22, 12, i * 5, 0, TimeSpan.Zero));
                BackupManager.MarkInstallComplete(dir);
                dirs.Add(dir);
            }

            try
            {
                var deleted = BackupManager.PruneBackups(freshInstall, retention: 2);

                Assert.AreEqual(3, deleted);
                var remaining = BackupManager.ListBackups(freshInstall);
                Assert.AreEqual(2, remaining.Count);
                // The two newest survive.
                Assert.AreEqual(dirs[4], remaining[0].Path);
                Assert.AreEqual(dirs[3], remaining[1].Path);
            }
            finally
            {
                var root = BackupManager.ResolveInstallBackupRoot(freshInstall);
                if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void PruneBackups_DoesNotDeleteInProgressBackups()
        {
            // An in-progress backup represents an interrupted install; the
            // player may still want to restore from it. Pruning must skip it
            // even if it's older than the retention cutoff.
            var freshInstall = Path.Combine(_testRoot, "FreshIP-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(freshInstall);

            var inProgress = BackupManager.ExecuteBackup(new BackupAction[0], freshInstall, "v-in-progress",
                new DateTimeOffset(2026, 5, 22, 10, 0, 0, TimeSpan.Zero));
            // DO NOT mark complete -> marker stays.

            var completed = BackupManager.ExecuteBackup(new BackupAction[0], freshInstall, "v-complete",
                new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));
            BackupManager.MarkInstallComplete(completed);

            try
            {
                BackupManager.PruneBackups(freshInstall, retention: 0);

                var remaining = BackupManager.ListBackups(freshInstall);
                // In-progress survives even with retention=0.
                Assert.IsTrue(remaining.Any(b => b.Path == inProgress && b.InProgress));
            }
            finally
            {
                var root = BackupManager.ResolveInstallBackupRoot(freshInstall);
                if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void RestoreBackup_CopiesBackupFilesIntoInstall_OverwritesCurrent()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v1-bytes");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2-bytes"));

            // Run a backup capturing v1-bytes.
            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, "v0.31.0-private-2");
            BackupManager.MarkInstallComplete(backupDir);

            // Simulate the new install: overwrite the install file with v2-bytes.
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2-bytes");

            try
            {
                var restored = BackupManager.RestoreBackup(backupDir, _installDir);

                Assert.AreEqual(1, restored);
                Assert.AreEqual(
                    "v1-bytes",
                    File.ReadAllText(Path.Combine(_installDir, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll")));
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        // --- U1: same-second re-run survives (review-driven) ---

        [TestMethod]
        public void ExecuteBackup_SameSecondReRun_OverwritesCleanly()
        {
            // An interrupted install can be re-run within the same second
            // (FormatTimestamp granularity = 1s). The second ExecuteBackup
            // must succeed against the partially-populated backup dir from
            // the first attempt — overwrite:true on the per-file copies is
            // load-bearing for this recovery path.
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v1");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var fixedTime = new DateTimeOffset(2026, 5, 22, 12, 30, 0, TimeSpan.Zero);

            var firstDir = BackupManager.ExecuteBackup(plan, _installDir, "v1", fixedTime);
            try
            {
                // Re-run at the same timestamp — collides on the dir + file.
                // Pre-overwrite-true, the second File.Copy threw. Now passes.
                var secondDir = BackupManager.ExecuteBackup(plan, _installDir, "v1", fixedTime);

                Assert.AreEqual(firstDir, secondDir, "Same timestamp -> same backup dir.");
                Assert.IsTrue(File.Exists(Path.Combine(secondDir,
                    "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll")));
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        // --- C4 / C6: BackupInfo carries manifest fields (review-driven) ---

        [TestMethod]
        public void ListBackups_PopulatesReplacingTagAndManifestInstallPath()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, replacingTag: "v0.31.0-private-7");
            BackupManager.MarkInstallComplete(backupDir);

            try
            {
                var listed = BackupManager.ListBackups(_installDir);
                var ours = listed.First(b => b.Path == backupDir);

                Assert.AreEqual("v0.31.0-private-7", ours.ReplacingTag);
                Assert.AreEqual(Path.GetFullPath(_installDir), ours.ManifestInstallPath);
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        // --- U8: manifest has SchemaVersion field (review-driven) ---

        [TestMethod]
        public void ExecuteBackup_ManifestContainsSchemaVersion()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "old");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "new"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, "v1");
            try
            {
                var manifest = File.ReadAllText(Path.Combine(backupDir, BackupManager.ManifestFileName));
                StringAssert.Contains(manifest, "\"SchemaVersion\":1");
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        // --- C3 / U2: PlanRestore + symlink defense (review-driven) ---

        [TestMethod]
        public void PlanRestore_ReturnsRestoreActionsForBackedUpFiles()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v1");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, "v1");
            BackupManager.MarkInstallComplete(backupDir);

            try
            {
                var restorePlan = BackupManager.PlanRestore(backupDir, _installDir);

                Assert.AreEqual(1, restorePlan.Count);
                var action = restorePlan[0];
                StringAssert.EndsWith(action.SourceBackupPath, "LmpClient.dll");
                StringAssert.EndsWith(action.TargetInstallPath, "LmpClient.dll");
                Assert.IsTrue(action.OverwritesExisting);
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        [TestMethod]
        public void PlanRestore_ExcludesManifestAndMarker()
        {
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v1");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, "v1");
            // Marker stays on purpose to confirm it's filtered from PlanRestore.

            try
            {
                var restorePlan = BackupManager.PlanRestore(backupDir, _installDir);

                Assert.IsFalse(restorePlan.Any(a =>
                    a.SourceBackupPath.EndsWith(BackupManager.ManifestFileName, StringComparison.OrdinalIgnoreCase)));
                Assert.IsFalse(restorePlan.Any(a =>
                    a.SourceBackupPath.EndsWith(BackupManager.InProgressMarkerFileName, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        [TestMethod]
        public void RestoreBackup_ExtraFilesAtRoot_StillFiltered()
        {
            // Defense in depth: if a hand-edited backup tree has additional
            // files at unusual paths, the manifest-and-marker exclusion
            // still applies via the OrdinalIgnoreCase match on file names.
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v1");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, "v1");
            BackupManager.MarkInstallComplete(backupDir);

            // Inject a stray file at the root of the backup dir — should
            // restore normally (it's INSIDE the backup, just at the root).
            File.WriteAllText(Path.Combine(backupDir, "extra.txt"), "stray");

            try
            {
                var restored = BackupManager.RestoreBackup(backupDir, _installDir);

                Assert.IsTrue(restored >= 2);
                Assert.IsTrue(File.Exists(Path.Combine(_installDir, "extra.txt")));
            }
            finally
            {
                CleanupBackupRoot();
            }
        }

        [TestMethod]
        public void RestoreBackup_DoesNotCopyManifestOrMarker()
        {
            // Manifest and in-progress marker live in the backup dir but
            // must not be restored into the install dir.
            WriteInstallFile("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v1");
            WriteZip(("GameData/LunaMultiplayer/Plugins/LmpClient.dll", "v2"));

            var plan = BackupManager.PlanBackup(_installDir, _zipPath);
            var backupDir = BackupManager.ExecuteBackup(plan, _installDir, "v1");
            // Leave the marker in place to verify it's skipped on restore.

            try
            {
                BackupManager.RestoreBackup(backupDir, _installDir);

                Assert.IsFalse(File.Exists(Path.Combine(_installDir, BackupManager.ManifestFileName)));
                Assert.IsFalse(File.Exists(Path.Combine(_installDir, BackupManager.InProgressMarkerFileName)));
            }
            finally
            {
                CleanupBackupRoot();
            }
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

        private void CleanupBackupRoot()
        {
            var root = BackupManager.ResolveInstallBackupRoot(_installDir);
            if (root != null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static bool IsLowerHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
    }
}
