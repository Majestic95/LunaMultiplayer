using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Server.Log
{
    /// <summary>
    /// Dedicated audit log that records every time a craft is created (first time the server sees a
    /// vessel's proto) or removed, so server operators can troubleshoot missing/re-appearing ships.
    ///
    /// The file lives at <c>logs/CraftCreationAndRemoval.txt</c> and is truncated at server start
    /// (via the static constructor) so each server run produces a fresh audit trail.
    ///
    /// This class deliberately depends only on <see cref="LunaLog.LogFolder"/> and
    /// <see cref="System.FileHandler"/> (for thread-safe writes) to keep a single responsibility:
    /// write audit entries. It does NOT extend <see cref="LmpCommon.BaseLogger"/> because we do not
    /// want these entries echoed to the console or the general <c>lmpserver_*.log</c> file.
    /// </summary>
    public static class CraftCreationAndRemovalLog
    {
        private static readonly string LogFilePath = Path.Combine(LunaLog.LogFolder, "CraftCreationAndRemoval.txt");

        static CraftCreationAndRemovalLog()
        {
            try
            {
                if (!System.FileHandler.FolderExists(LunaLog.LogFolder))
                    System.FileHandler.FolderCreate(LunaLog.LogFolder);

                // Reset the file on each server start so the audit trail reflects only the current run.
                var header =
                    $"# Craft Creation and Removal audit log" + Environment.NewLine +
                    $"# Server started at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC" + Environment.NewLine +
                    $"# Format: [Timestamp UTC] Vessel <GUID> (<Vessel Name>) created/removed by player <Player> (<Reason>)" + Environment.NewLine;

                System.FileHandler.WriteToFile(LogFilePath, header);
            }
            catch (Exception e)
            {
                // If we can't prepare the file (permissions, disk full, etc.) surface it in the
                // main log but don't crash the server - the audit log is advisory.
                LunaLog.Error($"Failed to initialize CraftCreationAndRemoval.txt: {e.Message}");
            }
        }

        /// <summary>
        /// Ensures the static constructor has run. Call this once at server startup so the audit
        /// file is truncated even if no craft events happen before the first log write.
        /// </summary>
        public static void Initialize()
        {
            // Touching a static member forces the static constructor to execute.
            _ = LogFilePath;
        }

        /// <summary>
        /// Record that a brand-new vessel was registered on the server.
        /// </summary>
        public static void LogCreated(Guid vesselId, string vesselName, string playerName, string reason)
        {
            WriteLine("created", vesselId, vesselName, playerName, reason);
        }

        /// <summary>
        /// Record that a vessel was removed from the server.
        /// </summary>
        public static void LogRemoved(Guid vesselId, string vesselName, string playerName, string reason)
        {
            WriteLine("removed", vesselId, vesselName, playerName, reason);
        }

        /// <summary>
        /// Pulls the <c>name = ...</c> field out of a raw KSP vessel config-node string without
        /// having to parse the whole node. Returns <c>null</c> if no name can be found.
        /// We scope to the top of the text and match "name" as a standalone field so we don't
        /// accidentally grab "moduleName" or part-level names.
        /// </summary>
        private static readonly Regex VesselNameRegex = new Regex(
            @"(?:^|\n)\s*name\s*=\s*(?<value>.*?)\s*(?:\r|\n|$)",
            RegexOptions.Compiled);

        public static string ExtractVesselName(string vesselConfigNodeText)
        {
            if (string.IsNullOrEmpty(vesselConfigNodeText)) return null;

            // Only scan the top of the file where the vessel-level fields live; past that we'd
            // start hitting part-level "name = <part identifier>" lines and pick up garbage.
            var scanLength = Math.Min(vesselConfigNodeText.Length, 2048);
            var header = vesselConfigNodeText.Substring(0, scanLength);

            var match = VesselNameRegex.Match(header);
            return match.Success ? match.Groups["value"].Value : null;
        }

        private static void WriteLine(string action, Guid vesselId, string vesselName, string playerName, string reason)
        {
            var safeName = string.IsNullOrWhiteSpace(vesselName) ? "Unknown" : vesselName.Trim();
            var safePlayer = string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName.Trim();
            var safeReason = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason.Trim();

            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Vessel {vesselId} ({safeName}) {action} by player {safePlayer} ({safeReason})" + Environment.NewLine;

            try
            {
                System.FileHandler.AppendToFile(LogFilePath, line);
            }
            catch (Exception e)
            {
                LunaLog.Error($"Failed to append to CraftCreationAndRemoval.txt: {e.Message}");
            }
        }
    }
}
