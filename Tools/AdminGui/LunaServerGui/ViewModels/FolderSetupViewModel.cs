using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using LunaServerGui.Models;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

public sealed partial class FolderSetupViewModel : ViewModelBase
{
    private readonly ServerFolderService _service;
    private readonly Action<ServerFolderValidation> _onValidation;

    [ObservableProperty] private string? _selectedFolderPath;
    [ObservableProperty] private ServerFolderValidation? _validation;

    /// <summary>
    /// True while the folder picker is open. UI binds the Browse button's
    /// IsEnabled to <c>!IsBrowsing</c> so double-clicks can't stack two
    /// pickers (platform behaviour for concurrent pickers is undefined).
    /// </summary>
    [ObservableProperty] private bool _isBrowsing;

    public FolderSetupViewModel(ServerFolderService service, Action<ServerFolderValidation> onValidation)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _onValidation = onValidation ?? throw new ArgumentNullException(nameof(onValidation));
    }

    /// <summary>
    /// Opens the OS folder picker and validates the selection. Called from
    /// the View's Browse button click handler — the View supplies the
    /// IStorageProvider obtained from its TopLevel.
    /// </summary>
    public async Task BrowseFolderAsync(IStorageProvider storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (IsBrowsing) return;
        IsBrowsing = true;
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select your LMPServer folder",
                AllowMultiple = false,
            });

            if (folders.Count == 0) return;

            var picked = folders[0].Path.LocalPath;
            ValidatePath(picked);
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    /// <summary>
    /// Validate an explicit path (used by Browse and could be reused by a
    /// future "paste path" / "recent folders" surface). Always updates
    /// <see cref="SelectedFolderPath"/> and <see cref="Validation"/>, and
    /// notifies the parent VM regardless of outcome — operators need to see
    /// "this folder is invalid" feedback, not silence.
    /// </summary>
    public void ValidatePath(string? path)
    {
        SelectedFolderPath = path;
        var result = _service.Validate(path);
        Validation = result;
        _onValidation(result);
    }
}
