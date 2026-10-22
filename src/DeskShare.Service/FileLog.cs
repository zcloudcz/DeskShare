using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>Append-only log in %ProgramData%\DeskShare\logs (and console when run interactively). Never throws.</summary>
internal static class FileLog
{
    private static readonly object Gate = new();

    /// <summary>
    /// The installer turns this off until %ProgramData%\DeskShare is proven to be ours: a pre-created junction there would
    /// otherwise make an elevated log append land anywhere. While off, messages go to the console only.
    /// </summary>
    public static volatile bool FileEnabled = true;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        try { Console.WriteLine(line); } catch (IOException) { /* no console (service) */ }
        if (!FileEnabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(UnattendedPaths.LogDir);
                File.AppendAllText(Path.Combine(UnattendedPaths.LogDir, "service.log"), line + Environment.NewLine);
            }
        }
        catch (Exception) { /* logging must never take the service down (e.g. console mode without admin rights) */ }
    }
}
