# Security Best Practices

This document provides comprehensive security guidance for deploying and operating DeskShare in production environments.

## Table of Contents

1. [Security Overview](#security-overview)
2. [Authorization and Authentication](#authorization-and-authentication)
3. [Network Security](#network-security)
4. [Input Validation and Rate Limiting](#input-validation-and-rate-limiting)
5. [Session Management](#session-management)
6. [Audit Logging and Monitoring](#audit-logging-and-monitoring)
7. [Deployment Security](#deployment-security)
8. [Incident Response](#incident-response)
9. [Security Checklist](#security-checklist)

---

## Security Overview

### Security Architecture

DeskShare implements defense-in-depth security:

```
┌─────────────────────────────────────────────────────────┐
│                    Security Layers                      │
├─────────────────────────────────────────────────────────┤
│ 1. Network Security (WSS/HTTPS, TURN, Firewall)       │
│ 2. Authorization (Explicit user approval required)     │
│ 3. Rate Limiting (120 inputs/second per client)        │
│ 4. Input Validation (Coordinate/value ranges)          │
│ 5. Session Timeout (30 minutes inactivity)             │
│ 6. Audit Logging (All operations logged via Serilog)   │
└─────────────────────────────────────────────────────────┘
```

### Threat Model

**Threats Mitigated:**
- ✅ Unauthorized remote access (authorization system)
- ✅ Input flooding/DoS (rate limiting)
- ✅ Invalid input injection (validation)
- ✅ Session hijacking (timeout + audit logging)
- ✅ Man-in-the-middle (WebRTC encryption)

**Known Limitations:**
- ⚠️ No multi-factor authentication (Phase 5)
- ⚠️ No client certificate validation (Phase 5)
- ⚠️ Auto-approve in development mode (must disable in production)
- ⚠️ UAC prompts cannot be controlled (Windows security limitation)

---

## Authorization and Authentication

### Production Authorization Dialog

**CRITICAL:** Replace auto-approve with user confirmation dialog.

**Current Development Behavior (INSECURE):**
```csharp
// src/ScreenSenderApp/Input/WindowsInputController.cs
public async Task<bool> RequestAuthorizationAsync(
    string requestingClientId,
    CancellationToken cancellationToken = default)
{
    // TODO: Show GUI authorization dialog
    // Currently auto-approves for development
    await Task.Delay(100, cancellationToken); // Simulate user decision

    return true; // ⚠️ ALWAYS APPROVES - NOT SAFE FOR PRODUCTION
}
```

**Production Implementation (Required):**

1. **Create Authorization Dialog UI:**

```csharp
using System.Windows.Forms;

public sealed class AuthorizationDialog : Form
{
    private Label _messageLabel;
    private Button _approveButton;
    private Button _denyButton;
    private TaskCompletionSource<bool> _resultSource;

    public AuthorizationDialog(string clientId)
    {
        InitializeComponent();
        _messageLabel.Text = $"Client {clientId.Substring(0, 8)}... is requesting remote control.\n\n" +
                            "Do you want to allow this?";

        _approveButton.Click += (s, e) => { _resultSource.SetResult(true); Close(); };
        _denyButton.Click += (s, e) => { _resultSource.SetResult(false); Close(); };
    }

    public async Task<bool> ShowDialogAsync()
    {
        _resultSource = new TaskCompletionSource<bool>();
        Show();
        BringToFront();
        return await _resultSource.Task;
    }
}
```

2. **Integrate into WindowsInputController:**

```csharp
public async Task<bool> RequestAuthorizationAsync(
    string requestingClientId,
    CancellationToken cancellationToken = default)
{
    _logger.LogInformation("Authorization requested by client: {ClientId}", requestingClientId);

    // Show GUI dialog on UI thread
    bool authorized = false;
    await Task.Run(() =>
    {
        var dialog = new AuthorizationDialog(requestingClientId);
        authorized = dialog.ShowDialogAsync().GetAwaiter().GetResult();
    });

    if (authorized)
    {
        AuthorizedClientId = requestingClientId;
        AuthorizationState = InputAuthorizationState.Authorized;
        _lastInputTime = DateTime.UtcNow;
        _logger.LogInformation("Remote control authorized for client: {ClientId}", requestingClientId);
    }
    else
    {
        _logger.LogWarning("Remote control denied for client: {ClientId}", requestingClientId);
    }

    return authorized;
}
```

### Configuration Options

**Add to appsettings.json:**

```json
{
  "RemoteControl": {
    "Enabled": true,
    "RequireAuthorization": true,
    "AutoApprove": false,  // MUST be false in production
    "AllowedClients": [],  // Optional whitelist of client IDs
    "MaxInputsPerSecond": 120,
    "SessionTimeoutMinutes": 30,
    "ShowAuthorizationDialog": true  // Show GUI dialog
  }
}
```

### Best Practices

✅ **DO:**
- Implement user confirmation dialog before authorizing
- Display client ID and timestamp in authorization request
- Log all authorization attempts (approved and denied)
- Provide "Always deny this client" option for blacklisting
- Add timeout to authorization dialog (auto-deny after 30 seconds)
- Show ongoing session indicator in system tray

❌ **DON'T:**
- Use auto-approve in production
- Allow authorization without user interaction
- Hide authorization requests
- Skip logging denied requests

---

## Network Security

### WebSocket Security (WSS)

**Development (Insecure):**
```
ws://localhost:5000/signal  // ⚠️ Unencrypted WebSocket
```

**Production (Secure):**
```
wss://yourserver.com:443/signal  // ✅ TLS-encrypted WebSocket
```

**Enable WSS in SignalingServer:**

1. **Install Certificate:**

```bash
# Option 1: Let's Encrypt (recommended for public servers)
dotnet tool install --global dotnet-certify
dotnet certify --domain yourserver.com

# Option 2: Self-signed for internal networks
dotnet dev-certs https --trust
```

2. **Configure HTTPS in appsettings.json:**

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:443",
        "Certificate": {
          "Path": "/etc/letsencrypt/live/yourserver.com/fullchain.pem",
          "KeyPath": "/etc/letsencrypt/live/yourserver.com/privkey.pem"
        }
      }
    }
  }
}
```

3. **Update WebClient URL:**

```javascript
// src/WebClient/index-control.html
const signalingUrl = 'wss://yourserver.com/signal';  // WSS not WS
```

### WebRTC Security

**STUN/TURN Configuration:**

**Development (Public STUN - may leak IP):**
```javascript
const config = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' }  // Public STUN server
    ]
};
```

**Production (Private TURN with authentication):**
```javascript
const config = {
    iceServers: [
        {
            urls: 'turn:turn.yourserver.com:3478',
            username: 'user',
            credential: 'password',
            credentialType: 'password'
        },
        {
            urls: 'turns:turn.yourserver.com:5349',  // TLS-encrypted TURN
            username: 'user',
            credential: 'password'
        }
    ]
};
```

**Setup coturn (TURN Server):**

```bash
# Install coturn
sudo apt-get install coturn

# Configure /etc/turnserver.conf
listening-port=3478
tls-listening-port=5349
listening-ip=0.0.0.0
relay-ip=YOUR_SERVER_IP
external-ip=YOUR_PUBLIC_IP
realm=yourserver.com
server-name=yourserver.com

# Enable authentication
lt-cred-mech
user=username:password

# TLS certificate
cert=/etc/letsencrypt/live/yourserver.com/fullchain.pem
pkey=/etc/letsencrypt/live/yourserver.com/privkey.pem

# Security
no-multicast-peers
no-loopback-peers
mobility
fingerprint
```

### Firewall Configuration

**Recommended Firewall Rules:**

```bash
# SignalingServer (WSS)
sudo ufw allow 443/tcp

# TURN server
sudo ufw allow 3478/tcp
sudo ufw allow 3478/udp
sudo ufw allow 5349/tcp

# WebRTC media (UDP range)
sudo ufw allow 49152:65535/udp
```

**Windows Firewall:**
```powershell
# Allow SignalingServer
New-NetFirewallRule -DisplayName "RemoteDesktop WSS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow

# Allow TURN
New-NetFirewallRule -DisplayName "RemoteDesktop TURN" -Direction Inbound -Protocol UDP -LocalPort 3478 -Action Allow
```

---

## Input Validation and Rate Limiting

### Rate Limiting Configuration

**Default Settings (Recommended):**

```csharp
// src/ScreenSenderApp/Input/WindowsInputController.cs
private const int MaxInputsPerSecond = 120;
private readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(1);
```

**Why 120 inputs/second:**
- Mouse move events: ~60/second (60 FPS)
- Keyboard events: ~10/second (fast typing)
- Mouse clicks/wheel: ~10/second
- Total: ~80/second typical, 120/second provides headroom

**Monitoring Rate Limit Violations:**

```csharp
public InputStatistics GetStatistics()
{
    return new InputStatistics
    {
        TotalInputsRejected = Interlocked.Read(ref _totalInputsRejected),
        CurrentInputRate = _recentInputs.Count,
        // ...
    };
}
```

**Alert on Excessive Rejections:**

```csharp
// Add monitoring
if (stats.TotalInputsRejected > 1000)
{
    _logger.LogWarning("High input rejection rate: {Count} inputs rejected. Possible attack or misconfigured client.",
        stats.TotalInputsRejected);

    // Optional: Auto-revoke authorization
    RevokeAuthorization();
}
```

### Input Validation Rules

**Mouse Coordinates:**

```csharp
private bool ApplyMouseMove(InputMessage input)
{
    // Validate normalized coordinates (0.0 - 1.0)
    if (input.X == null || input.Y == null ||
        input.X.Value < 0 || input.X.Value > 1 ||
        input.Y.Value < 0 || input.Y.Value > 1)
    {
        _logger.LogWarning("Input rejected: Coordinates out of range (X={X}, Y={Y})",
            input.X, input.Y);
        Interlocked.Increment(ref _totalInputsRejected);
        return false;
    }

    // Prevent coordinate injection exploits
    if (double.IsNaN(input.X.Value) || double.IsInfinity(input.X.Value))
    {
        _logger.LogWarning("Input rejected: Invalid coordinate value");
        Interlocked.Increment(ref _totalInputsRejected);
        return false;
    }

    // Apply input...
}
```

**Mouse Wheel:**

```csharp
private bool ApplyMouseWheel(InputMessage input)
{
    // Validate wheel delta (typical: -120 to +120 per notch)
    if (input.WheelDelta == null ||
        Math.Abs(input.WheelDelta.Value) > 1200)  // Max 10 notches
    {
        _logger.LogWarning("Input rejected: Excessive wheel delta ({Delta})",
            input.WheelDelta);
        Interlocked.Increment(ref _totalInputsRejected);
        return false;
    }

    // Apply input...
}
```

**Keyboard:**

```csharp
private bool ApplyKeyboard(InputMessage input, bool isKeyDown)
{
    // Validate virtual key code (0-254 range)
    if (input.KeyCode == null ||
        input.KeyCode.Value < 0 ||
        input.KeyCode.Value > 254)
    {
        _logger.LogWarning("Input rejected: Invalid key code ({KeyCode})",
            input.KeyCode);
        Interlocked.Increment(ref _totalInputsRejected);
        return false;
    }

    // Optional: Block specific keys (Windows key, etc.)
    var blockedKeys = new[] { 0x5B, 0x5C };  // VK_LWIN, VK_RWIN
    if (blockedKeys.Contains(input.KeyCode.Value))
    {
        _logger.LogDebug("Input rejected: Blocked key code ({KeyCode})",
            input.KeyCode);
        return false;
    }

    // Apply input...
}
```

---

## Session Management

### Session Timeout

**Default: 30 minutes of inactivity**

```csharp
private readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

private void CheckSessionTimeout()
{
    if (AuthorizationState == InputAuthorizationState.Authorized)
    {
        var timeSinceLastInput = DateTime.UtcNow - _lastInputTime;

        if (timeSinceLastInput > SessionTimeout)
        {
            _logger.LogInformation(
                "Remote control session timed out after {Minutes} minutes of inactivity",
                SessionTimeout.TotalMinutes);

            RevokeAuthorization();
        }
    }
}
```

**Recommended Timeout Values:**

| Environment | Timeout | Rationale |
|-------------|---------|-----------|
| High Security | 5 minutes | Minimize exposure window |
| Corporate | 15 minutes | Balance security and usability |
| Development | 30 minutes | Reduce frequent re-auth |
| Long Sessions | 60 minutes | Extended troubleshooting |

**Configuration:**

```json
{
  "RemoteControl": {
    "SessionTimeoutMinutes": 15,  // Adjust per environment
    "WarnBeforeTimeout": true,     // Send warning at 80% timeout
    "TimeoutWarningSeconds": 60    // Warn 60 seconds before
  }
}
```

### Session Tracking

**Track Active Sessions:**

```csharp
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, SessionInfo> _activeSessions = new();

    public void StartSession(string clientId)
    {
        var session = new SessionInfo
        {
            ClientId = clientId,
            StartTime = DateTime.UtcNow,
            LastActivityTime = DateTime.UtcNow,
            InputCount = 0
        };

        _activeSessions[clientId] = session;
        _logger.LogInformation("Session started: {ClientId}", clientId);
    }

    public void EndSession(string clientId)
    {
        if (_activeSessions.TryRemove(clientId, out var session))
        {
            var duration = DateTime.UtcNow - session.StartTime;
            _logger.LogInformation(
                "Session ended: {ClientId}, Duration: {Duration}, Inputs: {Count}",
                clientId, duration, session.InputCount);
        }
    }

    public IEnumerable<SessionInfo> GetActiveSessions() => _activeSessions.Values;
}
```

---

## Audit Logging and Monitoring

### Comprehensive Logging

**Log All Security Events:**

```csharp
// Authorization events
_logger.LogInformation("Authorization requested by client: {ClientId}", clientId);
_logger.LogInformation("Remote control authorized for client: {ClientId}", clientId);
_logger.LogWarning("Remote control denied for client: {ClientId}", clientId);

// Input validation failures
_logger.LogWarning("Input rejected: Coordinates out of range (X={X}, Y={Y})", x, y);
_logger.LogWarning("Input rejected: Rate limit exceeded ({Rate} inputs in last second)", rate);

// Session management
_logger.LogInformation("Remote control session timed out after {Minutes} minutes", timeout);
_logger.LogInformation("Authorization revoked for client: {ClientId}", clientId);

// Anomaly detection
_logger.LogWarning("Suspicious activity: {Count} rejected inputs from client {ClientId}", count, clientId);
```

### Structured Logging with Serilog

**Configure Serilog for Security Auditing:**

```json
{
  "Serilog": {
    "Using": ["Serilog.Sinks.File", "Serilog.Sinks.Console"],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "RemoteDesktop.Input": "Debug",  // Log all input events
        "RemoteDesktop.WebRTC": "Information"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "logs/security-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 90,  // Keep 90 days of logs
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "logs/audit-.log",
          "rollingInterval": "Day",
          "restrictedToMinimumLevel": "Warning",  // Only warnings/errors
          "retainedFileCountLimit": 365  // Keep 1 year
        }
      }
    ]
  }
}
```

### Monitoring and Alerting

**Key Metrics to Monitor:**

```csharp
public sealed class SecurityMetrics
{
    public int AuthorizationRequestsTotal { get; set; }
    public int AuthorizationsDenied { get; set; }
    public int SessionsActive { get; set; }
    public int SessionsTimedOut { get; set; }
    public int InputsRejectedTotal { get; set; }
    public int RateLimitViolations { get; set; }
    public int ValidationFailures { get; set; }
    public DateTime LastAuthorizationRequest { get; set; }
}
```

**Alert Conditions:**

| Condition | Threshold | Action |
|-----------|-----------|--------|
| Denied authorizations | > 5 per hour | Email alert |
| Rate limit violations | > 10 per minute | Auto-revoke + alert |
| Validation failures | > 100 per minute | Block client + alert |
| Session duration | > 2 hours | Warning notification |
| Failed connections | > 20 per hour | Check for attack |

---

## Deployment Security

### Production Deployment Checklist

**Before Deployment:**

- [ ] Replace auto-approve with GUI authorization dialog
- [ ] Enable WSS (WebSocket Secure) with valid certificate
- [ ] Configure TURN server with authentication
- [ ] Set `AutoApprove = false` in appsettings.json
- [ ] Configure firewall rules (allow only necessary ports)
- [ ] Enable structured logging with retention policy
- [ ] Set appropriate session timeout (15-30 minutes)
- [ ] Configure rate limiting (120 inputs/second)
- [ ] Test authorization flow end-to-end
- [ ] Review and enable all input validation
- [ ] Set up monitoring and alerting
- [ ] Document incident response procedures

### Code Signing

**Sign Executables (Windows):**

```powershell
# Acquire code signing certificate
# Sign ScreenSenderApp
signtool sign /f certificate.pfx /p password /tr http://timestamp.digicert.com /td sha256 /fd sha256 "RemoteDesktop.ScreenSenderApp.exe"

