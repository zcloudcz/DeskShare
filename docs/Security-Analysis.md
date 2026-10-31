# DeskShare Security Analysis

## Current security model

### ✅ What is already implemented:

1. **Passkey authentication (45 seconds TTL)**
   - Generated from `ServerId` (MAC address) + timestamp
   - SHA256 hash for unpredictability
   - Short lifetime (45s) limits the attack window

2. **Server ID from the MAC address**
   - Stable identifier (does not change on restart)
   - Unique for each machine
   - Used as a seed for the passkey

3. **Rate limiting**
   - 120 inputs/second
   - Protection against flooding attacks

4. **Input validation**
   - Coordinate range checks (0.0-1.0)
   - Key code validation
   - Protection against injection

5. **Session timeout**
   - 30 minutes of inactivity
   - Auto-revocation of authorization

## 🔴 Security issues

### Issue 1: **The MAC address is NOT a secure identifier**

**Why the MAC address will NOT help:**

```
❌ MYTH: "The MAC address is secret and can be used for authentication"

✅ REALITY: The MAC address is PUBLIC and easy to find out:

1. ARP Request (local network):
   $ arp -a
   Interface: 192.168.1.100
     Internet Address      Physical Address      Type
     192.168.1.1          00-11-22-33-44-55     dynamic  ← router MAC address
     192.168.1.50         AA-BB-CC-DD-EE-FF     dynamic  ← server MAC

2. DHCP Logs (network administrator):
   DHCP Server sees all MAC addresses when assigning IPs

3. Network Sniffing (attacker on the same network):
   Wireshark, tcpdump → instantly see all MAC addresses

4. WebRTC ICE Candidates:
   They contain local IP addresses that can be mapped to MAC addresses

5. JavaScript API (browser):
   navigator.mediaDevices.enumerateDevices() can reveal device IDs
```

### Issue 2: **Replay attacks**

**Attack scenario:**

```
1. The attacker eavesdrops on the network (WiFi sniffing, ARP spoofing)
2. Captures the WebSocket communication between the client and the server
3. Copies valid messages (including the passkey)
4. Replays them in their own connection

Example of a captured message:
{
  "type": "authenticate",
  "serverId": "AABBCCDDEEFF",  ← Found via ARP
  "passkey": "A3F7K9M2P",      ← Captured from the communication
  "clientId": "112233445566"    ← Attacker's MAC
}

5. SignalingServer accepts the message → successful authentication!
```

### Issue 3: **Man-in-the-Middle (MITM)**

**Current state:**

```
┌────────┐    ws://         ┌───────────┐    ws://      ┌────────┐
│ Client │◄─────────────────┤ Attacker  │◄──────────────┤ Server │
└────────┘  unencrypted     │  Proxy    │ unencrypted  └────────┘
                             └───────────┘
                                   ↓
                        Attacker sees EVERYTHING:
                        - Server ID
                        - Passkey
                        - ICE candidates
                        - SDP offers/answers
```

### Issue 4: **Session Hijacking**

**The attacker can:**

```
1. Capture the ClientId of a legitimate client
2. Create their own WebSocket connection with the same ClientId
3. SignalingServer cannot tell who the real client is
4. The attacker takes over control of the session
```

### Issue 5: **DoS attacks on SignalingServer**

```
for i in {1..10000}; do
  curl -X POST http://signaling.com/register \
    -d '{"serverId":"FAKE", "passkey":"FAKE123"}'
done

→ SignalingServer flooded with fake registrations
→ Legitimate servers cannot register
```

## 🛡️ Recommended improvements

### Level 1: **IMPLEMENT IMMEDIATELY** (critical)

#### 1.1 WSS (WebSocket Secure) - HTTPS/TLS

**Implementace:**

```bash
# Obtaining a certificate (Let's Encrypt)
sudo certbot certonly --standalone -d signaling.yourserver.com

# Kestrel configuration (SignalingServer/appsettings.json)
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

**Benefits:**
- ✅ Encryption of all communication
- ✅ Protection against eavesdropping
- ✅ Protection against MITM attacks
- ✅ The browser automatically requires WSS for WebRTC

**Necessity:** ⚠️ **CRITICAL** - without TLS the application is UNSAFE

---

#### 1.2 HMAC Request Signing

**The MAC address problem solved with HMAC:**

```csharp
// src/Core/Auth/RequestSigningService.cs

public sealed class RequestSigningService
{
    private const int SignatureValiditySeconds = 60;

