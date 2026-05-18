using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LunaServerGui.Models;

namespace LunaServerGui.Services;

// TODO(later): orphan-prevention when the GUI crashes. On Windows, the child
// server stays alive after the GUI process is killed, fighting any new server
// for the listen port. Fix is a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
// (P/Invoke); on Linux, prctl(PR_SET_PDEATHSIG). Out of scope for slice-1.

public enum ProcessState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

public sealed record StartResult(bool Success, string? ErrorMessage);

/// <summary>
/// Launches and supervises the Luna server child process.
/// All public events fire on background threads; UI consumers must marshal
/// handler bodies onto the UI thread (Avalonia: <c>Dispatcher.UIThread.Post</c>).
/// State machine: Stopped -&gt; Starting -&gt; Running -&gt; Stopping -&gt; Stopped.
/// <c>StopAsync</c> is best-effort: closes stdin (which exits the server's
/// command-reader thread but does NOT stop the server — the server has no
/// /quit stdin command), then kills the process tree after the graceful
/// timeout. Operators should be warned that in-flight backups may not flush.
/// </summary>
public sealed class ServerProcessService : IDisposable
{
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private Process? _process;
    private bool _disposed;
    private ProcessState _state = ProcessState.Stopped;
    private int? _lastExitCode;
    private Task? _inFlightStop;

    public event EventHandler<string>? OutputLineReceived;
    public event EventHandler<string>? ErrorLineReceived;
    public event EventHandler<ProcessState>? StateChanged;
    public event EventHandler<int>? Exited;

    public ProcessState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>The most recent process exit code, or null if the server has not yet exited.</summary>
    public int? LastExitCode
    {
        get { lock (_lock) return _lastExitCode; }
    }

