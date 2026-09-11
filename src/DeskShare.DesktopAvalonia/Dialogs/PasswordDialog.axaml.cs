using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
// Alias to avoid conflict with Avalonia.Controls.WindowIcon
using MsBoxIcon = MsBox.Avalonia.Enums.Icon;

namespace DeskShare.DesktopAvalonia.Dialogs;

/// <summary>
/// Password dialog for connecting to password-protected servers.
/// Avalonia equivalent of WPF's PasswordDialog.xaml.cs.
///
/// Key differences from WPF:
/// - Window base class is Avalonia.Controls.Window (not System.Windows.Window)
/// - DialogResult (bool?) replaced with Close(result) pattern
/// - PasswordBox.Password → TextBox.Text (Avalonia has no PasswordBox)
/// - MessageBox.Show() → MessageBoxManager.GetMessageBoxStandard().ShowAsync()
/// - ShowDialog() returns Task<T> in Avalonia (async by default)
/// </summary>
public partial class PasswordDialog : Window
{
    /// <summary>
    /// Gets the entered password.
    /// In WPF, this was PasswordBox.Password; in Avalonia, it's TextBox.Text.
    /// </summary>
    public string Password => PasswordBox.Text ?? string.Empty;

    public PasswordDialog(string connectionName)
    {
        InitializeComponent();
        ConnectionNameText.Text = $"Connecting to: {connectionName}";

        // Focus the password field when the dialog opens
        Opened += (s, e) => PasswordBox.Focus();
    }

    /// <summary>
    /// Default parameterless constructor required by Avalonia XAML designer.
    /// </summary>
    public PasswordDialog()
    {
        InitializeComponent();
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Text))
        {
            // Avalonia replacement for WPF's MessageBox.Show()
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Validation Error",
                "Please enter a password",
                ButtonEnum.Ok,
                MsBoxIcon.Warning);
            await msgBox.ShowWindowDialogAsync(this);
            return;
        }

        // Close dialog with true result (equivalent to WPF's DialogResult = true)
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        // Close dialog with false result (equivalent to WPF's DialogResult = false)
        Close(false);
    }

    private void PasswordBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Handle Enter key to submit the dialog
        if (e.Key == Key.Enter)
        {
            Connect_Click(sender, e);
        }
    }
}
