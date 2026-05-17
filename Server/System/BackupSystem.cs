// Retro-review S7 — this file bypasses FileHandler for archive I/O. Rationale: the
// `_archives` subtree is never touched by other LMP systems (no `Share*` writer, no
// `WarpSystem.BackupSubspaces`, no `VesselStoreSystem.PersistVesselToFile` ever names
// a path inside `_archives`), so the per-file lock that FileHandler centralises isn't
// load-bearing here. The outer `LockObj` provides the only mutual exclusion required
// against concurrent flush. If a future system ever writes into `_archives` outside
// this class, that invariant breaks — route through FileHandler then or rethink the
// lock model.

using Server.Context;
using Server.Events;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Server.System
{
    public class BackupSystem
    {
        //Subscribe to the exit event so a backup is performed when closing the server
        static BackupSystem() => ExitEvent.ServerClosing += RunBackup;

        private static readonly object LockObj = new object();

        // Directories/files inside <Universe>/ to include when an archive snapshot is taken.
        // Anything outside these lists (Screenshots, Flags, log files, _archives itself) is intentionally excluded.
        private static readonly string[] SnapshotDirs = { "Vessels", "Kerbals", "Groups", "Crafts", "Scenarios" };
        private static readonly string[] SnapshotFiles = { "Subspace.txt", "StartTime.txt" };

        public static string ArchivesPath => Path.Combine(ServerContext.UniverseDirectory, "_archives");

        public static async Task PerformBackupsAsync(CancellationToken token)
        {
            while (ServerContext.ServerRunning)
            {
                if (ServerContext.PlayerCount > 0)
                {
                    RunBackup();
                }

                try
                {
                    await Task.Delay(IntervalSettings.SettingsStore.BackupIntervalMs, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        public static void RunBackup()
        {
            lock (LockObj)
            {
                LunaLog.Debug("Performing backups...");
                VesselStoreSystem.BackupVessels();
                WarpSystem.BackupSubspaces();
                TimeSystem.BackupStartTime();
                ScenarioStoreSystem.BackupScenarios();
                LunaLog.Debug("Backups done");
            }
        }

        /// <summary>
        /// Long-running task that periodically writes a timestamped snapshot of the universe to
        /// <see cref="ArchivesPath"/>. Unlike <see cref="PerformBackupsAsync"/> (which flushes
        /// in-memory state to the canonical Universe files), this creates a separate copy that
        /// can be restored from later. Cadence is controlled by IntervalSettings.ArchiveBackupIntervalHours.
        /// </summary>
        public static async Task PerformArchiveBackupsAsync(CancellationToken token)
        {
            while (ServerContext.ServerRunning)
            {
                var intervalHours = IntervalSettings.SettingsStore.ArchiveBackupIntervalHours;
                if (intervalHours <= 0)
                {
                    // Archives disabled; sleep modestly and re-check in case settings are reloaded.
                    try { await Task.Delay(TimeSpan.FromMinutes(60), token); }
                    catch (TaskCanceledException) { break; }
                    continue;
                }

                try { await Task.Delay(TimeSpan.FromHours(intervalHours), token); }
                catch (TaskCanceledException) { break; }

                try
                {
                    RunArchiveBackup();
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Archive backup failed: {e}");
                }
            }
        }

        /// <summary>
        /// Writes a timestamped snapshot of the universe to <see cref="ArchivesPath"/>/yyyy-MM-dd_HH-mm-ss-fff/.
        /// Flushes in-memory state first via <see cref="RunBackup"/> so the snapshot reflects the latest data.
        /// Returns the absolute path of the new archive folder. Throws on failure (partial archive is removed).
        ///
        /// <para>Lock semantics (retro-review S10): the canonical Universe is flushed under
        /// <see cref="LockObj"/>, then the lock is dropped before the disk copy begins. A concurrent
        /// <see cref="RunBackup"/> may overwrite source files mid-copy; the resulting archive can
        /// then contain a partially-newer state. Acceptable for archive purposes — operators are
        /// warned to take a fresh archive after major game events rather than rely on
        /// snapshot-during-flux. The alternative (holding the lock for the full copy) blocks the
        /// 30-second flush task for the entire copy duration on a large universe.</para>
        /// </summary>
        public static string RunArchiveBackup()
        {
            string archiveDir;

            // Phase 1: flush in-memory state under the lock so the canonical files are
            // consistent at the moment we snapshot.
            lock (LockObj)
            {
                RunBackup();

                // Sub-second timestamp precision (retro-review S9). A second-precision timestamp
                // collided when a manual `/backup archive` landed in the same second as the periodic
                // task — the second run's File.Copy(overwrite:false) failure rolled back via
                // Directory.Delete(archiveDir, recursive) and destroyed the first run's archive.
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff");
                archiveDir = Path.Combine(ArchivesPath, timestamp);

                if (Directory.Exists(archiveDir) && Directory.EnumerateFileSystemEntries(archiveDir).Any())
                {
                    throw new IOException($"Archive directory '{archiveDir}' already exists and is non-empty; refusing to overwrite.");
                }

                Directory.CreateDirectory(archiveDir);
            }

            // Phase 2: copy the (now-consistent-on-disk) snapshot. No lock held; concurrent
            // RunBackup can overwrite source files mid-copy. See lock-semantics comment above.
            try
            {
                CopyUniverseSnapshot(archiveDir);
                EnforceRetentionPolicy();
                LunaLog.Normal($"Archive backup written: {archiveDir}");
                return archiveDir;
            }
            catch (Exception e)
            {
                LunaLog.Error($"Failed to write archive backup to {archiveDir}: {e}");
                try { if (Directory.Exists(archiveDir)) Directory.Delete(archiveDir, true); } catch { /* best-effort cleanup */ }
                throw;
            }
        }

        /// <summary>
        /// Returns archive folder names (timestamps) sorted newest-first. Empty list if no archives exist.
        /// </summary>
        public static IReadOnlyList<string> ListArchives()
        {
            if (!Directory.Exists(ArchivesPath)) return Array.Empty<string>();

            return Directory.GetDirectories(ArchivesPath)
                .Select(Path.GetFileName)
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Restores the canonical Universe files from a named archive. Refuses if any players are connected,
        /// since in-memory state would diverge from the restored files. Caller should restart the server after a successful restore.
        ///
        /// <para>Crash safety (retro-review M3, M4): before any destructive overwrite, an automatic
        /// pre-restore snapshot is taken via <see cref="RunArchiveBackup"/> with a
        /// <c>pre-restore-…</c> name. The restore then stages the archive contents in
        /// <c>&lt;Universe&gt;/_restoring/</c> and only moves them into place subtree-by-subtree
        /// after the staging copy completes successfully. A mid-copy crash leaves the staging
        /// folder behind; the canonical Universe is fully recoverable from the pre-restore
        /// snapshot. A crash AFTER staging is partially swept into the canonical tree (between
        /// subtrees) is the only remaining hole; the pre-restore snapshot still covers it.</para>
        /// </summary>
        /// <param name="timestamp">Archive folder name (e.g. "2026-05-16_19-30-00-123"). Must not contain path separators or "..".</param>
        /// <param name="message">Human-readable result message suitable for echoing to the console.</param>
        /// <returns>True on success, false otherwise.</returns>
        public static bool RestoreFromArchive(string timestamp, out string message)
        {
            if (string.IsNullOrWhiteSpace(timestamp) ||
                timestamp.Contains("..") ||
                timestamp.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                timestamp.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                message = "Invalid archive name.";
                return false;
            }

            var sourceDir = Path.Combine(ArchivesPath, timestamp);
            if (!Directory.Exists(sourceDir))
            {
                message = $"Archive '{timestamp}' not found.";
                return false;
            }

            if (ServerContext.PlayerCount > 0)
            {
                message = "Cannot restore while players are connected. Stop the server or kick all players first.";
                return false;
            }

            lock (LockObj)
            {
                string preRestoreArchive = null;
                string stagingDir = null;
                try
                {
                    var universeDir = ServerContext.UniverseDirectory;
                    EnsureUniverseRoot(universeDir);

                    // M3: take a pre-restore safety snapshot of the current state so the operator
                    // can recover if they picked the wrong timestamp or the restore fails midway.
                    // Outside the inner `lock (LockObj)` — RunArchiveBackup re-acquires it, which
                    // is fine because lock objects are reentrant in C#.
                    LunaLog.Normal("Taking pre-restore safety snapshot...");
                    preRestoreArchive = RunArchiveBackup();
                    var preRestoreName = Path.GetFileName(preRestoreArchive);
                    // Rename to a more obvious `pre-restore-…` prefix so it stands out in `backup list`.
                    var renamedPreRestore = Path.Combine(ArchivesPath, "pre-restore-" + preRestoreName);
                    Directory.Move(preRestoreArchive, renamedPreRestore);
                    preRestoreArchive = renamedPreRestore;

                    // M4: stage the archive in <Universe>/_restoring/, then move into place
                    // subtree-by-subtree once the stage is complete.
                    stagingDir = Path.Combine(universeDir, "_restoring");
                    if (Directory.Exists(stagingDir))
                    {
                        EnsureUniverseRoot(stagingDir);
                        Directory.Delete(stagingDir, recursive: true);
                    }
                    Directory.CreateDirectory(stagingDir);
                    CopyDirectoryRecursive(sourceDir, stagingDir);

                    // Stage complete. Now sweep subtree-by-subtree from staging into the canonical
                    // Universe. Each subtree is delete-then-move, which is the closest we can get
                    // to atomic on a single filesystem without a transactional API.
                    foreach (var subdir in Directory.GetDirectories(stagingDir))
                    {
                        var name = Path.GetFileName(subdir);
                        var dest = Path.Combine(universeDir, name);
                        EnsureUniverseRoot(dest);
                        if (Directory.Exists(dest))
                            Directory.Delete(dest, recursive: true);
                        Directory.Move(subdir, dest);
                    }

                    foreach (var file in Directory.GetFiles(stagingDir))
                    {
                        var name = Path.GetFileName(file);
                        var dest = Path.Combine(universeDir, name);
                        File.Copy(file, dest, overwrite: true);
                    }

                    // Staging now empty (subdirs moved out, files copied); remove it.
                    try { Directory.Delete(stagingDir, recursive: true); } catch { /* swept; not worth failing the restore */ }

                    message = $"Restored from archive '{timestamp}'. Pre-restore safety snapshot saved at '{Path.GetFileName(preRestoreArchive)}'. Restart the server to load the restored universe.";
                    LunaLog.Normal(message);
                    return true;
                }
                catch (Exception e)
                {
                    var preRestoreHint = preRestoreArchive != null
                        ? $" To recover the prior state, restore from '{Path.GetFileName(preRestoreArchive)}'."
                        : string.Empty;
                    message = $"Restore failed: {e.Message}.{preRestoreHint}";
                    LunaLog.Error($"Restore from '{timestamp}' failed: {e}{preRestoreHint}");

                    // Try to clean up the staging dir so a future restore attempt isn't blocked.
                    if (stagingDir != null)
                    {
                        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
                    }
                    return false;
                }
            }
        }

        private static void CopyUniverseSnapshot(string destDir)
        {
            var universeDir = ServerContext.UniverseDirectory;

            foreach (var name in SnapshotDirs)
            {
                var src = Path.Combine(universeDir, name);
                if (Directory.Exists(src))
                {
                    CopyDirectoryRecursive(src, Path.Combine(destDir, name));
                }
            }

            foreach (var name in SnapshotFiles)
            {
                var src = Path.Combine(universeDir, name);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(destDir, name), overwrite: false);
                }
            }
        }

        private static void CopyDirectoryRecursive(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
            foreach (var sub in Directory.GetDirectories(src))
                CopyDirectoryRecursive(sub, Path.Combine(dest, Path.GetFileName(sub)));
        }

        private static void EnforceRetentionPolicy()
        {
            var retention = IntervalSettings.SettingsStore.ArchiveBackupRetentionCount;
            if (retention <= 0) return;
            if (!Directory.Exists(ArchivesPath)) return;

            var archives = Directory.GetDirectories(ArchivesPath)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .ToArray();

            for (var i = retention; i < archives.Length; i++)
            {
                try
                {
                    // S8: assert the target sits under ArchivesPath before recursive-deleting.
                    EnsureUniverseRoot(archives[i].FullName);
                    archives[i].Delete(recursive: true);
                    LunaLog.Debug($"Retention: deleted old archive {archives[i].Name}");
                }
                catch (Exception e)
                {
                    LunaLog.Warning($"Retention: could not delete archive {archives[i].Name}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Retro-review S8: assert that <paramref name="targetPath"/> resolves to a location
        /// inside the canonical Universe directory (or beneath, including <c>_archives</c>).
        /// Defence against a misconfigured <see cref="ServerContext.UniverseDirectory"/> causing
        /// a recursive delete to fire against an arbitrary CWD-relative path. Throws on violation
        /// rather than silently no-oping so the operator sees the problem.
        /// </summary>
        private static void EnsureUniverseRoot(string targetPath)
        {
            var universeRoot = ServerContext.UniverseDirectory;
            if (string.IsNullOrWhiteSpace(universeRoot) || !Path.IsPathRooted(universeRoot))
                throw new InvalidOperationException($"ServerContext.UniverseDirectory ('{universeRoot}') is not a rooted path; refusing to perform destructive operations.");

            var resolvedTarget = Path.GetFullPath(targetPath);
            var resolvedRoot = Path.GetFullPath(universeRoot);

            // Append the separator so `Universe-other/` does not test as a prefix of `Universe/`.
            var rootWithSep = resolvedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? resolvedRoot
                : resolvedRoot + Path.DirectorySeparatorChar;

            if (!resolvedTarget.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing destructive operation: target '{resolvedTarget}' is outside Universe root '{resolvedRoot}'.");
        }
    }
}
