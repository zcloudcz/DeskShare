# FFmpeg Multiplatform Setup

## Overview

FFmpeg is used for video encoding/decoding across all platforms. Each platform needs its own native libraries.

## File structure

```
src/RemoteDesktop.Desktop/
├── ffmpeg/
│   ├── win-x64/           # Windows binaries
│   │   ├── avcodec-60.dll
│   │   ├── avformat-60.dll
│   │   ├── avutil-58.dll
│   │   └── swscale-7.dll
│   ├── linux-x64/         # Linux binaries (TO DO)
│   │   ├── libavcodec.so.60
│   │   ├── libavformat.so.60
│   │   ├── libavutil.so.58
│   │   └── libswscale.so.7
│   └── osx-x64/           # macOS binaries (TO DO)
│       ├── libavcodec.60.dylib
│       ├── libavformat.60.dylib
│       ├── libavutil.58.dylib
│       └── libswscale.7.dylib
```

## Implementation in .csproj

### Option 1: Conditional ItemGroup (recommended)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Windows -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
    <None Include="ffmpeg\win-x64\*.dll" CopyToOutputDirectory="PreserveNewest">
      <Link>ffmpeg\%(Filename)%(Extension)</Link>
    </None>
  </ItemGroup>

  <!-- Linux -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <None Include="ffmpeg\linux-x64\*.so*" CopyToOutputDirectory="PreserveNewest">
      <Link>ffmpeg\%(Filename)%(Extension)</Link>
    </None>
  </ItemGroup>

  <!-- macOS -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('OSX'))">
    <None Include="ffmpeg\osx-x64\*.dylib" CopyToOutputDirectory="PreserveNewest">
      <Link>ffmpeg\%(Filename)%(Extension)</Link>
    </None>
  </ItemGroup>
</Project>
```

### Option 2: Runtime Identifier (RID)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RuntimeIdentifiers>win-x64;linux-x64;osx-x64</RuntimeIdentifiers>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="ffmpeg\**\*" CopyToOutputDirectory="PreserveNewest">
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </Content>
  </ItemGroup>
</Project>
```

On build:
```bash
dotnet publish -r win-x64 -c Release
dotnet publish -r linux-x64 -c Release
dotnet publish -r osx-x64 -c Release
```

## Obtaining the FFmpeg binaries

### Windows (already implemented ✅)
The existing `ffmpeg/` folder contains the Windows DLL files.

### Linux (TODO)

**Option A: Download from official sources**
```bash
# Ubuntu/Debian
wget https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
tar xf ffmpeg-release-amd64-static.tar.xz
cp ffmpeg-*-amd64-static/*.so* src/RemoteDesktop.Desktop/ffmpeg/linux-x64/
```

**Option B: Use system libraries**
```bash
sudo apt install libavcodec-dev libavformat-dev libavutil-dev libswscale-dev
# Find the installed .so files and copy them
find /usr/lib -name "libav*.so*"
```

**Option C: NuGet package (easiest)**
```xml
<PackageReference Include="FFmpeg.Native.Linux" Version="6.0.0" />
```

### macOS (TODO)

**Option A: Homebrew**
```bash
brew install ffmpeg
# The libraries are in /opt/homebrew/lib/ (ARM) or /usr/local/lib/ (Intel)
cp /opt/homebrew/lib/libav*.dylib src/RemoteDesktop.Desktop/ffmpeg/osx-x64/
```

**Option B: Official builds**
```bash
wget https://evermeet.cx/ffmpeg/ffmpeg-6.0.tar.xz
tar xf ffmpeg-6.0.tar.xz
```

**Option C: NuGet package**
```xml
<PackageReference Include="FFmpeg.Native.macOS" Version="6.0.0" />
```

## Current state in the project

### Desktop.csproj (current implementation)

