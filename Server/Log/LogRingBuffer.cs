using System;

namespace Server.Log
{
    /// <summary>
    /// Fixed-capacity, thread-safe circular buffer of <see cref="LogEntry"/>
    /// instances. Captures every line that flows through <see cref="LunaLog"/>'s
    /// <c>AfterPrint</c> hook so the admin dashboard (Stage 3.7) and any
    /// in-process inspector can read recent server output without re-opening
    /// the rotating log file.
    ///
    /// Capacity is hard-coded (2000 entries, ≈ a few hundred KB at typical
    /// message sizes). If a future <c>LogSettings.RingBufferSize</c> is
    /// introduced this becomes the default; for now YAGNI applies.
    /// </summary>
    public static class LogRingBuffer
    {
        public const int Capacity = 2000;

        private static readonly LogEntry[] Buffer = new LogEntry[Capacity];
        private static readonly object Lock = new object();
        private static int _writeIndex;
        private static int _count;

        /// <summary>Appends an entry, overwriting the oldest when at capacity.</summary>
        public static void Add(LogEntry entry)
        {
            if (entry == null)
                return;

            lock (Lock)
            {
                Buffer[_writeIndex] = entry;
                _writeIndex = (_writeIndex + 1) % Capacity;
                if (_count < Capacity)
                    _count++;
            }
        }

        /// <summary>
        /// Returns a copy of the entries currently in the buffer, oldest first.
        /// Safe to enumerate even if <see cref="Add"/> is called concurrently.
        /// </summary>
        public static LogEntry[] Snapshot()
        {
            lock (Lock)
            {
                if (_count == 0)
                    return Array.Empty<LogEntry>();

                var result = new LogEntry[_count];
                var start = _count < Capacity ? 0 : _writeIndex;
                for (var i = 0; i < _count; i++)
                    result[i] = Buffer[(start + i) % Capacity];
                return result;
            }
        }

        /// <summary>Removes all entries. Primarily for tests and admin-reset.</summary>
        public static void Clear()
        {
            lock (Lock)
            {
                Array.Clear(Buffer, 0, Capacity);
                _writeIndex = 0;
                _count = 0;
            }
        }

        /// <summary>Number of entries currently held (0 ≤ Count ≤ Capacity).</summary>
        public static int Count
        {
            get
            {
                lock (Lock)
                    return _count;
            }
        }
    }
}
