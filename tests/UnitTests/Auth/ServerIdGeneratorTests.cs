using DeskShare.Core;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// The Server ID must survive network changes (dock, Wi-Fi vs. cable), so after the first derivation it is
/// read back from disk instead of being recomputed from whichever adapter happens to be up.
/// </summary>
public sealed class ServerIdGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskshare-id-" + Guid.NewGuid().ToString("N"));
    private string IdPath => Path.Combine(_dir, "server-id");

    [Fact]
    public void GetOrCreateServerId_PersistsAndReturnsTheSameIdLater()
    {
        var first = ServerIdGenerator.GetOrCreateServerId(IdPath);

        Assert.StartsWith(ServerIdGenerator.ServerIdPrefix, first);
        Assert.Equal(first, File.ReadAllText(IdPath));
        Assert.Equal(first, ServerIdGenerator.GetOrCreateServerId(IdPath));
    }

    [Fact]
    public void GetOrCreateServerId_HasShortNumericFormat()
    {
        // The first digit is 1-9 so the ID always shows as three groups of three digits.
        for (var i = 0; i < 200; i++)
            Assert.Matches("^server-[1-9][0-9]{8}$", ServerIdGenerator.GetOrCreateServerId(Path.Combine(_dir, $"id{i}")));
    }

    [Fact]
    public void GetOrCreateServerId_ReturnsAStoredIdInTheNewFormat()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(IdPath, "server-123456789");

        Assert.Equal("server-123456789", ServerIdGenerator.GetOrCreateServerId(IdPath));
    }

    [Theory]
    [InlineData("server-fromAnEarlierAdapter")] // old MAC-derived format
    [InlineData("server-012345678")]            // leading zero
    [InlineData("server-12345678")]             // too short
    public void GetOrCreateServerId_RegeneratesAnIdInAnOldFormat(string legacy)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(IdPath, legacy);

        var id = ServerIdGenerator.GetOrCreateServerId(IdPath);

        Assert.Matches("^server-[1-9][0-9]{8}$", id);
        Assert.Equal(id, File.ReadAllText(IdPath));
    }

    [Fact]
    public void RegenerateServerId_ReplacesThePersistedId()
    {
        var first = ServerIdGenerator.GetOrCreateServerId(IdPath);

        // 9e8 possible IDs: a few attempts are enough to differ from the first one.
        var next = Enumerable.Range(0, 5).Select(_ => ServerIdGenerator.RegenerateServerId(IdPath)).First(id => id != first);

        Assert.Equal(next, ServerIdGenerator.GetOrCreateServerId(IdPath));
    }

    [Theory]
    [InlineData("server-123456789", "123 456 789")]
    [InlineData("server-legacyId", "server-legacyId")] // not the new format: unchanged
    [InlineData("", "")]
    public void FormatServerIdForDisplay_GroupsDigitsInThrees(string serverId, string expected)
    {
        Assert.Equal(expected, ServerIdGenerator.FormatServerIdForDisplay(serverId));
    }

    [Theory]
    [InlineData("123 456 789", "server-123456789")]
    [InlineData("123456789", "server-123456789")]
    [InlineData("123-456-789", "server-123456789")]
    [InlineData("server-123456789", "server-123456789")]
    [InlineData("  server-123 456 789  ", "server-123456789")]
    [InlineData("SERVER-123456789", "server-123456789")]
    [InlineData("  server-oldLegacyId  ", "server-oldLegacyId")] // legacy ID: trimmed, otherwise unchanged
    [InlineData("12345678", "12345678")]                         // too short
    [InlineData("1234567890", "1234567890")]                     // too long
    [InlineData("012 345 678", "012 345 678")]                   // first digit 0 is never generated
    [InlineData("12a 456 789", "12a 456 789")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeServerIdInput_ReturnsWireFormatOrTheTrimmedInput(string? input, string expected)
    {
        Assert.Equal(expected, ServerIdGenerator.NormalizeServerIdInput(input));
    }

    [Fact]
    public void GetOrCreateServerId_IgnoresACorruptFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(IdPath, "garbage");

        var id = ServerIdGenerator.GetOrCreateServerId(IdPath);

        Assert.StartsWith(ServerIdGenerator.ServerIdPrefix, id);
        Assert.Equal(id, File.ReadAllText(IdPath));
    }

    private string ClientIdPath => Path.Combine(_dir, "client-id");

    [Fact]
    public void GetOrCreateClientId_HasRandomFormat_AndIsPersisted()
    {
        var first = ServerIdGenerator.GetOrCreateClientId(ClientIdPath);

        Assert.Matches("^client-[0-9a-f]{32}$", first);
        Assert.Equal(first, ServerIdGenerator.GetOrCreateClientId(ClientIdPath));
    }

    [Fact]
    public void GetOrCreateClientId_IsIndependentOfTheServerId()
    {
        var serverId = ServerIdGenerator.GetOrCreateServerId(IdPath);
        var clientId = ServerIdGenerator.GetOrCreateClientId(ClientIdPath);

        // The old scheme was "client-" + server id suffix; a viewer ID must not leak from the shown Server ID.
        Assert.DoesNotContain(serverId[ServerIdGenerator.ServerIdPrefix.Length..], clientId);
        Assert.NotEqual(clientId, ServerIdGenerator.GetOrCreateClientId(Path.Combine(_dir, "other-client-id")));
    }

    [Fact]
    public void GetOrCreateClientId_IgnoresACorruptFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ClientIdPath, "client-short");

        var id = ServerIdGenerator.GetOrCreateClientId(ClientIdPath);

        Assert.Matches("^client-[0-9a-f]{32}$", id);
        Assert.Equal(id, File.ReadAllText(ClientIdPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
