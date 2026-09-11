# Audit RemoteDesktopNet — nálezy + řešení

> Vygenerováno: 2026-06-11 (Claude Fable 5)
> Stav v době auditu: build OK, 347/347 testů zelených (324 unit + 23 integration).
> `appsettings.Production.json` má bezpečné hodnoty — kritické problémy jsou
> v defaultech, hot paths a duplicitách.

---

## 🔴 Kritické (opravit hned)

### 0. Chybí git repozitář
Složka není git repo. Žádná historie, žádný rollback, žádný CI.

**Řešení:** `git init`, initial commit, push na GitHub. Bez toho je každá další
změna hazard.

### 1. Timing-unsafe porovnání API klíče
**Kde:** `src/SignalingServer/Program.cs:629`

```csharp
if (string.IsNullOrEmpty(providedKey) || providedKey != diagnosticsApiKey)
    return Results.Unauthorized();
```

Porovnání `!=` umožňuje timing attack na diagnostics API klíč. Auth kód jinde
správně používá `CryptographicOperations.FixedTimeEquals` — tady ne.

**Řešení:** porovnat přes `CryptographicOperations.FixedTimeEquals` na UTF8
bytech obou hodnot. ~5 minut práce.

### 2. Nebezpečné defaulty v appsettings.json
**Kde:** `src/SignalingServer/appsettings.json:52-53`

```json
"AllowLegacyConnections": true,
"RequireHmacSignature": false
```

- `AllowLegacyConnections: true` = WebSocket připojení bez tokenu — útočníkovi
  stačí uhádnout/odposlechnout clientId (Program.cs:319-338).
- `RequireHmacSignature: false` = vypnutá replay ochrana (nonce se nevaliduje).

Production config to přepisuje správně, ale default = nebezpečí při deployi
s jiným než Production environmentem (Staging, zapomenutá env proměnná…).

**Řešení:** otočit defaulty na bezpečné hodnoty v base configu;
`appsettings.Development.json` ať si nebezpečné chování explicitně povolí.

### 3. Per-frame alokace v capture pipeline
**Kde:** `src/Core/Platforms/Windows/DesktopDuplicator.cs:209`
(+ duplikát `src/ScreenSenderApp/Capture/DesktopDuplicator.cs:209`)

```csharp
var data = new byte[dataSize]; // nová alokace každý frame
```

1080p @ 30 fps = ~250 MB/s alokací na Large Object Heap → GC stally
(100+ ms), frame dropy. Navíc `src/ScreenSenderApp/Conversion/PooledPixelConverter.cs:164`
rentne buffery z ArrayPoolu a pak výsledek stejně kopíruje do nových polí —
pooling tím ztrácí smysl (3 kopie na frame).

**Řešení:**
- Jeden reusable buffer per capturer instance (realokovat jen při změně rozlišení).
- `VideoFrame` ať vlastní pooled buffery a vrací je do poolu po encode
  (ownership flag / dispose pattern).

### 4. Masivní duplikace Core ↔ ScreenSenderApp — s driftem
Soubory existující v OBOU projektech a **už se rozjely**:

| Soubor | Stav |
|---|---|
| `Conversion/PixelConverter.cs` | liší se (203 vs 203 řádků, jiný obsah) |
| `Conversion/PooledPixelConverter.cs` | liší se (194 vs 200 řádků) |
| `Conversion/SimdPixelConverter.cs` | duplikát |
| `Capture/DesktopDuplicator.cs` | duplikát |
| `Input/WindowsInputController.cs` | duplikát |
| `Video/MultiClientVideoSource.cs` | liší se (405 vs 407) |
| `Video/SIPSorceryVideoSource.cs` | **KeyframeInterval 90 vs 5** (!) |
| `Video/StubVideoSource.cs` | duplikát |

Bugfix v jedné kopii druhou nespraví. ~2000+ řádků duplicit. Největší
dlouhodobé riziko údržby v celém repu.

**Řešení:** smazat kopie ze ScreenSenderApp, referencovat Core/Common.
Postup: nejdřív obalit testy, pak mazat. Rozdíl KeyframeInterval vyřešit
konfigurací, ne konstantou.

---

## 🟠 Vysoké

### 5. NonceCache hází výjimku při zaplnění (DoS)
**Kde:** `src/Core/Auth/NonceCache.cs:113-125`

Útočník floodem zaplní nonce cache → `InvalidOperationException` v auth path
→ legitimní klienti se nepřihlásí.

**Řešení:** vracet `false` místo throw, logovat IP pro rate-limit rozhodnutí.

### 6. CSP s unsafe-inline
**Kde:** `src/SignalingServer/Program.cs:212`

