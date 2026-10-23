using System.IO.Pipes;
using System.Text;
using DeskShare.Core.Auth;
using DeskShare.Unattended;
using Xunit;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Permanent password of an unattended host: key derivation (shared vector with tests/js/perm-vector.mjs), how the
/// viewers tell a password from a passkey, the key file and the app/helper exchange that writes it.
/// </summary>
public class PermanentPasswordTests
{
    // Same constants as tests/js/perm-vector.mjs
    private const string Password = "Žluťoučký kůň 2026";
    private const string ServerId = "server-123456789";
    private const string AuthKey = "JHEeLJCuuOig-SzfCVF1yovJDLH-yQas8TlWd0gJ5y8";
    private const string E2EKeyHex = "66325114af86ffca1b9bfe6d612c7d763d10c7ed509909362993ca2d29d295ec";

    // PBKDF2 with 600 000 iterations costs a few hundred ms; derive the vector once for all tests.
    private static readonly Lazy<E2EBinding.DerivedKeys> Vector = new(() => E2EBinding.FromPermanentPassword(Password, ServerId));

    [Fact]
    public void FromPermanentPassword_MatchesTheSharedVector()
    {
        Assert.Equal(AuthKey, Vector.Value.AuthKey);
        Assert.Equal(E2EKeyHex, Convert.ToHexString(Vector.Value.E2EKey).ToLowerInvariant());
    }

    [Fact]
    public void FromPermanentPassword_IsNormalizedButOtherwiseExact()
    {
        // Decomposed characters (e.g. from a macOS keyboard) are the same password...
        Assert.Equal(AuthKey, E2EBinding.FromPermanentPassword(Password.Normalize(NormalizationForm.FormD), ServerId).AuthKey);
        // ...but case matters (unlike a passkey), and the key is bound to the Server ID.
        Assert.NotEqual(AuthKey, E2EBinding.FromPermanentPassword(Password.ToUpperInvariant(), ServerId).AuthKey);
        Assert.NotEqual(AuthKey, E2EBinding.FromPermanentPassword(Password, "server-987654321").AuthKey);
        // And it never equals the passkey derivation of the same text.
        Assert.NotEqual(AuthKey, E2EBinding.DeriveKeys(Password, ServerId).AuthKey);
    }

    [Theory]
    [InlineData("A3F7K9M2P")]
    [InlineData("a3f-7k9-m2p")]
    [InlineData(" A3F 7K9 M2P ")]
    public void DeriveFromUserInput_TreatsAPasskeyAsPasskey(string typed) =>
        Assert.Equal(E2EBinding.DeriveKeys("A3F7K9M2P", ServerId).AuthKey, E2EBinding.DeriveFromUserInput(typed, ServerId).AuthKey);

