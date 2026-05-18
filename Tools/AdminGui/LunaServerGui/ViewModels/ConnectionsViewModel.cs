using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LunaServerGui.Models;
using LunaServerGui.Services;

namespace LunaServerGui.ViewModels;

/// <summary>
/// Drives the Connections tab. Subscribes to
/// <see cref="ServerProcessService.OutputLineReceived"/> (a multi-subscriber
/// event — the Console tab keeps its existing subscription unchanged), runs
/// each line through <see cref="ServerLogParser"/>, and prepends any matched
/// <see cref="ConnectionEvent"/> to <see cref="Events"/>.
///
/// Event surface intentionally narrow: handshake-accept + handshake-reject
/// only. Mod-mismatch rejections do NOT appear (validation is client-side;
/// server has no log line). The View surfaces this caveat in a banner.
/// </summary>
public sealed partial class ConnectionsViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Maximum events retained. New events are inserted at the top; once
    /// the cap is hit, the oldest at the bottom is dropped. Holds enough
    /// for a busy server's last hour-or-so without unbounded memory
    /// growth.
    /// </summary>
    public const int MaxEvents = 500;

    private readonly ServerProcessService _service;
    private bool _disposed;

    public ObservableCollection<ConnectionEvent> Events { get; } = new();

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _acceptedCount;
    [ObservableProperty] private int _rejectedCount;
    [ObservableProperty] private bool _isEmpty = true;

    public ConnectionsViewModel(ServerProcessService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.OutputLineReceived += OnOutputLineReceived;
    }

    private void OnOutputLineReceived(object? sender, string line)
    {
        if (!ServerLogParser.TryParse(line, DateTime.UtcNow, out var evt) || evt is null)
            return;

        // OutputLineReceived fires on a background thread per the service's
        // documented contract. ObservableCollection mutation + bound
        // [ObservableProperty] setters must run on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            Events.Insert(0, evt);
            while (Events.Count > MaxEvents)
                Events.RemoveAt(Events.Count - 1);
            RecomputeCounts();
        });
    }

    /// <summary>
    /// Recompute the chip counts from the current Events list, not by
    /// incrementing. Once the MaxEvents cap kicks in and old entries drop
    /// off the end, plain increment would diverge from the displayed list
    /// (the operator would see e.g. Total=500 but Accepted+Rejected sum
    /// to 600 — review SHOULD FIX #2).
    /// </summary>
    private void RecomputeCounts()
    {
        var accepted = 0;
        var rejected = 0;
        foreach (var e in Events)
        {
            if (e.Outcome == ConnectionOutcome.Accepted) accepted++;
            else if (e.Outcome == ConnectionOutcome.Rejected) rejected++;
        }
        TotalCount = Events.Count;
        AcceptedCount = accepted;
        RejectedCount = rejected;
        IsEmpty = Events.Count == 0;
    }

    [RelayCommand]
    private void Clear()
    {
        Events.Clear();
        RecomputeCounts();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.OutputLineReceived -= OnOutputLineReceived;
    }
}
