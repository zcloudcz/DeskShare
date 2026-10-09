using DeskShare.Core.Auth;

namespace DeskShare.UnitTests.Auth;

public class ServerOwnerSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskshare-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetOrCreate_CreatesOnce_AndReturnsSameValueAfterwards()
    {
        var path = Path.Combine(_dir, "nested", "server-owner.key");

        var first = new ServerOwnerSecretStore(path).GetOrCreate();
        // A fresh instance (= app restart) must read the persisted value, not generate a new one.
        var second = new ServerOwnerSecretStore(path).GetOrCreate();

        Assert.Equal(first, second);
        Assert.Equal(32, Convert.FromBase64String(first).Length);
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
