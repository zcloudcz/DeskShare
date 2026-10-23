using System.Runtime.Versioning;
using DeskShare.Core.Auth;
using DeskShare.Unattended;

namespace DeskShare.Service;

/// <summary>
/// Elevated <c>claim-proof [--pipe &lt;name&gt; --owner-pid &lt;pid&gt;]</c>: tells the desktop app the agent's Server ID
/// and computes the "My devices" claim proof for it with the agent's owner secret, which only administrators can
/// read. Only the proof goes back (see <see cref="ClaimProofSetup"/>); nothing is written and the service keeps running.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ClaimProofCommand
{
    // Bounds the exchange like set-password, so a stalled peer cannot keep an elevated process waiting.
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(60);

    public static int Run(string[] args) => PasswordCommand.RunOverPipeOrConsole(args, ClaimProofSetup.Verb, Exchange);

    private static int Exchange(TextReader input, TextWriter output)
    {
        // Admin check, and %ProgramData%\DeskShare must be the protected folder before we trust anything in it.
        var problem = PasswordCommand.PrepareDataDir();
        var serverId = problem is null ? PasswordSetup.ReadServerId(UnattendedPaths.DataDir) : null;
        var secret = serverId is null ? null : ClaimProofSetup.ReadOwnerSecret(UnattendedPaths.DataDir);
        if (problem is null && (serverId is null || secret is null))
            problem = "The service has not registered its Server ID yet. Enable unattended access, wait a minute and try again.";
        if (problem != null)
        {
            FileLog.Write("ERROR: claim-proof: " + problem);
            PasswordSetup.Reply(output, problem);
            return 1;
        }

        var key = DeviceClaimProof.ClaimKey(secret!);
        // Synchronous pipe reads cannot be cancelled: run on a worker and stop waiting after the timeout (see set-password).
        var exchange = Task.Run(() => ClaimProofSetup.RunHelper(input, output, serverId!,
            (accountKey, timestamp, nonce) => DeviceClaimProof.Compute(key, serverId!, accountKey, timestamp, nonce)));
        var error = exchange.Wait(ExchangeTimeout) ? exchange.Result : "timed out waiting for the app";
        if (error != null)
        {
            FileLog.Write("ERROR: claim-proof: " + error);
            return 1;
        }
        FileLog.Write($"Claim proof issued for {serverId}.");
        return 0;
    }
}
