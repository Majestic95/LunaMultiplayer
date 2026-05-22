using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Extracts a release zip on top of an existing KSP install using a
    // per-file overlay model. The semantics:
    //
    //   - Files in BOTH zip and install -> backup-then-overwrite. The
    //     overwrite is unconditional (no per-file Replace/Skip prompt).
    //     BackupManager.ExecuteBackup MUST have run before this method so
    //     the original bytes are recoverable via Rollback.
    //   - Files in zip but NOT install  -> created fresh.
    //   - Files in install but NOT zip  -> left UNTOUCHED. This is how
    //     GameData/LunaMultiplayer/Data/settings.xml and Flags/ survive
    //     across upgrades: they're not in the zip so the overlay never
    //     touches them.
    //
    // ZipSlip defense (CVE-2018-1002200 class): every entry path is joined
    // onto the install root, canonicalised, and checked to ensure the
    // resulting path is still under the install root. An entry like
    // '../../Windows/System32/foo.dll' would canonicalise OUTSIDE the
    // install and is rejected (action=Skip, reason=PathTraversal).
    //
    // Concurrency / locking: KSP must NOT be running during install. The
    // caller (Forms layer) gates on KspRunningCheck.ProbeKspRunningState
    // before invoking ExecuteOverlay. We DO NOT acquire any locks here —
    // if KSP is running, File.Copy will fail with a sharing violation and
    // the install loop surfaces the error to the player.
    //
    // Logging: ExecuteOverlay returns an OverlayResult carrying per-action
    // outcomes (Extracted / Skipped + reason). Forms layer writes the full
    // result to a log file the player can attach to error reports.
    public static class ZipInstaller
    {
        public enum ActionKind
        {
            // Zip entry will overwrite an existing install file. The previous
            // bytes MUST have been backed up via BackupManager already.
            Overwrite,

            // Zip entry will create a new install file (no prior install file
            // at that path).
            Create,

            // Zip entry is skipped — either it's a directory marker, has an
            // empty name, or fails the ZipSlip defense.
            Skip,
        }

        public enum SkipReason
        {
            // Not a skip — kind is Overwrite or Create.
            None,

            // Empty FullName on the zip entry. Corrupt zip.
            EmptyName,

            // FullName ends with '/' — a directory marker. The extract loop
            // creates parent dirs implicitly via Directory.CreateDirectory.
            Directory,

            // Path would escape the install root after normalisation. ZipSlip
            // attempt or a path-traversal sequence in a legitimate-looking
            // entry. Rejected unconditionally.
            PathTraversal,
        }

        // Builds the per-entry action plan WITHOUT touching the install
        // directory. The plan can be shown to the player (a "what's about
        // to happen" preview) before they click Install. The Forms layer
        // can also count action kinds to render a one-line summary ("12
        // overwrites, 3 new files, 0 skipped").
        public static IReadOnlyList<OverlayAction> PlanOverlay(string zipPath, string installDir)
        {
            if (string.IsNullOrWhiteSpace(zipPath))
            {
                throw new ArgumentException("zipPath must be non-empty.", nameof(zipPath));
            }
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }
            if (!File.Exists(zipPath))
            {
                throw new FileNotFoundException($"Release zip not found at '{zipPath}'.", zipPath);
            }

            var canonicalInstall = Path.GetFullPath(installDir);
            var actions = new List<OverlayAction>();

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                actions.Add(BuildPlanAction(entry, canonicalInstall));
            }

            return actions;
        }

        // Executes a plan against the install directory. Each Overwrite or
        // Create entry has its bytes streamed from the zip into the target
        // path; parent directories are created as needed. Skipped entries
        // pass through with no I/O.
        //
        // Returns an OverlayResult with per-action outcomes (success or
        // exception captured by reason text). Throws ONLY on fundamental
        // failures (zip cannot be opened, install dir missing entirely).
        // Per-entry failures are captured in the result so the install
        // surfaces them to the player without aborting the whole loop.
        //
        // The caller is responsible for running BackupManager.ExecuteBackup
        // BEFORE this — if Overwrite entries proceed without backup, a
        // botched install has no recoverable state.
        public static OverlayResult ExecuteOverlay(
            IReadOnlyList<OverlayAction> plan,
            string zipPath,
            string installDir)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (string.IsNullOrWhiteSpace(zipPath))
            {
                throw new ArgumentException("zipPath must be non-empty.", nameof(zipPath));
            }
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new ArgumentException("installDir must be non-empty.", nameof(installDir));
            }
            if (!Directory.Exists(installDir))
            {
                throw new DirectoryNotFoundException($"Install dir not found at '{installDir}'.");
            }

            var canonicalInstall = Path.GetFullPath(installDir);
            var outcomes = new List<OverlayOutcome>(plan.Count);
            var extracted = 0;
            var created = 0;
            var skipped = 0;
            var failed = 0;
            var duplicateEntries = new List<string>();
            var planDrift = false;

            using var archive = ZipFile.OpenRead(zipPath);
            // Index entries by FullName for O(1) lookup. Zip arrays are
            // typically a few thousand entries at most; HashSet+Dictionary
            // is overkill but cheap. Track duplicate FullNames defensively:
            // legal per the zip spec but produced only by buggy archivers,
            // and silently overwriting one with the other masks broken
            // upstream tooling. The last-entry-wins dictionary insert is
            // preserved so the install proceeds; we surface the duplicate
            // names through OverlayResult for the Forms layer to log.
            var entryIndex = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (entryIndex.ContainsKey(entry.FullName))
                {
                    duplicateEntries.Add(entry.FullName);
                }
                entryIndex[entry.FullName] = entry;
            }

            foreach (var action in plan)
            {
                if (action.Kind == ActionKind.Skip)
                {
                    outcomes.Add(new OverlayOutcome(action, Success: true, Error: null));
                    skipped++;
                    continue;
                }

                if (!entryIndex.TryGetValue(action.ZipEntryPath, out var entry))
                {
                    // Plan referenced an entry that doesn't exist in this
                    // zip — typically a Plan/Execute mismatch from passing
                    // different zips to the two calls. Surface BOTH as a
                    // failed entry AND via the PlanDriftDetected flag so
                    // the Forms layer can show a single "Plan was
                    // generated against a different zip — re-run Plan"
                    // banner instead of N independent failure messages.
                    outcomes.Add(new OverlayOutcome(action, Success: false,
                        Error: $"Zip entry '{action.ZipEntryPath}' not found in archive."));
                    failed++;
                    planDrift = true;
                    continue;
                }

                if (action.TargetPath is null)
                {
                    // Should be unreachable for Overwrite/Create kinds, but
                    // be defensive — a tampered plan must not be allowed
                    // to write to a null path.
                    outcomes.Add(new OverlayOutcome(action, Success: false,
                        Error: "Plan action has null TargetPath."));
                    failed++;
                    continue;
                }

                try
                {
                    var targetDir = Path.GetDirectoryName(action.TargetPath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    // ExtractToFile(..., overwrite: true) -> wipes any
                    // existing file. Backup has already preserved the
                    // original bytes by the time we get here.
                    entry.ExtractToFile(action.TargetPath, overwrite: true);
                    outcomes.Add(new OverlayOutcome(action, Success: true, Error: null));
                    extracted++;
                    if (action.Kind == ActionKind.Create) created++;
                }
                catch (Exception ex) when (
                    ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    outcomes.Add(new OverlayOutcome(action, Success: false,
                        Error: $"{ex.GetType().Name}: {ex.Message}"));
                    failed++;
                }
            }

            return new OverlayResult(
                canonicalInstall,
                outcomes,
                extracted,
                created,
                skipped,
                failed,
                planDrift,
                duplicateEntries);
        }

        // Builds the action for one zip entry without doing any I/O.
        // Visible for testing.
        internal static OverlayAction BuildPlanAction(ZipArchiveEntry entry, string canonicalInstall)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                return new OverlayAction(entry.FullName, null, ActionKind.Skip, SkipReason.EmptyName, entry.Length);
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                return new OverlayAction(entry.FullName, null, ActionKind.Skip, SkipReason.Directory, entry.Length);
            }

            if (!BackupManager.TryResolveInstallPath(canonicalInstall, entry.FullName, out var installPath))
            {
                return new OverlayAction(entry.FullName, null, ActionKind.Skip, SkipReason.PathTraversal, entry.Length);
            }

            var kind = File.Exists(installPath) ? ActionKind.Overwrite : ActionKind.Create;
            return new OverlayAction(entry.FullName, installPath, kind, SkipReason.None, entry.Length);
        }
    }

    // One zip entry's resolved action. ZipEntryPath uses the zip's native
    // forward-slash separators; TargetPath is the OS-native install path or
    // null for skipped entries. UncompressedSize is the zip's reported byte
    // count for the entry, surfaced so Forms can render a progress bar.
    public sealed record OverlayAction(
        string ZipEntryPath,
        string? TargetPath,
        ZipInstaller.ActionKind Kind,
        ZipInstaller.SkipReason SkipReason,
        long UncompressedSize);

    // Per-entry outcome after ExecuteOverlay runs.
    public sealed record OverlayOutcome(OverlayAction Action, bool Success, string? Error);

    // Aggregate result of an overlay execution.
    //
    // ExtractedCount = ALL entries successfully extracted (Create + Overwrite).
    // CreatedCount   = subset of ExtractedCount that were new files.
    //                  ExtractedCount - CreatedCount = files overwritten.
    // SkippedCount   = entries the plan classified as Skip (directory
    //                  markers, ZipSlip rejects, empty names).
    // FailedCount    = entries that threw or had a Plan/Execute mismatch.
    // PlanDriftDetected = true if at least one plan entry referenced a zip
    //                  entry path that was not present in the archive at
    //                  Execute time. Indicates the Plan was generated
    //                  against a different zip; Forms should surface a
    //                  single "re-run Plan" banner rather than N
    //                  independent failure messages.
    // DuplicateEntries = zip FullNames that appeared more than once in the
    //                  archive. Last-entry-wins by archive order; legal
    //                  per the zip spec but produced only by buggy
    //                  archivers. Forms should log so the upstream zip
    //                  tooling can be fixed.
    //
    // "Success" semantics: an install is FULLY clean only when
    // FailedCount == 0 AND PlanDriftDetected == false. With FailedCount > 0
    // some install-side files have the new version and some have the old;
    // the player MUST run Rollback because the install is in a mixed state
    // that won't load. Forms gates the Rollback button on this.
    public sealed record OverlayResult(
        string InstallDir,
        IReadOnlyList<OverlayOutcome> Outcomes,
        int ExtractedCount,
        int CreatedCount,
        int SkippedCount,
        int FailedCount,
        bool PlanDriftDetected,
        IReadOnlyList<string> DuplicateEntries);
}
