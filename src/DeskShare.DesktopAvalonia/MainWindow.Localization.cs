using DeskShare.Desktop.Shared.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskShare.DesktopAvalonia.Localization;

namespace DeskShare.DesktopAvalonia;

public partial class MainWindow
{
    private bool _controlApproved;
    private static readonly string[] LanguageChoices = ["auto", "cs", "sk", "en", "de", "pl", "uk", "fr", "es", "it", "pt", "ru", "tr", "ar", "zh", "ja", "ko", "id"];
    private void InitializeLanguage()
    {
        var languageItems = new[] { Loc.Instance.T("language.auto"), "Čeština", "Slovenčina", "English", "Deutsch", "Polski", "Українська", "Français", "Español", "Italiano", "Português", "Русский", "Türkçe", "العربية", "中文", "日本語", "한국어", "Bahasa Indonesia" }.Select(name => new ComboBoxItem { Content = name }).ToArray();
        languageItems[0].Bind(ContentControl.ContentProperty, new Avalonia.Data.Binding("[language.auto]") { Source = Loc.Instance });
        LanguagePicker.ItemsSource = languageItems;
        LanguagePicker.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageChoices, Loc.Instance.Choice));
        LanguagePicker.SelectionChanged += (_, _) =>
        {
            if (LanguagePicker.SelectedIndex >= 0) Loc.Instance.SetLanguage(LanguageChoices[LanguagePicker.SelectedIndex]);
            RefreshLanguage();
        };
        MainTabControl.SelectionChanged += (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Loc.Instance.ApplyDirection(this), Avalonia.Threading.DispatcherPriority.Loaded);
            if (!ReferenceEquals(args.Source, MainTabControl)) return;
            var target = (MainTabControl.SelectedItem == ClientTab ? ConnectAdvanced : ShareAdvanced).Content as StackPanel;
            if (target != null && !ReferenceEquals(AccountBar.Parent, target))
            {
                (AccountBar.Parent as Panel)?.Children.Remove(AccountBar);
                target.Children.Add(AccountBar);
            }
        };
        RefreshLanguage();
    }
    private void RefreshLanguage()
    {
        FlowDirection = Loc.Instance.Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var rtl = Loc.Instance.Language == "ar";
        HeaderGrid.ColumnDefinitions = new ColumnDefinitions(rtl ? "Auto,*" : "*,Auto");
        Grid.SetColumn(BrandPanel, rtl ? 1 : 0);
        Grid.SetColumn(LanguagePanel, rtl ? 0 : 1);
        BrandPanel.HorizontalAlignment = rtl ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        if (_isServerRunning)
        {
            UpdatePasskeyCountdown();
            UpdateServerStatus(ServerStatus.Running);
        }
        StartServerButton.Content = Loc.Instance.T("share.start");
        StopServerButton.Content = Loc.Instance.T("share.stop");
        PermissionText.Text = Loc.Instance.T(_controlApproved ? "control.active" : "control.view_only");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Loc.Instance.ApplyDirection(this), Avalonia.Threading.DispatcherPriority.Loaded);
        if (IsLoaded)
        {
            LoadSavedConnections();
            LoadConnectionHistory();
            ApplyPolicyLocks();
            if (_account?.Available == true) UpdateAccountUi();
        }
    }
    private void Home_Click(object? sender, RoutedEventArgs e)
    {
        HomeView.IsVisible = !_isServerRunning;
        MainTabControl.IsVisible = _isServerRunning;
        if (_isServerRunning) MainTabControl.SelectedItem = ServerTab;
    }
    private void NeedHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeClientConnections > 0) return;
        HomeView.IsVisible = false;
        MainTabControl.IsVisible = true;
        MainTabControl.SelectedItem = ServerTab;
        if (!_isServerRunning) StartServer_Click(sender, e);
    }
    private void HelpSomeone_Click(object? sender, RoutedEventArgs e)
    {
        if (_isServerRunning) return;
        HomeView.IsVisible = false;
        MainTabControl.IsVisible = true;
        MainTabControl.SelectedItem = ClientTab;
    }
    private void StopControl_Click(object? sender, RoutedEventArgs e) => _serverManager.RevokeRemoteControl();
    private void Logs_Click(object? sender, RoutedEventArgs e)
    {
        HomeView.IsVisible = false;
        MainTabControl.IsVisible = true;
        LogTab.IsVisible = true;
        MainTabControl.SelectedItem = LogTab;
    }
    private void Advanced_Click(object? sender, RoutedEventArgs e)
    {
        if (MainTabControl.SelectedItem == LogTab) MainTabControl.SelectedItem = ServerTab;
        HomeView.IsVisible = false;
        MainTabControl.IsVisible = true;
        ShareAdvanced.IsExpanded = true;
        ConnectAdvanced.IsExpanded = true;
    }
}
