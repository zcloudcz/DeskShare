using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeskShare.Core.Auth;

namespace DeskShare.DesktopAvalonia.Dialogs;

/// <summary>
/// Password + confirmation for unattended access. Closes with the password, or null when cancelled.
/// The rules live in <see cref="E2EBinding.ValidatePermanentPassword"/> so the viewers and this dialog agree on them.
/// </summary>
public partial class SetPasswordDialog : Window
{
    public SetPasswordDialog()
    {
        InitializeComponent();
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text ?? "";
        var error = E2EBinding.ValidatePermanentPassword(password)
            ?? (password != ConfirmBox.Text ? "The two passwords are not the same." : null);
        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }
        Close(password);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null as string);

    private void ConfirmBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }
}
