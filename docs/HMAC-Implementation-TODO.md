> **STATUS: FULLY IMPLEMENTED (2026-04-16)**
> All 7 tasks below have been completed and tested. 323/323 tests passing.

# HMAC + Nonce Implementation - TODO

## 📐 Architektura řešení

### Struktura projektu (3 nové třídy v Core)

```
RemoteDesktop.Core/
├── Auth/
│   ├── AuthenticationService.cs          (existující)
│   ├── RequestSigningService.cs          ← NOVÁ (HMAC signing/validation)
│   ├── NonceCache.cs                     ← NOVÁ (nonce storage + cleanup)
│   ├── ClientAuthenticationMessage.cs    (existující - rozšířit)
│   ├── ServerRegistrationMessage.cs      (existující - rozšířit)
│   └── ...
```

**Proč v Core, ne extra projekt?**
- ✅ Sdíleno mezi SignalingServer (validace) a Desktop (signing)
- ✅ Logicky patří k autentizaci
- ✅ Žádné extra dependencies (pouze System.Security.Cryptography)
- ✅ Jednodušší deployment

---

## 📋 TODO Tasks

### ✅ Task 1: Vytvoření RequestSigningService.cs

**Soubor:** `src/Core/Auth/RequestSigningService.cs`

**Co implementovat:**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Core.Auth;

/// <summary>
/// Provides HMAC-based request signing and validation.
/// Protects against replay attacks, tampering, and ensures request integrity.
/// </summary>
public sealed class RequestSigningService
{
    /// <summary>
    /// Signature validity window in seconds.
    /// Requests older than this are rejected.
    /// </summary>
    public const int SignatureValiditySeconds = 60;

    /// <summary>
    /// Creates HMAC-SHA256 signature for authentication request.
    /// </summary>
    /// <param name="serverId">Server ID (public identifier)</param>
    /// <param name="passkey">Passkey (shared secret)</param>
    /// <param name="timestamp">Request timestamp (UTC)</param>
    /// <param name="nonce">Unique request identifier (prevents replay)</param>
    /// <returns>Base64-encoded HMAC signature</returns>
    public static string SignRequest(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce)
    {
        // TODO: Input validation
        // TODO: Create message to sign (serverId|timestamp|nonce)
        // TODO: Compute HMAC-SHA256 using passkey as key
        // TODO: Return Base64 encoded signature
    }

    /// <summary>
    /// Validates HMAC signature of authentication request.
    /// </summary>
    /// <param name="serverId">Server ID from request</param>
    /// <param name="passkey">Current valid passkey</param>
    /// <param name="timestamp">Timestamp from request</param>
    /// <param name="nonce">Nonce from request</param>
    /// <param name="signature">Signature to validate</param>
    /// <returns>True if signature is valid and not expired, false otherwise</returns>
    public static bool ValidateSignature(
        string serverId,
        string passkey,
        DateTime timestamp,
        string nonce,
        string signature)
    {
        // TODO: Check timestamp validity (max 60 seconds old)
        // TODO: Recompute expected signature
        // TODO: Constant-time comparison (prevent timing attacks)
    }
}
```

**Kritéria dokončení:**
- [ ] Metoda `SignRequest()` generuje HMAC-SHA256 podpis
- [ ] Metoda `ValidateSignature()` ověřuje podpis
- [ ] Validace timestamp (max 60s old)
- [ ] Constant-time comparison pro prevenci timing attacks
- [ ] XML dokumentace pro všechny public metody

---

### ✅ Task 2: Vytvoření NonceCache.cs

**Soubor:** `src/Core/Auth/NonceCache.cs`

**Co implementovat:**

```csharp
using System.Collections.Concurrent;

namespace RemoteDesktop.Core.Auth;

/// <summary>
/// Thread-safe cache for tracking used nonces.
/// Prevents replay attacks by ensuring each nonce is used only once.
/// Automatically cleans up expired nonces.
/// </summary>
public sealed class NonceCache : IDisposable
{
    private readonly ConcurrentDictionary<string, DateTime> _usedNonces;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _nonceTtl;
    private bool _disposed;

