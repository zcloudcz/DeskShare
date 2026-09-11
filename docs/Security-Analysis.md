# Bezpečnostní analýza DeskShare

## Současný bezpečnostní model

### ✅ Co je již implementováno:

1. **Passkey autentizace (45 sekund TTL)**
   - Generována z `ServerId` (MAC adresa) + timestamp
   - SHA256 hash pro nepředvídatelnost
   - Krátká životnost (45s) omezuje útočné okno

2. **Server ID z MAC adresy**
   - Stabilní identifikátor (nemění se při restartu)
   - Unikátní pro každý stroj
   - Použito jako seed pro passkey

3. **Rate limiting**
   - 120 vstupů/sekundu
   - Ochrana proti flooding útokům

4. **Input validace**
   - Kontrola rozsahů souřadnic (0.0-1.0)
   - Validace key codes
   - Ochrana proti injection

5. **Session timeout**
   - 30 minut nečinnosti
   - Auto-revokace autorizace

## 🔴 Bezpečnostní problémy

### Problém 1: **MAC adresa NENÍ bezpečný identifikátor**

**Proč MAC adresa NEpomůže:**

```
❌ MÝTUS: "MAC adresa je tajná a lze ji použít pro autentizaci"

✅ REALITA: MAC adresa je VEŘEJNÁ a lze ji snadno zjistit:

1. ARP Request (lokální síť):
   $ arp -a
   Interface: 192.168.1.100
     Internet Address      Physical Address      Type
     192.168.1.1          00-11-22-33-44-55     dynamic  ← MAC adresa routeru
     192.168.1.50         AA-BB-CC-DD-EE-FF     dynamic  ← MAC serveru

2. DHCP Logs (správce sítě):
   DHCP Server vidí všechny MAC adresy při přidělování IP

3. Network Sniffing (útočník ve stejné síti):
   Wireshark, tcpdump → okamžitě vidí všechny MAC adresy

4. WebRTC ICE Candidates:
   Obsahují lokální IP adresy, které lze mapovat na MAC adresy

5. JavaScript API (browser):
   navigator.mediaDevices.enumerateDevices() může odhalit device IDs
```

### Problém 2: **Replay útoky**

**Scénář útoku:**

```
1. Útočník odposlouchává síť (WiFi sniffing, ARP spoofing)
2. Zachytí WebSocket komunikaci mezi klientem a serverem
3. Zkopíruje platné zprávy (včetně passkey)
4. Přehraje je ve vlastním spojení

Příklad zachycené zprávy:
{
  "type": "authenticate",
  "serverId": "AABBCCDDEEFF",  ← Zjištěno z ARP
  "passkey": "A3F7K9M2P",      ← Zachyceno z komunikace
  "clientId": "112233445566"    ← MAC útočníka
}

5. SignalingServer přijme zprávu → úspěšná autentizace!
```

### Problém 3: **Man-in-the-Middle (MITM)**

**Současný stav:**

```
┌────────┐    ws://         ┌───────────┐    ws://      ┌────────┐
│ Client │◄─────────────────┤ Attacker  │◄──────────────┤ Server │
└────────┘  nezašifrováno   │  Proxy    │ nezašifrováno└────────┘
                             └───────────┘
                                   ↓
                        Útočník vidí VŠE:
                        - Server ID
                        - Passkey
                        - ICE candidates
                        - SDP offers/answers
```

### Problém 4: **Session Hijacking**

**Útočník může:**

```
1. Zachytit ClientId legitimního klienta
2. Vytvořit vlastní WebSocket spojení se stejným ClientId
3. SignalingServer nerozliší, kdo je skutečný klient
4. Útočník přebírá kontrolu nad sesssion
```

### Problém 5: **DoS útoky na SignalingServer**

```
for i in {1..10000}; do
  curl -X POST http://signaling.com/register \
    -d '{"serverId":"FAKE", "passkey":"FAKE123"}'
done

→ SignalingServer zahlcen falešnými registracemi
→ Legitimní servery se nemohou registrovat
```

## 🛡️ Doporučená vylepšení

### Úroveň 1: **OKAMŽITĚ IMPLEMENTOVAT** (kritické)

#### 1.1 WSS (WebSocket Secure) - HTTPS/TLS

**Implementace:**

