using Avalonia.Controls;
using Avalonia.Interactivity;
using LunaServerGui.ViewModels;

namespace LunaServerGui.Views;

public partial class FolderSetupView : UserControl
{
    public FolderSetupView()
    {
        InitializeComponent();
    }

    // The folder picker requires an IStorageProvider obtained from the
    // hosting TopLevel — the VM can't reach that without coupling to the
    // view tree. Pattern: the View resolves the TopLevel and hands the
    // IStorageProvider to the VM. Click handler stays minimal.
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FolderSetupViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        await vm.BrowseFolderAsync(topLevel.StorageProvider);
    }
}
