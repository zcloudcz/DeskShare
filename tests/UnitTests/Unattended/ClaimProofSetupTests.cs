using System.IO.Pipes;
using DeskShare.Core.Auth;
using DeskShare.Unattended;

namespace DeskShare.UnitTests.Unattended;

/// <summary>The claim-proof exchange between the desktop app and the elevated helper (UAC left out).</summary>
public class ClaimProofSetupTests
{
    private const string ServerId = "server-123456789";
    private const string AccountKey = "00000000-1111-2222-3333-444444444444";
    private const string Nonce = "nonce-0123456789abcdef";

    [Fact]
    public void Helper_ProvesOnlyForItsOwnServerId_AndSendsOnlyTheProof()
    {
        var output = new StringWriter();
        (string, long, string)? asked = null;

        var error = ClaimProofSetup.RunHelper(new StringReader($"PROOF {AccountKey} 1792756800 {Nonce}\n"), output, ServerId,
            (a, t, n) => { asked = (a, t, n); return "PROOF=="; });

        Assert.Null(error);
        Assert.Equal((AccountKey, 1792756800L, Nonce), asked);
        Assert.Equal($"ID {ServerId}{Environment.NewLine}OK PROOF=={Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("PROOF only-two")]
    [InlineData("PROOF has|pipe 1792756800 nonce-0123456789abcdef")]   // would break the proof message
    [InlineData("PROOF acct -5 nonce-0123456789abcdef")]
    [InlineData("PROOF acct 1792756800 short")]
    [InlineData("SET JHEeLJCuuOig-SzfCVF1yovJDLH-yQas8TlWd0gJ5y8 AAAA")]  // another verb's message
    public void Helper_RejectsMalformedRequests_WithoutComputing(string line)
    {
        var output = new StringWriter();
        var computed = false;

        var error = ClaimProofSetup.RunHelper(new StringReader(line), output, ServerId, (_, _, _) => { computed = true; return ""; });

        Assert.NotNull(error);
        Assert.False(computed);
        Assert.EndsWith($"ERR invalid request{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task App_ReportsTheHelpersError()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClaimProofSetup.RunAppAsync(new StringReader("ERR no server id yet\n"), new StringWriter(), AccountKey, 1, Nonce));
        Assert.Equal("no server id yet", ex.Message);
    }

    [Fact]
    public void ReadOwnerSecret_IsNullUntilTheAgentWroteOne()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Null(ClaimProofSetup.ReadOwnerSecret(dir));
            File.WriteAllText(Path.Combine(dir, ClaimProofSetup.OwnerSecretFileName), "secret-1\r\n");
            Assert.Equal("secret-1", ClaimProofSetup.ReadOwnerSecret(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task AppAndHelper_OverTheCurrentUserPipe_ProduceAProofTheServerAccepts()
    {
        // The real transport, minus UAC: app = pipe server (current user only), helper = client.
        var name = "DeskShare-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var key = DeviceClaimProof.ClaimKey("agent-owner-secret");

        var helper = Task.Run(() =>
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            client.Connect(5000);
            using var reader = new StreamReader(client, leaveOpen: true);
            using var writer = new StreamWriter(client, leaveOpen: true);
            return ClaimProofSetup.RunHelper(reader, writer, ServerId,
                (a, t, n) => DeviceClaimProof.Compute(key, ServerId, a, t, n));
        });

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var appReader = new StreamReader(server, leaveOpen: true);
        using var appWriter = new StreamWriter(server, leaveOpen: true);
        var (serverId, proof) = await ClaimProofSetup.RunAppAsync(appReader, appWriter, AccountKey, 1792756800, Nonce)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(await helper);
        Assert.Equal(ServerId, serverId);
        Assert.True(DeviceClaimProof.Verify(key, ServerId, AccountKey, 1792756800, Nonce, proof));
    }
}