```bash
# Získání certifikátu (Let's Encrypt)
sudo certbot certonly --standalone -d signaling.yourserver.com

# Konfigurace Kestrel (SignalingServer/appsettings.json)
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:443",
        "Certificate": {
          "Path": "/etc/letsencrypt/live/signaling.yourserver.com/fullchain.pem",
          "KeyPath": "/etc/letsencrypt/live/signaling.yourserver.com/privkey.pem"
        }
      }
    }
  }
}
```

**Výhody:**
- ✅ Šifrování veškeré komunikace
- ✅ Ochrana proti odposlouchávání
- ✅ Ochrana proti MITM útokům
- ✅ Browser automaticky vyžaduje WSS pro WebRTC

**Nutnost:** ⚠️ **KRITICKÁ** - bez TLS je aplikace NEBEZPEČNÁ

---

#### 1.2 HMAC Request Signing

**Problém MAC adresy vyřešen pomocí HMAC:**

```csharp
// src/Core/Auth/RequestSigningService.cs

public sealed class RequestSigningService
{
    private const int SignatureValiditySeconds = 60;

    /// <summary>
    /// Vytvoří digitální podpis requestu pomocí HMAC-SHA256.
    /// </summary>
    /// <param name="serverId">Server ID (public)</param>
    /// <param name="passkey">Passkey (secret - sdílený po zobrazení na serveru)</param>
    /// <param name="timestamp">Timestamp requestu</param>
    /// <param name="nonce">Jedinečný identifikátor requestu (ochrana proti replay)</param>
    /// <returns>HMAC signature</returns>
    public static string SignRequest(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce)
    {
        // Kombinace dat pro podepsání
        string message = $"{serverId}|{timestamp:O}|{nonce}";

        // HMAC-SHA256 s passkey jako klíčem
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(passkey));
        byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));

        // Base64 pro přenos
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Ověří platnost podpisu requestu.
    /// </summary>
    public static bool ValidateSignature(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce,
        string signature)
    {
        // Kontrola časové validity (max 60 sekund starý)
        if (Math.Abs((DateTime.UtcNow - timestamp).TotalSeconds) > SignatureValiditySeconds)
        {
            return false;
        }

        // Přepočítat očekávaný podpis
        string expectedSignature = SignRequest(serverId, passkey, timestamp, nonce);

        // Konstantní časové porovnání (ochrana proti timing attacks)
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(expectedSignature),
            Convert.FromBase64String(signature));
    }
}
```

**Použití v klientovi:**

```csharp
// RemoteDesktop.Desktop/Services/ClientManager.cs

public async Task<bool> AuthenticateAsync(string serverId, string passkey)
{
    var nonce = Guid.NewGuid().ToString(); // Jedinečný ID pro tento request
    var timestamp = DateTime.UtcNow;

    var signature = RequestSigningService.SignRequest(
        serverId, passkey, timestamp, nonce);

    var authRequest = new ClientAuthenticationMessage
    {
        ServerId = serverId,
        Passkey = passkey,  // Stále posíláme pro zpětnou kompatibilitu
        ClientId = _clientId,
        Timestamp = timestamp,
        Nonce = nonce,
        Signature = signature  // ← Nový field
    };

    // Poslat na SignalingServer
    var response = await _httpClient.PostAsJsonAsync("/authenticate", authRequest);
    // ...
}
```

**Ověření na serveru:**

```csharp
// SignalingServer/Services/ConnectionManager.cs

public ClientAuthenticationResponse AuthenticateClient(ClientAuthenticationMessage auth)
{
    // 1. Kontrola signature PŘED kontrolou passkey
    if (!string.IsNullOrEmpty(auth.Signature))
    {
        if (!RequestSigningService.ValidateSignature(
            auth.ServerId,
            auth.Passkey,
            auth.Timestamp,
            auth.Nonce,
            auth.Signature))
        {
            _logger.LogWarning("Authentication failed - invalid signature | ServerId: {ServerId}",
                auth.ServerId);

            return new ClientAuthenticationResponse
            {
                Success = false,
                ErrorMessage = "Invalid request signature"
            };
        }
    }

    // 2. Kontrola nonce (ochrana proti replay)
    if (_usedNonces.Contains(auth.Nonce))
    {
        _logger.LogWarning("Replay attack detected - nonce already used | ServerId: {ServerId}",
            auth.ServerId);

        return new ClientAuthenticationResponse
        {
            Success = false,
            ErrorMessage = "Request already processed"
        };
    }

    // Uložit nonce (s TTL 60 sekund)
    _usedNonces.Add(auth.Nonce, DateTime.UtcNow.AddSeconds(60));

    // 3. Pokračovat s normální validací passkey
    // ...
}

// Nonce cache s automatickým vypršením
private readonly ConcurrentDictionary<string, DateTime> _usedNonces = new();
```

