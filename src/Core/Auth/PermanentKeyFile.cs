using System.Text.Json;

namespace DeskShare.Core.Auth;

/// <summary>
/// The file that holds the keys derived from the permanent password of an unattended host
/// (<c>%ProgramData%\DeskShare\permanent.key</c>, written by the elevated <c>DeskShare.Service.exe set-password</c>).
/// </summary>
/// <remarks>
/// The password itself is never stored, only what <c>E2EBinding.FromPermanentPassword</c> made of it: the agent needs
/// <c>AuthKey</c> to register it with the signaling server and <c>E2EKey</c> to verify viewers. Both keys are as good
/// as the password for connecting to THIS machine, so the file lives in the SYSTEM + Administrators only folder:
/// whoever can read it is already an administrator of the machine. The keys are bound to the Server ID (it is the
/// salt), so the file records it and a file made for another ID is ignored.
/// This file is also compiled into DeskShare.Service (linked source), which must not depend on DeskShare.Core, so it
/// uses nothing but the base class library.
/// </remarks>
public static class PermanentKeyFile
{
    public const string FileName = "permanent.key";

    private sealed record Content(int V, string ServerId, string AuthKey, string E2EKey);

    public static void Save(string path, string serverId, string authKey, byte[] e2eKey)
    {
        var json = JsonSerializer.Serialize(new Content(1, serverId, authKey, Convert.ToBase64String(e2eKey)));
        // Write next to the target and rename: a crash never leaves a half-written key file, and the temp file is
        // created in the same protected folder, so it gets the same (inherited) ACL as the final file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The keys for <paramref name="serverId"/>, or null when there is no file, it is unreadable, or it was made for another Server ID.</summary>
    public static (string AuthKey, byte[] E2EKey)? Load(string path, string serverId)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var c = JsonSerializer.Deserialize<Content>(File.ReadAllText(path));
            if (c is not { V: 1 } || c.ServerId != serverId || c.AuthKey is not { Length: 43 }) return null;
            var e2e = Convert.FromBase64String(c.E2EKey);
            return e2e.Length == 32 ? (c.AuthKey, e2e) : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException or ArgumentNullException)
        {
            return null;
        }
    }
}
