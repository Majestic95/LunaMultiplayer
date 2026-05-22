using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LunaMultiplayer.PlayerUpdater.Core.Backups;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Backups the files in a KSP install that are about to be overwritten by
    // the new release zip, so the player can roll back if the new version
    // breaks something. The flow during an install:
    //
    //   1. PlanBackup(installDir, zipPath) returns the per-file copy plan
    //      (only files that BOTH exist in the install AND appear in the
    //      zip — files unique to the install are preserved untouched by
    //      the install loop, never backed up).
    //   2. ExecuteBackup(plan, installDir) copies those files into a fresh
    //      timestamped directory under
    //      %LOCALAPPDATA%/LunaMultiplayer/PlayerUpdater/backups/<install-hash>/<timestamp>/
    //      and writes a manifest.json + an in-progress marker file.
    //   3. ZipInstaller.ExecuteOverlay extracts the new zip on top of the
    //      install (overwriting the files we just backed up).
    //   4. On success, MarkInstallComplete clears the in-progress marker.
    //
    // The install-hash directory level lets two side-by-side KSP installs
    // (e.g. a stable career save + a per-agency career save) maintain
    // separate backup chains. The hash is SHA-256 of the canonical install
    // path, truncated to 16 hex chars (64 bits = ~one-in-a-quintillion
    // collision probability across the player's KSP installs).
    //
    // In-progress marker: a zero-byte file 'in-progress.marker' next to the
    // backup manifest. Present means an install started but did not finish
    // cleanly. The Forms layer detects this on startup via
    // `ListBackups(installDir).Where(b => b.InProgress)` and offers a
    // "previous install was interrupted — restore from backup?" prompt. The
    // marker is removed by MarkInstallComplete after a successful
    // ZipInstaller.ExecuteOverlay returns.
    //
    // ORDERING INVARIANT (load-bearing for crash recovery): inside
    // ExecuteBackup the order MUST be (a) CreateDirectory, (b) write
    // in-progress.marker, (c) copy files, (d) write manifest.json, (e)
    // [later, from caller after ZipInstaller succeeds] MarkInstallComplete.
    // ListBackups skips dirs without a manifest, so a crash between (a) and
    // (d) leaves a stub that's invisible to enumeration AND invisible to
    // prune — it self-heals when the next install at the same install dir
    // succeeds and the prune pass eventually cleans up by directory delete.
    // PruneBackups never deletes a backup with InProgress=true regardless
    // of retention, so an actually-interrupted install is rescuable.
    //
    // Platform: this class targets Windows (net10.0-windows). The install
    // hash uses ToLowerInvariant() to normalise the case-insensitive
    // Windows path semantics. Running on a case-sensitive filesystem (Linux
    // via Mono for tests) would conflate `/ksp` and `/KSP` into the same
    // bucket — non-blocking because Windows is the only shipped target.
    //
    // Storage-leak edge case: if the player moves their KSP install from
    // `C:\KSP-Stable\` to `D:\KSP\`, the install hash changes and the new
    // location gets a fresh backup chain. Backups under the OLD hash
    // bucket become orphans — Forms can't trivially associate them with
    // any current install. Acceptable for v1 (cohort scale); future work
    // could scan all buckets for manifests whose InstallPath no longer
    // resolves and offer cleanup.
    public static class BackupManager
    {
        public const string BackupsSubdirectory = "LunaMultiplayer/PlayerUpdater/backups";
        public const string ManifestFileName = "manifest.json";
        public const string InProgressMarkerFileName = "in-progress.marker";

        // Default retention if UpdaterSettings.BackupRetention is unset.
        // Kept in sync with UpdaterSettings.DefaultBackupRetention — having
        // it here too lets BackupManager be called without a settings load
        // (e.g. from tests).
        public const int DefaultRetention = 3;

        // Length of the truncated SHA-256 install hash in hex chars. 16 chars
        // = 64 bits of entropy, sufficient to disambiguate the ~handful of
        // KSP installs a player might have side-by-side.
        public const int InstallHashLength = 16;

        // Enumerates files in a release zip that would overwrite an existing
        // file in the install. Returns the per-file copy plan; an empty list
        // means "no overwrites — the install dir has no overlap with the
        // zip", which happens on a fresh install. Plan items reference the
        // file by its zip entry path (forward-slash separated) so callers can
        // grep operator-side logs against the same identifiers ZipInstaller
        // uses.
        //
        // Defensive: skips zip entries that:
        //   - have empty names (corrupt zip entries)
        //   - end in '/' (directory entries — those don't carry file content)
        //   - resolve to a path that escapes the install root (ZipSlip
        //     defense — see ZipInstaller for the symmetric check on the
        //     extract side)
        //
        // A zip entry that DOES NOT exist in the install is not in the plan —
        // it'll be created fresh by the extract loop, not backed up.
        public static IReadOnlyList<BackupAction> PlanBackup(string installDir, string zipPath)
        {
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }
            if (string.IsNullOrWhiteSpace(zipPath))
            {
                throw new ArgumentException("zipPath must be non-empty.", nameof(zipPath));
            }
            if (!File.Exists(zipPath))
            {
                throw new FileNotFoundException($"Release zip not found at '{zipPath}'.", zipPath);
            }

            var canonicalInstall = Path.GetFullPath(installDir);
            var actions = new List<BackupAction>();

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FullName)) continue;
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

                if (!TryResolveInstallPath(canonicalInstall, entry.FullName, out var installPath))
                {
                    // Path traversal attempt or malformed entry — skip. The
                    // ZipInstaller side will reject the same entry during
                    // extract, so we never leave the install in an
                    // inconsistent state.
                    continue;
                }

                if (File.Exists(installPath))
                {
                    actions.Add(new BackupAction(entry.FullName, installPath));
                }
            }

            return actions;
        }

        // Executes a backup plan. Creates the timestamped backup directory
        // under installDir's hash bucket and copies each file in the plan.
        // Writes a manifest.json listing the backed-up entries + the source
        // install path + the timestamp + the LMP version being replaced.
        // Writes an in-progress.marker that the install loop must clear via
        // MarkInstallComplete after the extract completes.
        //
        // Returns the absolute path of the created backup directory. The
        // caller is responsible for invoking PruneBackups afterward (we
        // don't prune here because the install loop wants the new backup to
        // exist until the extract succeeds).
        //
        // If the backup directory cannot be created or a copy fails partway,
        // the partial backup is preserved on disk + the in-progress marker
        // is left in place so a re-run of the install can either complete
        // or restore from this partial state.
        public static string ExecuteBackup(
            IReadOnlyList<BackupAction> plan,
            string installDir,
            string? replacingTag,
            DateTimeOffset? now = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }

            var canonicalInstall = Path.GetFullPath(installDir);
            var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
            var backupDir = ResolveBackupDirectory(canonicalInstall, timestamp);

            Directory.CreateDirectory(backupDir);

            // Write the in-progress marker FIRST so a crash before the
            // copies start still surfaces "previous install in progress" on
            // next launch. Operators see the marker; partial backup state
            // is recoverable. Ordering invariant documented at the class
            // level — keep marker-then-files-then-manifest order.
            File.WriteAllBytes(Path.Combine(backupDir, InProgressMarkerFileName), Array.Empty<byte>());

            foreach (var action in plan)
            {
                if (!TryResolveInstallPath(canonicalInstall, action.ZipEntryPath, out var srcPath))
                {
                    // Plan came from PlanBackup so this shouldn't fire, but
                    // be defensive — a plan that has been tampered with
                    // post-generation must not be allowed to copy from
                    // outside the install.
                    continue;
                }

                var destPath = Path.Combine(
                    backupDir,
                    action.ZipEntryPath.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // overwrite:true is safe here — backupDir was just created
                // by us (or recovered from an interrupted install at the
                // same timestamp). overwrite:false would throw on the
                // same-second re-run path, leaving the backup half-done
                // with no recovery story.
                File.Copy(srcPath, destPath, overwrite: true);
            }

            BackupManifest.Write(
                Path.Combine(backupDir, ManifestFileName),
                canonicalInstall,
                plan,
                replacingTag,
                timestamp);
            return backupDir;
        }

        // Marks an install as having completed successfully by removing the
        // in-progress marker file. Called by the install loop AFTER
        // ZipInstaller.ExecuteOverlay returns successfully. If the marker
        // is missing (e.g. ExecuteBackup never ran) the call is a no-op.
        public static void MarkInstallComplete(string backupDir)
        {
            if (string.IsNullOrWhiteSpace(backupDir)) return;
            var marker = Path.Combine(backupDir, InProgressMarkerFileName);
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }

        // Lists existing backups for an install, newest first. The directory
        // names are RFC 3339-ish timestamps and sort lexicographically the
        // same as chronologically. Backup directories without a manifest
        // are skipped (corrupted or in-flight backups).
        public static IReadOnlyList<BackupInfo> ListBackups(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }

            var canonicalInstall = Path.GetFullPath(installDir);
            var installBackupRoot = ResolveInstallBackupRoot(canonicalInstall);
            if (installBackupRoot == null || !Directory.Exists(installBackupRoot))
            {
                return Array.Empty<BackupInfo>();
            }

            var directories = Directory.GetDirectories(installBackupRoot);
            var infos = new List<BackupInfo>(directories.Length);
            foreach (var dir in directories)
            {
                var manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath)) continue;

                if (!TryParseTimestampFromDirectoryName(Path.GetFileName(dir), out var timestamp))
                {
                    continue;
                }

                var inProgress = File.Exists(Path.Combine(dir, InProgressMarkerFileName));
                BackupManifest.TryReadFields(manifestPath, out var replacingTag, out var manifestInstallPath);
                infos.Add(new BackupInfo(dir, timestamp, inProgress, replacingTag, manifestInstallPath));
            }

            infos.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return infos;
        }

        // Prunes backups beyond the retention count. Keeps the most recent
        // 'retention' backups (sorted by timestamp desc); deletes the rest.
        // In-progress backups are NEVER pruned even if they're older than
        // the cutoff — the player may still want to restore from them.
        //
        // Returns the number of backups actually deleted. Errors during
        // deletion of an individual backup directory don't fail the whole
        // prune — they're swallowed so a single locked-file doesn't block
        // the next install.
        public static int PruneBackups(string installDir, int retention)
        {
            if (retention < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retention),
                    "retention must be non-negative.");
            }

            var backups = ListBackups(installDir);
            var prunable = backups.Where(b => !b.InProgress).Skip(retention).ToList();
            var deleted = 0;
            foreach (var info in prunable)
            {
                try
                {
                    Directory.Delete(info.Path, recursive: true);
                    deleted++;
                }
                catch (Exception ex) when (
                    ex is IOException
                    or UnauthorizedAccessException
                    or DirectoryNotFoundException)
                {
                    // Locked file, denied access, or already gone — leave
                    // for the next prune pass. We do NOT log here because
                    // BackupManager is library-free; the install loop can
                    // diff ListBackups before/after to see what failed.
                }
            }
            return deleted;
        }

        // Restores a backup by copying its contents back into the install
        // dir. Overwrites whatever is currently there at each backed-up
        // path. The Forms layer wires this to a Rollback button.
        //
        // Returns the number of files restored. Throws on outright I/O
        // failure (e.g. backup directory doesn't exist) — the caller wants
        // to surface a clear error to the player.
        public static int RestoreBackup(string backupDir, string installDir)
        {
            if (string.IsNullOrWhiteSpace(backupDir))
            {
                throw new ArgumentException("backupDir must be non-empty.", nameof(backupDir));
            }
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }
            if (!Directory.Exists(backupDir))
            {
                throw new DirectoryNotFoundException($"Backup directory not found at '{backupDir}'.");
            }

            var canonicalInstall = Path.GetFullPath(installDir);
            var canonicalBackup = Path.GetFullPath(backupDir);
            var restored = 0;

            // EnumerationOptions.AttributesToSkip = ReparsePoint prevents the
            // enumerator from descending into junctions or symbolic links
            // inside the backup tree. A hostile or hand-edited backup that
            // contained a junction pointing at C:\Windows\System32\ would
            // otherwise let RestoreBackup ingest arbitrary system files
            // into the install dir under whatever filename was attached to
            // the junction — a write-via-junction class vulnerability
            // symmetric with the ZipSlip defense on the extract side.
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            foreach (var sourceFile in Directory.EnumerateFiles(canonicalBackup, "*", enumOptions))
            {
                var relativePath = Path.GetRelativePath(canonicalBackup, sourceFile);
                if (string.Equals(relativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(relativePath, InProgressMarkerFileName, StringComparison.OrdinalIgnoreCase)) continue;

                // Defense in depth: even with the reparse-point skip above,
                // make sure relativePath does not contain '..' segments that
                // would escape canonicalInstall after the join. Same shape
                // as the ZipSlip defense in TryResolveInstallPath, applied
                // to the backup-side source path.
                if (!TryResolveInstallPath(canonicalInstall, relativePath.Replace(Path.DirectorySeparatorChar, '/'), out var destPath))
                {
                    continue;
                }

                // Also refuse a source file whose canonical path is outside
                // the backup root after symlink resolution (defense against
                // a regular file that's actually a hardlink-out — File.GetAttributes
                // doesn't flag hardlinks).
                var fileAttrs = File.GetAttributes(sourceFile);
                if ((fileAttrs & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                File.Copy(sourceFile, destPath, overwrite: true);
                restored++;
            }

            return restored;
        }

        // Pure-plan companion to RestoreBackup — returns the per-file plan
        // WITHOUT touching any disk state. The Forms layer renders this
        // before the player clicks the Rollback button. Same defenses as
        // RestoreBackup (reparse-point skip + relative-path validation), so
        // a hostile backup tree cannot mislead the preview either.
        public static IReadOnlyList<RestoreAction> PlanRestore(string backupDir, string installDir)
        {
            if (string.IsNullOrWhiteSpace(backupDir))
            {
                throw new ArgumentException("backupDir must be non-empty.", nameof(backupDir));
            }
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }
            if (!Directory.Exists(backupDir))
            {
                throw new DirectoryNotFoundException($"Backup directory not found at '{backupDir}'.");
            }

            var canonicalInstall = Path.GetFullPath(installDir);
            var canonicalBackup = Path.GetFullPath(backupDir);
            var actions = new List<RestoreAction>();

            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };

            foreach (var sourceFile in Directory.EnumerateFiles(canonicalBackup, "*", enumOptions))
            {
                var relativePath = Path.GetRelativePath(canonicalBackup, sourceFile);
                if (string.Equals(relativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(relativePath, InProgressMarkerFileName, StringComparison.OrdinalIgnoreCase)) continue;

                if (!TryResolveInstallPath(canonicalInstall, relativePath.Replace(Path.DirectorySeparatorChar, '/'), out var destPath))
                {
                    continue;
                }

                var fileAttrs = File.GetAttributes(sourceFile);
                if ((fileAttrs & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                actions.Add(new RestoreAction(sourceFile, destPath, File.Exists(destPath)));
            }

            return actions;
        }

        // Computes the install-hash directory name. Public so callers can
        // construct or inspect backup paths without needing to round-trip
        // through ListBackups.
        public static string ComputeInstallHash(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }

            var canonical = Path.GetFullPath(installDir);
            // Windows paths are case-insensitive but the byte representation
            // differs for 'C:\KSP' vs 'c:\ksp'; normalise to lowercase before
            // hashing so the same install always hashes to the same bucket.
            var canonicalLower = canonical.ToLowerInvariant();

            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalLower));
            return Convert.ToHexString(bytes, 0, InstallHashLength / 2).ToLowerInvariant();
        }

        // Resolves the root path under which all backups for THIS install
        // live: %LOCALAPPDATA%/LunaMultiplayer/PlayerUpdater/backups/<hash>/.
        // Returns null when LocalApplicationData can't be resolved (rare —
        // would mean a profile-less Windows install).
        public static string? ResolveInstallBackupRoot(string installDir)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) return null;

            var hash = ComputeInstallHash(installDir);
            return Path.Combine(
                localAppData,
                BackupsSubdirectory.Replace('/', Path.DirectorySeparatorChar),
                hash);
        }

        // Resolves the exact timestamped directory path a backup would write to.
        private static string ResolveBackupDirectory(string canonicalInstall, DateTime timestampUtc)
        {
            var root = ResolveInstallBackupRoot(canonicalInstall);
            if (root == null)
            {
                throw new InvalidOperationException(
                    "LocalApplicationData folder is unavailable — backups cannot be written. " +
                    "This typically indicates a profile-less Windows install or a missing user profile.");
            }
            return Path.Combine(root, FormatTimestamp(timestampUtc));
        }

        // ISO 8601-ish UTC timestamp with filesystem-safe characters
        // ('T' separator kept, ':' replaced with '-'). Lexicographic sort
        // matches chronological sort.
        internal static string FormatTimestamp(DateTime timestampUtc) =>
            timestampUtc.ToString("yyyy-MM-ddTHH-mm-ssZ", CultureInfo.InvariantCulture);

        internal static bool TryParseTimestampFromDirectoryName(string name, out DateTime timestampUtc)
        {
            return DateTime.TryParseExact(
                name,
                "yyyy-MM-ddTHH-mm-ssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestampUtc);
        }

        // Joins an install-relative zip entry path onto the install root and
        // refuses anything that escapes the canonical install dir (ZipSlip
        // defense). Returns false for entries that fail validation; true with
        // the joined path otherwise.
        internal static bool TryResolveInstallPath(string canonicalInstall, string zipEntryPath, out string installPath)
        {
            installPath = string.Empty;

            // Reject absolute paths (entry like '/etc/passwd' on Linux zips
            // or 'C:\Windows\...' on Windows zips).
            if (Path.IsPathRooted(zipEntryPath)) return false;

            string joined;
            try
            {
                joined = Path.GetFullPath(Path.Combine(
                    canonicalInstall,
                    zipEntryPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (
                ex is ArgumentException
                or PathTooLongException
                or NotSupportedException)
            {
                return false;
            }

            // Compare against the canonical install root WITH a trailing
            // separator so '/install' and '/install-evil' don't collide.
            var rootWithSep = canonicalInstall.EndsWith(Path.DirectorySeparatorChar)
                ? canonicalInstall
                : canonicalInstall + Path.DirectorySeparatorChar;
            if (!joined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            installPath = joined;
            return true;
        }

    }

    // One file slated for backup. ZipEntryPath uses forward slashes; the
    // matching install-side path is recomputed from canonicalInstall +
    // ZipEntryPath at copy time.
    public sealed record BackupAction(string ZipEntryPath, string CurrentInstallPath);

    // One existing backup on disk. Path is the absolute backup directory;
    // Timestamp is parsed from the directory name; InProgress is true when
    // the in-progress.marker file is present (the install that wrote this
    // backup did not complete cleanly). ReplacingTag + ManifestInstallPath
    // are read from the manifest.json on the fly so the Forms restore
    // dialog can render "Restore the install that was overwritten by
    // <ReplacingTag> on <Timestamp>" + cross-check ManifestInstallPath
    // against the current install path (catches install relocations where
    // the same hash bucket no longer maps to the same physical directory).
    // Both manifest-derived fields are null when the manifest is malformed
    // or pre-schema.
    public sealed record BackupInfo(
        string Path,
        DateTime Timestamp,
        bool InProgress,
        string? ReplacingTag,
        string? ManifestInstallPath);

    // One file slated for restore. SourceBackupPath is the absolute path
    // inside the backup dir; TargetInstallPath is the absolute path inside
    // the install dir where the file will land. OverwritesExisting is true
    // when an install-side file already exists at TargetInstallPath — the
    // Forms preview renders these distinctly from new-file restores.
    public sealed record RestoreAction(
        string SourceBackupPath,
        string TargetInstallPath,
        bool OverwritesExisting);
}