    /// <summary>
    /// Initializes nonce cache with automatic cleanup.
    /// </summary>
    /// <param name="nonceTtl">How long to keep nonces (default 60 seconds)</param>
    /// <param name="cleanupInterval">How often to run cleanup (default 30 seconds)</param>
    public NonceCache(
        TimeSpan? nonceTtl = null,
        TimeSpan? cleanupInterval = null)
    {
        // TODO: Initialize ConcurrentDictionary
        // TODO: Setup cleanup timer
        // TODO: Set default values (60s TTL, 30s cleanup)
    }

    /// <summary>
    /// Checks if nonce has been used and marks it as used if not.
    /// </summary>
    /// <param name="nonce">Nonce to check</param>
    /// <returns>True if nonce is fresh (not used), false if already used</returns>
    public bool TryUseNonce(string nonce)
    {
        // TODO: Try to add nonce to dictionary
        // TODO: Return false if already exists (replay attack)
        // TODO: Return true if added successfully
    }

    /// <summary>
    /// Removes expired nonces from cache.
    /// Called automatically by timer.
    /// </summary>
    private void CleanupExpiredNonces(object? state)
    {
        // TODO: Find nonces older than TTL
        // TODO: Remove them from dictionary
        // TODO: Log cleanup stats
    }

    /// <summary>
    /// Gets current cache statistics (for monitoring).
    /// </summary>
    public NonceCacheStatistics GetStatistics()
    {
        // TODO: Return count, oldest entry, etc.
    }

    public void Dispose()
    {
        // TODO: Stop cleanup timer
        // TODO: Clear dictionary
    }
}

public sealed class NonceCacheStatistics
{
    public int TotalNonces { get; set; }
    public DateTime? OldestNonce { get; set; }
    public DateTime? NewestNonce { get; set; }
}
```

**Kritéria dokončení:**
- [ ] Thread-safe implementace (ConcurrentDictionary)
- [ ] Metoda `TryUseNonce()` detekuje duplicity
- [ ] Automatický cleanup pomocí Timer
- [ ] Statistics API pro monitoring
- [ ] Proper disposal (stop timer)

---

### ✅ Task 3: Rozšíření ClientAuthenticationMessage

**Soubor:** `src/Core/Auth/ClientAuthenticationMessage.cs`

**Co přidat:**

```csharp
namespace RemoteDesktop.Core.Auth;

public sealed class ClientAuthenticationMessage
{
    // Existující fields
    public required string ServerId { get; set; }
    public required string Passkey { get; set; }
    public required string ClientId { get; set; }

