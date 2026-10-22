namespace DeskShare.Unattended;

/// <summary>Fixed names and locations of the unattended-access service. Nothing here is read from config.</summary>
public static class UnattendedPaths
{
    public const string ServiceName = "DeskShareService";
    public const string ServiceExeName = "DeskShare.Service.exe";

    /// <summary>Agent lives in this sub folder of the service folder, so one copy operation installs both.</summary>
    public const string AgentFolder = "agent";
    public const string AgentExeName = "DeskShare.ScreenSenderApp.exe";

    /// <summary>%ProgramFiles%\DeskShare\Service - inherits the admin-only ACL of Program Files, which is the whole point:
    /// the service runs as LocalSystem and must never execute anything a normal user can overwrite.</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DeskShare", "Service");

    /// <summary>%ProgramData%\DeskShare - config, server id, owner secret and logs (ACL: SYSTEM + Administrators only).</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DeskShare");

    public static string ConfigPath => Path.Combine(DataDir, "unattended.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>
    /// Agent exe for a given service folder. Always derived from the service exe location and verified to stay inside
    /// it (defence in depth against a future bug that lets a name carry "..").
    /// </summary>
    public static string ResolveAgentPath(string serviceDir)
    {
        var path = Path.GetFullPath(Path.Combine(serviceDir, AgentFolder, AgentExeName));
        if (!IsInside(serviceDir, path))
            throw new InvalidOperationException("Agent path escapes the service folder: " + path);
        return path;
    }

    /// <summary>True when <paramref name="candidate"/> is below <paramref name="root"/> (full-path, case-insensitive, no prefix tricks like "Service2").</summary>
    public static bool IsInside(string root, string candidate)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
