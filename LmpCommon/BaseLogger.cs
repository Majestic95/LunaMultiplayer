using LmpCommon.Enums;
using System;

namespace LmpCommon
{
    public class BaseLogger
    {
        protected virtual LogLevels LogLevel => LogLevels.Debug;
        protected virtual bool UseUtcTime => false;

        // [fix:BUG-037] Console.ForegroundColor / BackgroundColor / WriteLine / ResetColor
        // is process-wide state. Concurrent callers used to race the four-step "set fg → set bg →
        // write line → reset color" sequence, producing wrong-coloured lines, half-coloured
        // lines, and (worst case) a Color/Reset interleave that left the console in an unexpected
        // colour state. The lock makes the trio atomic per log line. Held only across the
        // Console operations — AfterPrint (file append + ring buffer add) runs outside so disk
        // latency doesn't extend the contention window. The level check runs before the lock so
        // below-threshold calls short-circuit without taking it or churning Console colour state
        // (pre-fix, public methods mutated ForegroundColor / ResetColor even when WriteLog would
        // discard the message — strict-improvement side effect of the refactor).
        //
        // Out-of-scope for this fix: BUG-037's "operator's in-progress input gets visually
        // overwritten" and "backspace-mangling" symptoms. Those need a Console.ReadLine
        // replacement / line-editor with terminal-emulator-aware re-emit — meaningful scope
        // and platform-specific risk; deferred. See the bug inventory for the residual surface.
        private static readonly object ConsoleLock = new object();

        /// <summary>
        /// Post-write hook. Server's <c>LunaLog</c> overrides this to append the formatted
        /// line to the on-disk log file and feed the ring buffer. Runs OUTSIDE the
        /// ConsoleLock so disk latency doesn't extend Console contention.
        ///
        /// Implementations MUST NOT call back into <c>BaseLogger</c> public methods.
        /// C#'s <c>lock</c> is reentrant on the same thread, so a logging callback would
        /// not deadlock, but its colour trio would visually interleave inside the outer
        /// line's lock window.
        /// </summary>
        protected virtual void AfterPrint(string line)
        {
            //Implement your own after logging code
        }

        #region Private methods

        private void Emit(LogLevels level, string type, string message, ConsoleColor foreground, ConsoleColor? background = null)
        {
            if (level > LogLevel) return;

            var output = UseUtcTime
                ? $"[{DateTime.UtcNow:HH:mm:ss}][{type}]: {message}"
                : $"[{DateTime.Now:HH:mm:ss}][{type}]: {message}";

            lock (ConsoleLock)
            {
                if (background.HasValue)
                    Console.BackgroundColor = background.Value;
                Console.ForegroundColor = foreground;
                Console.WriteLine(output);
                Console.ResetColor();
            }

            AfterPrint(output);
        }

        #endregion

        #region Public methods

        public void NetworkVerboseDebug(string message) =>
            Emit(LogLevels.VerboseNetworkDebug, "VerboseNetwork", message, ConsoleColor.Blue, ConsoleColor.DarkBlue);

        public void NetworkDebug(string message) =>
            Emit(LogLevels.NetworkDebug, "NetworkDebug", message, ConsoleColor.Cyan, ConsoleColor.DarkBlue);

        public void Debug(string message) =>
            Emit(LogLevels.Debug, "Debug", message, ConsoleColor.Green);

        public void Warning(string message) =>
            Emit(LogLevels.Normal, "Warning", message, ConsoleColor.Yellow);

        public void Info(string message) =>
            Emit(LogLevels.Normal, "Info", message, ConsoleColor.White);

        public void Normal(string message) =>
            Emit(LogLevels.Normal, "LMP", message, ConsoleColor.Gray);

        public void Error(string message) =>
            Emit(LogLevels.Normal, "Error", message, ConsoleColor.Red);

        public void Fatal(string message) =>
            Emit(LogLevels.Normal, "Fatal", message, ConsoleColor.Red, ConsoleColor.Yellow);

        public void ChatMessage(string message) =>
            Emit(LogLevels.Normal, "Chat", message, ConsoleColor.Cyan);

        #endregion
    }
}
