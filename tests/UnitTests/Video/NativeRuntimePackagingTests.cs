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

        var runtime = Path.Combine(dir, "vcruntime140.dll");
        Assert.True(File.Exists(runtime), "vcruntime140.dll must be shipped next to vpxmd.dll (see the comment in DeskShare.Core.csproj)");
        // A runtime of another architecture next to libvpx would block loading it even where the right one is installed.
        Assert.Equal(PeMachine(Path.Combine(dir, "vpxmd.dll")), PeMachine(runtime));
    }

    // IMAGE_FILE_HEADER.Machine: 0x8664 = x64, 0x14C = x86, 0xAA64 = ARM64.
    private static ushort PeMachine(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 0x3C;
        reader.BaseStream.Position = reader.ReadInt32() + 4; // skip the 4-byte PE signature
        return reader.ReadUInt16();
    }
}
