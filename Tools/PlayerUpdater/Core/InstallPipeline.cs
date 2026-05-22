using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Orchestrates the full install pipeline. Replaces what would otherwise
    // be a procedural sequence in the Forms layer with a single testable
    // entry point: `InstallAsync(request, progress, ct)`. The Forms layer
    // creates a request + an IProgress<InstallProgress> adapter, awaits
    // the call, and renders the InstallResult.
    //
    // Stage order (each reports a distinct InstallStage on progress):
    //   1. Preparing    — validate request, compute paths
    //   2. Downloading  — HttpDownloader streams the zip to a staging path
    //   3. Verifying    — HashVerifier validates SHA-256 against the GitHub digest
    //   4. BackingUp    — BackupManager copies overwritten files
    //   5. Extracting   — ZipInstaller overlays the new zip on the install
    //   6. Finalizing   — MarkInstallComplete + PruneBackups + delete staging
    //
    // Refusal semantics: each refusal returns an InstallResult with the
    // matching Outcome enum value + a diagnostic Error string + the partial
    // state (BackupDir if backup was created before failure, OverlayResult
    // if overlay ran). Forms can offer Rollback by passing BackupDir to
    // BackupManager.RestoreBackup.
    //
    // Disk space: refused with Outcome.DiskSpaceInsufficient. Unknown
    // outcome (UNC offline, virtual mount) does NOT refuse — Forms can
    // surface a warning, but the actual install I/O will fail with a
    // clearer error if there's a real problem.
    //
    // Hash verification: Mismatch refuses with Outcome.HashMismatch. Skipped
    // (digest absent on older releases) PROCEEDS with the install but
    // surfaces the skipped status via InstallResult.HashSkipped so Forms
    // can show a "release predates digest rollout — installed without
    // verification" notice.
    //
    // KSP-running gate: the pipeline does NOT check whether KSP is running.
    // That's the Forms layer's responsibility via KspRunningCheck before
    // the user clicks Install — surfacing it earlier in the UX (greyed-out
    // Install button) gives a better player experience than refusing
    // mid-pipeline with "KSP is open". The pipeline still bubbles the
    // resulting File.Copy sharing-violation error if KSP is running.
    //
    // Cancellation granularity: the Downloading stage respects
    // cancellationToken per 64 KB chunk (HttpDownloader). Stages 3-5
    // (Verifying / BackingUp / Extracting) are synchronous I/O that DO
    // NOT check the cancellation token mid-stage — cancellation is
    // honoured only at the stage boundaries. In practice each stage is
    // <2 seconds even for the 70 MB selfcontained zip, so mid-stage
    // cancellation is not a player-visible concern.
    public sealed class InstallPipeline : IDisposable
    {
        private readonly HttpDownloader _downloader;
        private readonly bool _ownsDownloader;

        // Default construction owns the HttpDownloader lifecycle. Tests
        // inject a FakeHttpMessageHandler-wrapped HttpDownloader and we
        // don't dispose it.
        public InstallPipeline(HttpDownloader? downloader = null)
        {
            _downloader = downloader ?? new HttpDownloader();
            _ownsDownloader = downloader is null;
        }

        public async Task<InstallResult> InstallAsync(
            InstallRequest request,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            // Pre-validate at the API boundary so Forms sees a clear
            // ArgumentException with a named field instead of a Stage-1
            // failure whose Error string is e.g.
            // "ArgumentOutOfRangeException: zipBytes must be non-negative".
            // The catchall below covers downstream issues; this catches
            // the malformed-request class up front.
            ValidateRequest(request);

            string? backupDir = null;
            OverlayResult? overlayResult = null;
            var stagingPath = ResolveStagingPath(request.Asset.Name);

            try
            {
                // -- Stage 1: Preparing --
                Report(progress, InstallStage.Preparing, 0, request.Asset.Size, request.Asset.Name);

                var spaceCheck = DiskSpaceCheck.Check(request.InstallDir, request.Asset.Size);
                if (spaceCheck.Outcome == DiskSpaceCheck.Outcome.Insufficient)
                {
                    return new InstallResult(
                        InstallOutcome.DiskSpaceInsufficient,
                        BackupDir: null,
                        OverlayResult: null,
                        HashSkipped: false,
                        Error: $"Insufficient disk space on {spaceCheck.DriveRoot}: " +
                               $"{spaceCheck.AvailableBytes:N0} bytes available, " +
                               $"{spaceCheck.RequiredBytes:N0} bytes required " +
                               $"(3x zip size for download + extract + backup).");
                }

                // -- Stage 2: Downloading --
                Report(progress, InstallStage.Downloading, 0, request.Asset.Size, request.Asset.Name);

                var downloadProgress = new ProgressAdapter<DownloadProgress>(p =>
                    Report(progress, InstallStage.Downloading, p.BytesRead,
                        p.TotalBytes > 0 ? p.TotalBytes : request.Asset.Size,
                        request.Asset.Name));
                await _downloader.DownloadAsync(
                    request.Asset.DownloadUrl,
                    stagingPath,
                    downloadProgress,
                    cancellationToken).ConfigureAwait(false);

                // -- Stage 3: Verifying --
                Report(progress, InstallStage.Verifying, 0, request.Asset.Size, request.Asset.Name);

                var hashResult = HashVerifier.VerifyFile(stagingPath, request.Asset.Sha256Hex);
                var hashSkipped = hashResult.Outcome == HashVerifier.Outcome.Skipped;
                if (hashResult.Outcome == HashVerifier.Outcome.Mismatch)
                {
                    return new InstallResult(
                        InstallOutcome.HashMismatch,
                        BackupDir: null,
                        OverlayResult: null,
                        HashSkipped: false,
                        Error: $"SHA-256 mismatch on downloaded zip. " +
                               $"Expected {hashResult.ExpectedHex}, computed {hashResult.ComputedHex}. " +
                               "Download may be corrupted or tampered. Retry; refuse if it persists.");
                }

                // -- Stage 4: BackingUp --
                Report(progress, InstallStage.BackingUp, 0, request.Asset.Size, request.Asset.Name);

                var backupPlan = BackupManager.PlanBackup(request.InstallDir, stagingPath);
                backupDir = BackupManager.ExecuteBackup(
                    backupPlan,
                    request.InstallDir,
                    request.ReplacingTag);

                // -- Stage 5: Extracting --
                Report(progress, InstallStage.Extracting, 0, request.Asset.Size, request.Asset.Name);

                var overlayPlan = ZipInstaller.PlanOverlay(stagingPath, request.InstallDir);
                overlayResult = ZipInstaller.ExecuteOverlay(overlayPlan, stagingPath, request.InstallDir);

                if (overlayResult.FailedCount > 0 || overlayResult.PlanDriftDetected)
                {
                    // Install is in a MIXED state — some files are new, some
                    // are old. Caller MUST offer Rollback (BackupDir is set).
                    // Leave the in-progress marker in place so a next-launch
                    // check via ListBackups surfaces "previous install was
                    // interrupted" even if the player closes the updater
                    // without rolling back.
                    return new InstallResult(
                        InstallOutcome.ExtractFailed,
                        backupDir,
                        overlayResult,
                        hashSkipped,
                        Error: $"Extract completed with {overlayResult.FailedCount} failed entries" +
                               (overlayResult.PlanDriftDetected ? " (plan drift detected — re-run Plan)" : "") +
                               ". Install is in a mixed state — Rollback recommended.");
                }

                // -- Stage 6: Finalizing --
                Report(progress, InstallStage.Finalizing, 0, request.Asset.Size, request.Asset.Name);

                BackupManager.MarkInstallComplete(backupDir);
                BackupManager.PruneBackups(request.InstallDir, request.BackupRetention);

                return new InstallResult(
                    InstallOutcome.Success,
                    backupDir,
                    overlayResult,
                    hashSkipped,
                    Error: null);
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation — Forms shows a "cancelled" dialog
                // and the player can re-run the install if they want.
                // Backup dir (if created) is left on disk with its in-
                // progress marker so the player can rollback if needed.
                throw;
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or System.Text.Json.JsonException
                or ArgumentException
                or FileNotFoundException)
            {
                // Catchall for stage-specific failures. The stage that failed
                // is detectable from the InstallResult: if BackupDir is null
                // the download or verify step failed; if OverlayResult is
                // null the backup step failed; otherwise the extract step
                // failed.
                var outcome = backupDir is null
                    ? (overlayResult is null
                        ? InstallOutcome.DownloadFailed
                        : InstallOutcome.ExtractFailed)
                    : InstallOutcome.ExtractFailed;
                return new InstallResult(
                    outcome,
                    backupDir,
                    overlayResult,
                    HashSkipped: false,
                    Error: $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Best-effort cleanup of the staging zip. Failure here is
                // harmless — it'll be picked up by the next system temp
                // sweep. We don't surface as an error because the install
                // already succeeded (or failed) by this point.
                try
                {
                    if (File.Exists(stagingPath)) File.Delete(stagingPath);
                }
                catch (Exception ex) when (
                    ex is IOException
                    or UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            if (_ownsDownloader)
            {
                _downloader.Dispose();
            }
        }

        // Internal seam for testing. Each call produces a unique staging
        // path so concurrent (parallel-test) calls don't collide.
        //
        // Cleanup contract: InstallAsync's finally-block deletes the
        // staging file on normal return or caught exception. Process-kill
        // / hard OOM / un-caught exception WILL leak the file — Windows
        // does not sweep %TEMP% automatically (only cleanmgr / Disk
        // Cleanup does). Forms layer should call SweepOrphanedStagingFiles
        // at startup to clean up zips orphaned by previous crashes.
        // The 'LunaMultiplayer-PlayerUpdater-' prefix is the sweep filter.
        internal static string ResolveStagingPath(string assetName)
        {
            // Use a Guid suffix so two install attempts in the same temp
            // dir don't conflict. The assetName prefix makes orphaned files
            // grep-friendly during diagnostic sessions.
            var safeName = assetName.Replace('/', '_').Replace('\\', '_');
            return Path.Combine(
                Path.GetTempPath(),
                $"{StagingFilePrefix}{Guid.NewGuid():N}-{safeName}");
        }

        public const string StagingFilePrefix = "LunaMultiplayer-PlayerUpdater-";

        // Best-effort cleanup of staging zips orphaned by a previous
        // PlayerUpdater process that exited without running InstallAsync's
        // finally-block (process kill, hard OOM). Forms layer calls this
        // at startup so stale ~70 MB self-contained zips don't accumulate
        // in %TEMP% over a player's lifetime of updater sessions.
        //
        // Returns the number of files deleted. Errors swallowed — a single
        // locked file from another in-flight install must not block the
        // sweep. Files newer than minAgeHours are skipped to avoid
        // racing a concurrent install that has just written its staging
        // file but has not yet reached the download-content stage.
        public static int SweepOrphanedStagingFiles(int minAgeHours = 24)
        {
            if (minAgeHours < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minAgeHours),
                    "minAgeHours must be non-negative.");
            }
            var tempRoot = Path.GetTempPath();
            if (!Directory.Exists(tempRoot)) return 0;

            var cutoff = DateTime.UtcNow.AddHours(-minAgeHours);
            var deleted = 0;
            try
            {
                foreach (var path in Directory.EnumerateFiles(tempRoot, StagingFilePrefix + "*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                        File.Delete(path);
                        deleted++;
                    }
                    catch (Exception ex) when (
                        ex is IOException
                        or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException) { }
            return deleted;
        }

        // Pre-flight validation that runs BEFORE Stage 1. Throws
        // ArgumentException naming the bad field so Forms can render a
        // clear error message without parsing a downstream exception
        // string. Each check mirrors a guard that exists in a downstream
        // helper (DiskSpaceCheck, BackupManager, ZipInstaller) — catching
        // them here gives the consumer one clean failure path instead of
        // four scattered exception types.
        private static void ValidateRequest(InstallRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.InstallDir))
            {
                throw new ArgumentException(
                    "InstallRequest.InstallDir must be non-empty.", nameof(request));
            }
            if (request.Asset is null)
            {
                throw new ArgumentException(
                    "InstallRequest.Asset must not be null.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.Asset.DownloadUrl))
            {
                throw new ArgumentException(
                    "InstallRequest.Asset.DownloadUrl must be non-empty.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.Asset.Name))
            {
                throw new ArgumentException(
                    "InstallRequest.Asset.Name must be non-empty.", nameof(request));
            }
            if (request.Asset.Size < 0)
            {
                throw new ArgumentException(
                    "InstallRequest.Asset.Size must be non-negative.", nameof(request));
            }
            if (request.BackupRetention < 0)
            {
                throw new ArgumentException(
                    "InstallRequest.BackupRetention must be non-negative.", nameof(request));
            }
        }

        private static void Report(
            IProgress<InstallProgress>? progress,
            InstallStage stage,
            long bytesProcessed,
            long totalBytes,
            string currentItem)
        {
            progress?.Report(new InstallProgress(stage, bytesProcessed, totalBytes, currentItem));
        }

        // Tiny adapter to translate the inner-stage IProgress<T> into the
        // outer pipeline-stage IProgress<InstallProgress>.
        private sealed class ProgressAdapter<T> : IProgress<T>
        {
            private readonly Action<T> _callback;
            public ProgressAdapter(Action<T> callback) { _callback = callback; }
            public void Report(T value) => _callback(value);
        }
    }

    // Input to InstallPipeline.InstallAsync. InstallDir is the KSP root the
    // overlay extracts onto. Asset comes from GitHubClient — Name +
    // DownloadUrl + Size + Sha256Hex are all consumed. ReplacingTag is
    // stamped into the backup manifest so the rollback UI can render
    // "Restore the install that was overwritten by <tag>". BackupRetention
    // is the count to keep post-prune (default from UpdaterSettings).
    public sealed record InstallRequest(
        string InstallDir,
        GitHubAsset Asset,
        string ReplacingTag,
        int BackupRetention);

    // Stages reported via IProgress<InstallProgress>. Forms maps these to
    // status labels + progress bar visibility.
    public enum InstallStage
    {
        Preparing,
        Downloading,
        Verifying,
        BackingUp,
        Extracting,
        Finalizing,
    }

    // Outcome enum from InstallAsync. Forms branches on this to choose
    // the success / rollback / error dialog.
    public enum InstallOutcome
    {
        // All stages completed cleanly. Player can launch KSP.
        Success,

        // Disk-space check refused before any download started. Player
        // sees AvailableBytes / RequiredBytes in the error.
        DiskSpaceInsufficient,

        // Download stage failed (HTTP error, transport error). Backup did
        // not run; the install is unchanged.
        DownloadFailed,

        // SHA-256 of the downloaded zip didn't match GitHub's digest.
        // Refused before backup; install unchanged.
        HashMismatch,

        // Backup or extract failed mid-pipeline. The install may be in a
        // mixed state — Rollback recommended. InstallResult.BackupDir
        // points at the backup to restore from.
        ExtractFailed,
    }

    // Result struct. BackupDir is the absolute path to the timestamped
    // backup directory; populated as soon as ExecuteBackup succeeds, so
    // it's available even on extract-stage failure. OverlayResult is the
    // ZipInstaller's per-entry outcomes; null when the pipeline never
    // reached the extract stage. HashSkipped is true when the asset had no
    // digest and the install proceeded without verification — the pipeline
    // does NOT refuse in that case; Forms decides whether to gate this
    // behind a user confirmation dialog ("This release predates SHA-256
    // verification — install anyway?"). Error is a single-line diagnostic
    // for the Forms error label.
    public sealed record InstallResult(
        InstallOutcome Outcome,
        string? BackupDir,
        OverlayResult? OverlayResult,
        bool HashSkipped,
        string? Error);

    // Periodic progress event from InstallAsync. BytesProcessed +
    // TotalBytes are meaningful during Downloading; for other stages they
    // are 0/Asset.Size respectively. CurrentItem is the asset name during
    // download and the current file path during BackingUp/Extracting (when
    // those stages add per-entry reporting; for now they fire once per
    // stage transition).
    public sealed record InstallProgress(
        InstallStage Stage,
        long BytesProcessed,
        long TotalBytes,
        string CurrentItem);
}
