using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using LunaServerGui.ViewModels;

namespace LunaServerGui.Views;

public partial class AdminActionsView : UserControl
{
    public AdminActionsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Wire the View-supplied confirm-dialog routine into the VM. The VM
    // can't reach a Window reference on its own (clean separation), so the
    // View injects the dialog hosting routine on DataContext bind.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not AdminActionsViewModel vm) return;
        vm.ConfirmAsync = ConfirmAsync;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return false;
        return await ConfirmDialog.ShowAsync(window, title, message, confirmText);
    }
}
