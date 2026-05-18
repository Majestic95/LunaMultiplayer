using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LunaServerGui.Models;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

public sealed partial class ServerControlViewModel : ViewModelBase
{
    private const int MaxLogLines = 5000;

    private readonly ServerProcessService _service;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private ServerEntrypoint? _entrypoint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendInputCommand))]
    private ProcessState _state = ProcessState.Stopped;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendInputCommand))]
    private string _commandInput = string.Empty;

    [ObservableProperty] private int? _lastExitCode;
    [ObservableProperty] private string? _lastError;

    /// <summary>
    /// Rolling buffer of stdout/stderr/GUI-injected lines. UI binds an
    /// ItemsControl or TextBox to this. All mutations marshal to
    /// the UI thread via <see cref="Dispatcher.UIThread"/>.
    /// </summary>
    public ObservableCollection<string> LogLines { get; } = new();

    public ServerControlViewModel(ServerProcessService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.StateChanged += OnStateChanged;
        _service.OutputLineReceived += OnOutputLine;
        _service.ErrorLineReceived += OnErrorLine;
        _service.Exited += OnExited;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Entrypoint is null) return;
        LastError = null;
        LastExitCode = null;
        AppendLog($"[GUI] Starting server: {Entrypoint.ExecutablePath} (working dir: {Entrypoint.WorkingDirectory})");
        var result = await _service.StartAsync(Entrypoint);
        if (!result.Success)
        {
            LastError = result.ErrorMessage;
            AppendLog($"[GUI] {result.ErrorMessage}");
        }
    }

    private bool CanStart() => Entrypoint is not null && State == ProcessState.Stopped;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        AppendLog("[GUI] Stopping server. Closing stdin first, then killing the process tree after a grace period. In-flight backups may not flush.");
        await _service.StopAsync();
    }

    private bool CanStop() => State == ProcessState.Running;

    [RelayCommand(CanExecute = nameof(CanSendInput))]
    private async Task SendInputAsync()
    {
        var cmd = CommandInput;
        CommandInput = string.Empty;
        AppendLog($"[GUI cmd>] {cmd}");
        try
        {
            await _service.SendCommandAsync(cmd);
        }
        catch (InvalidOperationException ex)
        {
            LastError = ex.Message;
            AppendLog($"[GUI] {ex.Message}");
        }
    }

    private bool CanSendInput() =>
        State == ProcessState.Running && !string.IsNullOrWhiteSpace(CommandInput);

    private void OnStateChanged(object? sender, ProcessState newState)
    {
        Dispatcher.UIThread.Post(() => State = newState);
    }

    private void OnOutputLine(object? sender, string line) => AppendLog(line);

    private void OnErrorLine(object? sender, string line) => AppendLog($"[stderr] {line}");

    private void OnExited(object? sender, int exitCode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LastExitCode = exitCode;
            AppendLog($"[GUI] Server exited with code {exitCode}.");
        });
    }

    private void AppendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        });
    }
}
