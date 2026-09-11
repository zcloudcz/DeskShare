# DeskShare – Multiplatformní možnosti (iOS / Android / macOS)

## Současný stav

**DeskShare** je .NET 8 aplikace pro vzdálenou plochu/sdílení obrazovky postavená na **WebRTC** (SIPSorcery). Architektura:

| Komponenta | Technologie | Platforma |
|---|---|---|
| SignalingServer | ASP.NET Core 8 (WebSocket) | Cross-platform |
| ScreenSenderApp | Console + DXGI/X11/ScreenCaptureKit | Win/Linux/macOS |
| Desktop Client | **WPF** | **Pouze Windows** |
| Web Client | HTML5/JS/Canvas + WebRTC | Všechny prohlížeče |
| Core | .NET 8 abstractions | Cross-platform |

Architektura je dobře navržená – rozhraní `ICapturer`, `IInputController`, `IClipboardManager` s platform-specific implementacemi. **Klíčový problém** pro mobile je WPF desktop klient (Windows-only) a absence nativních mobilních klientů.

---

## Existující macOS implementace (server-side)

Projekt již obsahuje **kompletní sadu macOS platform-specific implementací** v `src/Core/Platforms/macOS/`:

| Soubor | Účel | Status |
|---|---|---|
| `ScreenCaptureKitCapturer.cs` | Snímání obrazovky (macOS 12.3+) | Placeholder – potřebuje Obj-C bridge |
| `CGEventInputController.cs` | Simulace klávesnice/myši (CGEvent API) | Implementováno |
| `NSPasteboardClipboardManager.cs` | Schránka přes NSPasteboard (pbcopy/pbpaste) | Implementováno |
| `CGWindowManager.cs` | Správa oken (CGWindow API) | Základní stub |

`PlatformServiceFactory` (`src/Core/Platform/PlatformServiceFactory.cs`) již macOS detekuje a routuje na správné implementace. **ScreenSenderApp** (server pro sdílení obrazovky) tedy macOS podporuje.

**Klíčový problém:** Desktop **viewer klient** (`DeskShare.Desktop`) je postaven na **WPF = pouze Windows**. Na macOS neexistuje žádný nativní viewer.

---

## macOS Desktop Klient (Viewer) – Možnosti

### 6. Avalonia UI (Doporučeno pro desktop cross-platform)

**Avalonia** je open-source .NET UI framework – skutečný cross-platform nástupce WPF.

| | |
|---|---|
| **Platformy** | Windows, macOS, Linux (+ experimentálně iOS, Android, Web) |
| **Jazyk** | C# (plné sdílení kódu s existujícím projektem) |
| **UI** | XAML (syntakticky téměř identické s WPF) |
| **Effort** | **Nízký–střední** |

**Výhody:**
- **Nejbližší WPF ekvivalent** – existující XAML z `DeskShare.Desktop` lze migrovat s minimálními změnami
- Plné sdílení C# kódu: `DeskShare.Common`, `DeskShare.Core`, `WebSocketSignaler`
- macOS: nativní rendering přes CoreGraphics/Metal, správné chování okna, menu bar, dock ikona
- Linux: Funguje na X11 i Wayland
- Mature framework (v11+), aktivně vyvíjený, velká komunita
- NuGet: `Avalonia`, `Avalonia.Desktop` – jednoduchá integrace
- Podporuje HW akceleraci (Skia/Metal na macOS, Vulkan/OpenGL na Linux)

**Nevýhody:**
- Není oficiální Microsoft framework (ale má silnou komunitu a JetBrains backing)
- Drobné syntaktické rozdíly oproti WPF XAML (bindingová syntaxe, styly)
- Některé WPF-specifické features chybí (např. FlowDocument, XPS)

