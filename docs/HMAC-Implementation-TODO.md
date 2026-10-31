> **STATUS: FULLY IMPLEMENTED (2026-04-16)**
> All 7 tasks below have been completed and tested. 323/323 tests passing.

# HMAC + Nonce Implementation - TODO

## 📐 Solution architecture

### Project structure (3 new classes in Core)

```
RemoteDesktop.Core/
├── Auth/
│   ├── AuthenticationService.cs          (existing)
│   ├── RequestSigningService.cs          ← NEW (HMAC signing/validation)
│   ├── NonceCache.cs                     ← NEW (nonce storage + cleanup)
│   ├── ClientAuthenticationMessage.cs    (existing - extend)
│   ├── ServerRegistrationMessage.cs      (existing - extend)
│   └── ...
```

**Why in Core, not a separate project?**
- ✅ Shared between SignalingServer (validation) and Desktop (signing)
- ✅ Logically belongs to authentication
- ✅ No extra dependencies (only System.Security.Cryptography)
- ✅ Simpler deployment

---

## 📋 TODO Tasks

### ✅ Task 1: Creating RequestSigningService.cs

**File:** `src/Core/Auth/RequestSigningService.cs`

**What to implement:**

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

**Completion criteria:**
- [ ] The `SignRequest()` method generates an HMAC-SHA256 signature
- [ ] The `ValidateSignature()` method verifies the signature
- [ ] Timestamp validation (max 60s old)
- [ ] Constant-time comparison to prevent timing attacks
- [ ] XML documentation for all public methods

---

### ✅ Task 2: Creating NonceCache.cs

**File:** `src/Core/Auth/NonceCache.cs`

**What to implement:**

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

**Completion criteria:**
- [ ] Thread-safe implementation (ConcurrentDictionary)
- [ ] The `TryUseNonce()` method detects duplicates
- [ ] Automatic cleanup using a Timer
- [ ] Statistics API for monitoring
- [ ] Proper disposal (stop the timer)

---

### ✅ Task 3: Extending ClientAuthenticationMessage

**File:** `src/Core/Auth/ClientAuthenticationMessage.cs`

**What to add:**

```csharp
namespace RemoteDesktop.Core.Auth;

public sealed class ClientAuthenticationMessage
{
    // Existing fields
    public required string ServerId { get; set; }
    public required string Passkey { get; set; }
    public required string ClientId { get; set; }

    // ← NEW FIELDS FOR HMAC
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

**Completion criteria:**
- [ ] 3 new properties added (Timestamp, Nonce, Signature)
- [ ] Signature is nullable (for backward compatibility)
- [ ] XML documentation
- [ ] The same changes for `ServerRegistrationMessage` (if needed)

---

### ✅ Task 4: Update SignalingServer - HMAC validation

**File:** `src/SignalingServer/Services/ConnectionManager.cs`

**What to change in the `AuthenticateClient()` method:**

```csharp
public ClientAuthenticationResponse AuthenticateClient(ClientAuthenticationMessage auth)
{
    // ← NEW SECTION: HMAC VALIDATION (BEFORE the passkey check)

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

    // CONTINUE with the existing validation (passkey check, session check, etc.)
    // ...
}
```

**What else to add to ConnectionManager:**

```csharp
public sealed class ConnectionManager
{
    // Add dependency
    private readonly NonceCache _nonceCache;

    public ConnectionManager(ILogger<ConnectionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nonceCache = new NonceCache(); // ← NEW
        // ...
    }

