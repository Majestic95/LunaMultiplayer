using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using LunaServerGui.ViewModels;

namespace LunaServerGui.Views;

public partial class LaunchSettingsView : UserControl
{
    public LaunchSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Wire the View-supplied confirm-dialog routine into the VM, same shape
    // as AdminActionsView. Stays platform-agnostic: the VM never reaches
    // for a Window reference itself.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not LaunchSettingsViewModel vm) return;
        vm.ConfirmAsync = ConfirmAsync;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return false;
        return await ConfirmDialog.ShowAsync(window, title, message, confirmText);
    }
}
