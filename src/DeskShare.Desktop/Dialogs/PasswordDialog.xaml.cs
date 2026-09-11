using System.Windows;
using System.Windows.Input;

namespace DeskShare.Desktop;

/// <summary>
/// Password dialog for connecting to password-protected servers
/// </summary>
public partial class PasswordDialog : Window
{
    public string Password => PasswordBox.Password;

    public PasswordDialog(string connectionName)
    {
        InitializeComponent();
        ConnectionNameText.Text = $"Connecting to: {connectionName}";
        PasswordBox.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            MessageBox.Show("Please enter a password", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Connect_Click(sender, e);
        }
    }
}
