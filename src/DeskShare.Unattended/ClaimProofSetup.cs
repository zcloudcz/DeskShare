using System.Text.RegularExpressions;

namespace DeskShare.Unattended;

/// <summary>
/// How the desktop app (not elevated) gets the "My devices" claim proof for the unattended agent's Server ID from
/// <c>DeskShare.Service.exe claim-proof</c> (elevated). Same transport and peer checks as <see cref="PasswordSetup"/>
/// (current-user pipe, mutual process id checks, identification-only impersonation); both ends are here so the
/// exchange can be tested without UAC.
/// </summary>
/// <remarks>
/// Why a helper: the proof is keyed with the agent's owner secret (see DeskShare.Core.Auth.DeviceClaimProof), which
/// lives in the admin-only %ProgramData%\DeskShare. The helper computes the proof there and sends back only the
/// proof; the secret never leaves the elevated process. The proof is bound to the account, timestamp and nonce the
/// app sends, so it is good for exactly one claim of that account.
/// One line each, UTF-8:
/// <code>
/// helper -> app:  ID server-123456789
/// app -> helper:  PROOF &lt;accountKey&gt; &lt;unix seconds&gt; &lt;nonce&gt;
/// helper -> app:  OK &lt;proof base64&gt;   or   ERR &lt;reason&gt;
/// </code>
/// </remarks>
public static class ClaimProofSetup
{
    public const string Verb = "claim-proof";

    /// <summary>The agent's owner secret, next to its server-id (written by the agent, see ScreenSenderApp Program).</summary>
    public const string OwnerSecretFileName = "server-owner.key";

    // Same formats the signaling server accepts (AccountEndpoints); they also keep "|" and spaces out of the fields.
    private static readonly Regex AccountKeyRx = new(@"^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex NonceRx = new(@"^[A-Za-z0-9_-]{16,64}$", RegexOptions.CultureInvariant);

    /// <summary>The agent's owner secret from <paramref name="dataDir"/>, or null when the agent has not created one yet.</summary>
    public static string? ReadOwnerSecret(string dataDir)
    {
        var path = Path.Combine(dataDir, OwnerSecretFileName);
        var secret = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        return secret.Length > 0 ? secret : null;
    }

    /// <summary>Helper side. Returns null on success, otherwise the error that was also sent to the app.</summary>
    /// <param name="prove">Computes the proof for (accountKey, unix seconds, nonce); called only with well-formed values.</param>
    public static string? RunHelper(TextReader input, TextWriter output, string serverId, Func<string, long, string, string> prove)
    {
        output.WriteLine("ID " + serverId);
        output.Flush();

        var parts = (input.ReadLine() ?? "").Split(' ');
        if (parts.Length != 4 || parts[0] != "PROOF" || !AccountKeyRx.IsMatch(parts[1]) ||
            !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var timestamp) ||
            !NonceRx.IsMatch(parts[3]))
            return PasswordSetup.Reply(output, "invalid request");

        output.WriteLine("OK " + prove(parts[1], timestamp, parts[3]));
        output.Flush();
        return null;
    }

    /// <summary>App side. Returns the agent's Server ID and the proof for it.</summary>
    /// <exception cref="InvalidOperationException">The helper sent something unexpected or reported an error.</exception>
    public static async Task<(string ServerId, string Proof)> RunAppAsync(TextReader input, TextWriter output,
        string accountKey, long timestamp, string nonce, CancellationToken cancellationToken = default)
    {
        var first = await input.ReadLineAsync(cancellationToken) ?? "";
        if (first.StartsWith("ERR ", StringComparison.Ordinal)) throw new InvalidOperationException(first[4..]);
        var serverId = first.StartsWith("ID ", StringComparison.Ordinal) ? first[3..] : "";
        if (!Regex.IsMatch(serverId, @"^server-[1-9][0-9]{8}$")) throw new InvalidOperationException("The service helper did not send a valid Server ID.");

        await output.WriteLineAsync($"PROOF {accountKey} {timestamp} {nonce}".AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);

        var reply = await input.ReadLineAsync(cancellationToken) ?? "";
        if (reply.StartsWith("OK ", StringComparison.Ordinal)) return (serverId, reply[3..]);
        throw new InvalidOperationException(reply.StartsWith("ERR ", StringComparison.Ordinal)
            ? reply[4..] : "The service helper did not return a proof.");
    }
}
