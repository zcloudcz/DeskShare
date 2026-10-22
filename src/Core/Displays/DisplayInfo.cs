namespace DeskShare.Core.Displays;

/// <summary>
/// One capturable display. <see cref="Index"/>, <see cref="Name"/>, <see cref="Width"/>, <see cref="Height"/> and
/// <see cref="Primary"/> go to the viewer ("displays" message); the rest is host-only.
/// </summary>
/// <param name="Left">X of the display's top-left corner in the virtual desktop (negative when it lies left of the primary one).</param>
/// <param name="Top">Y of the display's top-left corner in the virtual desktop.</param>
/// <param name="AdapterIndex">DXGI adapter that owns the display (Windows).</param>
/// <param name="OutputIndex">DXGI output on that adapter (Windows) or the capturer's monitor index elsewhere.</param>
public sealed record DisplayInfo(int Index, string Name, int Width, int Height, bool Primary,
    int Left = 0, int Top = 0, int AdapterIndex = 0, int OutputIndex = 0)
{
    /// <summary>"Display 1 - 2880x1800 (primary)"; numbering starts at 1 because that is what users see in Windows settings.</summary>
    public static string FormatName(int index, int width, int height, bool primary) =>
        $"Display {index + 1} - {width}x{height}{(primary ? " (primary)" : "")}";
}

/// <summary>A rectangle in virtual desktop pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height);

/// <summary>Pure coordinate maths for input on a display that is not (or not only) the primary monitor.</summary>
public static class DisplayMapping
{
    /// <summary>
    /// Maps a point normalized to one display (0..1 in both axes) to the 0..65535 range that SendInput expects with
    /// MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, i.e. relative to the whole virtual screen.
    /// </summary>
    /// <param name="nx">Horizontal position inside the display, 0 = left edge, 1 = right edge.</param>
    /// <param name="ny">Vertical position inside the display, 0 = top edge, 1 = bottom edge.</param>
    /// <param name="display">The captured display in virtual desktop pixels (left/top may be negative).</param>
    /// <param name="virtualScreen">The bounding box of all displays (SM_*VIRTUALSCREEN).</param>
    public static (int X, int Y) ToVirtualDesk(double nx, double ny, ScreenRect display, ScreenRect virtualScreen)
    {
        // The last pixel of a display is Width - 1, so 1.0 lands on it and not one pixel outside.
        var px = display.Left + Math.Round(Math.Clamp(nx, 0, 1) * Math.Max(display.Width - 1, 0));
        var py = display.Top + Math.Round(Math.Clamp(ny, 0, 1) * Math.Max(display.Height - 1, 0));
        return (Scale(px - virtualScreen.Left, virtualScreen.Width), Scale(py - virtualScreen.Top, virtualScreen.Height));
    }

    private static int Scale(double offset, int extent) =>
        extent <= 1 ? 0 : (int)Math.Round(Math.Clamp(offset * 65535.0 / (extent - 1), 0, 65535));
}
