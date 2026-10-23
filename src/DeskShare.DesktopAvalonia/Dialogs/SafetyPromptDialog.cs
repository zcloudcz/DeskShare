using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DeskShare.DesktopAvalonia.Dialogs;

/// <summary>
/// A two-button question used for the anti-scam prompts: the warning before sharing and the "allow remote control?" approval.
/// Built in code (no XAML) because it is just text, an optional check box and two buttons.
/// </summary>
/// <remarks>
/// <see cref="Allowed"/> is true only when the person clicked the second (affirmative) button. Closing the window by
/// any other means, including <see cref="Window.Close()"/> from a timeout, counts as the first (safe) answer.
/// The window is shown non-modally and topmost so it cannot hide behind a full-screen app.
/// </remarks>
public sealed class SafetyPromptDialog : Window
{
    private readonly CheckBox? _dontShowAgain;

    public bool Allowed { get; private set; }

    public bool DontShowAgain => _dontShowAgain?.IsChecked == true;

    public SafetyPromptDialog(string title, string message, string safeButtonText, string affirmativeButtonText, string? dontShowAgainText = null)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var safe = new Button { Content = safeButtonText, MinWidth = 110, Margin = new Thickness(0, 0, 10, 0) };
        var affirmative = new Button { Content = affirmativeButtonText, MinWidth = 110 };
        safe.Click += (_, _) => Close();
        affirmative.Click += (_, _) => { Allowed = true; Close(); };

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        if (dontShowAgainText != null)
        {
            _dontShowAgain = new CheckBox { Content = dontShowAgainText };
            panel.Children.Add(_dontShowAgain);
        }
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { safe, affirmative }
        });
        Content = panel;
    }

    /// <summary>Shows the window and completes when it is closed. The answer is <see cref="Allowed"/>.</summary>
    public Task<bool> ShowAndWaitAsync()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => done.TrySetResult(Allowed);
        Show();
        Activate();
        return done.Task;
    }
}
