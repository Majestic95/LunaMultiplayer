using System;

namespace Server.Log
{
    /// <summary>
    /// Structured snapshot of a single log line captured by <see cref="LunaLog"/>.
    /// Populated by <see cref="LogRingBuffer"/> and consumed by the admin dashboard
    /// (see <c>Server/Web/WebServer.cs</c>) and any other in-process inspector that
    /// wants to read recent server output without re-reading the log file.
    /// </summary>
    public sealed class LogEntry
    {
        public DateTime TimestampUtc { get; }

        /// <summary>
        /// The type-tag emitted by <c>BaseLogger</c>: <c>Info</c>, <c>Warning</c>,
        /// <c>Error</c>, <c>Fatal</c>, <c>Debug</c>, <c>LMP</c> (from
        /// <c>LunaLog.Normal</c>), <c>Chat</c>, <c>NetworkDebug</c>, or
        /// <c>VerboseNetwork</c>. Empty string if the line couldn't be parsed.
        /// </summary>
        public string Level { get; }

        /// <summary>
        /// Parsed from a leading <c>[Subsystem]</c> bracket in the message body
        /// (LMP's existing convention, e.g. <c>[CleanContracts]: ...</c>). Empty
        /// when the message has no subsystem prefix.
        /// </summary>
        public string Subsystem { get; }

        /// <summary>The body of the message with the optional subsystem prefix stripped.</summary>
        public string Message { get; }

        /// <summary>The full line as it appeared in the console / log file.</summary>
        public string Formatted { get; }

        public LogEntry(DateTime timestampUtc, string level, string subsystem, string message, string formatted)
        {
            TimestampUtc = timestampUtc;
            Level = level ?? string.Empty;
            Subsystem = subsystem ?? string.Empty;
            Message = message ?? string.Empty;
            Formatted = formatted ?? string.Empty;
        }

        /// <summary>
        /// Parses a <c>BaseLogger</c>-formatted line of the form
        /// <c>[HH:mm:ss][Type]: message</c>, where <c>message</c> may itself begin
        /// with a <c>[Subsystem]</c> tag. Resilient to malformed input — never throws.
        /// </summary>
        public static LogEntry Parse(string formattedLine, DateTime timestampUtc)
        {
            if (string.IsNullOrEmpty(formattedLine))
                return new LogEntry(timestampUtc, string.Empty, string.Empty, string.Empty, formattedLine ?? string.Empty);

            var body = formattedLine;

            // Strip leading "[HH:mm:ss]" timestamp (10 chars: open bracket + 8 chars + close bracket).
            if (body.Length > 10 && body[0] == '[' && body[9] == ']')
                body = body.Substring(10);

            // Extract "[Type]:" — the level tag emitted by BaseLogger.
            var level = string.Empty;
            if (body.Length > 0 && body[0] == '[')
            {
                var close = body.IndexOf(']');
                if (close > 0)
                {
                    level = body.Substring(1, close - 1);
                    body = body.Substring(close + 1);
                    if (body.StartsWith(":"))
                        body = body.Substring(1);
                    body = body.TrimStart();
                }
            }

            // Extract optional "[Subsystem]" tag at the head of the message body.
            var subsystem = string.Empty;
            if (body.Length > 0 && body[0] == '[')
            {
                var close = body.IndexOf(']');
                if (close > 0)
                {
                    subsystem = body.Substring(1, close - 1);
                    body = body.Substring(close + 1);
                    if (body.StartsWith(":"))
                        body = body.Substring(1);
                    body = body.TrimStart();
                }
            }

            return new LogEntry(timestampUtc, level, subsystem, body, formattedLine);
        }
    }
}
