using System;
using System.Diagnostics;
using System.Threading;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Detects whether KSP is currently running and waits with backoff for the
    // process to close. Players often start the updater while KSP is still
    // shutting down (the loading bar is just past 100%, the lock-flush is
    // mid-broadcast, etc.), so a short retry window before refusing is the
    // right UX — but we don't want to spin forever if KSP genuinely refuses
    // to close.
    //
    // The retry loop is split into two pieces for testability:
    //   IsKspRunningOnce          — wraps Process.GetProcessesByName, hard to mock
    //   WaitForCloseUsingProbe    — pure orchestrator parameterised on a probe Func
    //                               + a sleep Action; unit-testable end-to-end
    //
    // For tri-state diagnostics (Running / NotRunning / Unknown), use
    // ProbeKspRunningState. The bool-returning IsKspRunningOnce maps Unknown
    // to Running (refuse-by-default-on-uncertain) so the install path errs
    // toward the safe side when the OS won't tell us.
    //
    // Defaults: 25 attempts × 200ms = 5 seconds total. Mirrors the spec's
    // "retry-with-backoff on sharing-violation IOException up to ~5s" upper
    // bound, applied to the higher-level "is KSP still up?" question.
    public static class KspRunningCheck
    {
        public const int DefaultMaxAttempts = 25;
        public const int DefaultDelayMs = 200;

        // Process names KSP1 ships under. Both 64-bit (modern KSP) and 32-bit
        // (legacy Windows builds) variants — checking both avoids a false
        // "not running" on a player who happens to be on the 32-bit branch.
        // We strip the .exe extension because Process.GetProcessesByName does.
        private static readonly string[] KspProcessNames = { "KSP_x64", "KSP" };

        // Tri-state result for diagnostic-aware callers (Forms layer in
        // sub-slice 4). Unknown means the OS refused to enumerate processes
        // (rare — typically a locked-down corporate account). The Forms layer
        // should refuse the install and prompt the operator to manually
        // confirm KSP is closed, rather than silently treating Unknown as
        // NotRunning and proceeding against a possibly-running KSP.
        public enum ProbeState
        {
            NotRunning,
            Running,
            Unknown,
        }

        // Probes the OS process list once and returns full diagnostics. If
        // EVERY name iteration throws OR returns no result, the state is
        // Unknown. If any name iteration finds a process, Running. Otherwise
        // NotRunning.
        public static ProbeState ProbeKspRunningState()
        {
            var anySucceeded = false;

            foreach (var name in KspProcessNames)
            {
                Process[]? processes = null;
                try
                {
                    processes = Process.GetProcessesByName(name);
                    anySucceeded = true;
                    if (processes.Length > 0) return ProbeState.Running;
                }
                catch (InvalidOperationException) { /* race: process gone between enum and read */ }
                catch (System.ComponentModel.Win32Exception) { /* OS-level permission denial */ }
                finally
                {
                    if (processes != null)
                    {
                        foreach (var p in processes) p.Dispose();
                    }
                }
            }

            return anySucceeded ? ProbeState.NotRunning : ProbeState.Unknown;
        }

        // Probes the OS process list once. Returns true if any KSP process is
        // currently running. Safe to call repeatedly — Process.GetProcessesByName
        // takes a snapshot, but each call is independent.
        //
        // On Unknown (no name iteration succeeded), returns true. This is
        // deliberate — refuse the install when we can't tell, rather than
        // proceed against a possibly-running KSP. Consumers who care about
        // the distinction should use ProbeKspRunningState.
        public static bool IsKspRunningOnce()
        {
            return ProbeKspRunningState() != ProbeState.NotRunning;
        }

        // Convenience wrapper around WaitForCloseUsingProbe that uses the live
        // OS probe and Thread.Sleep. Returns true if KSP closed within the
        // attempt window, false if it was still running on the final probe.
        //
        // At least one probe always runs — a maxAttempts of 0 or negative is
        // clamped up to 1 so the caller gets a definite answer with no sleep.
        // Use this when you want a single immediate check (e.g. unit tests).
        public static bool WaitForKspToClose(int maxAttempts = DefaultMaxAttempts, int delayMs = DefaultDelayMs)
        {
            return WaitForCloseUsingProbe(maxAttempts, delayMs, IsKspRunningOnce, Thread.Sleep);
        }

        // Pure orchestrator — takes the probe and sleep functions as parameters
        // so unit tests can inject deterministic counters in place of real OS
        // calls. The contract: probe is called BEFORE each potential sleep, and
        // a sleep only happens if there is at least one further attempt left.
        //
        // Returns true if any probe returned false during the loop (KSP closed
        // within the window). Returns false if every probe returned true.
        public static bool WaitForCloseUsingProbe(
            int maxAttempts,
            int delayMs,
            Func<bool> isRunningProbe,
            Action<int> sleepFn)
        {
            if (isRunningProbe == null) throw new ArgumentNullException(nameof(isRunningProbe));
            if (sleepFn == null) throw new ArgumentNullException(nameof(sleepFn));

            var attempts = Math.Max(1, maxAttempts);
            var sleep = Math.Max(0, delayMs);

            for (var i = 0; i < attempts; i++)
            {
                if (!isRunningProbe()) return true;
                // Don't sleep after the final probe — the caller already knows
                // the answer at that point.
                if (i < attempts - 1) sleepFn(sleep);
            }

            return false;
        }
    }
}