    public async Task<StartResult> StartAsync(ServerEntrypoint entrypoint)
    {
        ArgumentNullException.ThrowIfNull(entrypoint);

        Process process;
        ProcessState? eventToFire = null;
        try
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ServerProcessService));
                if (_state != ProcessState.Stopped)
                    return new StartResult(false, $"Cannot start while in {_state} state.");

                process = new Process
                {
                    StartInfo = BuildStartInfo(entrypoint),
                    EnableRaisingEvents = true,
                };
                process.OutputDataReceived += OnOutputDataReceived;
                process.ErrorDataReceived += OnErrorDataReceived;
                process.Exited += OnProcessExited;
                _process = process;
                _lastExitCode = null;

                _state = ProcessState.Starting;
                eventToFire = ProcessState.Starting;
            }
        }
        finally
        {
            if (eventToFire.HasValue) StateChanged?.Invoke(this, eventToFire.Value);
        }

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
        }
        catch (Exception ex)
        {
            ProcessState? rollback = null;
            lock (_lock)
            {
                if (_process == process) _process = null;
                if (_state == ProcessState.Starting)
                {
                    _state = ProcessState.Stopped;
                    rollback = ProcessState.Stopped;
                }
            }
            if (rollback.HasValue) StateChanged?.Invoke(this, rollback.Value);
            try { process.Dispose(); } catch (Exception) { /* best effort; child never launched */ }
            return new StartResult(false, $"Failed to start server: {ex.Message}");
        }

        // BeginOutput/ErrorReadLine can throw if (a) the child exited
        // microseconds after Start() returned, or (b) Dispose ran on another
        // thread between the lock release above and here. StartAsync's
        // contract is to return a StartResult, not to throw — catch.
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            ProcessState? rollback = null;
            lock (_lock)
            {
                if (_process == process) _process = null;
                if (_state == ProcessState.Starting)
                {
                    _state = ProcessState.Stopped;
                    rollback = ProcessState.Stopped;
                }
            }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* race with natural exit */ }
            try { process.Dispose(); } catch (Exception) { /* best effort */ }
            if (rollback.HasValue) StateChanged?.Invoke(this, rollback.Value);
            return new StartResult(false,
                $"Server process exited or service was disposed before output redirection completed: {ex.Message}");
        }

        ProcessState? runningEvent = null;
        lock (_lock)
        {
            if (_disposed)
            {
                // Dispose raced with us — clean up the orphan and bail.
                if (_process == process) _process = null;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception) { /* best effort */ }
                try { process.Dispose(); } catch (Exception) { /* best effort */ }
                return new StartResult(false, "Service was disposed during start.");
            }
            // Guard against the immediate-exit race: OnProcessExited may have
            // already moved us to Stopped. Don't clobber that.
            if (_state == ProcessState.Starting)
            {
                _state = ProcessState.Running;
                runningEvent = ProcessState.Running;
            }
        }
        if (runningEvent.HasValue) StateChanged?.Invoke(this, runningEvent.Value);

        // Suppress async-method warning when no await is reached.
        await Task.CompletedTask;
        return new StartResult(true, null);
    }

    public async Task SendCommandAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Cache the Process under the lock so the rest of this method works
        // against a stable reference. There IS a TOCTOU window where the
        // server exits between this lock-release and the WriteLineAsync below:
        // we rely on the broken-pipe exceptions (IOException /
        // ObjectDisposedException) to surface the resulting failure cleanly.
        Process process;
        lock (_lock)
        {
            if (_state != ProcessState.Running || _process is null)
                throw new InvalidOperationException("Server is not running.");
            process = _process;
        }

        try
        {
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Failed to send command (pipe broken — did the server exit?): {ex.Message}", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new InvalidOperationException("Server is not running.", ex);
        }
    }

    public Task StopAsync(TimeSpan? gracefulTimeout = null)
    {
        var timeout = gracefulTimeout ?? DefaultStopTimeout;

        Process process;
        ProcessState? eventToFire = null;
        lock (_lock)
        {
            // Coalesce concurrent stop calls onto the same task — the second
            // caller awaits the first.
            if (_inFlightStop is not null) return _inFlightStop;
            if (_state == ProcessState.Stopped) return Task.CompletedTask;
            if (_process is null) return Task.CompletedTask;
            process = _process;
            _state = ProcessState.Stopping;
            eventToFire = ProcessState.Stopping;
            _inFlightStop = StopInternalAsync(process, timeout);
        }
        if (eventToFire.HasValue) StateChanged?.Invoke(this, eventToFire.Value);
        return _inFlightStop;
    }

    private async Task StopInternalAsync(Process process, TimeSpan timeout)
    {
        try
        {
            // Step 1: close stdin so the server's command thread exits cleanly.
            // Server has no /quit command, so this alone does NOT stop the
            // server — but it's good citizenship and the server logs the EOF.
            try { process.StandardInput.Close(); }
            catch (Exception) { /* pipe already broken; continue to step 2 */ }

            // Step 2: wait briefly in case the server is exiting on its own
            // (e.g. operator just sent /restartserver).
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) { /* timed out; fall through to kill */ }
            catch (Exception) { /* unexpected; fall through to kill */ }

            // Step 3: if still alive, kill the whole process tree. The risk
            // (in-flight backups not flushed) is surfaced to the operator by
            // the UI before they invoke Stop.
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception) { /* race with natural exit */ }

            // Cleanup is handled by OnProcessExited (Process.Exited fires
            // after kill).
        }
        finally
        {
            lock (_lock) { _inFlightStop = null; }
        }
    }

    public void Dispose()
    {
        Process? process;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* best effort; may have exited */ }
            try { process.Dispose(); }
            catch (Exception) { /* best effort */ }
        }
    }

    private void OnOutputDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
            OutputLineReceived?.Invoke(this, e.Data);
    }

    private void OnErrorDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
            ErrorLineReceived?.Invoke(this, e.Data);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Process? process;
        int exitCode;
        ProcessState? eventToFire = null;

        lock (_lock)
        {
            process = _process;
            _process = null;
            if (_state != ProcessState.Stopped)
            {
                _state = ProcessState.Stopped;
                eventToFire = ProcessState.Stopped;
            }
            try { _lastExitCode = process?.ExitCode; }
            catch (Exception) { _lastExitCode = -1; }
        }

        // Fire StateChanged outside the lock so synchronous handlers cannot
        // deadlock by re-entering the service.
        if (eventToFire.HasValue) StateChanged?.Invoke(this, eventToFire.Value);

        if (process is null) return;
        try { exitCode = process.ExitCode; } catch (Exception) { exitCode = -1; }

        // Async stdout/stderr pumps may still have buffered data. The
        // parameterless WaitForExit is the documented overload that waits
        // for async-redirected output to flush (WaitForExitAsync does NOT
        // flush them). Drain + Dispose + Exited fire on a worker so this IO
        // callback returns promptly; operator sees final server log lines
        // BEFORE the Exited notification, which is the correct ordering.
        _ = Task.Run(() =>
        {
            try { process.WaitForExit(); }
            catch (Exception) { /* already disposed or weird state; best effort */ }
            try { process.Dispose(); }
            catch (Exception) { /* best effort */ }
            Exited?.Invoke(this, exitCode);
        });
    }

    private static ProcessStartInfo BuildStartInfo(ServerEntrypoint entrypoint)
    {
        var psi = new ProcessStartInfo
        {
            FileName = entrypoint.ExecutablePath,
            WorkingDirectory = entrypoint.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in entrypoint.Arguments)
            psi.ArgumentList.Add(arg);
        return psi;
    }
}
