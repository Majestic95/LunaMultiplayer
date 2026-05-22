using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Writes a human-readable per-entry log when an install fails partway
    // through the Extract stage. The InstallPipeline already captures every
    // file's outcome in OverlayResult.Outcomes, but the ErrorDialog only
    // surfaces the aggregate count ("3 failed entries") — without the per-
    // entry detail an operator can't tell which file was locked, and a
    // diagnostic round-trip requires shipping logs back to the maintainer.
    //
    // Scope:
    //   - WriteFailureLog produces a single .txt file under a caller-supplied
    //     directory (typically the PlayerUpdater exe's own folder so the
    //     player can find it next to the program they just used).
    //   - Best-effort: write failure does NOT throw. The MainForm install-
    //     failure branch surfaces the log path only when WriteFailureLog
    //     returns a non-null path; otherwise it falls back to the legacy
    //     details-only error text. A log-write IOException must not
    //     replace the actual install failure with a misleading "couldn't
    //     write log" error in the player's eyes.
    //   - Only writes when result.OverlayResult is non-null. Pre-extract
    //     failures (Download / Hash / DiskSpace) already have a single
    //     informative error string in result.Error and don't benefit from
    //     a per-entry log.
    //
    // The FormatLog helper is `internal` so unit tests can pin the output
    // format without touching the filesystem. The directory and timestamp
    // are taken as parameters so tests are deterministic across runs.
    public static class InstallLogWriter
    {
        // Filename prefix for log files. Used by the writer + by any future
        // cleanup pass (e.g. "delete logs older than 30 days").
        public const string FilenamePrefix = "install-log-";

        // Writes a per-entry log for the given failed install. Returns the
        // absolute path of the log on success, or null when:
        //   - result.OverlayResult is null (pre-extract failure — no per-
        //     entry detail to write)
        //   - the log directory could not be created
        //   - the file write itself failed (permissions, disk full, etc.)
        //
        // A return of null is non-fatal — the caller should fall back to
        // showing result.Error in the error dialog without a log-file hint.
        public static string? WriteFailureLog(
            InstallResult result,
            string installDir,
            string assetName,
            string replacingTag,
            string logDir,
            DateTime timestampUtc)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.OverlayResult == null) return null;
            if (string.IsNullOrWhiteSpace(logDir)) return null;

            string fileName;
            try
            {
                fileName = BuildFileName(timestampUtc);
            }
            catch (FormatException)
            {
                // DateTime.ToString with the invariant format we use can't
                // realistically throw, but guard against a future format
                // change that might. Fail safely — operator sees the
                // dialog without a log hint.
                return null;
            }

            var path = Path.Combine(logDir, fileName);
            var content = FormatLog(result, installDir, assetName, replacingTag, timestampUtc);

            try
            {
                Directory.CreateDirectory(logDir);
                File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return path;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        // Pure formatter — produces the log body without touching the filesystem.
        // Visible for testing. The exact text is part of the operator-facing
        // contract; changes here should preserve the "failed entries first,
        // then full log" structure so a grep for "FAIL" finds the actionable
        // lines at the top.
        internal static string FormatLog(
            InstallResult result,
            string installDir,
            string assetName,
            string replacingTag,
            DateTime timestampUtc)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var sb = new StringBuilder();
            sb.AppendLine("# LMP PlayerUpdater install log");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"# Timestamp:     {timestampUtc:O}"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"# Outcome:       {result.Outcome}"));
            sb.AppendLine($"# KSP install:   {installDir ?? "(unknown)"}");
            sb.AppendLine($"# Asset:         {assetName ?? "(unknown)"}");
            sb.AppendLine($"# Replacing tag: {(string.IsNullOrEmpty(replacingTag) ? "(none)" : replacingTag)}");
            sb.AppendLine();

            var overlay = result.OverlayResult;
            if (overlay == null)
            {
                sb.AppendLine("(No overlay result — failure occurred before the Extract stage. See pipeline error below.)");
            }
            else
            {
                var overwrites = overlay.ExtractedCount - overlay.CreatedCount;
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"Summary: {overlay.ExtractedCount} extracted ({overlay.CreatedCount} new, {overwrites} overwritten), " +
                    $"{overlay.SkippedCount} skipped, {overlay.FailedCount} failed"));
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"Plan drift detected: {overlay.PlanDriftDetected}"));
                if (overlay.DuplicateEntries.Count > 0)
                {
                    sb.AppendLine($"Duplicate zip entries (last-wins): {string.Join(", ", overlay.DuplicateEntries)}");
                }
                sb.AppendLine();

                // Failed entries first — most actionable for the operator.
                sb.AppendLine("# Failed entries (sharing violations, permissions, etc.):");
                var anyFailed = false;
                foreach (var o in overlay.Outcomes.Where(x => !x.Success))
                {
                    anyFailed = true;
                    sb.AppendLine($"  FAIL  {o.Action.ZipEntryPath}");
                    sb.AppendLine($"        → {o.Error ?? "(no error text)"}");
                }
                if (!anyFailed)
                {
                    sb.AppendLine("  (none)");
                }
                sb.AppendLine();

                // Full per-entry trail. Useful for diagnosing plan-drift +
                // for confirming which files DID land successfully when the
                // operator is deciding whether rollback is necessary.
                sb.AppendLine("# Full per-entry log:");
                foreach (var o in overlay.Outcomes)
                {
                    var tag = o.Success ? "OK  " : "FAIL";
                    var kindStr = o.Action.Kind.ToString().ToLowerInvariant();
                    sb.AppendLine($"  {tag}  [{kindStr}] {o.Action.ZipEntryPath}");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"Pipeline error: {result.Error ?? "(none)"}");

            return sb.ToString();
        }

        // Builds the timestamped log filename. Format chosen so a directory
        // sort by name is also a sort by time, and so the file is portable
        // across filesystems (no colons — Windows rejects them in filenames).
        private static string BuildFileName(DateTime timestampUtc)
        {
            return FilenamePrefix
                + timestampUtc.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)
                + "Z.txt";
        }
    }
}
