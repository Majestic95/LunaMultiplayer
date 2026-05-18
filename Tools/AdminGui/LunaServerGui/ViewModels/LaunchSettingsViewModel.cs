using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LunaServerGui.Models;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

/// <summary>
/// Read-only display of the six priority settings files in
/// &lt;ServerFolder&gt;/Config. Slice 1D-1 surface: load + display only;
/// slice 1D-2 adds the save path; slice 1D-3 adds validation; slice 1D-4
/// adds difficulty-flip warnings and the PerAgencyCareer pre-save gate.
///
/// The VM is driven by MainWindowViewModel calling <see cref="SetServerFolder"/>
/// when the folder validation outcome changes. A null/whitespace folder
/// produces an empty Groups collection + a NoFolderMessage; an existing
/// folder without a Config/ subdirectory shows a friendlier message than
/// six "MissingFile" rows.
/// </summary>
public sealed partial class LaunchSettingsViewModel : ViewModelBase
{
    private readonly SettingsCatalogService _catalog;

    [ObservableProperty] private string? _serverFolderPath;
    [ObservableProperty] private string? _configDirectoryPath;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasGroups;

    public ObservableCollection<SettingsGroup> Groups { get; } = new();

    public LaunchSettingsViewModel(SettingsCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        StatusMessage = "Select a server folder on the Folder & Setup tab to load settings.";
    }

    /// <summary>
    /// Called by MainWindowViewModel on folder-validation change. Passing
    /// null clears the form (operator deselected the folder or picked an
    /// invalid one). Triggers an immediate reload.
    /// </summary>
    public void SetServerFolder(string? folderPath)
    {
        ServerFolderPath = folderPath;
        ConfigDirectoryPath = string.IsNullOrWhiteSpace(folderPath)
            ? null
            : Path.Combine(folderPath, "Config");
        Reload();
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    public void Reload()
    {
        Groups.Clear();

        if (string.IsNullOrWhiteSpace(ServerFolderPath))
        {
            StatusMessage = "Select a server folder on the Folder & Setup tab to load settings.";
            HasGroups = false;
            return;
        }

        var configDir = ConfigDirectoryPath!;
        if (!Directory.Exists(configDir))
        {
            // The Folder & Setup tab already reports Config/ as not-yet-created
            // for new installs. Mirror that wording so the operator isn't
            // confused by two different messages for the same condition.
            StatusMessage = $"Config/ folder does not exist yet at {configDir}. The server creates it on first start; come back here once the server has run once.";
            HasGroups = false;
            return;
        }

        IReadOnlyList<SettingsGroup> loaded;
        try
        {
            loaded = _catalog.LoadAll(configDir);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to enumerate settings: {ex.GetBaseException().Message}";
            HasGroups = false;
            return;
        }

        foreach (var group in loaded)
            Groups.Add(group);

        HasGroups = Groups.Count > 0;
        StatusMessage = $"Loaded {Groups.Count} settings groups from {configDir}.";
    }

    private bool CanReload() => !string.IsNullOrWhiteSpace(ServerFolderPath);

    partial void OnServerFolderPathChanged(string? value) => ReloadCommand.NotifyCanExecuteChanged();
}
