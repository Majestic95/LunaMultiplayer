using Server.Command.Command.Base;
using Server.Log;
using Server.System;
using System;

namespace Server.Command.Command
{
    public class BackupCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var args = commandArgs.Trim();
            var lower = args.ToLower();

            if (string.IsNullOrEmpty(lower) || lower == "now")
            {
                LunaLog.Normal("Manual backup initiated...");
                BackupSystem.RunBackup();
                LunaLog.Normal("Manual backup completed successfully.");
                return true;
            }

            if (lower == "archive")
            {
                LunaLog.Normal("Manual archive backup initiated...");
                try
                {
                    var dir = BackupSystem.RunArchiveBackup();
                    LunaLog.Normal($"Manual archive backup completed: {dir}");
                    return true;
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Archive backup failed: {e.Message}");
                    return false;
                }
            }

            if (lower == "list")
            {
                var archives = BackupSystem.ListArchives();
                if (archives.Count == 0)
                {
                    LunaLog.Normal("No archive snapshots present.");
                }
                else
                {
                    LunaLog.Normal($"Archive snapshots ({archives.Count}, newest first):");
                    foreach (var a in archives) LunaLog.Normal($"  {a}");
                }
                return true;
            }

            if (lower.StartsWith("restore "))
            {
                // Preserve original casing of the timestamp argument.
                var timestamp = args.Substring("restore ".Length).Trim();
                var ok = BackupSystem.RestoreFromArchive(timestamp, out var message);
                LunaLog.Normal(message);
                return ok;
            }

            LunaLog.Normal("Usage:");
            LunaLog.Normal("  /backup [now]                 - flush in-memory state to canonical Universe files (no separate copy kept)");
            LunaLog.Normal("  /backup archive               - write a timestamped snapshot under <Universe>/_archives/");
            LunaLog.Normal("  /backup list                  - list available archive snapshots");
            LunaLog.Normal("  /backup restore <timestamp>   - replace canonical Universe with a named archive (no players must be online; restart server afterwards)");
            return false;
        }
    }
}