**Doporučená architektura:**
```
DeskShare.DesktopAvalonia (nový projekt)
├── App.axaml                  (Avalonia Application – nahrazuje App.xaml)
├── Views/
│   ├── MainWindow.axaml       (migrace z DeskShare.Desktop/MainWindow.xaml)
│   ├── ConnectionView.axaml   (připojení k serveru)
│   └── RemoteDesktopView.axaml (zobrazení vzdálené plochy + input)
├── ViewModels/                (MVVM – sdílené s WPF verzí)
├── DeskShare.Common           (existující – přímo reference)
├── DeskShare.Core             (existující – WebSocket signaling)
└── Platform/
    ├── macOS/                 (macOS-specifické: NSMenu, dock, permissions dialogy)
    └── Linux/                 (Linux-specifické: tray ikona, DBus integrace)
```

**macOS-specifické požadavky:**
- **Code Signing:** Aplikace musí být podepsána pro distribuci mimo App Store (Developer ID)
- **Notarization:** Apple vyžaduje notarizaci pro macOS 10.15+ (Gatekeeper)
- **App Sandbox:** Volitelné, ale doporučené pro App Store distribuce
- **Oprávnění (Entitlements):**
  - `com.apple.security.network.client` – síťový přístup (WebSocket/WebRTC)
  - Screen Recording permission – pokud viewer potřebuje sdílet vlastní obrazovku zpět
- **Distribution:** DMG/PKG installer nebo přes Mac App Store
- **Minimální verze:** macOS 10.15+ (Catalina) – .NET 8 requirement

---

### 7. MAUI pro macOS Desktop (Alternativa)

.NET MAUI podporuje i macOS (přes Mac Catalyst), takže mobilní MAUI klient (sekce 1) může běžet i na macOS desktopu.

| | |
|---|---|
| **Technologie** | .NET MAUI + Mac Catalyst |
| **Výhody** | Jeden codebase pro mobile i macOS desktop |
| **Nevýhody** | Mac Catalyst UI nerespektuje macOS design konvence tak dobře jako nativní AppKit; WPF migraci je těžší než u Avalonia |

---

## Možnosti multiplatformního řešení (Mobile)

### 1. .NET MAUI (Doporučeno)

**Microsoft .NET Multi-platform App UI** – oficiální nástupce Xamarin.Forms.

| | |
|---|---|
| **Platformy** | iOS, Android, Windows, macOS |
| **Jazyk** | C# (sdílení existujícího kódu z `DeskShare.Common` a `DeskShare.Core`) |
| **UI** | XAML (velmi podobné WPF – snadná migrace) |
| **WebRTC** | Přes binding na nativní knihovny (WebRTC.NET, nebo nativní WebRTC iOS/Android SDK přes Platform Invoke) |
| **Effort** | Střední |

**Výhody:**
- Maximální sdílení kódu – `DeskShare.Common` modely (Frame, VideoFrame, SignalingMessage, InputMessage) se použijí přímo
- `WebSocketSignaler` z `DeskShare.Core` funguje beze změn na všech platformách
- XAML syntaxe velmi podobná WPF → snadná migrace UI z `DeskShare.Desktop`
- Jeden codebase pro iOS + Android + Windows + macOS
- Nativní výkon, přístup ke platform-specific API (hardwarový dekodér videa)
- Serilog, DI, Configuration – vše funguje v MAUI beze změn

**Nevýhody:**
- WebRTC integrace vyžaduje nativní binding (SIPSorcery na mobilech má omezení)
- MAUI je stále relativně mladý framework, občas bugy
- Velikost aplikace (cca 30-50 MB kvůli .NET runtime)

**Doporučená architektura:**
```
DeskShare.Mobile (MAUI)
├── Shared UI (XAML)
├── DeskShare.Common (existující - přímo reference)
├── DeskShare.Core (existující - WebSocket signaling)
├── Platforms/
│   ├── iOS/     → AVFoundation pro video decode, nativní WebRTC
│   └── Android/ → MediaCodec pro video decode, nativní WebRTC
└── Services/
    ├── MobileWebRTCService (wrapper nad nativními WebRTC SDK)
    ├── TouchToMouseMapper (touch gesta → mouse eventy)
    └── MobileKeyboardService (virtuální klávesnice → InputMessage)
```

---

### 2. Vylepšený Web Client (PWA) – Nejrychlejší cesta

Existující `WebClient/` už funguje v prohlížečích včetně mobilních. Stačí ho rozšířit na **Progressive Web App**.

