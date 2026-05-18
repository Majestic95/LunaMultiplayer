using Avalonia.Controls;
using LunaServerGui.ViewModels;

namespace LunaServerGui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Prevent window close while the child server is running — closing would
    // kill the server process (no graceful /quit stdin command exists; see
    // ServerProcessService XML doc). Operator must Stop the server first;
    // the CloseBlockedMessage shown in the status bar explains why.
    //
    // Order: base.OnClosing first so any other Closing subscribers see live
    // VM state, then Dispose only on the close-allowed path.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.TryAllowClose())
        {
            e.Cancel = true;
            return;
        }
        vm.Dispose();
    }
}