# Sign SignalingServer
signtool sign /f certificate.pfx /p password /tr http://timestamp.digicert.com /td sha256 /fd sha256 "RemoteDesktop.SignalingServer.exe"

# Verify signature
signtool verify /pa "RemoteDesktop.ScreenSenderApp.exe"
```

### Installer Security (WiX)

**Require Administrator Elevation:**

```xml
<!-- Phase 5: WiX installer -->
<Package InstallerVersion="500" Compressed="yes" InstallScope="perMachine" InstallPrivileges="elevated" />

<Feature Id="ProductFeature" Title="DeskShare" Level="1">
    <ComponentRef Id="ScreenSenderApp" />
    <ComponentRef Id="SignalingServer" />
</Feature>

<CustomAction Id="CheckAdminRights" Error="Administrator rights are required to install DeskShare" />
```

---

## Incident Response

### Security Incident Procedures

**1. Unauthorized Access Detected:**

```
IMMEDIATE ACTIONS:
1. Revoke all active authorizations
2. Stop ScreenSenderApp service
3. Review audit logs for timeline
4. Identify compromised client IDs
5. Block client IDs in firewall
6. Rotate TURN server credentials
7. Notify security team

INVESTIGATION:
1. Check logs/audit-*.log for authorization events
2. Review logs/security-*.log for validation failures
3. Analyze network traffic (if captured)
4. Identify attack vector (brute force, credential theft, etc.)

