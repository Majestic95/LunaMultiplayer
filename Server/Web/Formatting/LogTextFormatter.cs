using LmpCommon;
using Server.Log;
using System.Text;

namespace Server.Web.Formatting
{
    /// <summary>
    /// Renders <see cref="LogRingBuffer"/> as a human-readable text dump for
    /// the admin dashboard's <c>GET /log</c> endpoint. The structured JSON
    /// view at <c>GET /logjson</c> remains for tooling.
    ///
    /// The output is intentionally close to what an operator would see in the
    /// console / log file — a small context header followed by raw formatted
    /// lines, oldest first. Operators can grep / tail in a browser tab without
    /// touching the server filesystem.
    /// </summary>
    public static class LogTextFormatter
    {
        public static string Format()
        {
            var entries = LogRingBuffer.Snapshot();
            var sb = new StringBuilder(2048 + entries.Length * 120);

            // Header: gives an operator everything they need to identify
            // the server build before they scroll into the log body.
            sb.Append("# fork:      ").AppendLine(ForkBuildInfo.ForkName);
            sb.Append("# protocol:  ").AppendLine(LmpVersioning.CurrentVersion.ToString());
            sb.Append("# fixes:     ").AppendLine(string.Join(" ", ForkBuildInfo.ActiveFixes));
            sb.Append("# ring:      ").Append(entries.Length).Append('/').Append(LogRingBuffer.Capacity)
              .AppendLine(" entries (oldest first)");
            sb.AppendLine("# ---");

            if (entries.Length == 0)
            {
                sb.AppendLine("(log ring buffer is empty)");
                return sb.ToString();
            }

            foreach (var entry in entries)
            {
                // Prefer the Formatted string as captured by LunaLog.AfterPrint;
                // it's already the exact line operators see in the console.
                // Fall back to reconstructed form only if that capture is missing.
                if (!string.IsNullOrEmpty(entry.Formatted))
                {
                    sb.AppendLine(entry.Formatted);
                }
                else
                {
                    sb.Append('[').Append(entry.TimestampUtc.ToString("HH:mm:ss")).Append("][")
                      .Append(entry.Level).Append("]: ");
                    if (!string.IsNullOrEmpty(entry.Subsystem))
                        sb.Append('[').Append(entry.Subsystem).Append("]: ");
                    sb.AppendLine(entry.Message);
                }
            }

            return sb.ToString();
        }
    }
}