| | |
|---|---|
| **Platformy** | iOS Safari, Android Chrome, všechny prohlížeče |
| **Technologie** | HTML5, JavaScript, WebRTC (už existuje) |
| **Effort** | **Nízký** |

**Výhody:**
- **Už funguje** – `client.js` + `remote-control.js` obsahují WebRTC streaming i input control
- Žádná instalace z App/Play Store
- Minimální development – přidat Service Worker, manifest.json, touch optimalizaci
- WebRTC je nativně podporován v iOS Safari 11+ a Chrome Android

**Nevýhody:**
- iOS omezení: Safari je povinný engine → některé WebRTC funkce omezené
- Bez push notifikací na iOS (od iOS 16.4 jsou ale PWA push notifications podporovány)
- Nižší výkon než nativní aplikace (JS dekódování vs. HW dekodér)
- Nemůže běžet na pozadí / žádný background processing
- UX omezení (žádný přístup k nativním gestures, haptic feedback)

**Co je potřeba udělat:**
```
WebClient/
├── manifest.json          (PWA manifest - ikony, theme, display: standalone)
├── service-worker.js      (offline cache, background sync)
├── index.html             (responsive meta viewport, touch events)
├── client.js              (existující - doplnit touch gesture mapping)
├── remote-control.js      (existující - doplnit touch→mouse konverzi)
└── styles/
    └── mobile.css         (responsive layout, touch-friendly ovládání)
```

---

### 3. Flutter + WebRTC

| | |
|---|---|
| **Platformy** | iOS, Android, Web, Windows, macOS, Linux |
| **Jazyk** | Dart |
| **WebRTC** | `flutter_webrtc` (mature, well-maintained plugin) |
| **Effort** | Vysoký |

**Výhody:**
- Excelentní WebRTC podpora (`flutter_webrtc` je production-ready)
- Nativní výkon na obou platformách
- Velký ekosystém a komunita
- Jeden codebase pro všechny platformy včetně webu
- Hot reload → rychlý vývoj

**Nevýhody:**
- **Dart** – žádné sdílení kódu s existujícím C# codebase
- Signaling WebSocket klient je potřeba reimplementovat v Dartu
- Modely (SignalingMessage, InputMessage) je potřeba duplikovat
- Tým se musí naučit nový jazyk a framework
- Dva oddělené codebasy na údržbu

---

### 4. React Native + WebRTC

| | |
|---|---|
| **Platformy** | iOS, Android |
| **Jazyk** | TypeScript/JavaScript |
| **WebRTC** | `react-native-webrtc` |
| **Effort** | Střední-vysoký |

**Výhody:**
- Existující `WebClient/` JS kód lze částečně reusovat
- `react-native-webrtc` je mature knihovna
- Velká komunita, spousta dostupných vývojářů
- Blíže k webovému kódu než Flutter

**Nevýhody:**
- JavaScript bridge overhead → potenciálně nižší výkon pro video streaming
- Žádné sdílení C# kódu
- React Native má issues s video streaming performance na starších zařízeních
- Dva oddělené codebasy

---

### 5. Nativní iOS (Swift) + Nativní Android (Kotlin)

| | |
|---|---|
| **iOS** | Swift + WebRTC.framework (Google) |
| **Android** | Kotlin + WebRTC AAR (Google) |
| **Effort** | **Velmi vysoký** |

**Výhody:**
- Absolutně nejlepší výkon a UX
- Plný přístup k HW video dekodérům (VideoToolbox iOS, MediaCodec Android)
- Nejlepší integrace s OS (background modes, push, widgets)
- Nejlepší WebRTC podpora (Google official SDK)

**Nevýhody:**
- Dvojnásobný development a údržba (dva jazyky, dva codebasy)
- Žádné sdílení kódu s .NET
- Nejvyšší náklady a časová náročnost
- Potřeba expertů na Swift i Kotlin

---

## Srovnávací tabulka

