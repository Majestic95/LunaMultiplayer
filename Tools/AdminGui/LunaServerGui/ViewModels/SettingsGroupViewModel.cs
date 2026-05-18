using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LunaServerGui.Models;
using LunaServerGui.Services;
using LunaServerGui.SettingsCatalog;

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
    private readonly CrossFieldValidator? _crossFieldValidator;

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
    [ObservableProperty] private bool _hasValidationError;

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
        IReadOnlyList<SettingsFieldViewModel> fields,
        CrossFieldValidator? crossFieldValidator = null)
    {
        _xml = xml ?? throw new ArgumentNullException(nameof(xml));
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _crossFieldValidator = crossFieldValidator;
        DisplayName = displayName;
        FilePath = filePath;
        Status = status;
        Detail = detail;
        Fields = new ObservableCollection<SettingsFieldViewModel>(fields);

        foreach (var f in Fields)
        {
            f.PropertyChanged += OnFieldPropertyChanged;
            // Only evaluate rules for files that actually loaded from disk.
            // MissingFile/ParseError groups display POCO defaults — running
            // RequiredRule against them would falsely red-flag ServerName=""
            // on a file the GUI just told the operator doesn't exist yet
            // (review finding #4).
            if (ValuesAreFromFile) f.EvaluateRulesNow();
        }
        if (ValuesAreFromFile) RecomputeCrossFieldErrors();
        RecomputeAggregateState();
    }

    private void OnFieldPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only the dirty/error flags affect the group-level aggregate.
        // Don't recompute on every TextValue keystroke; the field VM's
        // partial OnTextValueChanged handler flips IsDirty + ParseError +
        // ValidationError together, which fires this once per relevant
        // change. We also need to re-run cross-field validation on any
        // dirty change because cross-field rules read other fields' commits.
        switch (e.PropertyName)
        {
            case nameof(SettingsFieldViewModel.IsDirty):
                RecomputeCrossFieldErrors();
                RecomputeAggregateState();
                break;
            case nameof(SettingsFieldViewModel.ParseError):
            case nameof(SettingsFieldViewModel.ValidationError):
            case nameof(SettingsFieldViewModel.CrossFieldError):
                RecomputeAggregateState();
                break;
        }
    }

    /// <summary>
    /// Build a shadow instance of the Definition POCO populated with each
    /// field's effective edit (operator's in-progress value when dirty +
    /// parses cleanly; on-disk original otherwise), then run the cross-
    /// field validator against the shadow. The real wrapped instance is
    /// NEVER mutated by this path — eliminating the snapshot/restore
    /// fragility flagged in the slice 1D-3 review (Cross-field validator
    /// could leave torn state if the validator threw before finally).
    /// </summary>
    private void RecomputeCrossFieldErrors()
    {
        foreach (var f in Fields) f.CrossFieldError = null;
        if (_crossFieldValidator is null) return;

        var shadow = Activator.CreateInstance(_instance.GetType());
        if (shadow is null) return;

        foreach (var f in Fields)
        {
            // Fields with a parse error fall back to their on-disk
            // original — the cross-field rule sees the value that's
            // CURRENTLY in the file, not a half-typed text.
            var committed = f.IsDirty && string.IsNullOrEmpty(f.ParseError) && f.WriteToInstance(shadow);
            if (!committed) f.WriteOriginalToInstance(shadow);
        }

        var errors = _crossFieldValidator(shadow);
        foreach (var (fieldName, msg) in errors)
        {
            var fieldVm = Fields.FirstOrDefault(f => f.Name == fieldName);
            if (fieldVm is not null) fieldVm.CrossFieldError = msg;
        }
    }

    private void RecomputeAggregateState()
    {
        var dirty = 0;
        var hasParse = false;
        var hasValidation = false;
        foreach (var f in Fields)
        {
            if (f.IsDirty) dirty++;
            if (!string.IsNullOrEmpty(f.ParseError)) hasParse = true;
            // Per-field ValidationError on a NON-dirty field is shown red
            // (so the operator knows about it) but does NOT block Save —
            // the operator inherited that problem from the loaded file and
            // shouldn't be forced to fix it before saving unrelated edits.
            // (Review finding #3: constructor-time validation was blocking
            // save on a pre-broken file even when the operator was editing
            // an entirely different field.) CrossFieldError IS counted
            // regardless: it fires precisely because operator edits caused
            // an inconsistency, so blocking save is the correct response.
            if (f.IsDirty && !string.IsNullOrEmpty(f.ValidationError)) hasValidation = true;
            if (!string.IsNullOrEmpty(f.CrossFieldError)) hasValidation = true;
        }
        DirtyCount = dirty;
        HasDirty = dirty > 0;
        HasParseError = hasParse;
        HasValidationError = hasValidation;
        CanSave = ValuesAreFromFile && HasDirty && !HasParseError && !HasValidationError;
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
    /// Discard all in-progress edits and restore each field's editor to
    /// its captured original value. Delegates to
    /// <see cref="SettingsFieldViewModel.RevertToOriginal"/> which uses
    /// the typed original (review finding #1: round-tripping DisplayValue
    /// for bools silently failed when the original was null/"(unset)").
    /// The partial OnXxxChanged handlers fire and re-evaluate
    /// IsDirty/ParseError/ValidationError.
    /// </summary>
    public void RevertAll()
    {
        foreach (var f in Fields)
            f.RevertToOriginal();
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
