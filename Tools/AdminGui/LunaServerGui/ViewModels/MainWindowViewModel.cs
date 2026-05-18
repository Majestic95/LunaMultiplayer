using System;
using CommunityToolkit.Mvvm.ComponentModel;
using LunaServerGui.Models;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ServerFolderService _folderService = new();
    private readonly ServerProcessService _processService = new();
    private bool _disposed;

    public FolderSetupViewModel FolderSetup { get; }
    public ServerControlViewModel ServerControl { get; }

    /// <summary>
    /// When non-null, the most recent attempt to close the window was blocked.
    /// Bound to a TextBlock on MainWindow so the operator sees why their close
    /// did not take effect.
    /// </summary>
    [ObservableProperty] private string? _closeBlockedMessage;

    public MainWindowViewModel()
    {
        FolderSetup = new FolderSetupViewModel(_folderService, OnValidationChanged);
        ServerControl = new ServerControlViewModel(_processService);
    }

    /// <summary>
    /// Called by MainWindow.OnClosing. Returns true if the window may close
    /// (server is stopped); false otherwise. The blocked-message is tailored
    /// to the current state so the operator knows what to do — telling them
    /// to "Stop the server" while the server is still Starting would be a
    /// dead-end (the Stop button is disabled until Running).
    /// </summary>
    public bool TryAllowClose()
    {
        var state = _processService.State;
        if (state == ProcessState.Stopped)
        {
            CloseBlockedMessage = null;
            return true;
        }
        CloseBlockedMessage = state switch
        {
            ProcessState.Starting =>
                "Server is starting. Wait for it to finish starting, then Stop it from the Console tab before closing.",
            ProcessState.Stopping =>
                "Server is stopping. Wait for it to finish.",
            ProcessState.Running =>
                "Server is running. Stop the server from the Console tab before closing this window. (Closing now would kill the server process and may lose in-flight backups.)",
            _ =>
                $"Server is in an unexpected state ({state}). Stop the server before closing.",
        };
        return false;
    }

    private void OnValidationChanged(ServerFolderValidation validation)
    {
        ServerControl.Entrypoint = validation.CanLaunch ? validation.Entrypoint : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processService.Dispose();
    }
}
