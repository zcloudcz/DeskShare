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
    public void GetOrCreateServerId_PrefersTheStoredIdOverTheCurrentMac()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(IdPath, "server-fromAnEarlierAdapter");

        Assert.Equal("server-fromAnEarlierAdapter", ServerIdGenerator.GetOrCreateServerId(IdPath));
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
