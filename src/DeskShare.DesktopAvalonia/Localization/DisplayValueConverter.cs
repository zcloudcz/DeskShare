using System.Globalization;
using Avalonia.Data.Converters;
using DeskShare.Core;

namespace DeskShare.DesktopAvalonia.Localization;

/// <summary>Formats values from the shared, persisted connection models without changing stored user data.</summary>
public sealed class DisplayValueConverter : IValueConverter
{
    public static DisplayValueConverter Instance { get; } = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return value;
        var key = text switch
        {
            "Online" => "status.online",
            "Offline" => "status.offline",
            "Trusted" => "status.trusted",
            "Connected" => "status.connected",
            "Failed" => "status.failed",
            _ => null
        };
        if (key != null) return Loc.Instance.T(key);
        if (text.StartsWith("server-", StringComparison.Ordinal)) return ServerIdGenerator.FormatServerIdForDisplay(text);
        return text.StartsWith("Last used: ", StringComparison.Ordinal) ? text[11..] : text;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
