using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LunaServerGui.Views;

/// <summary>
/// Reusable modal confirm dialog for destructive actions. The static
/// <see cref="ShowAsync"/> helper returns true on Confirm, false on Cancel
/// (including window-close via Escape, the OS title-bar close button, or
/// any other path that fires Closing without OnConfirm running).
/// </summary>
public partial class ConfirmDialog : Window
{
    private TaskCompletionSource<bool>? _tcs;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static async Task<bool> ShowAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "Confirm",
        string cancelText = "Cancel")
    {
        var dialog = new ConfirmDialog
        {
            Title = title,
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;

        // Any close path that didn't run OnConfirm counts as Cancel. This
        // covers Esc (IsCancel button + OS handling), the title-bar [X], and
        // Alt+F4. TrySetResult is idempotent against an earlier OnConfirm.
        dialog.Closing += (_, _) => dialog._tcs!.TrySetResult(false);

        await dialog.ShowDialog(owner);
        return await dialog._tcs!.Task;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _tcs?.TrySetResult(true);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _tcs?.TrySetResult(false);
        Close();
    }
}