```xml
<!-- Copies the FFmpeg DLL files to the output folder -->
<Target Name="CopyFFmpegLibraries" AfterTargets="Build">
  <ItemGroup>
    <FFmpegFiles Include="ffmpeg\*.dll" />
  </ItemGroup>
  <Copy SourceFiles="@(FFmpegFiles)"
        DestinationFolder="$(OutputPath)ffmpeg"
        SkipUnchangedFiles="true" />
  <Message Text="FFmpeg libraries copied to output directory: $(OutputPath)ffmpeg"
           Importance="high" />
</Target>
```

**Problem**: It copies only `.dll` files (Windows). It needs an update for `.so` (Linux) and `.dylib` (macOS).

### Recommended update:

```xml
<Target Name="CopyFFmpegLibraries" AfterTargets="Build">
  <!-- Windows -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
    <FFmpegFiles Include="ffmpeg\win-x64\*.dll" />
  </ItemGroup>

  <!-- Linux -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <FFmpegFiles Include="ffmpeg\linux-x64\*.so*" />
  </ItemGroup>

  <!-- macOS -->
  <ItemGroup Condition="$([MSBuild]::IsOSPlatform('OSX'))">
    <FFmpegFiles Include="ffmpeg\osx-x64\*.dylib" />
  </ItemGroup>

  <Copy SourceFiles="@(FFmpegFiles)"
        DestinationFolder="$(OutputPath)ffmpeg"
        SkipUnchangedFiles="true" />
  <Message Text="FFmpeg libraries copied to output directory: $(OutputPath)ffmpeg"
           Importance="high" />
</Target>
```

## Testing

### Windows
```powershell
dotnet build
dotnet run
# FFmpeg DLLs should be in bin/Debug/net8.0-windows/ffmpeg/
```

### Linux (after adding the .so files)
```bash
dotnet build
dotnet run
# FFmpeg .so files should be in bin/Debug/net8.0/ffmpeg/
ldd bin/Debug/net8.0/ffmpeg/libavcodec.so.60  # Check dependencies
```

### macOS (after adding the .dylib files)
```bash
dotnet build
dotnet run
# FFmpeg .dylib files should be in bin/Debug/net8.0/ffmpeg/
otool -L bin/Debug/net8.0/ffmpeg/libavcodec.60.dylib  # Check dependencies
```

## Alternative solution: NuGet packages

Instead of copying manually, we can use ready-made NuGet packages:

```xml
<ItemGroup>
  <!-- Automatically downloads the right version for the current platform -->
  <PackageReference Include="FFmpeg.AutoGen" Version="6.1.0" />

  <!-- Or use platform-specific packages -->
  <PackageReference Include="FFmpeg.Native.Win-x64" Version="6.0.0" Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
  <PackageReference Include="FFmpeg.Native.Linux-x64" Version="6.0.0" Condition="'$(RuntimeIdentifier)' == 'linux-x64'" />
  <PackageReference Include="FFmpeg.Native.macOS-x64" Version="6.0.0" Condition="'$(RuntimeIdentifier)' == 'osx-x64'" />
</ItemGroup>
```

### Benefits of the NuGet approach:
- ✅ Automatic download of the correct binaries
- ✅ Easier version updates
- ✅ Smaller GIT repository size
- ✅ No manual file copying

### Drawbacks:
- ❌ Dependency on external packages
- ❌ Sometimes outdated versions
- ❌ Less control over the FFmpeg version

## Recommendation

**For development**: Use the `FFmpeg.AutoGen` NuGet package for simplicity

**For production**: Manage the FFmpeg binaries manually in the `ffmpeg/` folder for full control

## TODO

- [ ] Download and add the Linux FFmpeg .so files
- [ ] Download and add the macOS FFmpeg .dylib files
- [ ] Update RemoteDesktop.Desktop.csproj with multiplatform copying
- [ ] Test on a Linux machine
- [ ] Test on a macOS machine
- [ ] Document the FFmpeg library versions (we currently do not know which version we use)
