using System;
using CommunityToolkit.Mvvm.ComponentModel;
using LunaServerGui.Models;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ServerFolderService _folderService = new();
    private readonly ServerProcessService _processService = new();
    private readonly SettingsXmlService _settingsXml = new();
    private readonly SettingsCatalogService _settingsCatalog;
    private bool _disposed;

    public FolderSetupViewModel FolderSetup { get; }
    public ServerControlViewModel ServerControl { get; }
    public LaunchSettingsViewModel LaunchSettings { get; }
    public AdminActionsViewModel AdminActions { get; }

    /// <summary>
    /// When non-null, the most recent attempt to close the window was blocked.
    /// Bound to a TextBlock on MainWindow so the operator sees why their close
    /// did not take effect.
    /// </summary>
    [ObservableProperty] private string? _closeBlockedMessage;

    public MainWindowViewModel()
    {
        _settingsCatalog = new SettingsCatalogService(_settingsXml);
        FolderSetup = new FolderSetupViewModel(_folderService, OnValidationChanged);
        ServerControl = new ServerControlViewModel(_processService);
        LaunchSettings = new LaunchSettingsViewModel(_settingsCatalog);
        AdminActions = new AdminActionsViewModel(_processService)
        {
            // AdminActions.Restart needs to bring the server back up after
            // its Stop completes. Reuse ServerControl's Start path so the
            // entrypoint resolution and CanExecute gating stay in one place.
            StartAsyncCallback = async () =>
            {
                if (ServerControl.StartCommand.CanExecute(null))
                    await ServerControl.StartCommand.ExecuteAsync(null);
            },
            CanStartCallback = () => ServerControl.Entrypoint is not null,
        };
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
        // LaunchSettings loads files under <folder>/Config — forward only if
        // the folder itself exists, so an invalid pick (bad path, file
        // instead of folder) shows the "select a folder" placeholder rather
        // than a misleading "Config not yet created" message. Missing
        // entrypoint is still OK: admins inspecting an unpacked release zip
        // with no Server.exe should still be able to read settings.
        var folderForSettings =
            !string.IsNullOrWhiteSpace(validation.Path)
            && System.IO.Directory.Exists(validation.Path)
                ? validation.Path
                : null;
        LaunchSettings.SetServerFolder(folderForSettings);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processService.Dispose();
    }
}
