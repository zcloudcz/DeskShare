namespace DeskShare.UnitTests.Video;

/// <summary>
/// libvpx (vpxmd.dll, VP8 encode and decode) needs vcruntime140.dll. A clean Windows without the VC++ Redistributable
/// does not have it, and then VP8 fails completely (a host without a GPU encoder sends no picture at all). DeskShare.Core
/// ships the DLL next to the app; this checks that it ends up next to vpxmd.dll in a build output.
/// </summary>
public sealed class NativeRuntimePackagingTests
{
    [Fact]
    public void VcRuntime_IsShippedNextToLibvpx()
    {
        if (!OperatingSystem.IsWindows()) return; // vpxmd.dll and the VC++ runtime are Windows only
        var dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, "vpxmd.dll"))) return; // this build has no libvpx

        Assert.True(File.Exists(Path.Combine(dir, "vcruntime140.dll")),
            "vcruntime140.dll must be shipped next to vpxmd.dll (see the comment in DeskShare.Core.csproj)");
    }
}