    public void Dispose()
    {
        _nonceCache?.Dispose(); // ← Add cleanup
        // ...
    }
}
```

**Completion criteria:**
- [ ] HMAC validation BEFORE the passkey check
- [ ] Nonce replay detection
- [ ] Fallback for legacy clients (without signature)
- [ ] Logging of all attempts (success/failure)
- [ ] NonceCache dependency injection

---

### ✅ Task 5: Update Desktop - Signing requests

**File:** `src/RemoteDesktop.Desktop/Services/ClientManager.cs` (or wherever you do authentication)

**What to change:**

```csharp
private async Task<bool> AuthenticateWithSignalingServerAsync(
    string serverId,
    string passkey,
    string password)
{
    try
    {
        // ← NEW: HMAC signature generation

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
            Timestamp = timestamp,      // ← NEW
            Nonce = nonce,              // ← NEW
            Signature = signature       // ← NEW
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

**Completion criteria:**
- [ ] Nonce generation (Guid.NewGuid())
- [ ] Current timestamp (DateTime.UtcNow)
- [ ] HMAC signature using RequestSigningService
- [ ] Populating the new fields in ClientAuthenticationMessage
- [ ] Logging for debugging

---

### ✅ Task 6: Unit tests

**File:** `tests/UnitTests/Auth/RequestSigningServiceTests.cs` (NEW)

**What to test:**

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

**File:** `tests/UnitTests/Auth/NonceCacheTests.cs` (NEW)

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

**Completion criteria:**
- [ ] Tests for SignRequest (generation)
- [ ] Tests for ValidateSignature (validation)
- [ ] Tests for tampering detection
- [ ] Tests for timestamp expiration
- [ ] Tests for nonce replay detection
- [ ] Tests for thread safety
- [ ] Code coverage > 90%

---

### ✅ Task 7: Documentation

**What to update:**

1. **`docs/Security-Analysis.md`** - Add the section "HMAC Implementation Status"
2. **`README.md`** - Mention HMAC security
3. **`docs/security-best-practices.md`** - Add HMAC best practices

**Example for README:**

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

**Completion criteria:**
- [ ] Security-Analysis.md updated
- [ ] README.md security section updated
- [ ] HMAC usage examples documented
- [ ] Migration guide for existing deployments

---

## 🎯 Acceptance Criteria (overall)

### Functional requirements:
- [ ] New clients use the HMAC signature
- [ ] SignalingServer validates the signature
- [ ] Replay attacks are blocked (duplicate nonce)
- [ ] Old clients keep working (fallback without signature)
- [ ] Expired requests are rejected (> 60s)

### Non-functional requirements:
- [ ] Zero extra dependencies (only System.Security.Cryptography)
- [ ] Thread-safe implementation
- [ ] Performance: < 1ms overhead per request
- [ ] Memory: NonceCache is bounded (auto-cleanup)
- [ ] Logging of all security events

### Testing:
- [ ] Unit tests passed (>90% coverage)
- [ ] Integration test: signed request → server validation
- [ ] Negative test: replay attack → blocked
- [ ] Negative test: tampered request → rejected
- [ ] Performance test: 100 requests/s without problems

---

## 📊 Effort estimate

| Task | Complexity | Estimated time |
|------|-----------|------------|
| Task 1: RequestSigningService | Medium | 1-2 hours |
| Task 2: NonceCache | Medium | 1-2 hours |
| Task 3: Extending messages | Easy | 30 minutes |
| Task 4: SignalingServer update | Medium | 1 hour |
| Task 5: Desktop update | Easy | 1 hour |
| Task 6: Unit tests | Medium | 2-3 hours |
| Task 7: Documentation | Easy | 1 hour |
| **TOTAL** | | **8-11 hours** |

---

## 🚀 Recommended implementation order

1. **Task 1** - RequestSigningService (the foundation of everything)
2. **Task 2** - NonceCache (needed for validation)
3. **Task 3** - Extending messages (API contract)
4. **Task 6** - Unit tests for Tasks 1 & 2 (verify logic)
5. **Task 4** - SignalingServer update (server-side)
6. **Task 5** - Desktop update (client-side)
7. **Task 6** - Integration tests (end-to-end)
8. **Task 7** - Documentation

---

## ✅ Definition of Done

**A task is done when:**
- [ ] The code is written and builds without warnings
- [ ] Unit tests passed (green)
- [ ] XML documentation for all public APIs
- [ ] Logging in the important places
- [ ] Code review (if you work in a team)
- [ ] Manual test (try authentication end-to-end)

**The whole feature is done when:**
- [ ] All 7 tasks are completed
- [ ] Integration tests passed
- [ ] Documentation updated
- [ ] No regression (old features still work)
- [ ] Performance impact < 5%

---

## 🐛 Possible problems and solutions

### Problem: "Clock skew between client and server"
**Solution:** Increase SignatureValiditySeconds from 60s to 120s (for tolerance)

### Problem: "NonceCache grows without bound"
**Solution:** The cleanup timer (every 30s) removes nonces older than the TTL

### Problém: "High memory usage"
**Solution:** Limit the max size of NonceCache (e.g. 10000 nonces)

### Problém: "Backward compatibility"
**Solution:** Signature is nullable - old clients do not send it, the server accepts that

---

## 📞 Need help?

If you run into a problem with any task:
1. Check Security-Analysis.md for the reference implementation
2. Try writing a unit test first (TDD approach)
3. Write me a question - I am happy to help with a specific problem!

---

**Ready to start? Begin with Task 1! 🚀**