REMEDIATION:
1. Patch vulnerability if identified
2. Update firewall rules
3. Enhance monitoring/alerting
4. Update documentation
```

**2. Rate Limit Attack:**

```
IMMEDIATE ACTIONS:
1. Auto-revoke authorization (already implemented)
2. Log client ID and IP address
3. Temporary block in firewall

INVESTIGATION:
1. Check if legitimate client misconfiguration
2. Review input patterns for attack signature
3. Determine if targeted or automated

REMEDIATION:
1. Contact client if legitimate
2. Lower rate limit if attack continues
3. Implement IP-based blocking
```

**3. Session Hijacking Suspected:**

```
IMMEDIATE ACTIONS:
1. Revoke all active sessions
2. Require re-authorization
3. Review WebRTC encryption status
4. Check for man-in-the-middle

INVESTIGATION:
1. Analyze session timeline in logs
2. Check for duplicate client IDs
3. Review ICE candidate sources
4. Verify TLS certificate validity

REMEDIATION:
1. Enforce WSS/TURN over TLS
2. Implement client certificate authentication (Phase 5)
3. Add session token validation
```

### Log Analysis Commands

**Find All Authorization Events:**
```bash
grep "Authorization" logs/security-*.log | grep -E "(requested|authorized|denied)"
```

**Find Rate Limit Violations:**
```bash
grep "Rate limit exceeded" logs/security-*.log | awk '{print $1, $2, $NF}' | sort | uniq -c
```

**Find Validation Failures:**
```bash
grep "Input rejected" logs/security-*.log | awk -F': ' '{print $2}' | sort | uniq -c | sort -nr
```

**Active Sessions:**
```bash
grep "Session started" logs/security-*.log | tail -10
```

---

## Security Checklist

### Development Environment

- [ ] Use auto-approve only in isolated development environment
- [ ] Never expose development server to internet
- [ ] Use `ws://localhost` only for local testing
- [ ] Review code for hardcoded credentials (none should exist)
- [ ] Run security-focused unit tests
- [ ] Test authorization denial flow
- [ ] Test rate limiting with stress tests
- [ ] Test session timeout behavior