    // ← NOVÉ FIELDS PRO HMAC
    /// <summary>
    /// Request timestamp (UTC). Used for signature validation.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Unique request identifier. Prevents replay attacks.
    /// </summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 signature of request.
    /// Computed as: HMAC(serverId|timestamp|nonce, passkey)
    /// </summary>
    public string? Signature { get; set; }
}
```

**Kritéria dokončení:**
- [ ] Přidány 3 nové properties (Timestamp, Nonce, Signature)
- [ ] Signature je nullable (pro zpětnou kompatibilitu)
- [ ] XML dokumentace
- [ ] Stejné změny pro `ServerRegistrationMessage` (pokud potřeba)

---

### ✅ Task 4: Update SignalingServer - Validace HMAC

**Soubor:** `src/SignalingServer/Services/ConnectionManager.cs`

**Co upravit v metodě `AuthenticateClient()`:**

```csharp
public ClientAuthenticationResponse AuthenticateClient(ClientAuthenticationMessage auth)
{
    // ← NOVÁ SEKCE: HMAC VALIDATION (PŘED passkey check)

    // 1. Check if signature is provided (new clients)
    if (!string.IsNullOrEmpty(auth.Signature))
    {
        // Validate HMAC signature
        if (!RequestSigningService.ValidateSignature(
            auth.ServerId,
            auth.Passkey,  // Current passkey from session
            auth.Timestamp,
            auth.Nonce,
            auth.Signature))
        {
            _logger.LogWarning(
                "Authentication failed - invalid HMAC signature | ServerId: {ServerId} | ClientId: {ClientId}",
                auth.ServerId, auth.ClientId);

            return new ClientAuthenticationResponse
            {
                Success = false,
                ErrorMessage = "Invalid request signature"
            };
        }

        // Check nonce (prevent replay attacks)
        if (!_nonceCache.TryUseNonce(auth.Nonce))
        {
            _logger.LogWarning(
                "Replay attack detected - nonce already used | ServerId: {ServerId} | Nonce: {Nonce}",
                auth.ServerId, auth.Nonce);

            return new ClientAuthenticationResponse
            {
                Success = false,
                ErrorMessage = "Request already processed (replay detected)"
            };
        }

        _logger.LogInformation(
            "HMAC signature validated successfully | ServerId: {ServerId} | ClientId: {ClientId}",
            auth.ServerId, auth.ClientId);
    }
    else
    {
        // Legacy client (no signature) - log warning
        _logger.LogWarning(
            "Client authenticated without HMAC signature (legacy mode) | ServerId: {ServerId}",
            auth.ServerId);
    }

    // POKRAČOVAT s existující validací (passkey check, session check, etc.)
    // ...
}
```

**Co ještě přidat do ConnectionManager:**

```csharp
public sealed class ConnectionManager
{
    // Přidat dependency
    private readonly NonceCache _nonceCache;

    public ConnectionManager(ILogger<ConnectionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nonceCache = new NonceCache(); // ← NOVÝ
        // ...
    }

    public void Dispose()
    {
        _nonceCache?.Dispose(); // ← Přidat cleanup
        // ...
    }
}
```

**Kritéria dokončení:**
- [ ] HMAC validace PŘED passkey check
- [ ] Nonce replay detection
- [ ] Fallback pro legacy klienty (bez signature)
- [ ] Logování všech pokusů (úspěch/failure)
- [ ] NonceCache dependency injection

---

### ✅ Task 5: Update Desktop - Signing requestů

**Soubor:** `src/RemoteDesktop.Desktop/Services/ClientManager.cs` (nebo kde děláte autentizaci)

**Co upravit:**

```csharp
private async Task<bool> AuthenticateWithSignalingServerAsync(
    string serverId,
    string passkey,
    string password)
{
    try
    {
        // ← NOVÉ: Generování HMAC signature

        var nonce = Guid.NewGuid().ToString(); // Unique request ID
        var timestamp = DateTime.UtcNow;

        var signature = RequestSigningService.SignRequest(
            serverId,
            passkey,
            timestamp,
            nonce);

        _logger.LogInformation(
            "Signing authentication request | ServerId: {ServerId} | Nonce: {Nonce}",
            serverId, nonce);

        // Create signed request
        var authRequest = new ClientAuthenticationMessage
        {
            ServerId = serverId,
            Passkey = passkey,
            ClientId = _clientId,
            Timestamp = timestamp,      // ← NOVÝ
            Nonce = nonce,              // ← NOVÝ
            Signature = signature       // ← NOVÝ
        };

        // Send to SignalingServer
        var response = await _httpClient.PostAsJsonAsync(
            $"{_signalingServerUrl}/authenticate",
            authRequest);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Authentication failed | StatusCode: {StatusCode} | Error: {Error}",
                response.StatusCode, errorContent);
            return false;
        }

        var authResult = await response.Content.ReadFromJsonAsync<ClientAuthenticationResponse>();

        if (authResult?.Success == true)
        {
            _logger.LogInformation("Authentication successful with HMAC signature");
            return true;
        }

        return false;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Authentication request failed");
        return false;
    }
}
```

**Kritéria dokončení:**
- [ ] Generování nonce (Guid.NewGuid())
- [ ] Aktuální timestamp (DateTime.UtcNow)
- [ ] HMAC signature pomocí RequestSigningService
- [ ] Naplnění nových fields v ClientAuthenticationMessage
- [ ] Logování pro debugging

---

### ✅ Task 6: Unit testy

**Soubor:** `tests/UnitTests/Auth/RequestSigningServiceTests.cs` (NOVÝ)

**Co testovat:**

```csharp
using RemoteDesktop.Core.Auth;
using Xunit;

