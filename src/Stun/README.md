# RemoteDesktop.Stun

**STUN (Session Traversal Utilities for NAT) Server** implementace podle RFC 5389.

## 🎯 Účel

STUN server umožňuje WebRTC klientům **zjistit jejich veřejnou IP adresu a port** za NAT (Network Address Translation). Toto je první krok v procesu ICE (Interactive Connectivity Establishment) pro navázání P2P spojení.

## 📦 Funkce

- ✅ **RFC 5389 compliant** - Plná implementace STUN protokolu
- ✅ **Binding Request/Response** - Základní STUN funkčnost
- ✅ **XOR-MAPPED-ADDRESS** - Obfuskovaná veřejná adresa (proti packet sniffing)
- ✅ **IPv4 a IPv6** - Podpora obou protokolů
- ✅ **Background Service** - Integrace s .NET Hosted Service
- ✅ **Statistics API** - Real-time statistiky požadavků

## ⚠️ Port Configuration

STUN server používá **port 3478** (RFC 5389 standard).

**Poznámka:** V této implementaci běží STUN a TURN na **separátních portech** (STUN: 3478, TURN: 3479). V produkčním nasazení mohou STUN a TURN sdílet stejný port, ale vyžaduje to unified server implementaci.

## 🔧 Použití

### Jako standalone služba

```csharp
using RemoteDesktop.Stun;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices(services =>
{
    services.AddSingleton(new StunServerOptions
    {
        Port = 3478  // Standard STUN port
    });
    services.AddHostedService<StunServer>();
});

var host = builder.Build();
await host.RunAsync();
```

### Integrace s ASP.NET Core (jako v SignalingServeru)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add STUN server
builder.Services.AddSingleton(new StunServerOptions
{
    Port = 3478
});
builder.Services.AddHostedService<StunServer>();

var app = builder.Build();
app.Run();
```

## 📊 Statistiky

STUN server poskytuje runtime statistiky:

```csharp
var stunServer = serviceProvider.GetService<StunServer>();
var stats = stunServer.GetStatistics();

Console.WriteLine($"Requests Processed: {stats.RequestsProcessed}");
Console.WriteLine($"Responses Sent: {stats.ResponsesSent}");
Console.WriteLine($"Errors: {stats.ErrorsEncountered}");
Console.WriteLine($"Running: {stats.IsRunning}");
```

Nebo přes HTTP endpoint (v SignalingServeru):

```bash
curl http://localhost:5000/stun/statistics
```

Odpověď:
```json
{
  "status": "Running",
  "port": 3478,
  "requestsProcessed": 1523,
  "responsesSent": 1520,
  "errorsEncountered": 3,
  "timestamp": "2025-11-08T17:30:00Z"
}
```

## 🔍 STUN Protokol

### Message Format

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|0 0|     STUN Message Type     |         Message Length        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                         Magic Cookie (0x2112A442)             |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
|                     Transaction ID (96 bits)                  |
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Supported Message Types

- `0x0001` - Binding Request
- `0x0101` - Binding Response
- `0x0111` - Binding Error Response

### Supported Attributes

- `0x0020` - XOR-MAPPED-ADDRESS (veřejná IP:port klienta)
- `0x8022` - SOFTWARE (identifikace serveru)

## 🌐 Konfigurace

```csharp
var options = new StunServerOptions
{
    Port = 3478,                    // UDP port (default 3478)
    BindAddress = IPAddress.Any,    // Bind na všechna rozhraní
    MaxConcurrentRequests = 1000    // Max současných požadavků
};
```

### appsettings.json

```json
{
  "Stun": {
    "Port": 3478
  }
}
```

## 🧪 Testování

### Pomocí `stunclient` (Linux/Mac)

```bash
# Install stun client
sudo apt-get install stuntman-client  # Ubuntu/Debian
brew install stuntman                  # macOS

# Test STUN server
stunclient localhost 3478
```

Výstup:
```
Binding test: success
Local address: 192.168.1.100:54321
Mapped address: 203.0.113.50:54321
```

### Pomocí WebRTC v browseru

```javascript
const pc = new RTCPeerConnection({
  iceServers: [
    { urls: 'stun:localhost:3478' }
  ]
});

pc.onicecandidate = (event) => {
  if (event.candidate) {
    console.log('ICE candidate:', event.candidate);
  }
};

// Trigger ICE gathering
pc.createDataChannel('test');
const offer = await pc.createOffer();
await pc.setLocalDescription(offer);
```

## 🔐 Bezpečnost

- **UDP only** - STUN používá pouze UDP (ne TCP)
- **No authentication** - Basic STUN nemá autentizaci (na rozdíl od TURN)
- **Rate limiting** - Doporučeno implementovat na firewall úrovni
- **XOR obfuskace** - IP adresy jsou XOR-ovány pro prevenci packet inspection

## 📚 Reference

- [RFC 5389](https://tools.ietf.org/html/rfc5389) - Session Traversal Utilities for NAT (STUN)
- [RFC 5780](https://tools.ietf.org/html/rfc5780) - NAT Behavior Discovery Using STUN
- [WebRTC ICE](https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Protocols) - Interactive Connectivity Establishment

## 🛠️ Development

### Build

```bash
dotnet build src/Stun/RemoteDesktop.Stun.csproj
```

### Run Tests

```bash
dotnet test tests/Stun.Tests/
```

## 📝 Licence

MIT License - Part of DeskShare project

---

**Version:** 1.0.0
**Last Updated:** 2025-11-08
**Status:** ✅ Production Ready
