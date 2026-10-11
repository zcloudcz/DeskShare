namespace DeskShare.Core.Conversion;

/// <summary>
/// Decides the resolution that is actually encoded and sent, given the captured screen size.
/// </summary>
public static class OutputSize
{
    /// <summary>
    /// Computes the output size: width capped at <paramref name="maxWidth"/>, aspect ratio preserved,
    /// both dimensions even (I420 stores chroma at half resolution, so odd sizes are not allowed).
    /// Never upscales. <paramref name="maxWidth"/> of 0 (or less) means "native", i.e. no scaling.
    /// </summary>
    public static (int Width, int Height) Compute(int sourceWidth, int sourceHeight, int maxWidth)
    {
        if (maxWidth <= 0 || sourceWidth <= maxWidth)
            return (sourceWidth, sourceHeight);

        // Round down to even so we stay within the requested cap; at least 2 so a cap of 1 cannot yield width 0.
        var width = Math.Max(2, maxWidth & ~1);
        var height = ToEven(sourceHeight * (double)width / sourceWidth);
        return (width, height);
    }

    // Nearest even number, at least 2.
    private static int ToEven(double value) =>
        Math.Max(2, (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2);
}