    /// <summary>
    /// Creates a digital signature of the request using HMAC-SHA256.
    /// </summary>
    /// <param name="serverId">Server ID (public)</param>
    /// <param name="passkey">Passkey (secret - shared after being displayed on the server)</param>
    /// <param name="timestamp">Request timestamp</param>
    /// <param name="nonce">Unique request identifier (replay protection)</param>
    /// <returns>HMAC signature</returns>
    public static string SignRequest(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce)
    {
        // Combination of data to sign
        string message = $"{serverId}|{timestamp:O}|{nonce}";

        // HMAC-SHA256 with the passkey as the key
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(passkey));
        byte[] hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));

        // Base64 for transport
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Verifies the validity of the request signature.
    /// </summary>
    public static bool ValidateSignature(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce,
        string signature)
    {
        // Time validity check (max 60 seconds old)
        if (Math.Abs((DateTime.UtcNow - timestamp).TotalSeconds) > SignatureValiditySeconds)
        {
            return false;
        }

        // Recompute the expected signature
        string expectedSignature = SignRequest(serverId, passkey, timestamp, nonce);

        // Constant-time comparison (protection against timing attacks)
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(expectedSignature),
            Convert.FromBase64String(signature));
    }
}
```

**Usage in the client:**

```csharp
// RemoteDesktop.Desktop/Services/ClientManager.cs

public async Task<bool> AuthenticateAsync(string serverId, string passkey)
{
    var nonce = Guid.NewGuid().ToString(); // Unique ID for this request
    var timestamp = DateTime.UtcNow;

    var signature = RequestSigningService.SignRequest(
        serverId, passkey, timestamp, nonce);

    var authRequest = new ClientAuthenticationMessage
    {
        ServerId = serverId,
        Passkey = passkey,  // We still send it for backward compatibility
        ClientId = _clientId,
        Timestamp = timestamp,
        Nonce = nonce,
        Signature = signature  // ← New field
    };

    // Send to SignalingServer
    var response = await _httpClient.PostAsJsonAsync("/authenticate", authRequest);
    // ...
}
```

**Verification on the server:**

```csharp
// SignalingServer/Services/ConnectionManager.cs

public ClientAuthenticationResponse AuthenticateClient(ClientAuthenticationMessage auth)
{
    // 1. Check the signature BEFORE checking the passkey
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

    // 2. Nonce check (replay protection)
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

    // Store the nonce (with a TTL of 60 seconds)
    _usedNonces.Add(auth.Nonce, DateTime.UtcNow.AddSeconds(60));

    // 3. Continue with the normal passkey validation
    // ...
}

// Nonce cache with automatic expiry
private readonly ConcurrentDictionary<string, DateTime> _usedNonces = new();
```

**Benefits of HMAC:**
- ✅ Replay attack protection (nonce)
- ✅ Tampering protection (signature)
- ✅ Time validity (timestamp)
- ✅ The passkey serves as a shared secret
- ✅ No need to store passwords on the server

---

#### 1.3 Connection Fingerprinting

**Instead of the MAC address, use TLS Certificate Pinning:**

```csharp
// src/Core/Auth/DeviceFingerprintService.cs

public sealed class DeviceFingerprintService
{
    /// <summary>
    /// Generates a stable device fingerprint without using the MAC address.
    /// </summary>
    public static string GenerateDeviceFingerprint()
    {
        var components = new List<string>
        {
            // 1. Machine GUID (Windows Registry - stable)
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

        // Hash of the combination
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
            // /etc/machine-id or /var/lib/dbus/machine-id
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

    // More methods for CPU ID, Motherboard Serial, etc.
    // (WMI queries on Windows, dmidecode on Linux, ioreg on macOS)
}
```

**Benefits:**
- ✅ More stable than the MAC address (does not change when the network card is replaced)
- ✅ Harder to spoof (requires knowing more HW parameters)
- ✅ Cross-platform (Windows/Linux/macOS)

**Drawbacks:**
- ⚠️ Can still be bypassed (virtual machines, HW emulation)
- ⚠️ Changes after an OS reinstall or HW replacement

---

### Level 2: **RECOMMENDED** (strong security)

#### 2.1 TLS Client Certificates (mTLS)

**Strongest authentication:**

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

**Generating client certificates:**

```bash
# 1. Creating the CA (Certificate Authority)
openssl genrsa -out ca.key 4096
openssl req -new -x509 -days 3650 -key ca.key -out ca.crt \
  -subj "/CN=RemoteDesktop CA"

# 2. Creating the client certificate
openssl genrsa -out client.key 2048
openssl req -new -key client.key -out client.csr \
  -subj "/CN=Client-$CLIENT_ID"

# 3. Signing with the CA
openssl x509 -req -in client.csr -CA ca.crt -CAkey ca.key \
  -CAcreateserial -out client.crt -days 365

# 4. Export to PKCS12 (for Windows/Browser)
openssl pkcs12 -export -out client.pfx \
  -inkey client.key -in client.crt -certfile ca.crt
```

**SignalingServer configuration:**

```csharp
// Program.cs
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureHttpsDefaults(httpsOptions =>
    {
        httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        httpsOptions.CheckCertificateRevocation = true;

        // Client certificate validation
        httpsOptions.ClientCertificateValidation = (cert, chain, errors) =>
        {
            if (errors != SslPolicyErrors.None)
                return false;

            // Check that the certificate was issued by our CA
            var issuer = cert.Issuer;
            if (!issuer.Contains("RemoteDesktop CA"))
                return false;

            // Check expiration
            if (cert.NotAfter < DateTime.Now)
                return false;

            return true;
        };
    });
});
```

**Usage in the client:**

```csharp
// Load client certificate
var clientCert = new X509Certificate2("client.pfx", "password");

