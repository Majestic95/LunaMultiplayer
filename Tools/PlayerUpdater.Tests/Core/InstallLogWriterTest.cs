using System;
using System.Collections.Generic;
using System.IO;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class InstallLogWriterTest
    {
        // Fixed timestamp keeps the log filename + body deterministic
        // across runs, so a test failure points at a real bug rather
        // than a stray clock skew.
        private static readonly DateTime FixedTimestamp =
            new DateTime(2026, 5, 22, 18, 30, 0, DateTimeKind.Utc);

        // -- FormatLog (pure helper) ----------------------------------

        [TestMethod]
        public void FormatLog_NoOverlayResult_NotesPreExtractFailure()
        {
            var result = new InstallResult(
                InstallOutcome.DownloadFailed,
                BackupDir: null,
                OverlayResult: null,
                HashSkipped: false,
                Error: "HttpRequestException: connection reset");

            var text = InstallLogWriter.FormatLog(
                result, @"C:\KSP", "LunaMultiplayer-Client-Release.zip", "v0.30.0",
                FixedTimestamp);

            StringAssert.Contains(text, "DownloadFailed");
            StringAssert.Contains(text, "No overlay result");
            StringAssert.Contains(text, "HttpRequestException: connection reset");
            StringAssert.Contains(text, "v0.30.0");
        }

        [TestMethod]
        public void FormatLog_AllSuccessful_StillEmitsFullPerEntryLog()
        {
            // Defensive: even a "success" should format cleanly if a
            // future caller misuses the writer. FailedCount==0, summary
            // lists 2 extracted, no FAIL lines.
            var overlay = BuildOverlay(new (string, ZipInstaller.ActionKind, bool, string?)[]
            {
                ("plugin.dll", ZipInstaller.ActionKind.Overwrite, true, null),
                ("readme.txt", ZipInstaller.ActionKind.Create, true, null),
            });
            var result = new InstallResult(
                InstallOutcome.Success, BackupDir: "backups/x", OverlayResult: overlay,
                HashSkipped: false, Error: null);

            var text = InstallLogWriter.FormatLog(
                result, @"C:\KSP", "asset.zip", "v0.31.0",
                FixedTimestamp);

            StringAssert.Contains(text, "Summary: 2 extracted (1 new, 1 overwritten), 0 skipped, 0 failed");
            StringAssert.Contains(text, "  OK    [overwrite] plugin.dll");
            StringAssert.Contains(text, "  OK    [create] readme.txt");
            StringAssert.Contains(text, "(none)"); // Failed-entries section
        }

        [TestMethod]
        public void FormatLog_PartialFailure_LeadsWithFailedEntries()
        {
            // Mirrors the user's reported scenario: 2 succeeded, 3 failed.
            // Failed entries should appear FIRST (operator-actionable) and
            // be repeated in the full per-entry log below.
            var overlay = BuildOverlay(new (string, ZipInstaller.ActionKind, bool, string?)[]
            {
                ("GameData/LunaMultiplayer/Plugins/LmpClient.dll",  ZipInstaller.ActionKind.Overwrite, true,  null),
                ("GameData/LunaMultiplayer/Plugins/LmpCommon.dll",  ZipInstaller.ActionKind.Overwrite, true,  null),
                ("GameData/LunaMultiplayer/LunaMultiplayer.version", ZipInstaller.ActionKind.Overwrite, false, "IOException: The process cannot access the file because it is being used by another process"),
                ("GameData/LunaMultiplayer/Icons/lock.png",          ZipInstaller.ActionKind.Overwrite, false, "UnauthorizedAccessException: Access to the path is denied"),
                ("GameData/LunaMultiplayer/Resources/foo.cfg",       ZipInstaller.ActionKind.Create,    false, "IOException: sharing violation"),
            });
            var result = new InstallResult(
                InstallOutcome.ExtractFailed,
                BackupDir: @"C:\KSP\LMP-Backup\2026-05-22T18-30-00Z",
                OverlayResult: overlay,
                HashSkipped: false,
                Error: "Extract completed with 3 failed entries. Install is in a mixed state — Rollback recommended.");

            var text = InstallLogWriter.FormatLog(
                result, @"C:\KSP", "LunaMultiplayer-Client-Release.zip", "v0.31.0",
                FixedTimestamp);

            // Header has the outcome and replacing tag
            StringAssert.Contains(text, "ExtractFailed");
            StringAssert.Contains(text, "v0.31.0");
            // Summary line
            StringAssert.Contains(text, "Summary: 2 extracted (0 new, 2 overwritten), 0 skipped, 3 failed");
            // Failed-entries section (actionable for operator)
            StringAssert.Contains(text, "Failed entries");
            StringAssert.Contains(text, "FAIL  GameData/LunaMultiplayer/LunaMultiplayer.version");
            StringAssert.Contains(text, "IOException: The process cannot access the file");
            StringAssert.Contains(text, "FAIL  GameData/LunaMultiplayer/Icons/lock.png");
            StringAssert.Contains(text, "FAIL  GameData/LunaMultiplayer/Resources/foo.cfg");
            // Full per-entry log
            StringAssert.Contains(text, "OK    [overwrite] GameData/LunaMultiplayer/Plugins/LmpClient.dll");
            StringAssert.Contains(text, "FAIL  [overwrite] GameData/LunaMultiplayer/LunaMultiplayer.version");
            // Pipeline error
            StringAssert.Contains(text, "Extract completed with 3 failed entries");
        }

        [TestMethod]
        public void FormatLog_DuplicateZipEntries_AreNoted()
        {
            // Surfaces buggy-archiver duplicate entries so a maintainer can
            // fix the upstream tooling.
            var overlay = new OverlayResult(
                InstallDir: @"C:\KSP",
                Outcomes: Array.Empty<OverlayOutcome>(),
                ExtractedCount: 0, CreatedCount: 0, SkippedCount: 0, FailedCount: 0,
                PlanDriftDetected: false,
                DuplicateEntries: new List<string> { "GameData/LunaMultiplayer/dup.dll" });
            var result = new InstallResult(
                InstallOutcome.ExtractFailed, BackupDir: null, OverlayResult: overlay,
                HashSkipped: false, Error: null);

            var text = InstallLogWriter.FormatLog(
                result, @"C:\KSP", "asset.zip", "", FixedTimestamp);

            StringAssert.Contains(text, "Duplicate zip entries");
            StringAssert.Contains(text, "GameData/LunaMultiplayer/dup.dll");
        }

        [TestMethod]
        public void FormatLog_NullReplacingTag_RendersNonePlaceholder()
        {
            var overlay = BuildOverlay(Array.Empty<(string, ZipInstaller.ActionKind, bool, string?)>());
            var result = new InstallResult(
                InstallOutcome.ExtractFailed, BackupDir: null, OverlayResult: overlay,
                HashSkipped: false, Error: null);

            var text = InstallLogWriter.FormatLog(
                result, @"C:\KSP", "asset.zip", replacingTag: string.Empty,
                FixedTimestamp);

            StringAssert.Contains(text, "Replacing tag: (none)");
        }

        [TestMethod]
        public void FormatLog_NullResult_Throws()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                InstallLogWriter.FormatLog(null!, @"C:\KSP", "asset.zip", "v0.31.0", FixedTimestamp));
        }

        // -- WriteFailureLog (filesystem) -----------------------------

        [TestMethod]
        public void WriteFailureLog_NoOverlayResult_ReturnsNull()
        {
            // Pre-extract failures (Download / Hash / DiskSpace) already
            // have a single actionable error in result.Error — no log is
            // useful, and producing an empty one would be noise.
            var result = new InstallResult(
                InstallOutcome.DownloadFailed,
                BackupDir: null, OverlayResult: null,
                HashSkipped: false, Error: "HTTP 503");

            var logDir = Path.Combine(Path.GetTempPath(), "lmp-test-log-" + Path.GetRandomFileName());
            try
            {
                var path = InstallLogWriter.WriteFailureLog(
                    result, @"C:\KSP", "asset.zip", "v0.31.0", logDir, FixedTimestamp);

                Assert.IsNull(path);
                Assert.IsFalse(Directory.Exists(logDir),
                    "WriteFailureLog must NOT create the log dir when it isn't going to write anything.");
            }
            finally
            {
                if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
            }
        }

        [TestMethod]
        public void WriteFailureLog_HappyPath_WritesFileAndReturnsPath()
        {
            var overlay = BuildOverlay(new (string, ZipInstaller.ActionKind, bool, string?)[]
            {
                ("plugin.dll", ZipInstaller.ActionKind.Overwrite, false, "IOException: locked"),
            });
            var result = new InstallResult(
                InstallOutcome.ExtractFailed, BackupDir: null, OverlayResult: overlay,
                HashSkipped: false, Error: "Extract completed with 1 failed entries");

            var logDir = Path.Combine(Path.GetTempPath(), "lmp-test-log-" + Path.GetRandomFileName());
            try
            {
                var path = InstallLogWriter.WriteFailureLog(
                    result, @"C:\KSP", "asset.zip", "v0.31.0", logDir, FixedTimestamp);

                Assert.IsNotNull(path);
                Assert.IsTrue(File.Exists(path!));
                StringAssert.Contains(Path.GetFileName(path)!, "install-log-2026-05-22-183000Z.txt");

                var content = File.ReadAllText(path!);
                StringAssert.Contains(content, "ExtractFailed");
                StringAssert.Contains(content, "FAIL  plugin.dll");
            }
            finally
            {
                if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
            }
        }

        [TestMethod]
        public void WriteFailureLog_NullLogDir_ReturnsNull()
        {
            // Best-effort contract: invalid log dir returns null without throwing.
            var overlay = BuildOverlay(Array.Empty<(string, ZipInstaller.ActionKind, bool, string?)>());
            var result = new InstallResult(
                InstallOutcome.ExtractFailed, BackupDir: null, OverlayResult: overlay,
                HashSkipped: false, Error: null);

            var path = InstallLogWriter.WriteFailureLog(
                result, @"C:\KSP", "asset.zip", "v0.31.0", logDir: null!, FixedTimestamp);

            Assert.IsNull(path);
        }

        [TestMethod]
        public void WriteFailureLog_WhitespaceLogDir_ReturnsNull()
        {
            var overlay = BuildOverlay(Array.Empty<(string, ZipInstaller.ActionKind, bool, string?)>());
            var result = new InstallResult(
                InstallOutcome.ExtractFailed, BackupDir: null, OverlayResult: overlay,
                HashSkipped: false, Error: null);

            var path = InstallLogWriter.WriteFailureLog(
                result, @"C:\KSP", "asset.zip", "v0.31.0", logDir: "   ", FixedTimestamp);

            Assert.IsNull(path);
        }

        [TestMethod]
        public void WriteFailureLog_NullResult_Throws()
        {
            // ArgumentNullException on null result — the writer can't
            // recover any meaningful behaviour from this, and silently
            // swallowing would mask a caller bug.
            Assert.ThrowsException<ArgumentNullException>(() =>
                InstallLogWriter.WriteFailureLog(
                    null!, @"C:\KSP", "asset.zip", "v0.31.0",
                    Path.GetTempPath(), FixedTimestamp));
        }

        [TestMethod]
        public void WriteFailureLog_LogDirNotYetCreated_IsCreated()
        {
            // The log dir doesn't have to exist beforehand — the writer
            // calls Directory.CreateDirectory.
            var overlay = BuildOverlay(new (string, ZipInstaller.ActionKind, bool, string?)[]
            {
                ("plugin.dll", ZipInstaller.ActionKind.Overwrite, false, "IOException: locked"),
            });
            var result = new InstallResult(
                InstallOutcome.ExtractFailed, BackupDir: null, OverlayResult: overlay,
                HashSkipped: false, Error: null);

            var nestedLogDir = Path.Combine(
                Path.GetTempPath(),
                "lmp-test-log-" + Path.GetRandomFileName(),
                "nested", "subdir");
            try
            {
                Assert.IsFalse(Directory.Exists(nestedLogDir));

                var path = InstallLogWriter.WriteFailureLog(
                    result, @"C:\KSP", "asset.zip", "v0.31.0", nestedLogDir, FixedTimestamp);

                Assert.IsNotNull(path);
                Assert.IsTrue(Directory.Exists(nestedLogDir));
            }
            finally
            {
                // Walk up to the top-level test dir for cleanup.
                var topDir = Path.GetFullPath(Path.Combine(nestedLogDir, "..", ".."));
                if (Directory.Exists(topDir)) Directory.Delete(topDir, recursive: true);
            }
        }

        // -- helpers --------------------------------------------------

        private static OverlayResult BuildOverlay(
            IReadOnlyList<(string Path, ZipInstaller.ActionKind Kind, bool Success, string? Error)> entries)
        {
            var outcomes = new List<OverlayOutcome>(entries.Count);
            var extracted = 0;
            var created = 0;
            var failed = 0;
            foreach (var e in entries)
            {
                var action = new OverlayAction(e.Path, @"C:\KSP\" + e.Path, e.Kind, ZipInstaller.SkipReason.None, 100);
                outcomes.Add(new OverlayOutcome(action, e.Success, e.Error));
                if (e.Success)
                {
                    extracted++;
                    if (e.Kind == ZipInstaller.ActionKind.Create) created++;
                }
                else
                {
                    failed++;
                }
            }
            return new OverlayResult(
                InstallDir: @"C:\KSP",
                Outcomes: outcomes,
                ExtractedCount: extracted,
                CreatedCount: created,
                SkippedCount: 0,
                FailedCount: failed,
                PlanDriftDetected: false,
                DuplicateEntries: new List<string>());
        }
    }
}
