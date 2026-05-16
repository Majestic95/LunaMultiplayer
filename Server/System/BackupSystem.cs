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
        /// Writes a timestamped snapshot of the universe to <see cref="ArchivesPath"/>/yyyy-MM-dd_HH-mm-ss/.
        /// Flushes in-memory state first via <see cref="RunBackup"/> so the snapshot reflects the latest data.
        /// Returns the absolute path of the new archive folder. Throws on failure (partial archive is removed).
        /// </summary>
        public static string RunArchiveBackup()
        {
            lock (LockObj)
            {
                RunBackup();

                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
                var archiveDir = Path.Combine(ArchivesPath, timestamp);

                try
                {
                    Directory.CreateDirectory(archiveDir);
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
        /// </summary>
        /// <param name="timestamp">Archive folder name (e.g. "2026-05-16_19-30-00"). Must not contain path separators or "..".</param>
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
                try
                {
                    var universeDir = ServerContext.UniverseDirectory;

                    foreach (var subdir in Directory.GetDirectories(sourceDir))
                    {
                        var name = Path.GetFileName(subdir);
                        var dest = Path.Combine(universeDir, name);
                        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                        CopyDirectoryRecursive(subdir, dest);
                    }

                    foreach (var file in Directory.GetFiles(sourceDir))
                    {
                        var name = Path.GetFileName(file);
                        var dest = Path.Combine(universeDir, name);
                        File.Copy(file, dest, overwrite: true);
                    }

                    message = $"Restored from archive '{timestamp}'. Restart the server to load the restored universe.";
                    LunaLog.Normal(message);
                    return true;
                }
                catch (Exception e)
                {
                    message = $"Restore failed: {e.Message}";
                    LunaLog.Error($"Restore from '{timestamp}' failed: {e}");
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
                    archives[i].Delete(recursive: true);
                    LunaLog.Debug($"Retention: deleted old archive {archives[i].Name}");
                }
                catch (Exception e)
                {
                    LunaLog.Warning($"Retention: could not delete archive {archives[i].Name}: {e.Message}");
                }
            }
        }
    }
}
