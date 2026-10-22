namespace DeskShare.Unattended;

public static class ServiceVersion
{
    /// <summary>
    /// True when the installed service is older than the app. Accepts "1.2.3", "1.2.3.0", "1.2.3-beta.1+abc" (suffix ignored).
    /// An unparsable version on either side means "no update" so a broken string never nags the user.
    /// </summary>
    public static bool UpdateAvailable(string? installed, string? app)
    {
        if (!TryParse(installed, out var i) || !TryParse(app, out var a))
            return false;
        return i < a;
    }

    public static bool TryParse(string? text, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var core = text.Trim().Split('-', '+')[0];
        if (!Version.TryParse(core, out var v))
            return false;
        // Normalise to 4 components so 1.2.3 == 1.2.3.0
        version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        return true;
    }
}
