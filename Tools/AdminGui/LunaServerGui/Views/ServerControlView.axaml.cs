using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using LunaServerGui.ViewModels;

namespace LunaServerGui.Views;

public partial class ServerControlView : UserControl
{
    private INotifyCollectionChanged? _subscribedLog;

    public ServerControlView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedLog is not null)
            _subscribedLog.CollectionChanged -= OnLogChanged;

        if (DataContext is ServerControlViewModel vm)
        {
            _subscribedLog = vm.LogLines;
            _subscribedLog.CollectionChanged += OnLogChanged;
        }
        else
        {
            _subscribedLog = null;
        }
    }

    // Auto-scroll the log pane to the bottom on every new line. Cheap and
    // matches the operator's expectation of "log tail" behaviour.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => LogScrollViewer.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    // Enter sends the command (in addition to the Send button's IsDefault).
    private void OnCommandInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not ServerControlViewModel vm) return;
        if (vm.SendInputCommand.CanExecute(null))
        {
            vm.SendInputCommand.Execute(null);
            e.Handled = true;
        }
    }
}