    [Fact]
    public void DeriveFromUserInput_TreatsEverythingElseAsPassword() =>
        Assert.Equal(AuthKey, E2EBinding.DeriveFromUserInput(Password, ServerId).AuthKey);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("A3F7K9M2")]        // 8: neither a passkey nor long enough
    [InlineData("short-pass1")]     // 11 characters
    public void DeriveFromUserInput_RejectsInputThatIsNeither(string? typed) =>
        Assert.Throws<ArgumentException>(() => E2EBinding.DeriveFromUserInput(typed, ServerId));

    [Theory]
    [InlineData("correct horse battery", true)]
    [InlineData("exactly12chr", true)]
    [InlineData("only11chars", false)]
    [InlineData("abc-def-ghi---", false)]  // 14 characters, but viewers would read it as a passkey
    [InlineData(" leading space pw", false)]
    [InlineData("trailing space pw ", false)]
    [InlineData(null, false)]
    public void ValidatePermanentPassword(string? password, bool valid) =>
        Assert.Equal(valid, E2EBinding.ValidatePermanentPassword(password) is null);

    [Fact]
    public void KeyRing_KeepsThePermanentKeyForever()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var ring = new E2EKeyRing(() => now);
        ring.AddPermanent(Vector.Value.E2EKey);
        var fp = string.Join(':', Enumerable.Repeat("AB", 32));
        var mac = E2EBinding.ComputeMac(Vector.Value.E2EKey, E2EBinding.ViewerRole, fp, fp);

        now = now.AddYears(5);

        Assert.True(ring.TryVerify(E2EBinding.ViewerRole, fp, fp, mac, out _));
    }

    [Fact]
    public void KeyFile_RoundTrips_AndIsIgnoredForAnotherServerOrWhenBroken()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perm-{Guid.NewGuid():N}.key");
        try
        {
            Assert.Null(PermanentKeyFile.Load(path, ServerId)); // no file

            PermanentKeyFile.Save(path, ServerId, Vector.Value.AuthKey, Vector.Value.E2EKey);
            var loaded = PermanentKeyFile.Load(path, ServerId);
            Assert.Equal(AuthKey, loaded!.Value.AuthKey);
            Assert.Equal(Vector.Value.E2EKey, loaded.Value.E2EKey);
            Assert.DoesNotContain(Password, File.ReadAllText(path)); // never the password itself

            // Keys are bound to the Server ID: after an ID change they must not be offered.
            Assert.Null(PermanentKeyFile.Load(path, "server-987654321"));

            File.WriteAllText(path, "{ not json");
            Assert.Null(PermanentKeyFile.Load(path, ServerId));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void KeyFile_InheritsTheProtectedDataDirAcl()
    {
        // permanent.key is created inside %ProgramData%\DeskShare and gets its ACL by inheritance: every ACE of the
        // folder must therefore apply to files (OI) and sub folders (CI), and none may grant Users or Everyone.
        var aces = AclSpec.DataDirSddl[AclSpec.DataDirSddl.IndexOf('(')..].Split(')', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(aces, ace => Assert.Contains(";OICI;", ace));
        Assert.Equal(new[] { "SY", "BA" }, aces.Select(a => a.Split(';')[^1]));
    }

    [Fact]
    public void ReadServerId_AcceptsOnlyAValidId()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Null(PasswordSetup.ReadServerId(dir));
            File.WriteAllText(Path.Combine(dir, PasswordSetup.ServerIdFileName), "server-012345678"); // leading zero: not an id
            Assert.Null(PasswordSetup.ReadServerId(dir));
            File.WriteAllText(Path.Combine(dir, PasswordSetup.ServerIdFileName), ServerId + "\r\n");
            Assert.Equal(ServerId, PasswordSetup.ReadServerId(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Helper_ParsesTheKeysFromStdin_AndSavesThem()
    {
        var e2e = Convert.ToBase64String(Vector.Value.E2EKey);
        var output = new StringWriter();
        (string, byte[])? saved = null;

        var error = PasswordSetup.RunHelper(new StringReader($"SET {AuthKey} {e2e}\n"), output, ServerId, (a, e) => saved = (a, e));

        Assert.Null(error);
        Assert.Equal($"ID {ServerId}{Environment.NewLine}OK{Environment.NewLine}", output.ToString());
        Assert.Equal(AuthKey, saved!.Value.Item1);
        Assert.Equal(Vector.Value.E2EKey, saved.Value.Item2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("my secret password")]                                                   // a raw password is never accepted
    [InlineData("SET tooShort AAAA")]
    [InlineData("SET JHEeLJCuuOig-SzfCVF1yovJDLH-yQas8TlWd0gJ5y8 AAAA")]               // e2e key not 32 bytes
    [InlineData("SET JHEeLJCuuOig-SzfCVF1yovJDLH-yQas8TlWd0gJ5y8 not-base64!")]
    public void Helper_RejectsAnythingButWellFormedKeys(string line)
    {
        var output = new StringWriter();
        var saved = false;

        var error = PasswordSetup.RunHelper(new StringReader(line), output, ServerId, (_, _) => saved = true);

        Assert.NotNull(error);
        Assert.False(saved);
        Assert.EndsWith($"ERR invalid keys{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task AppAndHelper_OverTheCurrentUserPipe_SetTheKeysForTheHelpersServerId()
    {
        // The real transport, minus UAC: app = pipe server (current user only), helper = client.
        var name = "DeskShare-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        (string, byte[])? saved = null;

        var helper = Task.Run(() =>
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            client.Connect(5000);
            using var reader = new StreamReader(client, leaveOpen: true);
            using var writer = new StreamWriter(client, leaveOpen: true);
            return PasswordSetup.RunHelper(reader, writer, ServerId, (a, e) => saved = (a, e));
        });

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var appReader = new StreamReader(server, leaveOpen: true);
        using var appWriter = new StreamWriter(server, leaveOpen: true);
        string? derivedFor = null;
        var serverId = await PasswordSetup.RunAppAsync(appReader, appWriter, id =>
        {
            derivedFor = id;
            return Task.FromResult((Vector.Value.AuthKey, Vector.Value.E2EKey));
        }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(await helper);
        Assert.Equal(ServerId, serverId);
        Assert.Equal(ServerId, derivedFor);
        Assert.Equal(AuthKey, saved!.Value.Item1);
    }

    [Fact]
    public async Task AcceptHelper_DropsAnotherProcessThatConnectsFirst_AndWaitsForTheLaunchedHelper()
    {
        const int squatterPid = 111, helperPid = 222;
        var name = "DeskShare-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // Seam for the real PipePeer.ClientProcessId: the first client "is" the squatter, the second the helper.
        var connections = 0;
        var accept = PasswordSetup.AcceptHelperAsync(server, helperPid,
            _ => ++connections == 1 ? squatterPid : helperPid, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        using var squatter = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        await squatter.ConnectAsync(5000);
        // The squatter is disconnected without receiving a byte (EOF, or the pipe already reports it broken).
        var buffer = new byte[16];
        int read;
        try { read = await squatter.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { read = 0; }
        Assert.Equal(0, read);
        Assert.False(accept.IsCompleted);

        using var helper = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        await helper.ConnectAsync(5000);
        await accept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.IsConnected);
        Assert.Equal(2, connections);
    }

    [Fact]
    public async Task PipePeer_ReportsTheProcessAtTheOtherEnd()
    {
        if (!OperatingSystem.IsWindows()) return; // GetNamedPipe*ProcessId is Windows only
        var name = "DeskShare-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await client.ConnectAsync(5000);
        await accept;

        // Both ends are this test process here; in production they are the app and the helper.
        Assert.Equal(Environment.ProcessId, PipePeer.ClientProcessId(server));
        Assert.Equal(Environment.ProcessId, PipePeer.ServerProcessId(client));
    }

    [Fact]
    public async Task App_ReportsTheHelpersError()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PasswordSetup.RunAppAsync(
            new StringReader("ERR Administrator rights are required.\n"), new StringWriter(),
            _ => throw new InvalidOperationException("must not derive")));
        Assert.Equal("Administrator rights are required.", ex.Message);
    }
}