var handler = new HttpClientHandler();
handler.ClientCertificates.Add(clientCert);

var httpClient = new HttpClient(handler)
{
    BaseAddress = new Uri("https://signaling.yourserver.com")
};

// All requests are now authenticated by the certificate
```

**Benefits:**
- ✅ Strongest authentication (standard in banking, enterprise)
- ✅ The certificate = the key to the server (cannot be guessed)
- ✅ Certificate revocation (CRL, OCSP)
- ✅ Native browser support

**Drawbacks:**
- ⚠️ More complex deployment (certificate distribution)
- ⚠️ Requires PKI infrastructure (CA management)
- ⚠️ User experience (importing the certificate into the browser)

---

#### 2.2 JWT Tokens for Session Management

**Use JWT instead of a plaintext ClientId:**

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

**Benefits:**
- ✅ Stateless (the server does not need to store session state)
- ✅ Self-contained (the token contains all claims)
- ✅ Standard (RFC 7519)
- ✅ Expiration built-in

---

### Level 3: **ENTERPRISE** (maximum security)

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
// The browser supports WebAuthn
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

## 📊 Comparison of security levels

| Method | Level | Protects against | Complexity | Cost |
|--------|--------|---------------|-----------|------|
| **Passkey only** | 🔴 Low | - | Easy | €0 |
| **+ WSS/TLS** | 🟡 Medium | MITM, Sniffing | Medium | €0-50/year |
| **+ HMAC Signing** | 🟢 Good | Replay, Tampering | Medium | €0 |
| **+ Device Fingerprint** | 🟢 Good | MAC spoofing | Medium | €0 |
| **+ Client Certificates** | 🔵 Very good | Session hijacking | Higher | €0-200/year |
| **+ JWT Tokens** | 🔵 Very good | Session replay | Medium | €0 |
| **+ MFA (TOTP)** | 🟣 Excellent | Credential theft | Higher | €0 |
| **+ IP Whitelist** | 🟣 Excellent | Remote attacks | Low | €0 |
| **+ FIDO2/WebAuthn** | 🟣 Highest | All attacks | High | €20-50/key |

---

## ✅ Recommended implementation (step by step)

### Phase 1: CRITICAL (implement IMMEDIATELY)

1. ✅ **WSS/TLS** - Encryption of all communication
   - Obtain a Let's Encrypt certificate
   - Configure Kestrel for HTTPS
   - Change all URLs from `ws://` to `wss://`

2. ✅ **HMAC Request Signing** - Replay protection
   - Implement `RequestSigningService`
   - Add `Signature`, `Timestamp`, `Nonce` to `ClientAuthenticationMessage`
   - Validate on the server before authorization

3. ✅ **Nonce Cache** - Replay attack prevention
   - Store used nonces with a TTL of 60s
   - Reject duplicate requests

### Phase 2: RECOMMENDED (within a month)

4. ✅ **Device Fingerprint** - Better device identification
   - Replace the MAC address with a combination of HW identifiers
   - Cross-platform implementation

5. ✅ **Rate Limiting on SignalingServer**
   - Limits on the `/register`, `/authenticate` endpoints
   - IP-based throttling (max 10 attempts/minute)

6. ✅ **Audit Logging**
   - Log all authentication attempts
   - Store IP addresses, timestamps, results
   - Retention 90 days

### Phase 3: PRODUCTION (before deployment)

7. ✅ **Client Certificates (optional)**
   - For high-security environments
   - Corporate deployment

8. ✅ **JWT Tokens**
   - Session management
   - Stateless authentication

9. ✅ **Monitoring & Alerting**
   - Failed authentications > 5/hour → alert
   - Nonce replay detection → block IP
   - Rate limit exceeded → alert

---

## 🎯 Conclusion

### Answer to your question:

> **"Will adding the MAC address to the request help?"**

**❌ NO** - the MAC address:
- Is public (visible on the network)
- Is easy to find out (ARP, DHCP)
- Can be spoofed (MAC spoofing)
- Adds no security

### ✅ WHAT WILL ACTUALLY HELP:

1. **WSS/TLS** (top priority) - encryption
2. **HMAC Signing** - integrity protection
3. **Nonce** - replay protection
4. **Device Fingerprint** - better identification (but not primary authentication)
5. **Client Certificates** - for enterprise use

### 🚀 Start with:

```bash
# 1. Install certbot
sudo snap install certbot --classic

# 2. Obtain a certificate
sudo certbot certonly --standalone -d signaling.yourserver.com

# 3. Configure appsettings.json
# 4. Change ws:// to wss://
# 5. Implement HMAC signing
# 6. Test
```

**Security is not about one magic solution, but about layers of protection (defense-in-depth).**

---

**Questions?** I'll gladly help you with implementing specific parts!
