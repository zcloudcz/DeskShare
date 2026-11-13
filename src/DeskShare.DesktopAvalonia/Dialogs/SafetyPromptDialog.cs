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

    public SafetyPromptDialog(string title, string message, string safeButtonText, string? affirmativeButtonText, string? dontShowAgainText = null)
    {
        Title = "DeskShare";
        Width = 700;
        Background = Brush.Parse("#F4F7FB");
        FlowDirection = Localization.Loc.Instance.Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var safe = new Button { Content = safeButtonText, MinWidth = 110, MinHeight = 52, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var affirmative = new Button { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsVisible = affirmativeButtonText != null, Content = affirmativeButtonText, MinWidth = 150, MinHeight = 52, Background = Brush.Parse("#0B72D9"), Foreground = Brushes.White };
        safe.MinHeight = 52;
        Opened += (_, _) => safe.Focus();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        safe.Click += (_, _) => Close();
        affirmative.Click += (_, _) => { Allowed = true; Close(); };

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock { TextAlignment = Localization.Loc.Instance.TextAlignment, Text = title, FontSize = 28, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { TextAlignment = Localization.Loc.Instance.TextAlignment, Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
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
    public Task<bool> ShowAndWaitAsync(Window? owner = null)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => done.TrySetResult(Allowed);
        if (owner != null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _ = ShowDialog(owner);
        }
        else Show();
        Activate();
        return done.Task;
    }
}
