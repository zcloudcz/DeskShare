using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DeskShare.Core;

namespace DeskShare.DesktopAvalonia.Localization;

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    private readonly Dictionary<string, Dictionary<string, string>> _strings = new();
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesktop.NET", "settings.json");
    public TextAlignment TextAlignment => TextAlignment.DetectFromContent;
    public FlowDirection FlowDirection => Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public string Choice { get; private set; } = "auto";
    public string Language { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => T(key);
    private Loc()
    {
        foreach (var lang in LanguageResolver.Supported)
        {
            using var stream = AssetLoader.Open(new Uri($"avares://DeskShare/Assets/i18n/{lang}.json"));
            _strings[lang] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        try { Choice = JsonNode.Parse(File.ReadAllText(_path))?["language"]?.GetValue<string>() ?? "auto"; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
        SetLanguage(Choice, false);
    }
    public string T(string key, object? args = null)
    {
        var value = _strings[Language].GetValueOrDefault(key) ?? _strings["en"].GetValueOrDefault(key) ?? key;
        if (args != null)
            foreach (var property in args.GetType().GetProperties())
                value = value.Replace("{" + property.Name + "}", Convert.ToString(property.GetValue(args), CultureInfo.CurrentCulture));
        return value;
    }
    /// <summary>Apply direction after tab templates have materialized, keeping code and input presenters LTR.</summary>
    public void ApplyDirection(Control root)
    {
        root.FlowDirection = FlowDirection;
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            var ltr = text.Classes.Contains("ltr") || text.GetVisualAncestors().OfType<TextBox>().Any();
            text.FlowDirection = ltr ? FlowDirection.LeftToRight : FlowDirection;
            text.TextAlignment = TextAlignment.DetectFromContent;
        }
    }

    public void SetLanguage(string choice, bool save = true)
    {
        Choice = choice;
        var preferred = new List<string>();
        if (OperatingSystem.IsLinux())
            foreach (var variable in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
                preferred.AddRange((Environment.GetEnvironmentVariable(variable) ?? "").Split(':'));
        preferred.Add(CultureInfo.CurrentUICulture.Name);
        Language = LanguageResolver.Resolve(choice, preferred);
        if (save)
        {
            try
            {
                var settings = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new() : new JsonObject();
                settings["language"] = choice;
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Choice)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextAlignment)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
