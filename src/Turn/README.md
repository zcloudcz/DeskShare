# RemoteDesktop.Turn

**TURN (Traversal Using Relays around NAT) Server** implementace podle RFC 5766.

## 🎯 Účel

TURN server poskytuje **relay funkcionalitu** pro WebRTC klienty, kteří se nemohou spojit peer-to-peer kvůli restriktivním NAT nebo firewall. TURN je **fallback řešení**, když STUN a přímé P2P spojení selže.

## 📦 Funkce

- ✅ **RFC 5766 compliant** - Plná implementace TURN protokolu
- ✅ **Relay Allocations** - Alokace relay adres pro klienty
- ✅ **Long-term Credentials** - MD5+HMAC-SHA1 autentizace
- ✅ **Permissions** - Kontrola přístupu peer adres
- ✅ **Channel Binding** - Optimalizované relayování pro konkrétní peery
- ✅ **Lifetime Management** - Automatické expirování alokací
- ✅ **Background Service** - Integrace s .NET Hosted Service
- ✅ **Statistics API** - Real-time statistiky alokací a přenesených dat

## 🔧 Použití

### Jako standalone služba

```csharp
using RemoteDesktop.Turn;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices(services =>
{
    services.AddSingleton(new TurnServerOptions
    {
        Port = 3479,                    // TURN port (3478 typically used by STUN)
        Realm = "DeskShare",
        EnableTestUser = true,          // Pouze pro development!
        MinRelayPort = 49152,
        MaxRelayPort = 65535
    });
    services.AddHostedService<TurnServer>();
});

var host = builder.Build();

// Přidat uživatele pro autentizaci
var turnServer = host.Services.GetRequiredService<TurnServer>();
turnServer.AddUser("username", "password");

await host.RunAsync();
```

### Integrace s ASP.NET Core (jako v SignalingServeru)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add TURN server
builder.Services.AddSingleton(new TurnServerOptions
{
    Port = 3479,  // Different from STUN (3478)
    Realm = "DeskShare",
    EnableTestUser = false  // Vypnout v produkci!
});
builder.Services.AddHostedService<TurnServer>();

var app = builder.Build();
app.Run();
```

## 🔐 Autentizace

TURN používá **Long-term Credentials** mechanismus:

```
Key = MD5(username:realm:password)
MESSAGE-INTEGRITY = HMAC-SHA1(key, STUN message)
```

### Přidání uživatelů

```csharp
var turnServer = serviceProvider.GetService<TurnServer>();
turnServer.AddUser("alice", "secret123");
turnServer.AddUser("bob", "password456");
```

### Test uživatel (development only)

```csharp
var options = new TurnServerOptions
{
    EnableTestUser = true  // Vytvoří testuser/testpass
};
```

⚠️ **VAROVÁNÍ:** Test user zakázat v produkci!

## 📊 Statistiky

TURN server poskytuje detailní runtime statistiky:

```csharp
var turnServer = serviceProvider.GetService<TurnServer>();
var stats = turnServer.GetStatistics();

Console.WriteLine($"Requests Processed: {stats.RequestsProcessed}");
Console.WriteLine($"Allocations Created: {stats.AllocationsCreated}");
Console.WriteLine($"Active Allocations: {stats.ActiveAllocations}");
Console.WriteLine($"Total Bytes Relayed: {stats.TotalByteRelayed}");
Console.WriteLine($"Running: {stats.IsRunning}");
```

Nebo přes HTTP endpoint (v SignalingServeru):

```bash
curl http://localhost:5000/turn/statistics
```

Odpověď:
```json
{
  "status": "Running",
  "port": 3478,
  "requestsProcessed": 542,
  "allocationsCreated": 23,
  "activeAllocations": 5,
  "errorsEncountered": 2,
  "totalBytesRelayed": 15728640,
  "timestamp": "2025-11-08T17:30:00Z"
}
```

## 🔍 TURN Protokol

### Podporované Message Types

- `0x0001` - Binding Request (STUN compatibility)
- `0x0003` - Allocate Request
- `0x0004` - Refresh Request
- `0x0006` - Send Indication
- `0x0008` - CreatePermission Request
- `0x0009` - ChannelBind Request

### Alokace Lifecycle

1. **Allocate Request** - Klient požaduje relay adresu
2. **Allocate Response** - Server přidělí relay endpoint
3. **CreatePermission** - Klient povolí komunikaci s peer
4. **Send/Receive** - Data jsou relayována mezi klientem a peerem
5. **Refresh** - Klient obnoví životnost alokace (každých ~5 minut)
6. **Expiration** - Po 10 minutách bez refreshe je alokace smazána

### Permissions

- **Platnost:** 5 minut
- **Účel:** Kontrola, které peer adresy mohou komunikovat
- **Automatické obnovení:** CreatePermission lze volat opakovaně

### Channel Binding

- **Channel Numbers:** 0x4000-0x7FFF
- **Platnost:** 10 minut
- **Účel:** Optimalizované relayování bez STUN overhead

## 🌐 Konfigurace

### Programaticky

```csharp
var options = new TurnServerOptions
{
    Port = 3478,                    // UDP port (default 3478)
    Realm = "DeskShare",    // Autentizační realm
    EnableTestUser = false,         // Test user (development only)
    BindAddress = IPAddress.Any,    // Bind na všechna rozhraní
    MinRelayPort = 49152,           // Min port pro relay alokace
    MaxRelayPort = 65535            // Max port pro relay alokace
};
```

### appsettings.json

```json
{
  "Turn": {
    "Enabled": true,
    "Port": 3479,
    "Realm": "DeskShare",
    "EnableTestUser": false,
    "MinRelayPort": 49152,
    "MaxRelayPort": 65535
  }
}
```

**DŮLEŽITÉ:** TURN server je ve výchozím stavu **vypnutý** (`"Enabled": false`).

Když je TURN vypnutý, jsou dostupné pouze **P2P spojení** přes STUN. To je doporučené nastavení pro produkci, protože TURN server generuje vysokou zátěž bandwidth.

**Poznámka o portech:** TURN a STUN mohou sdílet stejný port (3478) v produkčním nasazení, ale vyžaduje to unified server implementaci. V této implementaci používáme **separate porty** (STUN: 3478, TURN: 3479) pro jednoduchost.

Pro development použijte `appsettings.Development.json`:
```json
{
  "Turn": {
    "Enabled": true,
    "EnableTestUser": true
  }
}
```

## 🧪 Testování

### Pomocí WebRTC v browseru

```javascript
const pc = new RTCPeerConnection({
  iceServers: [
    {
      urls: 'turn:localhost:3478',
      username: 'testuser',
      credential: 'testpass'
    }
  ]
});

