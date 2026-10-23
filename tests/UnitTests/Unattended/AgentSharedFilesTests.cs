using System.Security.Cryptography;
using System.Text;
using DeskShare.Unattended;

namespace DeskShare.UnitTests.Unattended;

public sealed class AgentSharedFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentshared-" + Guid.NewGuid().ToString("N"));
    private string AppRoot => Path.Combine(_root, "app");
    private string ServiceDir => Path.Combine(_root, "staging");
    private string AgentDir => Path.Combine(ServiceDir, "agent");

    public AgentSharedFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(AppRoot, "ffmpeg"));
        Directory.CreateDirectory(AgentDir);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void Manifest(params string[] lines) =>
        File.WriteAllLines(Path.Combine(AgentDir, AgentSharedFiles.ManifestName), lines);

    [Fact]
    public void Restore_CopiesVerifiedFilesIncludingSubfolders_AndRemovesManifest()
    {
        File.WriteAllText(Path.Combine(AppRoot, "a.dll"), "A");
        File.WriteAllText(Path.Combine(AppRoot, "ffmpeg", "b.dll"), "B");
        Manifest($"{Sha("A")}  a.dll", $"{Sha("B").ToLowerInvariant()}  ffmpeg/b.dll");

        Assert.Equal(2, AgentSharedFiles.Restore(ServiceDir, AppRoot));

        Assert.Equal("A", File.ReadAllText(Path.Combine(AgentDir, "a.dll")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(AgentDir, "ffmpeg", "b.dll")));
        Assert.False(File.Exists(Path.Combine(AgentDir, AgentSharedFiles.ManifestName)));
    }

    [Fact]
    public void Restore_WithoutManifest_DoesNothing() =>
        Assert.Equal(0, AgentSharedFiles.Restore(ServiceDir, AppRoot));

    [Fact]
    public void Restore_HashMismatch_Throws_AndLeavesNoTamperedFile()
    {
        File.WriteAllText(Path.Combine(AppRoot, "a.dll"), "planted");
        Manifest($"{Sha("A")}  a.dll");

        Assert.Throws<InvalidOperationException>(() => AgentSharedFiles.Restore(ServiceDir, AppRoot));

        Assert.False(File.Exists(Path.Combine(AgentDir, "a.dll")));
    }

    [Fact]
    public void Restore_MissingSource_Throws()
    {
        Manifest($"{Sha("A")}  a.dll");
        Assert.Throws<InvalidOperationException>(() => AgentSharedFiles.Restore(ServiceDir, AppRoot));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("..\\outside.dll")]
    public void Restore_PathEscapingTheFolders_Throws(string rel)
    {
        File.WriteAllText(Path.Combine(_root, "outside.dll"), "A");
        Manifest($"{Sha("A")}  {rel}");

        Assert.Throws<InvalidOperationException>(() => AgentSharedFiles.Restore(ServiceDir, AppRoot));
        Assert.False(File.Exists(Path.Combine(ServiceDir, "outside.dll")));
    }

    [Fact]
    public void Restore_MalformedLine_Throws()
    {
        Manifest("not-a-hash a.dll");
        Assert.Throws<InvalidOperationException>(() => AgentSharedFiles.Restore(ServiceDir, AppRoot));
    }

    [Theory]
    [InlineData(@"C:\App\service\")] // AppContext.BaseDirectory form (trailing separator)
    [InlineData(@"C:\App\service")]
    public void AppRootOf_IsTheParentOfTheServiceFolder(string serviceDir) =>
        Assert.Equal(@"C:\App", AgentSharedFiles.AppRootOf(serviceDir));
}
