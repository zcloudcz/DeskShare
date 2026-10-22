using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>Append-only log in %ProgramData%\DeskShare\logs (and console when run interactively). Never throws.</summary>
internal static class FileLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        try { Console.WriteLine(line); } catch (IOException) { /* no console (service) */ }
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
