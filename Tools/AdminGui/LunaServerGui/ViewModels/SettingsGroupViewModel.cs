using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LunaServerGui.Models;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

/// <summary>
/// One settings file's editable form. Wraps the loaded POCO instance + a
/// list of per-field editor VMs. Status / Detail / FilePath are inherited
/// from the underlying <see cref="SettingsGroup"/> model so the view's
/// header chrome from slice 1D-1 keeps working unchanged.
///
/// Writing is delegated to <see cref="SettingsXmlService"/>. The
/// confirm-dialog gate and post-save reload are orchestrated by the parent
/// <see cref="LaunchSettingsViewModel"/> — this VM just provides
/// <see cref="TryWrite"/> and the change summaries.
/// </summary>
public sealed partial class SettingsGroupViewModel : ObservableObject
{
    private readonly SettingsXmlService _xml;
    private readonly object _instance;

    public string DisplayName { get; }
    public string FilePath { get; }
    public SettingsGroupStatus Status { get; }
    public string? Detail { get; }
    public bool ValuesAreFromFile => Status == SettingsGroupStatus.Loaded;
    public double FieldsOpacity => ValuesAreFromFile ? 1.0 : 0.55;

    public ObservableCollection<SettingsFieldViewModel> Fields { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    private int _dirtyCount;

    [ObservableProperty] private bool _hasDirty;
    [ObservableProperty] private bool _hasParseError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _canSave;

    /// <summary>
    /// Invoked by <see cref="SaveCommand"/>. Set by
    /// <see cref="LaunchSettingsViewModel"/> at construction time so the
    /// parent can orchestrate confirm-dialog + post-save reload without
    /// the Group VM needing to know about either.
    /// </summary>
    public Func<SettingsGroupViewModel, Task>? SaveCallback { get; set; }

    public SettingsGroupViewModel(
        SettingsXmlService xml,
        string displayName,
        string filePath,
        SettingsGroupStatus status,
        string? detail,
        object instance,
        IReadOnlyList<SettingsFieldViewModel> fields)
    {
        _xml = xml ?? throw new ArgumentNullException(nameof(xml));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        DisplayName = displayName;
        FilePath = filePath;
        Status = status;
        Detail = detail;
        Fields = new ObservableCollection<SettingsFieldViewModel>(fields);

        foreach (var f in Fields)
            f.PropertyChanged += OnFieldPropertyChanged;
        RecomputeAggregateState();
    }

    private void OnFieldPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only the dirty/parse-error flags affect the group-level aggregate.
        // Don't recompute on every TextValue keystroke; the field VM's
        // partial OnTextValueChanged handler is what flips IsDirty +
        // ParseError, which fires this once per relevant change.
        if (e.PropertyName is nameof(SettingsFieldViewModel.IsDirty)
            or nameof(SettingsFieldViewModel.ParseError))
        {
            RecomputeAggregateState();
        }
    }

    private void RecomputeAggregateState()
    {
        var dirty = 0;
        var hasError = false;
        foreach (var f in Fields)
        {
            if (f.IsDirty) dirty++;
            if (!string.IsNullOrEmpty(f.ParseError)) hasError = true;
        }
        DirtyCount = dirty;
        HasDirty = dirty > 0;
        HasParseError = hasError;
        // Only allow Save when the underlying file was actually loaded —
        // MissingFile/ParseError groups show POCO defaults and writing them
        // back would silently create a brand-new file with placeholder
        // values, which the operator might not want.
        CanSave = ValuesAreFromFile && HasDirty && !HasParseError;
    }

    /// <summary>
    /// Lines for the confirm-dialog body: "FieldName: oldValue → newValue"
    /// for every dirty field. Ordered to match the form's field order so
    /// the operator can cross-reference visually.
    /// </summary>
    public IReadOnlyList<string> CollectChangeSummaries()
        => Fields.Where(f => f.IsDirty).Select(f => f.ChangeSummary()).ToList();

    /// <summary>
    /// Field-name + error-message pairs for any parse failure. The Save
    /// flow refuses to proceed when this is non-empty.
    /// </summary>
    public IReadOnlyList<string> CollectParseErrors()
        => Fields
            .Where(f => !string.IsNullOrEmpty(f.ParseError))
            .Select(f => $"{f.Name}: {f.ParseError}")
            .ToList();

    /// <summary>
    /// Commit every dirty field back to the POCO, then write the POCO to
    /// disk via <see cref="SettingsXmlService.Write"/>. Throws on parse
    /// failure or I/O error — callers gate on <see cref="CanSave"/> first.
    /// (Method does NOT follow Try* convention because it throws; named
    /// after the underlying SettingsXmlService.Write.)
    /// </summary>
    public SettingsXmlService.WriteResult Write()
    {
        foreach (var f in Fields)
        {
            if (!f.TryCommitToInstance())
                throw new InvalidOperationException(
                    $"Cannot save: field '{f.Name}' has unresolved parse error: {f.ParseError}");
        }
        return _xml.Write(_instance, FilePath);
    }

    [RelayCommand(CanExecute = nameof(CanInvokeSave))]
    private async Task SaveAsync()
    {
        if (SaveCallback is null) return;
        await SaveCallback(this);
    }
    private bool CanInvokeSave() => CanSave && SaveCallback is not null;

    [RelayCommand(CanExecute = nameof(CanInvokeRevert))]
    private void Revert() => RevertAll();
    private bool CanInvokeRevert() => HasDirty;

    /// <summary>
    /// Discard all in-progress edits and restore each field's editor to its
    /// original value. The view binds the Revert button to this. Triggers
    /// per-field IsDirty/ParseError recompute via the PropertyChanged
    /// handler.
    /// </summary>
    public void RevertAll()
    {
        foreach (var f in Fields)
        {
            // Setting the editor surfaces back to their captured originals
            // is enough: the partial OnXxxChanged handlers in
            // SettingsFieldViewModel re-evaluate IsDirty and clear
            // ParseError.
            if (f.Editor == FieldEditorKind.Bool && bool.TryParse(f.DisplayValue, out var b))
                f.BoolValue = b;
            else if (f.Editor == FieldEditorKind.Enum)
                f.EnumValue = f.DisplayValue;
            else if (f.Editor == FieldEditorKind.Text)
                f.TextValue = f.DisplayValue == "(unset)" ? string.Empty : f.DisplayValue;
        }
    }

    /// <summary>
    /// Detach PropertyChanged subscriptions before this group is replaced
    /// by a reload — otherwise the parent's ObservableCollection.Clear
    /// leaves orphaned handlers wired to stale field VMs.
    /// </summary>
    public void Detach()
    {
        foreach (var f in Fields)
            f.PropertyChanged -= OnFieldPropertyChanged;
    }
}