namespace RemoteDesktop.UnitTests.Auth;

public class RequestSigningServiceTests
{
    [Fact]
    public void SignRequest_GeneratesValidSignature()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        // Act
        var signature = RequestSigningService.SignRequest(
            serverId, passkey, timestamp, nonce);

        // Assert
        Assert.NotNull(signature);
        Assert.NotEmpty(signature);
        // Signature should be Base64
        Assert.True(IsBase64String(signature));
    }

    [Fact]
    public void ValidateSignature_ValidRequest_ReturnsTrue()
    {
        // Arrange
        var serverId = "AABBCCDDEEFF";
        var passkey = "A3F7K9M2P";
        var timestamp = DateTime.UtcNow;
        var nonce = Guid.NewGuid().ToString();

        var signature = RequestSigningService.SignRequest(
            serverId, passkey, timestamp, nonce);

        // Act
        var isValid = RequestSigningService.ValidateSignature(
            serverId, passkey, timestamp, nonce, signature);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateSignature_TamperedServerId_ReturnsFalse()
    {
        // Test tampering detection
    }

    [Fact]
    public void ValidateSignature_ExpiredTimestamp_ReturnsFalse()
    {
        // Test timestamp validation (> 60s old)
    }

    [Fact]
    public void ValidateSignature_WrongPasskey_ReturnsFalse()
    {
        // Test wrong passkey detection
    }

    [Fact]
    public void SignRequest_SameInputs_GeneratesSameSignature()
    {
        // Test deterministic behavior
    }

    // Helper method
    private bool IsBase64String(string s)
    {
        try
        {
            Convert.FromBase64String(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

**Soubor:** `tests/UnitTests/Auth/NonceCacheTests.cs` (NOVÝ)

```csharp
public class NonceCacheTests
{
    [Fact]
    public void TryUseNonce_NewNonce_ReturnsTrue()
    {
        // Test accepting fresh nonce
    }

    [Fact]
    public void TryUseNonce_DuplicateNonce_ReturnsFalse()
    {
        // Test replay detection
    }

    [Fact]
    public void CleanupExpiredNonces_RemovesOldEntries()
    {
        // Test automatic cleanup
    }

    [Fact]
    public async Task TryUseNonce_ConcurrentAccess_ThreadSafe()
    {
        // Test thread safety with parallel requests
    }
}
```

**Kritéria dokončení:**
- [ ] Testy pro SignRequest (generování)
- [ ] Testy pro ValidateSignature (validace)
- [ ] Testy pro tampering detection
- [ ] Testy pro timestamp expiration
- [ ] Testy pro nonce replay detection
- [ ] Testy pro thread safety
- [ ] Code coverage > 90%

---

### ✅ Task 7: Dokumentace

**Co aktualizovat:**

1. **`docs/Security-Analysis.md`** - Přidat sekci "HMAC Implementation Status"
2. **`README.md`** - Zmínit HMAC security
3. **`docs/security-best-practices.md`** - Přidat HMAC best practices

**Příklad pro README:**

```markdown
## Security Features

✅ **HMAC Request Signing** - All authentication requests are cryptographically signed
  - HMAC-SHA256 signatures prevent tampering
  - Nonce-based replay attack prevention
  - 60-second signature validity window

✅ **Time-based Passkeys** - Auto-rotating 45-second codes
✅ **Rate Limiting** - 120 inputs/second
✅ **Input Validation** - Range checking, type validation
✅ **Session Timeout** - 30-minute inactivity limit
```

**Kritéria dokončení:**
- [ ] Security-Analysis.md updated
- [ ] README.md security section updated
- [ ] HMAC usage examples documented
- [ ] Migration guide pro existující deploymenty

---

## 🎯 Acceptance Criteria (celkově)

### Funkční požadavky:
- [ ] Nové klienty používají HMAC signature
- [ ] SignalingServer validuje signature
- [ ] Replay útoky jsou blokovány (duplicitní nonce)
- [ ] Staré klienty fungují (fallback bez signature)
- [ ] Expirované requesty jsou odmítnuty (> 60s)

### Non-funkční požadavky:
- [ ] Zero dependencies navíc (pouze System.Security.Cryptography)
- [ ] Thread-safe implementace
- [ ] Performance: < 1ms overhead na request
- [ ] Memory: NonceCache limitován (auto-cleanup)
- [ ] Logování všech security events

### Testování:
- [ ] Unit testy proběhly (>90% coverage)
- [ ] Integration test: signed request → server validation
- [ ] Negative test: replay attack → blocked
- [ ] Negative test: tampered request → rejected
- [ ] Performance test: 100 requestů/s bez problémů

---

## 📊 Odhad práce

| Task | Složitost | Odhad času |
|------|-----------|------------|
| Task 1: RequestSigningService | Střední | 1-2 hodiny |
| Task 2: NonceCache | Střední | 1-2 hodiny |
| Task 3: Rozšíření messages | Snadné | 30 minut |
| Task 4: SignalingServer update | Střední | 1 hodina |
| Task 5: Desktop update | Snadné | 1 hodina |
| Task 6: Unit testy | Střední | 2-3 hodiny |
| Task 7: Dokumentace | Snadné | 1 hodina |
| **CELKEM** | | **8-11 hodin** |

---

## 🚀 Doporučené pořadí implementace

1. **Task 1** - RequestSigningService (základ všeho)
2. **Task 2** - NonceCache (potřebný pro validaci)
3. **Task 3** - Rozšíření messages (API kontrakt)
4. **Task 6** - Unit testy pro Task 1 & 2 (verify logic)
5. **Task 4** - SignalingServer update (server-side)
6. **Task 5** - Desktop update (client-side)
7. **Task 6** - Integration testy (end-to-end)
8. **Task 7** - Dokumentace

---

## ✅ Definition of Done

**Task je hotový když:**
- [ ] Kód je napsaný a builduje bez warningů
- [ ] Unit testy proběhly (green)
- [ ] XML dokumentace pro všechny public API
- [ ] Logování na důležitých místech
- [ ] Code review (pokud pracujete v týmu)
- [ ] Manual test (zkusit autentizaci end-to-end)

**Celá feature je hotová když:**
- [ ] Všech 7 tasků je completed
- [ ] Integration testy proběhly
- [ ] Dokumentace aktualizována
- [ ] No regression (staré funkce fungují)
- [ ] Performance impact < 5%

---

## 🐛 Možné problémy a řešení

### Problém: "Časový posun mezi klientem a serverem"
**Řešení:** Zvětšit SignatureValiditySeconds z 60s na 120s (pro tolerance)

### Problém: "NonceCache roste do nekonečna"
**Řešení:** Cleanup timer (každých 30s) odstraní nonce starší než TTL

### Problém: "High memory usage"
**Řešení:** Limitovat max velikost NonceCache (např. 10000 nonces)

### Problém: "Backward compatibility"
**Řešení:** Signature je nullable - staré klienty neposílají, server akceptuje

---

## 📞 Potřebujete pomoc?

Pokud narazíte na problém při některém tasku:
1. Zkontrolujte Security-Analysis.md pro referenční implementaci
2. Zkuste napsat unit test nejdřív (TDD approach)
3. Napište mi otázku - rád pomohu s konkrétním problémem!

---

**Ready to start? Začněte s Task 1! 🚀**