| Kritérium | Avalonia (desktop) | MAUI | PWA | Flutter | React Native | Native |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| **Sdílení C# kódu** | ★★★★★ | ★★★★★ | ★☆☆☆☆ | ☆☆☆☆☆ | ★☆☆☆☆ | ☆☆☆☆☆ |
| **Výkon (video)** | ★★★★★ | ★★★★☆ | ★★★☆☆ | ★★★★☆ | ★★★☆☆ | ★★★★★ |
| **Rychlost vývoje** | ★★★★★ | ★★★★☆ | ★★★★★ | ★★★☆☆ | ★★★☆☆ | ★★☆☆☆ |
| **WebRTC podpora** | ★★★★☆ | ★★★☆☆ | ★★★★★ | ★★★★★ | ★★★★☆ | ★★★★★ |
| **UX kvalita** | ★★★★★ | ★★★★☆ | ★★★☆☆ | ★★★★★ | ★★★★☆ | ★★★★★ |
| **Náklady údržby** | ★★★★★ | ★★★★☆ | ★★★★★ | ★★★☆☆ | ★★★☆☆ | ★☆☆☆☆ |
| **App Store distribuce** | ★★★★☆ | ★★★★★ | ★★☆☆☆ | ★★★★★ | ★★★★★ | ★★★★★ |
| **macOS desktop UX** | ★★★★★ | ★★★☆☆ | ★★★☆☆ | ★★★★☆ | ☆☆☆☆☆ | ★★★★★ |
| **WPF migrace** | ★★★★★ | ★★★★☆ | ☆☆☆☆☆ | ☆☆☆☆☆ | ☆☆☆☆☆ | ☆☆☆☆☆ |

---

## Doporučení

### Fáze 1 – Okamžitě (nízký effort): **PWA**
Rozšířit existující `WebClient/` na PWA. Přidat touch optimalizaci, responsive design, Service Worker. Tím ihned pokryjete iOS i Android přes prohlížeč.

### Fáze 2a – Střednědobě (desktop): **Avalonia UI pro macOS/Linux**
Vytvořit cross-platform desktop viewer v Avalonia:
- Migrace XAML z `DeskShare.Desktop` (WPF) → `DeskShare.DesktopAvalonia` s minimálními změnami
- Plné sdílení `DeskShare.Common` + `DeskShare.Core`
- Jeden desktop codebase pro Windows + macOS + Linux
- macOS distribuce: podepsat Developer ID certifikátem, notarizovat přes `xcrun notarytool`
- Existující macOS platform kód (`src/Core/Platforms/macOS/`) se použije přímo

### Fáze 2b – Střednědobě (mobile): **.NET MAUI**
Vytvořit nativní mobilní klient v MAUI. Maximálně využijete:
- `DeskShare.Common` – všechny modely přímo
- `DeskShare.Core` – WebSocket signaling, konfiguraci
- Znalost C# a .NET v týmu
- Podobnost WPF ↔ MAUI XAML pro migraci UI

Pro WebRTC na mobilech doporučuji wrapper nad nativními Google WebRTC SDK přes MAUI Platform Invoke, protože SIPSorcery nemá plnou mobilní podporu.

### Alternativa pokud tým zná Dart/JS: **Flutter**
Pokud je v týmu zkušenost s Flutterem, `flutter_webrtc` je nejlepší WebRTC řešení pro mobile. Ale znamená to separátní codebase v Dartu.

---

## macOS Checklist (pro Fázi 2a)

- [ ] Vytvořit `DeskShare.DesktopAvalonia` projekt (Avalonia 11+)
- [ ] Migrovat MainWindow XAML z WPF → Avalonia AXAML
- [ ] Dokončit `ScreenCaptureKitCapturer` implementaci (Obj-C bridge nebo FFmpeg AVFoundation)
- [ ] Dokončit `CGWindowManager` implementaci
- [ ] Otestovat na macOS (ARM64 Apple Silicon + x64 Intel)
- [ ] Nastavit code signing (Developer ID Application certificate)
- [ ] Nastavit notarizaci (`xcrun notarytool submit`)
- [ ] Vytvořit DMG installer (nebo .app bundle)
- [ ] Otestovat macOS permissions flow (Screen Recording, Accessibility)
- [ ] CI/CD: Přidat macOS build do pipeline (GitHub Actions `macos-latest` runner)
