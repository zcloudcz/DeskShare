# FFmpeg Native Libraries Deployment Guide

RemoteDesktop.Desktop používá **FFmpeg** pro dekódování VP8 video streamu. FFmpeg knihovny jsou **nativní DLL soubory** a musí být nasazeny spolu s aplikací.

## 🎯 Automatické řešení (Doporučeno)

### Metoda 1: PowerShell Script

Spusťte PowerShell script, který automaticky stáhne potřebné DLL soubory:

```powershell
cd src\RemoteDesktop.Desktop
powershell -ExecutionPolicy Bypass -File download-ffmpeg.ps1
```

Script stáhne následující soubory do `src\RemoteDesktop.Desktop\ffmpeg\`:
- `avcodec-60.dll` (Video kodek knihovna)
- `avdevice-60.dll` (Zařízení I/O)
- `avfilter-9.dll` (Video filtry)
- `avformat-60.dll` (Multiplexing/demultiplexing)
- `avutil-58.dll` (Utility funkce)
- `swresample-4.dll` (Audio resampling)
- `swscale-7.dll` (Video scaling)

**Při dalším buildu** se DLL automaticky zkopírují do output složky.

### Metoda 2: Windows Package Manager (winget)

Nainstalujte FFmpeg globálně v systému:

```powershell
winget install "FFmpeg (Shared)" --version 7.0
```

FFmpeg bude dostupný v PATH a aplikace ho najde automaticky.

## 📦 Manuální instalace

### Stáhnout z FFmpeg.AutoGen Repository

1. Jděte na: https://github.com/Ruslan-B/FFmpeg.AutoGen/tree/master/FFmpeg/bin/x64
2. Stáhněte všechny DLL soubory (viz seznam výše)
3. Zkopírujte je do:
   - `src\RemoteDesktop.Desktop\ffmpeg\` (pro development)
   - `bin\Debug\net8.0-windows\` (přímo do output složky)

### Stáhnout z gyan.dev (Oficiální builds)

1. Jděte na: https://www.gyan.dev/ffmpeg/builds/#release-builds
2. Stáhněte **"Shared"** build (ne "Static")
3. Rozbalte archiv
4. Zkopírujte DLL soubory z `bin\` složky do `src\RemoteDesktop.Desktop\ffmpeg\`

## 🔧 Jak to funguje

### MSBuild Automatizace

Projekt obsahuje MSBuild targets, které automaticky kopírují FFmpeg knihovny:

```xml
<Target Name="CopyFFmpegLibraries" AfterTargets="Build">
  <!-- Kopíruje DLL ze složky ffmpeg/ do output složky -->
</Target>

<Target Name="WarnMissingFFmpeg" AfterTargets="Build">
  <!-- Upozorní, pokud FFmpeg chybí -->
</Target>
```

### Pořadí vyhledávání FFmpeg knihoven:

1. **NuGet cache**: `%USERPROFILE%\.nuget\packages\ffmpeg.autogen\7.0.0\runtimes\win-x64\native\`
2. **Lokální složka**: `src\RemoteDesktop.Desktop\ffmpeg\`
3. **System PATH**: FFmpeg nainstalovaný přes winget nebo jinak

## 🚀 Release Build

Pro vytvoření release buildu s FFmpeg knihovnami:

```powershell
# 1. Stáhnout FFmpeg (pokud ještě není)
cd src\RemoteDesktop.Desktop
.\download-ffmpeg.ps1

# 2. Build v Release mode
cd ..\..
dotnet build -c Release

# 3. Publikovat aplikaci
dotnet publish src\RemoteDesktop.Desktop -c Release -o publish\Desktop
```

FFmpeg DLL soubory budou automaticky zahrnuty v output složce.

## 📋 Verifikace instalace

Po buildu zkontrolujte, že FFmpeg DLL jsou v output složce:

```powershell
ls src\RemoteDesktop.Desktop\bin\Debug\net8.0-windows\*.dll | Select-String -Pattern "av|sw"
```

Měli byste vidět:
```
avcodec-60.dll
avdevice-60.dll
avfilter-9.dll
avformat-60.dll
avutil-58.dll
swresample-4.dll
swscale-7.dll
```

## ⚠️ Troubleshooting

### Build Warning: "FFmpeg native libraries not found!"

**Příčina:** FFmpeg DLL soubory nejsou dostupné.

**Řešení:**
1. Spusťte `download-ffmpeg.ps1` script
2. Nebo nainstalujte FFmpeg přes winget
3. Nebo zkopírujte DLL manuálně

### Runtime Error: "Unable to load DLL 'avcodec-60.dll'"

**Příčina:** FFmpeg knihovny nejsou v output složce nebo v PATH.

**Řešení:**
1. Rebuild projektu (trigger automatické kopírování)
2. Zkontrolujte, že DLL jsou ve stejné složce jako .exe
3. Zkontrolujte PATH pro system-wide FFmpeg instalaci

### Chybějící VCRUNTIME140.dll

**Příčina:** FFmpeg vyžaduje Microsoft Visual C++ Redistributable.

**Řešení:**
```powershell
winget install Microsoft.VCRedist.2015+.x64
```

## 📊 Velikost souborů

| DLL soubor | Přibližná velikost |
|------------|-------------------|
| avcodec-60.dll | ~15 MB |
| avformat-60.dll | ~2 MB |
| avutil-58.dll | ~1 MB |
| swscale-7.dll | ~1 MB |
| ostatní | < 1 MB |
| **Celkem** | **~20 MB** |

## 🔐 Licence

FFmpeg knihovny jsou licencovány pod **LGPL 2.1+** (pro shared builds).

Redistribuce je povolena za předpokladu dodržení LGPL podmínek:
- Dynamické linkování (DLL) - ✅ Používáme
- Poskytnutí LGPL notice - ✅ Zahrnuto v dokumentaci
- Source code není vyžadován pro LGPL shared libraries

Více informací: https://www.ffmpeg.org/legal.html

## 📚 Reference

- **FFmpeg.AutoGen**: https://github.com/Ruslan-B/FFmpeg.AutoGen
- **SIPSorceryMedia.FFmpeg**: https://github.com/sipsorcery-org/SIPSorceryMedia.FFmpeg
- **FFmpeg oficiální builds**: https://www.gyan.dev/ffmpeg/builds/
- **FFmpeg dokumentace**: https://ffmpeg.org/documentation.html

---

**Poslední update:** 2025-11-08
**FFmpeg verze:** 7.0
**Platforma:** Windows x64
**Status:** ✅ Automatický deployment implementován
