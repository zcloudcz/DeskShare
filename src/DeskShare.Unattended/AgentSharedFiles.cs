using System.Security.Cryptography;

namespace DeskShare.Unattended;

/// <summary>
/// The release package ships the agent's DLLs that are byte-identical to the desktop app's ones only once (in the app
/// root) and lists them in <c>agent/agent-shared.txt</c>, one "sha256-hex  relative-path" line per file (the path is
/// relative to the agent folder AND to the app root, e.g. "SIPSorcery.dll" or "ffmpeg/avcodec-62.dll").
/// The elevated installer calls <see cref="Restore"/> on the freshly copied STAGING folder, so the installed result is
/// the same as if the files had been shipped twice.
/// Security: the files are copied into the admin-protected folder under Program Files and hashed THERE (the app folder
/// is user-writable, hashing the source would leave a window to swap it). The LocalSystem service never loads
/// anything from the per-user app folder; a hash mismatch aborts the install.
/// </summary>
public static class AgentSharedFiles
{
    public const string ManifestName = "agent-shared.txt";

    /// <summary>Copies the listed files from <paramref name="appRoot"/> into <c>serviceDir/agent</c>. Returns the file count (0 = no manifest, nothing to do).</summary>
    /// <exception cref="InvalidOperationException">Bad manifest, missing source file or hash mismatch.</exception>
    public static int Restore(string serviceDir, string appRoot)
    {
        var agentDir = Path.GetFullPath(Path.Combine(serviceDir, UnattendedPaths.AgentFolder));
        var manifest = Path.Combine(agentDir, ManifestName);
        if (!File.Exists(manifest)) return 0; // e.g. the MSI ships a complete agent folder

        appRoot = Path.GetFullPath(appRoot);
        var count = 0;
        foreach (var line in File.ReadAllLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ') throw new InvalidOperationException($"Bad line in {ManifestName}: {line}");
            var expected = line[..64];
            var rel = line[66..];

            var src = Path.GetFullPath(Path.Combine(appRoot, rel));
            var dst = Path.GetFullPath(Path.Combine(agentDir, rel));
            // A manifest path must not escape either folder ("..", absolute paths).
            if (!UnattendedPaths.IsInside(appRoot, src) || !UnattendedPaths.IsInside(agentDir, dst))
                throw new InvalidOperationException($"Path outside the package in {ManifestName}: {rel}");
            if (!File.Exists(src)) throw new InvalidOperationException($"Shared file missing in the app folder: {rel}");

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, true);
            if (!Hash(dst).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(dst);
                throw new InvalidOperationException($"Shared file {rel} does not match the packaged hash.");
            }
            count++;
        }
        File.Delete(manifest); // installed folder is identical to a package that shipped the files twice
        return count;
    }

    private static string Hash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
