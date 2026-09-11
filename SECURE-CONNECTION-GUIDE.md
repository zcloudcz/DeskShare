# 🔒 Secure Connection Guide

## Přehled nového zabezpečeného připojení

Aplikace nyní používá **Server ID** systém pro bezpečné a cílené připojení mezi klienty a servery.

## 🎯 Jak to funguje

### 1. Server ID konfigurace

ScreenSenderApp má nyní **fixní Server ID** v konfiguraci (`appsettings.json`):

```json
{
  "Signaling": {
    "ServerUrl": "ws://localhost:5151/signal",
    "ServerId": "screen-sender-001"
  }
}
```

### 2. Flow připojení

```
┌─────────────┐                    ┌──────────────┐                    ┌─────────────┐
│ WebClient   │                    │  Signaling   │                    │ ScreenSender│
│             │                    │   Server     │                    │             │
└──────┬──────┘                    └──────┬───────┘                    └──────┬──────┘
       │                                  │                                   │
       │  1. Connect to signaling         │                                   │
       ├─────────────────────────────────►│                                   │
       │                                  │                                   │
       │  2. Identify (Client ID)         │                                   │
       │◄─────────────────────────────────┤                                   │
       │                                  │                                   │
       │                                  │  3. Connect with Server ID        │
       │                                  │◄──────────────────────────────────┤
       │                                  │                                   │
       │  4. ConnectionRequest            │                                   │
       │     (TargetId: screen-sender-001)│                                   │
       ├─────────────────────────────────►│───────────────────────────────────►
       │                                  │                                   │
       │                                  │  5. Offer (WebRTC SDP)            │
       │◄──────────────────────────────────────────────────────────────────────┤
       │                                  │                                   │
       │  6. Answer (WebRTC SDP)          │                                   │
       ├──────────────────────────────────────────────────────────────────────►
       │                                  │                                   │
       │  7. ICE Candidates exchange      │                                   │
       │◄─────────────────────────────────────────────────────────────────────►
       │                                  │                                   │
       │  8. WebRTC P2P Connection Established                                │
       │═══════════════════════════════════════════════════════════════════════►
```

## 📝 Krok za krokem

### Server (ScreenSenderApp)

1. **Konfigurace Server ID** v `src/ScreenSenderApp/appsettings.json`:
   ```json
   {
     "Signaling": {
       "ServerId": "screen-sender-001"  // <- Změň na vlastní unikátní ID
     }
   }
   ```

2. **Spuštění aplikace**:
   ```bash
   cd src/ScreenSenderApp
   dotnet run
   ```

3. **Start WebRTC Session**:
   - V menu zvol `[3] Start WebRTC Session`
   - Aplikace vypíše tvůj **Server ID**
   - Sdílej toto ID s klienty, kteří se mají připojit

### Klient (WebClient)

1. **Otevři** `src/WebClient/index.html` v prohlížeči

2. **Zadej Server ID**:
   - Do pole "Server ID" zadej ID serveru (např. `screen-sender-001`)
   - Zkontroluj "Signaling Server" URL (default: `ws://localhost:5151/signal`)

3. **Připoj se**:
   - Klikni na tlačítko **"Connect"**
   - WebClient automaticky pošle **ConnectionRequest** na server
   - Server odpoví **Offer** a začne streamovat

4. **Sleduj video**:
   - Po úspěšném připojení uvidíš živý stream obrazovky serveru

## 🔐 Bezpečnostní výhody

### ✅ Co to přináší:

1. **Cílené připojení**
   - Klient se připojuje pouze ke konkrétnímu serveru
   - Server reaguje pouze na požadavky určené pro jeho ID

2. **Vícenásobné servery**
   - Můžeš spustit více instancí ScreenSenderApp s různými Server ID
   - Každý klient se připojí pouze k serveru, který zadal

3. **Autorizace**
   - Server ID funguje jako "přístupový klíč"
   - Bez správného ID se klient nepřipojí

4. **Izolace**
   - Různé páry klient-server na stejném signaling serveru
   - Žádné interference mezi různými sezeními

## 🚀 Rychlé spuštění

### Pomocí BAT souboru:

```bash
start-demo.bat
```

Postupuj podle instrukcí na obrazovce:
1. Počkej na načtení obou konzolí
2. V ScreenSenderApp zmáčkni `[3]`
3. Zkopíruj zobrazené Server ID
4. V prohlížeči ověř Server ID a klikni "Connect"

## 🔧 Pokročilá konfigurace

### Více serverů najednou

**Server 1** (`appsettings.json`):
```json
{
  "Signaling": {
    "ServerId": "server-desktop-01",
    "ServerUrl": "ws://localhost:5151/signal"
  }
}
```

**Server 2** (jiná složka nebo PC):
```json
{
  "Signaling": {
    "ServerId": "server-laptop-02",
    "ServerUrl": "ws://localhost:5151/signal"
  }
}
```

Klienti pak zadají buď `server-desktop-01` nebo `server-laptop-02` podle toho, ke kterému se chtějí připojit.

### Vlastní Server ID

Server ID může být:
- ✅ Alfanumerické znaky, pomlčky, podtržítka
- ✅ Příklad: `my-server-123`, `office_pc`, `john-laptop`
- ❌ Nepoužívej mezery nebo speciální znaky

## 📊 Typy signaling zpráv

| Type | Název | Popis |
|------|-------|-------|
| 0 | Identify | Server přiřadí Client ID |
| 1 | Offer | WebRTC SDP offer |
| 2 | Answer | WebRTC SDP answer |
| 3 | IceCandidate | ICE kandidát pro NAT traversal |
| 4 | Error | Chybová zpráva |
| 5 | ConnectionRequest | **NOVÉ** - Požadavek na připojení ke konkrétnímu serveru |

## ❓ Troubleshooting

### Problém: "No video stream"

1. **Zkontroluj Server ID**:
   - Ujisti se, že ID v prohlížeči **přesně odpovídá** ID v konzoli ScreenSenderApp
   - Je case-sensitive!

2. **Zkontroluj WebRTC Session**:
   - V ScreenSenderApp musíš zmáčknout `[3]` před připojením klienta
   - Server musí být ve stavu "Waiting for WebClient to initiate connection..."

3. **Zkontroluj porty**:
   - SignalingServer: `5151`
   - Všechny komponenty musí používat stejný port

### Problém: "Connection failed"

1. **Restartuj všechno**:
   - Zavři všechna okna
   - Znovu spusť `start-demo.bat`

2. **Zkontroluj logy**:
   - V konzoli ScreenSenderApp se podívej na chybové hlášky
   - V prohlížeči otevři F12 → Console

## 🎉 Shrnutí

- ✅ **Server ID** v `appsettings.json`
- ✅ **WebClient** zadává Server ID
- ✅ **Automatické spojení** po odeslání ConnectionRequest
- ✅ **Bezpečnější** a **škálovatelnější** architektura
- ✅ **Podpora více serverů** současně

---

**Vytvořeno:** 2025-11-05
**Verze:** 2.0 (Secure Connection)
