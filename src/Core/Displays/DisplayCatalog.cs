using Serilog;

namespace DeskShare.Core.Displays;

/// <summary>Lists the displays the host can share.</summary>
public static class DisplayCatalog
{
    /// <summary>
    /// Enumerates the displays. Windows lists every DXGI output; the other platforms capture a single target today
    /// (Linux X11: the whole X screen, Wayland: the portal stream, macOS: one avfoundation screen), so they report one entry
    /// and the viewer never gets a switch option.
    /// </summary>
    /// <param name="fallbackWidth">Size of the capture target when it cannot be enumerated (non-Windows, or DXGI found nothing).</param>
    /// <param name="fallbackHeight">See <paramref name="fallbackWidth"/>.</param>
    #pragma warning disable CA1416 // Validate platform compatibility
    public static IReadOnlyList<DisplayInfo> Enumerate(int fallbackWidth, int fallbackHeight)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var monitors = Platforms.Windows.DesktopDuplicator.EnumerateMonitors();
                if (monitors.Count > 0)
                {
                    return monitors
                        .Select((m, i) => new DisplayInfo(i, DisplayInfo.FormatName(i, m.Width, m.Height, m.IsPrimary), m.Width, m.Height,
                            m.IsPrimary, m.Left, m.Top, m.AdapterIndex, m.OutputIndex))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Display enumeration failed; offering the configured display only");
            }
        }

        // ponytail: macOS could list its avfoundation screens, but their sizes are only known once a frame arrives; add when needed.
        return new[] { new DisplayInfo(0, DisplayInfo.FormatName(0, fallbackWidth, fallbackHeight, true), fallbackWidth, fallbackHeight, true) };
    }
    #pragma warning restore CA1416

    /// <summary>Index of the display that matches the configured adapter/output, or 0 when none does.</summary>
    public static int FindConfigured(IReadOnlyList<DisplayInfo> displays, int adapterIndex, int outputIndex)
    {
        for (var i = 0; i < displays.Count; i++)
        {
            if (displays[i].AdapterIndex == adapterIndex && displays[i].OutputIndex == outputIndex)
                return i;
        }
        return 0;
    }
}