**Výhody HMAC:**
- ✅ Ochrana proti replay útokům (nonce)
- ✅ Ochrana proti tampering (signature)
- ✅ Časová validita (timestamp)
- ✅ Passkey slouží jako shared secret
- ✅ Žádná potřeba ukládat hesla na serveru

---

#### 1.3 Connection Fingerprinting

**Místo MAC adresy použít TLS Certificate Pinning:**

```csharp
// src/Core/Auth/DeviceFingerprintService.cs

public sealed class DeviceFingerprintService
{
    /// <summary>
    /// Generuje stabilní fingerprint zařízení bez použití MAC adresy.
    /// </summary>
    public static string GenerateDeviceFingerprint()
    {
        var components = new List<string>
        {
            // 1. Machine GUID (Windows Registry - stabilní)
            GetMachineGuid(),

            // 2. CPU ID
            GetProcessorId(),

            // 3. Motherboard Serial
            GetMotherboardSerial(),

            // 4. BIOS Serial
            GetBiosSerial(),

            // 5. System Drive Serial
            GetSystemDriveSerial()
        };

        // Hash kombinace
        var combined = string.Join("|", components.Where(c => !string.IsNullOrEmpty(c)));
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));

        // Base64 URL-safe encoding
        return Convert.ToBase64String(hashBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string GetMachineGuid()
    {
        if (OperatingSystem.IsWindows())
        {
            // HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography\MachineGuid
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
        }
        else if (OperatingSystem.IsLinux())
        {
            // /etc/machine-id nebo /var/lib/dbus/machine-id
            if (File.Exists("/etc/machine-id"))
                return File.ReadAllText("/etc/machine-id").Trim();
        }
        else if (OperatingSystem.IsMacOS())
        {
            // IOPlatformUUID
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ioreg",
                    Arguments = "-rd1 -c IOPlatformExpertDevice",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            // Parse IOPlatformUUID from output
        }

        return string.Empty;
    }

    // Další metody pro CPU ID, Motherboard Serial, atd.
    // (WMI queries na Windows, dmidecode na Linux, ioreg na macOS)
}
```

**Výhody:**
- ✅ Stabilnější než MAC adresa (nemění se při změně síťovky)
- ✅ Těžší na podvržení (potřeba znát více HW parametrů)
- ✅ Multiplatformní (Windows/Linux/macOS)

**Nevýhody:**
- ⚠️ Stále lze obejít (virtuální stroje, HW emulace)
- ⚠️ Mění se při reinstalaci OS nebo výměně HW

---

### Úroveň 2: **DOPORUČENO** (silná bezpečnost)

#### 2.1 TLS Client Certificates (mTLS)

**Nejsilnější autentizace:**

```
┌────────┐              ┌──────────────┐
│ Client │              │    Server    │
└────┬───┘              └──────┬───────┘
     │                         │
     │ 1. TLS Handshake        │
     ├────────────────────────►│
     │                         │
     │ 2. Server Certificate   │
     │◄────────────────────────┤
     │    (verify server)      │
     │                         │
     │ 3. Client Certificate   │
     ├────────────────────────►│
     │    (verify client)      │
     │                         │
     │ 4. Mutual Auth OK       │
     │◄───────────────────────►│
     │                         │
```

**Generování klientských certifikátů:**

```bash
# 1. Vytvoření CA (Certificate Authority)
openssl genrsa -out ca.key 4096
openssl req -new -x509 -days 3650 -key ca.key -out ca.crt \
  -subj "/CN=RemoteDesktop CA"

# 2. Vytvoření klientského certifikátu
openssl genrsa -out client.key 2048
openssl req -new -key client.key -out client.csr \
  -subj "/CN=Client-$CLIENT_ID"

# 3. Podepsání CA
openssl x509 -req -in client.csr -CA ca.crt -CAkey ca.key \
  -CAcreateserial -out client.crt -days 365

# 4. Export do PKCS12 (pro Windows/Browser)
openssl pkcs12 -export -out client.pfx \
  -inkey client.key -in client.crt -certfile ca.crt
```

**Konfigurace SignalingServer:**

