# FFmpeg Multiplatform Setup

## Přehled

FFmpeg je používán pro video encoding/decoding napříč všemi platformami. Každá platforma potřebuje své vlastní nativní knihovny.

## Struktura souborů

```
src/RemoteDesktop.Desktop/
├── ffmpeg/
│   ├── win-x64/           # Windows binárky
│   │   ├── avcodec-60.dll
│   │   ├── avformat-60.dll
│   │   ├── avutil-58.dll
│   │   └── swscale-7.dll
│   ├── linux-x64/         # Linux binárky (TO DO)
│   │   ├── libavcodec.so.60
│   │   ├── libavformat.so.60
│   │   ├── libavutil.so.58
│   │   └── libswscale.so.7
│   └── osx-x64/           # macOS binárky (TO DO)
│       ├── libavcodec.60.dylib
│       ├── libavformat.60.dylib
│       ├── libavutil.58.dylib
│       └── libswscale.7.dylib
```

## Implementace v .csproj

### Varianta 1: Conditional ItemGroup (doporučeno)

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

### Varianta 2: Runtime Identifier (RID)

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

Při buildu:
```bash
dotnet publish -r win-x64 -c Release
dotnet publish -r linux-x64 -c Release
dotnet publish -r osx-x64 -c Release
```

## Získání FFmpeg binárních souborů

### Windows (již implementováno ✅)
Stávající `ffmpeg/` složka obsahuje Windows DLL soubory.

### Linux (TODO)

**Možnost A: Stažení z oficiálních zdrojů**
```bash
# Ubuntu/Debian
wget https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz
tar xf ffmpeg-release-amd64-static.tar.xz
cp ffmpeg-*-amd64-static/*.so* src/RemoteDesktop.Desktop/ffmpeg/linux-x64/
```

**Možnost B: Použití systémových knihoven**
```bash
sudo apt install libavcodec-dev libavformat-dev libavutil-dev libswscale-dev
# Najít instalované .so soubory a zkopírovat
find /usr/lib -name "libav*.so*"
```

**Možnost C: NuGet balíček (nejjednodušší)**
```xml
<PackageReference Include="FFmpeg.Native.Linux" Version="6.0.0" />
```

### macOS (TODO)

**Možnost A: Homebrew**
```bash
brew install ffmpeg
# Knihovny jsou v /opt/homebrew/lib/ (ARM) nebo /usr/local/lib/ (Intel)
cp /opt/homebrew/lib/libav*.dylib src/RemoteDesktop.Desktop/ffmpeg/osx-x64/
```

**Možnost B: Oficiální builds**
```bash
wget https://evermeet.cx/ffmpeg/ffmpeg-6.0.tar.xz
tar xf ffmpeg-6.0.tar.xz
```

**Možnost C: NuGet balíček**
```xml
<PackageReference Include="FFmpeg.Native.macOS" Version="6.0.0" />
```

## Aktuální stav v projektu

### Desktop.csproj (aktuální implementace)

```xml
<!-- Zkopíruje FFmpeg DLL soubory do output složky -->
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

**Problém**: Kopíruje pouze `.dll` soubory (Windows). Potřebuje update pro `.so` (Linux) a `.dylib` (macOS).

### Doporučená aktualizace:

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

## Testování

### Windows
```powershell
dotnet build
dotnet run
# FFmpeg DLLs by měly být v bin/Debug/net8.0-windows/ffmpeg/
```

### Linux (po přidání .so souborů)
```bash
dotnet build
dotnet run
# FFmpeg .so by měly být v bin/Debug/net8.0/ffmpeg/
ldd bin/Debug/net8.0/ffmpeg/libavcodec.so.60  # Zkontrolovat závislosti
```

### macOS (po přidání .dylib souborů)
```bash
dotnet build
dotnet run
# FFmpeg .dylib by měly být v bin/Debug/net8.0/ffmpeg/
otool -L bin/Debug/net8.0/ffmpeg/libavcodec.60.dylib  # Zkontrolovat závislosti
```

## Alternativní řešení: NuGet balíčky

Místo manuálního kopírování můžeme použít hotové NuGet balíčky:

```xml
<ItemGroup>
  <!-- Automaticky stáhne správnou verzi pro aktuální platformu -->
  <PackageReference Include="FFmpeg.AutoGen" Version="6.1.0" />

  <!-- Nebo používat platform-specific packages -->
  <PackageReference Include="FFmpeg.Native.Win-x64" Version="6.0.0" Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
  <PackageReference Include="FFmpeg.Native.Linux-x64" Version="6.0.0" Condition="'$(RuntimeIdentifier)' == 'linux-x64'" />
  <PackageReference Include="FFmpeg.Native.macOS-x64" Version="6.0.0" Condition="'$(RuntimeIdentifier)' == 'osx-x64'" />
</ItemGroup>
```

### Výhody NuGet přístupu:
- ✅ Automatické stahování správných binárních souborů
- ✅ Snadnější aktualizace verzí
- ✅ Menší velikost GIT repozitáře
- ✅ Žádné ruční kopírování souborů

### Nevýhody:
- ❌ Závislost na externe balíčky
- ❌ Někdy zastaralé verze
- ❌ Menší kontrola nad verzí FFmpeg

## Doporučení

**Pro vývoj**: Použít NuGet balíček `FFmpeg.AutoGen` pro jednoduchost

**Pro produkci**: Manuálně spravovat FFmpeg binárky v `ffmpeg/` složce pro plnou kontrolu

## TODO

- [ ] Stáhnout a přidat Linux FFmpeg .so soubory
- [ ] Stáhnout a přidat macOS FFmpeg .dylib soubory
- [ ] Aktualizovat RemoteDesktop.Desktop.csproj s multiplatformním kopírováním
- [ ] Otestovat na Linux stroji
- [ ] Otestovat na macOS stroji
- [ ] Dokumentovat verze FFmpeg knihoven (současně nevíme kterou verzi používáme)
