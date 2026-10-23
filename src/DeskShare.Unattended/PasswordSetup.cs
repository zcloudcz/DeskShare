using System.Text.RegularExpressions;

namespace DeskShare.Unattended;

/// <summary>
/// How the desktop app (not elevated) and <c>DeskShare.Service.exe set-password</c> (elevated) set the permanent
/// password of the unattended agent. Both ends are here so the exchange can be tested without UAC.
/// </summary>
/// <remarks>
/// Why this split: the keys are bound to the agent's Server ID, which lives in the admin-only %ProgramData%\DeskShare,
/// so the app cannot read it. The elevated helper cannot be given the password on its command line either: command
/// lines end up in process lists and security logs. So the helper sends the Server ID, the app derives the keys
/// itself (PBKDF2, see E2EBinding.FromPermanentPassword) and sends back only the derived keys. The password never
/// leaves the app; the helper only writes the keys into the protected key file.
/// Transport: a named pipe that only the current user can open (UAC runs the helper with ShellExecute, which cannot
/// redirect stdin), or plain stdin/stdout when an administrator runs the helper by hand.
/// One line each, UTF-8:
/// <code>
/// helper -> app:  ID server-123456789
/// app -> helper:  SET &lt;authKey base64url, 43 chars&gt; &lt;e2eKey base64, 32 bytes&gt;
/// helper -> app:  OK          or   ERR &lt;reason&gt;
/// </code>
/// </remarks>
public static class PasswordSetup
{
    public const string SetVerb = "set-password";
    public const string ClearVerb = "clear-password";
    public const string ServerIdFileName = "server-id";

    private static readonly Regex ServerIdRx = new(@"^server-[1-9][0-9]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex AuthKeyRx = new(@"^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

    /// <summary>The agent's Server ID from <paramref name="dataDir"/>, or null when the agent has not created one yet.</summary>
    public static string? ReadServerId(string dataDir)
    {
        var path = Path.Combine(dataDir, ServerIdFileName);
        var id = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        return id != null && ServerIdRx.IsMatch(id) ? id : null;
    }

    /// <summary>Helper side. Returns null on success, otherwise the error that was also sent to the app.</summary>
    /// <param name="save">Writes the key file; called only with well-formed keys.</param>
    public static string? RunHelper(TextReader input, TextWriter output, string serverId, Action<string, byte[]> save)
    {
        output.WriteLine("ID " + serverId);
        output.Flush();

        var parts = (input.ReadLine() ?? "").Split(' ');
        byte[]? e2eKey = null;
        if (parts.Length != 3 || parts[0] != "SET" || !AuthKeyRx.IsMatch(parts[1]) ||
            !TryBase64(parts[2], out e2eKey) || e2eKey!.Length != 32)
            return Reply(output, "invalid keys");

        save(parts[1], e2eKey);
        output.WriteLine("OK");
        output.Flush();
        return null;
    }

    /// <summary>App side. Returns the Server ID the password was set for (to show the user what to connect to).</summary>
    /// <param name="derive">Derives authKey and e2eKey for that Server ID (slow: PBKDF2).</param>
    /// <exception cref="InvalidOperationException">The helper sent something unexpected or reported an error.</exception>
    public static async Task<string> RunAppAsync(TextReader input, TextWriter output, Func<string, Task<(string AuthKey, byte[] E2EKey)>> derive)
    {
        var first = await input.ReadLineAsync() ?? "";
        if (first.StartsWith("ERR ", StringComparison.Ordinal)) throw new InvalidOperationException(first[4..]);
        var serverId = first.StartsWith("ID ", StringComparison.Ordinal) ? first[3..] : "";
        if (!ServerIdRx.IsMatch(serverId)) throw new InvalidOperationException("The service helper did not send a valid Server ID.");

        var (authKey, e2eKey) = await derive(serverId);
        await output.WriteLineAsync($"SET {authKey} {Convert.ToBase64String(e2eKey)}");
        await output.FlushAsync();

        var reply = await input.ReadLineAsync();
        if (reply == "OK") return serverId;
        throw new InvalidOperationException(reply?.StartsWith("ERR ", StringComparison.Ordinal) == true
            ? reply[4..] : "The service helper did not confirm the password.");
    }

    /// <summary>Sends "ERR reason" (also usable by the helper before the exchange starts, e.g. no Server ID yet).</summary>
    public static string Reply(TextWriter output, string error)
    {
        output.WriteLine("ERR " + error);
        output.Flush();
        return error;
    }

    private static bool TryBase64(string s, out byte[]? bytes)
    {
        try { bytes = Convert.FromBase64String(s); return true; }
        catch (FormatException) { bytes = null; return false; }
    }
}