```csharp
// Program.cs
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureHttpsDefaults(httpsOptions =>
    {
        httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        httpsOptions.CheckCertificateRevocation = true;

        // Validace klientského certifikátu
        httpsOptions.ClientCertificateValidation = (cert, chain, errors) =>
        {
            if (errors != SslPolicyErrors.None)
                return false;

            // Kontrola, že certifikát je vydán naší CA
            var issuer = cert.Issuer;
            if (!issuer.Contains("RemoteDesktop CA"))
                return false;

            // Kontrola expirace
            if (cert.NotAfter < DateTime.Now)
                return false;

            return true;
        };
    });
});
```

**Použití v klientovi:**

```csharp
// Load client certificate
var clientCert = new X509Certificate2("client.pfx", "password");

var handler = new HttpClientHandler();
handler.ClientCertificates.Add(clientCert);

var httpClient = new HttpClient(handler)
{
    BaseAddress = new Uri("https://signaling.yourserver.com")
};

// Nyní všechny requesty jsou autentizovány certifikátem
```

**Výhody:**
- ✅ Nejsilnější autentizace (standardní v banking, enterprise)
- ✅ Certifikát = klíč k serveru (nelze uhodnout)
- ✅ Revokace certifikátů (CRL, OCSP)
- ✅ Browser nativní podpora

**Nevýhody:**
- ⚠️ Složitější deployment (distribuce certifikátů)
- ⚠️ Potřeba PKI infrastruktury (CA management)
- ⚠️ User experience (import certifikátu do browseru)

---

#### 2.2 JWT Tokens pro Session Management

**Místo plaintext ClientId používat JWT:**

```csharp
// src/Core/Auth/TokenService.cs

public sealed class TokenService
{
    private readonly string _jwtSecret;
    private readonly string _issuer = "RemoteDesktop.SignalingServer";

    public string GenerateSessionToken(string clientId, string serverId, TimeSpan validity)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_jwtSecret);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("clientId", clientId),
                new Claim("serverId", serverId),
                new Claim("sessionId", Guid.NewGuid().ToString()),
                new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            }),
            Expires = DateTime.UtcNow.Add(validity),
            Issuer = _issuer,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_jwtSecret);

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero
            }, out _);

            return principal;
        }
        catch
        {
            return null;
        }
    }
}
```

**Workflow:**

```
1. Client → Server: authenticate (passkey)
2. Server validates passkey
3. Server generates JWT token
4. Server → Client: token
5. Client stores token
6. All subsequent requests: Authorization: Bearer <token>
7. Server validates token (no need to check passkey again)
```

**Výhody:**
- ✅ Stateless (server nemusí ukládat session state)
- ✅ Self-contained (token obsahuje všechny claims)
- ✅ Standard (RFC 7519)
- ✅ Expiration built-in

---

### Úroveň 3: **ENTERPRISE** (maximální bezpečnost)

#### 3.1 Multi-Factor Authentication (MFA)

```csharp
// 1. Passkey (something you have - displayed on server)
// 2. TOTP Code (time-based one-time password - Google Authenticator)

public async Task<bool> AuthenticateWithMFA(
    string serverId,
    string passkey,
    string totpCode)
{
    // Validate passkey
    if (!ValidatePasskey(serverId, passkey, DateTime.UtcNow))
        return false;

    // Validate TOTP
    var totp = new Totp(GetTotpSecret(serverId));
    if (!totp.VerifyTotp(totpCode, out _, VerificationWindow.RfcSpecifiedNetworkDelay))
        return false;

    return true;
}
```

#### 3.2 IP Whitelisting

```csharp
// appsettings.json
{
  "Security": {
    "AllowedIpRanges": [
      "192.168.1.0/24",    // Local network
      "10.0.0.0/8",        // VPN
      "203.0.113.0/24"     // Office IP range
    ]
  }
}

// Middleware
app.Use(async (context, next) =>
{
    var remoteIp = context.Connection.RemoteIpAddress;

    if (!IsIpAllowed(remoteIp))
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Access denied - IP not whitelisted");
        return;
    }

    await next();
});
```

#### 3.3 Hardware Security Keys (FIDO2/WebAuthn)

```javascript
// Browser podporuje WebAuthn
const credential = await navigator.credentials.create({
    publicKey: {
        challenge: new Uint8Array(32), // From server
        rp: { name: "RemoteDesktop" },
        user: {
            id: new Uint8Array(16),
            name: clientId,
            displayName: "Client Device"
        },
        pubKeyCredParams: [{ alg: -7, type: "public-key" }],
        authenticatorSelection: {
            authenticatorAttachment: "platform", // TouchID, Windows Hello
            userVerification: "required"
        }
    }
});

// Send credential.response to server for verification
```