`script-src 'unsafe-inline'` + `style-src 'unsafe-inline'` neguje XSS ochranu CSP.

**Řešení:** přesunout inline skripty/styly do souborů, případně nonce-based CSP.

### 7. TURN test user testuser/testpass
**Kde:** `src/Turn/TurnServer.cs:35-39`, `appsettings.Development.json` má
`EnableTestUser: true`.

Config flag stačí k otevření relay pro kohokoliv (zneužitelné jako open proxy).

**Řešení:** smazat kód úplně. Minimálně: fail-fast při `EnableTestUser: true`
v Production env.

### 8. UI thread bloky ve viewerech
**Kde:** `src/DeskShare.Desktop/Windows/ProjectionWindow.xaml.cs:127-162`
(+ stejný pattern v `DeskShare.DesktopAvalonia/Windows/ProjectionWindow.axaml.cs`)

`await SendInputAsync(...)` přímo v mouse-move handleru. Při špatné síti UI
zamrzá 10–50 ms na každý pohyb myši.

**Řešení:** bounded `Channel<InputMessage>(100)` + background consumer task;
handlery jen zapisují do channelu.

### 9. WPF vs Avalonia: ~90 % duplikované business logiky
`MainWindow` i `ProjectionWindow` mají téměř identickou logiku v obou UI
frameworcích (connection management, saved connections, frame rendering,
input forwarding, clipboard polling).

**Řešení:** vytáhnout framework-agnostickou logiku do `DeskShare.Desktop.Shared`
(projekt existuje, ale je využitý jen částečně).

---

## 🟡 Střední

| # | Problém | Kde | Řešení |
|---|---|---|---|
| 10 | TURN spawnuje neomezeně tasků per packet → thread pool saturace | `src/Turn/TurnServer.cs:91` | `SemaphoreSlim` limit souběžného zpracování |
| 11 | STUN bez rate limitu = amplification/reflection vektor | `src/Stun/StunServer.cs` | per-source-IP rate limit |
| 12 | Diagnostické endpointy (`/statistics`, `/servers/status`) bez rate limitu | `src/SignalingServer/Program.cs:635+` | stejný limiter jako `/authenticate` |
| 13 | Broadcast posílá signaling zprávy všem klientům bez TargetId filtru | `src/SignalingServer/Services/ConnectionManager.cs:183-209` | vyžadovat TargetId pro SDP/ICE, broadcast jen pro service zprávy |
| 14 | Flaky testy: `Thread.Sleep(600)` ×6, sleep-based asserty | `AdaptiveBitrateControllerTests.cs`, `ConnectionManagerSignalingTests.cs:145,240` | fake clock abstrakce (`ITimeProvider`) |
| 15 | Integrační testy bindují pevné UDP porty 13478/13479 | `tests/IntegrationTests/Stun`, `Turn` | port 0 (auto-assign), číst přidělený port |
| 16 | Nulové testy: WebRTCSession, signaling routing (Program.cs endpointy), AzureTableSessionStorage, celý WebClient JS | `tests/` | priorita: e2e auth flow test (register → passkey → sign → authenticate) |
| 17 | Beta balíček `OpenTelemetry.Exporter.Prometheus.AspNetCore 1.10.0-beta.1` | `ScreenSenderApp.csproj` | pin stable verzi až vyjde |
| 18 | Hardcoded URL/porty `localhost:5151`, `localhost:9090` na 4+ místech | `ServerManager.cs:68,186`, `OnlineStatusMonitor.cs:20`, `ScreenSenderService.cs:448` | appsettings + `IConfiguration` |
| 19 | Verze mismatch `Serilog.Sinks.Console` 6.0.0 vs 6.1.1 | ScreenSenderApp vs Desktop | sjednotit na 6.1.1 |
| 20 | `async void` input handlery (12×) — výjimka může shodit UI thread | ProjectionWindow (WPF i Avalonia) | extrahovat `async Task` helpery, void wrapper jen try/catch |
| 21 | Passkey v `<input type="text">` — persistuje v autofill/history | `src/WebClient/index.html:166` | `type="password"`, clear po připojení |

---

## Doporučené pořadí prací

1. **Den 1:** git init + fix #1 (FixedTimeEquals) + #2 (defaulty) + #5
   (NonceCache return false) — malé změny, vysoký dopad.
2. **Týden 1:** #3 buffer pooling (největší perf zisk) + #7 TURN test user.
3. **Týden 2–3:** #4 konsolidace duplicit Core/ScreenSenderApp (nejdřív testy,
   pak mazat kopie) + #8 input channel + #9 sdílená viewer logika.
4. **Průběžně:** testy z tabulky — hlavně e2e auth flow (#16) a fix flaky
   testů (#14, #15).
