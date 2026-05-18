using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

/// <summary>
/// Drives the Admin Actions tab. Maps form inputs to stdin commands sent
/// through <see cref="ServerProcessService.SendCommandAsync"/>. Command
/// results stream back to the Console tab's log pane — this VM only sends
/// and reports a brief status message ("Sent: …" / error). The View
/// supplies the confirm-dialog routine for destructive actions because
/// dialog hosting needs a Window reference the VM cannot reach.
/// </summary>
public sealed partial class AdminActionsViewModel : ViewModelBase
{
    private static readonly TimeSpan RestartStopDeadline = TimeSpan.FromSeconds(5);

    private readonly ServerProcessService _service;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BroadcastCommand))]
    [NotifyCanExecuteChangedFor(nameof(KickCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ListClientsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ListLocksCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectionStatsCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackupNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartServerCommand))]
    private ProcessState _state = ProcessState.Stopped;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BroadcastCommand))]
    private string _broadcastMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KickCommand))]
    [NotifyCanExecuteChangedFor(nameof(BanCommand))]
    private string _playerName = string.Empty;

    [ObservableProperty] private string _playerReason = string.Empty;

    /// <summary>
    /// Last-action result, surfaced to the UI so the operator gets feedback
    /// without having to switch tabs. Cleared on state transitions out of
    /// <see cref="ProcessState.Running"/> so a stale "Sent kick for X." is
    /// not still showing after the server has gone away.
    /// </summary>
    [ObservableProperty] private string? _statusMessage;

    /// <summary>
    /// Supplied by the View on DataContext bind. Returns true on Confirm,
    /// false on Cancel. Tuple args: (title, message, confirmButtonText).
    /// </summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Supplied by <see cref="MainWindowViewModel"/> on construction. Invoked
    /// after a clean Stop has landed during Restart, to bring the server back
    /// up under the GUI's supervision. Reuses ServerControl's Start path so
    /// the entrypoint and per-button state-machine stay in one place.
    /// </summary>
    public Func<Task>? StartAsyncCallback { get; set; }

    /// <summary>
    /// Supplied by <see cref="MainWindowViewModel"/>. Returns true when
    /// <see cref="StartAsyncCallback"/> is viable RIGHT NOW (i.e. a valid
    /// entrypoint is bound and the underlying StartCommand would execute).
    /// Used by Restart to fail fast BEFORE stopping the running server, so
    /// the operator does not lose their server because a folder selection
    /// went stale in another tab.
    /// </summary>
    public Func<bool>? CanStartCallback { get; set; }

    public AdminActionsViewModel(ServerProcessService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.StateChanged += OnStateChanged;
    }

    private bool IsServerRunning() => State == ProcessState.Running;

    [RelayCommand(CanExecute = nameof(CanBroadcast))]
    private async Task BroadcastAsync()
    {
        var msg = BroadcastMessage.Trim();
        await SendAsync($"/say {msg}", $"Broadcast: {msg}");
        BroadcastMessage = string.Empty;
    }
    private bool CanBroadcast() => IsServerRunning() && !string.IsNullOrWhiteSpace(BroadcastMessage);

    [RelayCommand(CanExecute = nameof(CanKick))]
    private async Task KickAsync()
    {
        var name = PlayerName;
        await SendAsync($"/kick {name}{QuotedReason()}", $"Sent kick for {name}.");
    }
    private bool CanKick() => IsServerRunning() && !string.IsNullOrWhiteSpace(PlayerName);

    [RelayCommand(CanExecute = nameof(CanBan))]
    private async Task BanAsync()
    {
        var name = PlayerName;
        if (ConfirmAsync is null)
        {
            StatusMessage = "Confirm UI not ready — try again in a moment.";
            return;
        }
        var ok = await ConfirmAsync(
            "Ban player",
            $"Ban player '{name}' from the server? Bans persist in LMPPlayerBans.txt and prevent reconnect by their UniqueIdentifier.",
            "Ban");
        if (!ok) return;

        // Re-check after the confirm await — the server may have exited while
        // the dialog was open. A failed send would still surface a clean
        // error, but a pre-check makes the operator feedback friendlier.
        if (!IsServerRunning())
        {
            StatusMessage = "Server is no longer running — ban not sent.";
            return;
        }
        await SendAsync($"/ban {name}{QuotedReason()}", $"Sent ban for {name}.");
    }
    private bool CanBan() => IsServerRunning() && !string.IsNullOrWhiteSpace(PlayerName);

    [RelayCommand(CanExecute = nameof(IsServerRunning))]
    private Task ListClientsAsync() => SendAsync("/listclients", "Sent /listclients — see Console tab.");

    [RelayCommand(CanExecute = nameof(IsServerRunning))]
    private Task ListLocksAsync() => SendAsync("/listlocks", "Sent /listlocks — see Console tab.");

    [RelayCommand(CanExecute = nameof(IsServerRunning))]
    private Task ConnectionStatsAsync() => SendAsync("/connectionstats", "Sent /connectionstats — see Console tab.");

    [RelayCommand(CanExecute = nameof(IsServerRunning))]
    private Task BackupNowAsync() => SendAsync("/backup now", "Sent /backup now — flush starting.");

    [RelayCommand(CanExecute = nameof(IsServerRunning))]
    private async Task RestartServerAsync()
    {
        if (ConfirmAsync is null)
        {
            StatusMessage = "Confirm UI not ready — try again in a moment.";
            return;
        }
        var ok = await ConfirmAsync(
            "Restart server",
            "Restart the running server? The GUI will stop the current process, wait for it to exit, then start a new one. Same supervised-process surface — no orphans.",
            "Restart");
        if (!ok) return;
        if (!IsServerRunning())
        {
            StatusMessage = "Server is no longer running — restart skipped.";
            return;
        }

        // Fail fast BEFORE we kill the running server. If the entrypoint
        // bound to ServerControl.StartCommand has gone stale (folder
        // re-validation, etc.), we won't be able to relaunch — and the
        // operator just lost their running server for no gain.
        if (CanStartCallback?.Invoke() == false)
        {
            StatusMessage = "Cannot restart: no valid entrypoint to relaunch with. Re-validate the server folder on the Folder & Setup tab.";
            return;
        }

        StatusMessage = "Stopping server for restart...";
        await _service.StopAsync();

        // StopAsync returns after the kill but OnProcessExited (a background
        // event handler) is what flips State to Stopped. Poll briefly.
        var deadline = DateTime.UtcNow + RestartStopDeadline;
        while (State != ProcessState.Stopped && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        if (State != ProcessState.Stopped)
        {
            StatusMessage = "Stop did not complete in time. Click Start in the Console tab manually.";
            return;
        }

        if (StartAsyncCallback is null)
        {
            StatusMessage = "Server stopped. Restart callback not wired — click Start in the Console tab.";
            return;
        }
        StatusMessage = "Restarting server...";
        await StartAsyncCallback();
    }

    private string QuotedReason()
    {
        // Server-side parser (CommandSystemHelperMethods.SplitCommand) uses
        // regex "[^"]+"|[^ "]+ — unquoted multi-word reasons truncate to the
        // first word. Quote-wrap on send so the operator's full reason
        // survives. Server strips the quotes via .Trim('"'). Trailing space
        // before the opening quote keeps the existing one-token-then-space
        // shape so empty-reason produces a clean "/kick name" without a
        // dangling pair of empty quotes.
        if (string.IsNullOrWhiteSpace(PlayerReason)) return string.Empty;
        var reason = PlayerReason.Replace("\"", string.Empty);
        return $" \"{reason}\"";
    }

    private async Task SendAsync(string command, string successMessage)
    {
        try
        {
            await _service.SendCommandAsync(command);
            StatusMessage = successMessage;
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Send failed: {ex.Message}";
        }
    }

    private void OnStateChanged(object? sender, ProcessState newState)
    {
        Dispatcher.UIThread.Post(() =>
        {
            State = newState;
            // Stale status from the last action (e.g. "Sent kick for Bob.")
            // is misleading once the server is no longer running. Clear it
            // on any transition AWAY from Running. Operator gets a fresh
            // status on the next action.
            if (newState != ProcessState.Running)
                StatusMessage = null;
        });
    }
}
