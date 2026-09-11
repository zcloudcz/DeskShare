using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskShare.DesktopAvalonia.Dialogs;

/// <summary>
/// Dialog for entering a passkey when connecting to a saved server.
/// Replaces Microsoft.VisualBasic.Interaction.InputBox which is Windows-only.
///
/// Usage:
///   var dialog = new PasskeyDialog();
///   var result = await dialog.ShowDialog&lt;string?&gt;(ownerWindow);
///   if (result != null) { /* user entered passkey */ }
/// </summary>
public partial class PasskeyDialog : Window
{
    /// <summary>
    /// Gets the entered passkey string.
    /// </summary>
    public string Passkey => PasskeyTextBox.Text ?? string.Empty;

    public PasskeyDialog()
    {
        InitializeComponent();

        // Focus the passkey input when dialog opens
        Opened += (s, e) => PasskeyTextBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PasskeyTextBox.Text))
        {
            return; // Don't close if empty
        }

        // Close dialog and return the passkey string
        Close(PasskeyTextBox.Text.Trim());
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        // Close dialog and return null (user cancelled)
        Close(null as string);
    }

    private void PasskeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