### Staging Environment

- [ ] Enable WSS with self-signed certificate
- [ ] Configure TURN server with authentication
- [ ] Enable GUI authorization dialog
- [ ] Set `AutoApprove = false`
- [ ] Configure realistic rate limits
- [ ] Enable full audit logging
- [ ] Test end-to-end security flows
- [ ] Perform penetration testing
- [ ] Review firewall rules
- [ ] Test incident response procedures

### Production Environment

- [ ] Use valid TLS certificates (Let's Encrypt or commercial)
- [ ] Enable WSS on port 443
- [ ] Configure authenticated TURN server
- [ ] GUI authorization dialog mandatory
- [ ] `AutoApprove = false` enforced
- [ ] Rate limiting: 120 inputs/second
- [ ] Session timeout: 15-30 minutes
- [ ] Audit logs retained for 90+ days
- [ ] Monitoring and alerting configured
- [ ] Firewall rules restrictive (whitelist approach)
- [ ] Code signed with valid certificate
- [ ] Incident response plan documented
- [ ] Regular security audits scheduled
- [ ] Backup and recovery tested

### Ongoing Security Maintenance

- [ ] Review audit logs weekly
- [ ] Monitor rate limit violations daily
- [ ] Check for denied authorizations
- [ ] Update TLS certificates before expiry
- [ ] Rotate TURN server credentials monthly
- [ ] Review active sessions daily
- [ ] Update dependencies for security patches
- [ ] Conduct quarterly security audits
- [ ] Test incident response procedures quarterly
- [ ] Review and update firewall rules

---

## References

### Security Standards

- **OWASP Top 10:** https://owasp.org/www-project-top-ten/
- **WebRTC Security:** https://webrtc-security.github.io/
- **CWE-770:** Allocation of Resources Without Limits or Throttling
- **CWE-20:** Improper Input Validation

### Implementation Guides

- **TURN Server Setup:** https://github.com/coturn/coturn
- **Let's Encrypt:** https://letsencrypt.org/getting-started/
- **Serilog Best Practices:** https://github.com/serilog/serilog/wiki/Configuration-Basics

### Related Documentation

- [Remote Control Usage Guide](remote-control-guide.md)
- [Troubleshooting Guide](troubleshooting.md)
- [README.md](../README.md)

---

**Last Updated:** November 2025
**Version:** 1.0
**Classification:** Internal Use
**Maintainer:** DeskShare Security Team
