using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

/// <summary>
/// Editable display of the six priority settings files in
/// &lt;ServerFolder&gt;/Config. Slice 1D-2 surface: load + edit + save with
/// timestamped backup + atomic write + confirm-dialog change summary;
/// slice 1D-3 adds validation; slice 1D-4 adds difficulty-flip warnings
/// and the PerAgencyCareer pre-save gate (the two PerAgencyCareer fields
/// are locked in this slice — see SettingsCatalogService.LockedFields).
///
/// The View supplies <see cref="ConfirmAsync"/> on DataContext bind so the
/// VM can stay platform-agnostic. Save orchestration runs here so the
/// per-group SettingsGroupViewModel doesn't need to know about Window
/// references or post-save reload mechanics.
/// </summary>
public sealed partial class LaunchSettingsViewModel : ViewModelBase
{
    private readonly SettingsCatalogService _catalog;

    [ObservableProperty] private string? _serverFolderPath;
    [ObservableProperty] private string? _configDirectoryPath;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasGroups;
    [ObservableProperty] private bool _isReloading;

    public ObservableCollection<SettingsGroupViewModel> Groups { get; } = new();

    /// <summary>
    /// Supplied by the View on DataContext bind. Returns true on Confirm,
    /// false on Cancel. Tuple args: (title, message, confirmButtonText).
    /// Mirrors the AdminActionsViewModel.ConfirmAsync pattern from slice 1C.
    /// </summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

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
        _ = ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    public async Task ReloadAsync()
    {
        if (IsReloading) return;

        // Discard-dirty gate: an operator who clicks Reload (or re-picks the
        // folder from Folder & Setup) while another tab has unsaved edits
        // would silently lose those edits without this confirm. Skipping
        // the gate if ConfirmAsync isn't wired yet (the very first reload
        // during DataContextChanged race) is OK because Groups is still
        // empty at that point.
        var dirtyTotal = Groups.Sum(g => g.DirtyCount);
        if (dirtyTotal > 0 && ConfirmAsync is not null)
        {
            var ok = await ConfirmAsync(
                "Discard unsaved changes?",
                $"Reloading will discard {dirtyTotal} unsaved change(s) across {Groups.Count(g => g.HasDirty)} group(s). Save first?",
                "Discard");
            if (!ok)
            {
                StatusMessage = "Reload cancelled — your unsaved changes are preserved.";
                return;
            }
        }

        IsReloading = true;
        try
        {
            DetachAndClear();

            if (string.IsNullOrWhiteSpace(ServerFolderPath))
            {
                StatusMessage = "Select a server folder on the Folder & Setup tab to load settings.";
                HasGroups = false;
                return;
            }

            var configDir = ConfigDirectoryPath!;
            if (!Directory.Exists(configDir))
            {
                StatusMessage = $"Config/ folder does not exist yet at {configDir}. The server creates it on first start; come back here once the server has run once.";
                HasGroups = false;
                return;
            }

            IReadOnlyList<SettingsGroupViewModel> loaded;
            try
            {
                // Reflection + XML read are sync work — push off the UI thread.
                // Six small files: typical end-to-end is a few tens of ms, but
                // the right shape ahead of slice 1D-3's validation (which adds
                // more per-field processing) is async-by-default.
                loaded = await Task.Run(() => _catalog.LoadGroupViewModels(configDir));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to enumerate settings: {ex.GetBaseException().Message}";
                HasGroups = false;
                return;
            }

            foreach (var group in loaded)
            {
                group.SaveCallback = SaveGroupAsync;
                Groups.Add(group);
            }

            HasGroups = Groups.Count > 0;
            StatusMessage = $"Loaded {Groups.Count} settings groups from {configDir}.";
        }
        finally
        {
            IsReloading = false;
        }
    }

    private bool CanReload() => !string.IsNullOrWhiteSpace(ServerFolderPath) && !IsReloading;

    partial void OnServerFolderPathChanged(string? value) => ReloadCommand.NotifyCanExecuteChanged();
    partial void OnIsReloadingChanged(bool value) => ReloadCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Detach PropertyChanged subscriptions on the existing groups before
    /// the ObservableCollection clear so that listeners on stale field VMs
    /// don't leak across reloads.
    /// </summary>
    private void DetachAndClear()
    {
        foreach (var g in Groups)
            g.Detach();
        Groups.Clear();
    }

    /// <summary>
    /// Save orchestration entrypoint, set as <see cref="SettingsGroupViewModel.SaveCallback"/>
    /// when each group is added. Pulls the change summary out of the group,
    /// pops the confirm dialog via <see cref="ConfirmAsync"/>, writes via
    /// the group's TryWrite (which delegates to <see cref="SettingsXmlService"/>),
    /// then triggers a full reload so the edited group's editors reset to
    /// their new originals.
    /// </summary>
    public async Task SaveGroupAsync(SettingsGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.CanSave)
        {
            StatusMessage = $"{group.DisplayName}: nothing to save or unresolved parse errors prevent save.";
            return;
        }
        if (ConfirmAsync is null)
        {
            StatusMessage = "Confirm UI not ready — try again in a moment.";
            return;
        }

        var changes = group.CollectChangeSummaries();
        var fileName = Path.GetFileName(group.FilePath);
        var backupDir = Path.Combine(Path.GetDirectoryName(group.FilePath) ?? string.Empty,
            SettingsXmlService.BackupSubdirectory);

        var body =
            $"Save {changes.Count} change(s) to {fileName}?\n\n" +
            string.Join("\n", changes.Select(c => "  • " + c)) +
            $"\n\nA timestamped backup of the current file will be copied to {backupDir} before writing.\n\n" +
            "Note: any XML elements not recognised by the GUI (e.g. settings added by a newer server " +
            "version) will be removed on save — same as the server's own load-then-save cycle. " +
            "The backup preserves the file as-is so anything dropped can be restored from there.";

        var ok = await ConfirmAsync($"Save changes — {group.DisplayName}", body, "Save");
        if (!ok)
        {
            StatusMessage = $"{group.DisplayName}: save cancelled.";
            return;
        }

        SettingsXmlService.WriteResult writeResult;
        try
        {
            // Write is sync (file I/O + serialization). Push off the UI
            // thread because the XML writer's XmlDocument round-trip is the
            // heaviest path in this VM and we'd otherwise freeze the dialog
            // dismissal animation on slower disks.
            writeResult = await Task.Run(group.Write);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{group.DisplayName}: save FAILED. {ex.GetBaseException().Message}";
            return;
        }

        // We're on the UI thread here — async/await captures the dispatcher
        // SynchronizationContext, so the continuation after Task.Run resumed
        // on the dispatcher already. No explicit Dispatcher.UIThread.Invoke
        // needed.
        StatusMessage = writeResult switch
        {
            { Skipped: true } =>
                $"{group.DisplayName}: no changes written (serialized content matched the existing file).",
            { BackupPath: null } =>
                $"{group.DisplayName}: saved {changes.Count} change(s). (No prior file to back up.)",
            { BackupPath: var p } =>
                $"{group.DisplayName}: saved {changes.Count} change(s). Backup at {p}.",
        };

        // Full reload so the saved group's editor state resets to its new
        // originals and dirty flags clear. Re-reads all six files (a few
        // ms); simpler than per-group surgical reload and any
        // external-edit-since-load is also picked up.
        await ReloadAsync();
    }
}
