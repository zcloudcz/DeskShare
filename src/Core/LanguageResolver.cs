namespace DeskShare.Core;

/// <summary>Resolves a saved language or the first supported preferred language.</summary>
public static class LanguageResolver
{
    public static readonly string[] Supported = ["cs", "sk", "en", "de", "pl", "uk", "fr", "es", "it", "pt", "ru", "tr", "ar", "zh", "ja", "ko", "id"];
    public static string Resolve(string? saved, IEnumerable<string> preferred)
    {
        static string? Match(string? tag)
        {
            var primary = tag?.Trim().Split('-', '_', '.')[0].ToLowerInvariant();
            return Supported.Contains(primary) ? primary : null;
        }
        return (saved != "auto" ? Match(saved) : null) ?? preferred.Select(Match).FirstOrDefault(x => x != null) ?? "en";
    }
}
