using System.Security.Cryptography;

namespace DeskShare.Core.Auth;

/// <summary>
/// Keeps a per-installation random secret that proves to the SignalingServer that a
/// registration for a given ServerId comes from the same installation that claimed it first.
/// </summary>
/// <remarks>
/// The ServerId is derived from the MAC address, so it is guessable and static. Without this
/// secret anyone could re-register an existing ServerId and take over its sessions.
/// </remarks>
public sealed class ServerOwnerSecretStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private string? _cached;

    /// <param name="path">Key file location. Defaults to %APPDATA%/DeskShare/server-owner.key; injectable for tests.</param>
    public ServerOwnerSecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShare", "server-owner.key");
    }

    /// <summary>Returns the stored secret, creating and persisting a new one on first use.</summary>
    public string GetOrCreate()
    {
        lock (_lock)
        {
            if (_cached != null)
                return _cached;

            if (File.Exists(_path))
            {
                var existing = File.ReadAllText(_path).Trim();
                if (existing.Length > 0)
                    return _cached = existing;
            }

            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Create the file and lock down its permissions BEFORE writing the secret into it,
            // otherwise it would briefly be world-readable on Unix. On Windows %APPDATA% is already per-user.
            File.WriteAllText(_path, string.Empty);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(_path, secret);

            return _cached = secret;
        }
    }
}