---

## 📊 Porovnání bezpečnostních úrovní

| Metoda | Úroveň | Ochrana proti | Složitost | Cena |
|--------|--------|---------------|-----------|------|
| **Passkey only** | 🔴 Nízká | - | Snadné | €0 |
| **+ WSS/TLS** | 🟡 Střední | MITM, Sniffing | Střední | €0-50/rok |
| **+ HMAC Signing** | 🟢 Dobrá | Replay, Tampering | Střední | €0 |
| **+ Device Fingerprint** | 🟢 Dobrá | MAC spoofing | Střední | €0 |
| **+ Client Certificates** | 🔵 Velmi dobrá | Session hijacking | Vyšší | €0-200/rok |
| **+ JWT Tokens** | 🔵 Velmi dobrá | Session replay | Střední | €0 |
| **+ MFA (TOTP)** | 🟣 Excelentní | Credential theft | Vyšší | €0 |
| **+ IP Whitelist** | 🟣 Excelentní | Remote attacks | Nízká | €0 |
| **+ FIDO2/WebAuthn** | 🟣 Nejvyšší | All attacks | Vysoká | €20-50/key |

---

## ✅ Doporučená implementace (krok za krokem)

### Fáze 1: KRITICKÉ (implementovat OKAMŽITĚ)

1. ✅ **WSS/TLS** - Šifrování veškeré komunikace
   - Získat Let's Encrypt certifikát
   - Nakonfigurovat Kestrel pro HTTPS
   - Změnit všechny URL z `ws://` na `wss://`

2. ✅ **HMAC Request Signing** - Ochrana proti replay
   - Implementovat `RequestSigningService`
   - Přidat `Signature`, `Timestamp`, `Nonce` do `ClientAuthenticationMessage`
   - Validovat na serveru před autorizací

3. ✅ **Nonce Cache** - Prevence replay útoků
   - Ukládat použité nonce s TTL 60s
   - Zamítnout duplicitní requesty

### Fáze 2: DOPORUČENÉ (během měsíce)

4. ✅ **Device Fingerprint** - Lepší identifikace zařízení
   - Nahradit MAC adresu kombinací HW identifikátorů
   - Multiplatformní implementace

5. ✅ **Rate Limiting na SignalingServer**
   - Limity na `/register`, `/authenticate` endpointy
   - IP-based throttling (max 10 pokusů/minutu)

6. ✅ **Audit Logging**
   - Logovat všechny autentizační pokusy
   - Ukládat IP adresy, timestamps, výsledky
   - Retention 90 dní

### Fáze 3: PRODUKCE (před nasazením)

7. ✅ **Client Certificates (volitelné)**
   - Pro high-security prostředí
   - Corporate deployment

8. ✅ **JWT Tokens**
   - Session management
   - Stateless autentizace

9. ✅ **Monitoring & Alerting**
   - Neúspěšné autentizace > 5/hod → alert
   - Nonce replay detekce → block IP
   - Rate limit překročení → alert

---

## 🎯 Závěr

### Odpověď na vaši otázku:

> **"Pomůže přidat MAC adresu do requestu?"**

**❌ NE** - MAC adresa:
- Je veřejná (viditelná v síti)
- Lze snadno zjistit (ARP, DHCP)
- Lze podvrhnout (MAC spoofing)
- Nepřidává žádnou bezpečnost

### ✅ CO SKUTEČNĚ POMŮŽE:

1. **WSS/TLS** (nejvyšší priorita) - šifrování
2. **HMAC Signing** - ochrana integrity
3. **Nonce** - ochrana proti replay
4. **Device Fingerprint** - lepší identifikace (ale ne primární autentizace)
5. **Client Certificates** - pro enterprise použití

### 🚀 Začněte s:

```bash
# 1. Nainstalovat certbot
sudo snap install certbot --classic

# 2. Získat certifikát
sudo certbot certonly --standalone -d signaling.yourserver.com

# 3. Nakonfigurovat appsettings.json
# 4. Změnit ws:// na wss://
# 5. Implementovat HMAC signing
# 6. Testovat
```

**Bezpečnost není o jednom kouzelném řešení, ale o vrstvách ochrany (defense-in-depth).**

---

**Otázky?** Rád vám pomůžu s implementací konkrétních částí!
