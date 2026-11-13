using System.Text.Json;
using System.Text.RegularExpressions;
using DeskShare.Core;

namespace DeskShare.UnitTests;

public class LanguageTests
{
    [Theory]
    [InlineData("de-AT", "de")]
    [InlineData("pt-BR", "pt")]
    [InlineData("zh-Hant-TW", "zh")]
    [InlineData("sv", "en")]
    public void ResolvesPrimaryLanguage(string preferred, string expected) =>
        Assert.Equal(expected, LanguageResolver.Resolve("auto", [preferred]));

    [Fact]
    public void SavedChoiceWins() => Assert.Equal("ar", LanguageResolver.Resolve("ar", ["de-AT"]));

    [Fact]
    public void SkipsUnsupportedPreferences() => Assert.Equal("de", LanguageResolver.Resolve(null, ["sv-SE", "de-AT"]));

    [Fact]
    public void EveryTranslationHasTheSameKeysAndPlaceholders()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "i18n");
        Dictionary<string, string> Read(string language) => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(directory, language + ".json")))!;
        var english = Read("en");
        foreach (var language in LanguageResolver.Supported)
        {
            var translated = Read(language);
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            foreach (var (key, value) in english)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{language}: {key}");
                static string[] Placeholders(string text) => Regex.Matches(text, @"\{\w+\}").Select(m => m.Value).Order().ToArray();
                Assert.Equal(Placeholders(value), Placeholders(translated[key]));
            }
        }
    }
}
