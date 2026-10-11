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

    [Fact]
    public void GetOrCreate_WhenTheFileCannotBeWritten_UsesAnInMemorySecretForTheProcess()
    {
        // The "directory" is a regular file, so creating the key file below it must fail.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var store = new ServerOwnerSecretStore(Path.Combine(blocker, "server-owner.key"));

        var first = store.GetOrCreate();

        Assert.Equal(32, Convert.FromBase64String(first).Length);
        Assert.Equal(first, store.GetOrCreate()); // stable for the life of the process
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
