using Server.Log;

namespace Server.Web.Structures
{
    /// <summary>
    /// JSON payload for <c>GET /log</c>. Snapshots <see cref="LogRingBuffer"/>
    /// on construction so each request gets the entries that were resident at
    /// the moment the response was assembled. The snapshot is oldest-first;
    /// operators tailing the dashboard expect chronological order.
    /// </summary>
    public class LogSnapshot
    {
        public int Capacity { get; } = LogRingBuffer.Capacity;

        public int Count { get; }

        public LogEntry[] Entries { get; }

        public LogSnapshot()
        {
            Entries = LogRingBuffer.Snapshot();
            Count = Entries.Length;
        }
    }
}