pc.onicecandidate = (event) => {
  if (event.candidate) {
    console.log('ICE candidate:', event.candidate);
    if (event.candidate.type === 'relay') {
      console.log('✅ TURN relay candidate získán!');
    }
  }
};

// Trigger ICE gathering
pc.createDataChannel('test');
const offer = await pc.createOffer();
await pc.setLocalDescription(offer);
```

### Pomocí turnutils_uclient (coturn tools)

```bash
# Install coturn utilities
sudo apt-get install coturn

# Test TURN allocation
turnutils_uclient -v -u testuser -w testpass localhost
```

## 🏗️ Architektura

### Komponenty

```
TurnServer.cs          - Hlavní TURN server (BackgroundService)
├── TurnAuthenticator.cs   - Long-term credentials autentizace
├── RelayEngine.cs         - Správa relay socketů a forwarding
└── TurnAllocation.cs      - Reprezentace jedné alokace

TurnServerOptions.cs   - Konfigurace serveru
TurnServerStatistics.cs - Runtime statistiky
```

### Flow: Allocate Request

```
1. Client → TURN: Allocate Request (USERNAME, REALM, MESSAGE-INTEGRITY)
2. TURN validates credentials (MD5 + HMAC-SHA1)
3. TURN creates relay socket on random port
4. TURN creates TurnAllocation object
5. TURN starts relay listener task
6. TURN → Client: Allocate Response (XOR-RELAYED-ADDRESS, LIFETIME)
```

### Flow: Data Relay

```
Client → TURN (Send Indication):
  - TURN checks permission for peer address
  - TURN forwards data to peer via relay socket

Peer → TURN (UDP packet):
  - TURN receives on relay socket
  - TURN checks permission
  - TURN forwards to client (wrapped in Data Indication)
```

## 🔐 Bezpečnost

- ✅ **Long-term credentials** - MD5 + HMAC-SHA1 autentizace
- ✅ **Permission system** - Jen povolené peer adresy mohou komunikovat
- ✅ **Allocation expiration** - Automatické uvolnění po 10 minutách
- ⚠️ **Disable test user in production** - `EnableTestUser = false`
- ⚠️ **Rate limiting** - Doporučeno implementovat na firewall úrovni
- ⚠️ **Bandwidth limiting** - V produkci implementovat QoS

## 📚 Reference

- [RFC 5766](https://tools.ietf.org/html/rfc5766) - Traversal Using Relays around NAT (TURN)
- [RFC 5389](https://tools.ietf.org/html/rfc5389) - Session Traversal Utilities for NAT (STUN)
- [RFC 6062](https://tools.ietf.org/html/rfc6062) - TURN Extensions for TCP Allocations
- [WebRTC TURN](https://developer.mozilla.org/en-US/docs/Web/API/RTCPeerConnection/RTCPeerConnection#using_a_turn_server)

## 🆚 STUN vs TURN

| Feature | STUN | TURN |
|---------|------|------|
| Účel | Zjistit veřejnou IP/port | Relayovat data přes server |
| Bandwidth | Nízká (jen discovery) | Vysoká (veškerá data) |
| Autentizace | Ne (basic STUN) | Ano (long-term credentials) |
| NAT traversal | ✅ Symmetric NAT selže | ✅ Funguje vždy |
| Náklady | Nízké | Vysoké (bandwidth) |
| Privacy | Lepší (P2P) | Horší (data přes server) |

**Doporučení:** Vždy použít STUN jako primární, TURN jako fallback.

## 🛠️ Development

### Build

```bash
dotnet build src/Turn/RemoteDesktop.Turn.csproj
```

### Run Tests

```bash
dotnet test tests/Turn.Tests/
```

## 🚧 TODO / Budoucí Vylepšení

- [ ] TCP relay support (RFC 6062)
- [ ] TLS/DTLS support
- [ ] Bandwidth limiting per allocation
- [ ] Geographic load balancing
- [ ] Persistent user storage (database)
- [ ] Admin API (list allocations, kick users, etc.)
- [ ] Prometheus metrics export
- [ ] IPv6 support

## 📝 Licence

MIT License - Part of DeskShare project

---

**Version:** 1.0.0
**Last Updated:** 2025-11-08
**Status:** ✅ Production Ready (with caveats - see Security section)
