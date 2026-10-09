# DeskShare - Production Readiness Plan

> Analysis and task specification for three deployment phases.
> Each phase must be fully completed and tested before moving to the next.
>
> **Current state (2026-04-16):** 323/323 tests passing, all features functional,
> but the application is running in DEVELOPMENT mode with no encryption, no real
> authorization dialog, hardcoded credentials, and no API-level protections.

---

## Phase 1 — CRITICAL (Week 1)

**Goal:** Eliminate all vulnerabilities that would allow an attacker to intercept
credentials, hijack sessions, or take control of the remote desktop without
user consent. After this phase the application can be safely exposed to a
trusted private network.

---

### 1.1 Enable TLS / WSS everywhere

**Problem:**
All WebSocket connections use unencrypted `ws://` protocol. Authentication data
(passkeys, HMAC signatures, nonce values) travel in plaintext. A passive
network observer on the same LAN can capture everything.

**Affected files and current values:**

| File | Line | Current value |
|------|------|---------------|
| `src/ScreenSenderApp/appsettings.json` | Signaling.ServerUrl | `ws://localhost:5151/signal` |
| `src/DeskShare.Desktop/appsettings.json` | SignalingUrl | `ws://localhost:5151/signal` |
| `src/DeskShare.DesktopAvalonia/appsettings.json` | SignalingUrl | `ws://localhost:5151/signal` |
| `src/WebClient/index.html` | input default | `ws://localhost:5151/signal` |
| `src/WebClient/index-control.html` | input default | `ws://localhost:5000/signal` |
| `src/SignalingServer/Program.cs` | WebSocket options | No TLS configuration |

**Tasks:**

1. **Configure Kestrel HTTPS in SignalingServer**
   - Add `Kestrel:Endpoints` section to `appsettings.json` with HTTPS endpoint
     (port 5151 for HTTPS, drop plain HTTP or redirect it).
   - Support certificate path + password from configuration so production
     deployments can supply a real certificate (Let's Encrypt, Azure, etc.).
   - For development, keep the ASP.NET Core dev certificate working
     via `appsettings.Development.json`.

2. **Add HTTPS redirect middleware**
   - Add `app.UseHttpsRedirection()` in `Program.cs` before
     `app.UseCors()` so any accidental HTTP request is redirected.

3. **Switch all default URLs to `wss://`**
   - Update every `appsettings.json` and HTML file listed above.
   - Keep them configurable (they already are), but the defaults must be secure.

4. **Configure TLS for TURN server**
   - Uncomment and configure `cert` and `pkey` lines in
     `docker/coturn/turnserver.conf` (lines 30-31).
   - Document certificate provisioning in the deployment guide.

**Acceptance criteria:**
- SignalingServer starts on HTTPS by default.
- All WebSocket connections negotiate `wss://`.
- HTTP requests to SignalingServer are redirected to HTTPS.
- TURN server accepts DTLS connections on port 5349.
- Existing unit and integration tests still pass (323/323).

---

### 1.2 Implement real authorization dialog for remote control

**Problem:**
`src/ScreenSenderApp/Input/WindowsInputController.cs` lines 217-221:

```csharp
// TODO: Show authorization dialog to user
// For now, simulate a brief delay and auto-approve for development
await Task.Delay(1000, cancellationToken);
bool authorized = true;  // TODO: Replace with actual user response
```

Any authenticated client immediately gets full mouse/keyboard control.
The security-best-practices.md explicitly marks this as "NOT SAFE FOR PRODUCTION".

**Tasks:**

1. **Create an authorization dialog UI component**
   - Show a modal window with: requesting client ID, IP address, timestamp.
   - Two buttons: "Allow" and "Deny".
   - Auto-deny after 30-second timeout (no silent approval).
   - Must work in both WPF (`DeskShare.Desktop`) and Avalonia
     (`DeskShare.DesktopAvalonia`) via `DeskShare.Desktop.Shared` abstraction.

2. **Update `WindowsInputController.RequestAuthorizationAsync`**
   - Replace `bool authorized = true` with actual dialog result.
   - Log the decision (allow/deny/timeout) with client details.

3. **Add a "Block this client" option**
   - If the user denies, offer to permanently block the client ID
     for the current session.
   - Blocked clients receive an immediate denial on subsequent requests.

4. **Write unit tests**
   - Test authorization flow: grant, deny, timeout, block.
   - Test that blocked clients cannot re-request authorization.
   - Test session timeout (30 min inactivity) still revokes authorization.

**Acceptance criteria:**
- Remote control requests show a visible dialog to the host user.
- No input commands are processed until the user explicitly clicks "Allow".
- Timeout (30 s) results in denial, not approval.
- Blocked clients receive immediate denial.
- All new tests pass.

---

### 1.3 Replace hardcoded TURN credentials

**Problem:**
`docker/coturn/turnserver.conf` contains:
- Line 24: `user=remoteuser:RemotePass123!`
- Line 80: `cli-password=AdminPass123!`

These credentials are committed to version control and documented in
`docker/coturn/README.md`.

**Tasks:**

1. **Move credentials to environment variables**
   - Remove hardcoded user/password from `turnserver.conf`.
   - Use `docker-compose.yml` environment variables or Docker secrets.
   - Update `README.md` with instructions for setting credentials.

2. **Disable CLI access in production**
   - Set `no-cli` in `turnserver.conf` (or bind CLI only to 127.0.0.1).

3. **Generate strong credentials in deployment script**
   - Add a helper script or note in the release setup that generates
     random TURN credentials on first deployment.

**Acceptance criteria:**
- No plaintext passwords in any committed file.
- TURN server starts successfully with environment-provided credentials.
- CLI management port is disabled or restricted to localhost.

---

### 1.4 Add rate limiting to API endpoints

**Problem:**
`/register` and `/authenticate` endpoints in `Program.cs` (lines 328-384)
accept unlimited requests. An attacker can brute-force the 9-character passkey
(32^9 combinations, but only 45-second validity window makes targeted brute
force feasible at high request rates) or flood `/register` to exhaust memory.

**Tasks:**

1. **Add ASP.NET Core built-in rate limiting middleware**
   - Use `builder.Services.AddRateLimiter()` (available since .NET 7).
   - Configure a **fixed window** policy for `/authenticate`:
     max 10 requests per 60 seconds per IP.
   - Configure a **fixed window** policy for `/register`:
     max 5 requests per 60 seconds per IP.
   - Return HTTP 429 (Too Many Requests) with a `Retry-After` header.

2. **Log rate limit violations**
   - Log IP address and endpoint when rate limit is hit.
   - Include in `/statistics` endpoint output.

3. **Make limits configurable**
   - Add `RateLimiting` section to `appsettings.json` so production
     deployments can tune thresholds without code changes.

**Acceptance criteria:**
- 11th `/authenticate` request within 60 seconds from the same IP returns 429.
- 6th `/register` request within 60 seconds from the same IP returns 429.
- Rate limit events are logged with IP and timestamp.
- Limits are configurable via `appsettings.json`.
- Integration test verifies rate limiting behavior.

---

### 1.5 Authenticate WebSocket connections

**Problem:**
`Program.cs` line 136: the `/signal` WebSocket endpoint accepts any connection
and trusts the `clientId` query parameter. An attacker who knows a legitimate
client ID can connect with that ID and intercept signaling messages.

**Tasks:**

1. **Require a short-lived token for WebSocket upgrade**
   - After successful `/authenticate`, return a one-time WebSocket token
     (UUID, valid for 30 seconds, stored in `ConnectionManager`).
   - Client must pass this token as query parameter: `/signal?token=xxx`.
   - Server validates the token before accepting the WebSocket upgrade.
   - Token is consumed on use (single-use).

2. **Remove user-controlled `clientId` from query string**
   - Server assigns the `clientId` from the authenticated session,
     not from the query parameter.
   - Existing behavior (generate GUID if no query param) becomes the only path.

3. **Reject unauthenticated WebSocket connections**
   - If token is missing or invalid, return HTTP 401 before upgrade.
   - Log the rejected connection attempt.

**Acceptance criteria:**
- WebSocket upgrade without a valid token returns 401.
- Token is single-use and expires after 30 seconds.
- Client ID is server-assigned, never user-provided.
- Integration test verifies: authenticate → get token → connect WebSocket.

---

## Phase 2 — HIGH PRIORITY (Week 2-3)

**Goal:** Harden the application for deployment on untrusted networks.
Add operational infrastructure (CI/CD, environment configs, connection
limits) so the application can be reliably deployed and monitored.

---

### 2.1 Create `appsettings.Production.json`

**Problem:**
No production-specific configuration exists. If `ASPNETCORE_ENVIRONMENT` is
not explicitly set to `Production`, CORS defaults to allowing any origin
(Program.cs lines 86-92). Storage defaults to InMemory (volatile).

**Tasks:**

1. **Create `src/SignalingServer/appsettings.Production.json`:**
   ```json
   {
     "AllowedHosts": "<production-domain>",
     "Cors": {
       "AllowedOrigins": ["https://<production-domain>"]
     },
     "Storage": {
       "Type": "AzureTableStorage"
     },
     "Turn": {
       "Enabled": true,
       "EnableTestUser": false
     },
     "Logging": {
       "LogLevel": {
         "Default": "Warning",
         "DeskShare": "Information"
       }
     }
   }
   ```

2. **Create `src/ScreenSenderApp/appsettings.Production.json`:**
   - Set `Signaling.ServerUrl` to `wss://<production-url>/signal`.
   - Set log retention to 90 days.
   - Disable `OpenTelemetry` Prometheus endpoint (or bind to localhost only).

3. **Add environment validation on startup**
   - Log a clear WARNING at startup if `ASPNETCORE_ENVIRONMENT` is not
     `Production` when running outside `localhost`.

**Acceptance criteria:**
- Production config files exist with hardened defaults.
- CORS is restricted to explicit origins in production.
- Storage uses persistent backend in production.
- Startup warns if environment is not correctly set.

---

### 2.2 Add connection limits and request size validation

**Problem:**
No limit on concurrent WebSocket connections. No limit on HTTP request body
size. An attacker can open thousands of connections or send oversized payloads
to exhaust server memory.

**Tasks:**

1. **Configure maximum concurrent WebSocket connections**
   - Add a configurable limit (default: 100) in `ConnectionManager`.
   - Return HTTP 503 (Service Unavailable) when limit is reached.
   - Log when connection limit is hit.

2. **Configure request body size limits**
   - Set `MaxRequestBodySize` in Kestrel options (default: 64 KB for API
     endpoints — authentication messages are small).
   - Set WebSocket message buffer to a reasonable maximum (16 KB instead
     of current 4 KB — signaling messages are small).

3. **Add idle connection cleanup**
   - Disconnect WebSocket clients that send no messages for 5 minutes
     (current keep-alive is 120 seconds but no inactivity disconnect).
   - `ConnectionManager` already has `LastActivityAt` tracking — use it.

**Acceptance criteria:**
- 101st concurrent WebSocket connection is rejected with 503.
- HTTP request larger than 64 KB is rejected with 413.
- Idle WebSocket connections are closed after 5 minutes.
- All limits are configurable via `appsettings.json`.

---

### 2.3 Add security headers middleware

**Problem:**
No security-related HTTP headers are set. Browsers don't receive instructions
about content security, framing, or transport security.

**Tasks:**

1. **Add security headers middleware in `Program.cs`:**
   - `Strict-Transport-Security: max-age=31536000; includeSubDomains` (HSTS)
   - `X-Content-Type-Options: nosniff`
   - `X-Frame-Options: DENY`
   - `Referrer-Policy: strict-origin-when-cross-origin`
   - `Content-Security-Policy: default-src 'self'; connect-src 'self' wss:;`

2. **Add HSTS preload consideration**
   - Document in deployment guide whether to add `preload` directive.

**Acceptance criteria:**
- All responses include security headers.
- Browser DevTools shows no security warnings.
- CSP does not break WebSocket or WebRTC connections.

---

### 2.4 Update outdated NuGet packages

**Problem:**
- `System.Net.WebSockets.Client` version 4.3.2 is from 2017 and may have
  known vulnerabilities. .NET 8 has built-in WebSocket support.
- `OpenTelemetry.Exporter.Prometheus.AspNetCore` is a beta package (1.10.0-beta.1).

**Tasks:**

1. **Remove `System.Net.WebSockets.Client` 4.3.2**
   - This package is unnecessary on .NET 8 — `ClientWebSocket` is part of
     the runtime. Remove the NuGet reference from
     `DeskShare.ScreenSenderApp.csproj`.
   - Verify all `ClientWebSocket` usage still compiles.

2. **Evaluate Prometheus exporter**
   - If beta is acceptable, document the decision.
   - If not, switch to `OpenTelemetry.Exporter.Prometheus.HttpListener`
     (stable) or remove Prometheus and use the JSON `/statistics` endpoint.

3. **Run `dotnet list package --outdated`**
   - Update any other packages with known vulnerabilities.

**Acceptance criteria:**
- No NuGet package with known CVEs.
- `System.Net.WebSockets.Client` removed from .csproj.
- All 323 tests still pass after updates.

---

### 2.5 Restrict CORS policy

**Problem:**
Production CORS (Program.cs lines 94-101) uses `.AllowAnyMethod()` and
`.AllowAnyHeader()` combined with `.AllowCredentials()`. This is overly
permissive even when origins are restricted.

**Tasks:**

1. **Restrict allowed methods**
   - Allow only `GET`, `POST` (the application doesn't use PUT/DELETE/PATCH).

2. **Restrict allowed headers**
   - Allow only `Content-Type`, `Authorization`, `X-Requested-With`.

3. **Verify WebRTC/WebSocket CORS behavior**
   - WebSocket upgrade requests and WebRTC ICE don't rely on CORS,
     so restricting methods/headers won't break real-time features.

**Acceptance criteria:**
- OPTIONS preflight returns only GET and POST as allowed methods.
- PUT/DELETE/PATCH requests from cross-origin are rejected.
- WebSocket and WebRTC connections work as before.

---

### 2.6 Set up CI/CD pipeline

**Problem:**
No automated build/test/deploy pipeline. Only a manual PowerShell script
(superseded by `.github/workflows/release.yml`).

**Tasks:**

1. **Create GitHub Actions workflow (`.github/workflows/build-test.yml`):**
   - Trigger on push to `main` and on pull requests.
   - Steps: restore → build → test → report results.
   - Run on `windows-latest` (DXGI tests need Windows).

2. **Create release workflow (`.github/workflows/release.yml`):**
   - Trigger on tag push (`v*`).
   - Build portable self-contained package (`win-x64`).
   - Create GitHub Release with ZIP artifact.

3. **Add build status badge to README.md.**

**Acceptance criteria:**
- Every push to `main` runs all 323 tests automatically.
- Pull requests show test results before merge.
- Tagged commits produce a downloadable release package.

---

## Phase 3 — MEDIUM PRIORITY (Week 3-4)

**Goal:** Operational maturity. Make the application easy to deploy,
monitor, and maintain in production environments.

---

### 3.1 Create Dockerfile for SignalingServer

**Problem:**
No containerized deployment option for the main application. Only CoTURN
has a Docker setup.

**Tasks:**

1. **Create `Dockerfile` in project root:**
   - Multi-stage build: restore → build → publish → runtime.
   - Base image: `mcr.microsoft.com/dotnet/aspnet:8.0`.
   - Expose ports 5151 (HTTPS) and 9090 (metrics).
   - Include health check: `HEALTHCHECK CMD curl -f http://localhost:5151/health`.

2. **Create `docker-compose.yml` in project root:**
   - Services: `signaling-server`, `coturn`.
   - Shared network for inter-service communication.
   - Volume mounts for logs, certificates, configuration.
   - Environment variables for secrets.

3. **Document deployment with Docker in `docs/deployment-guide.md`.**

**Acceptance criteria:**
- `docker-compose up` starts SignalingServer + CoTURN.
- Health check endpoint accessible from host.
- Logs persisted to host volume.
- TLS certificates mountable as volumes.

---

### 3.2 Implement graceful shutdown

**Problem:**
No application-level graceful shutdown handling. Active WebSocket connections
are dropped immediately on server stop. No connection drain period.

**Tasks:**

1. **Register `IHostApplicationLifetime` handlers in `Program.cs`:**
   - On `ApplicationStopping`: log shutdown, stop accepting new connections.
   - Send "server shutting down" message to all connected WebSocket clients.
   - Wait up to 10 seconds for active connections to close cleanly.

2. **Add shutdown timeout configuration:**
   - Configurable grace period (default: 10 seconds).
   - After timeout, force-close remaining connections.

3. **Update `ConnectionManager.Dispose()`:**
   - Close all active WebSocket connections with `NormalClosure` status.
   - Dispose nonce cache and timers (already done).

**Acceptance criteria:**
- Connected clients receive a shutdown notification.
- Server waits up to 10 seconds for clean disconnects.
- No `ObjectDisposedException` in logs during shutdown.

---

### 3.3 Configure monitoring and alerting

**Problem:**
Health checks and statistics endpoints exist, but no alerting is configured.
Failed authentication attempts, rate limit violations, and connection issues
go unnoticed.

**Tasks:**

1. **Add structured log alerts for security events:**
   - Failed authentication: log at `Warning` level with IP, serverId.
   - Rate limit hit: log at `Warning` level with IP, endpoint.
   - WebSocket token validation failure: log at `Warning` level.
   - Remote control authorization denied: log at `Information` level.

2. **Add `/metrics` Prometheus endpoint to SignalingServer:**
   - Expose: active connections, authentication attempts (success/fail),
     rate limit hits, message throughput.
   - Reuse the existing OpenTelemetry setup from ScreenSenderApp as reference.

3. **Create alerting rules document (`docs/alerting-rules.md`):**
   - Define thresholds for Prometheus/Grafana alerts:
     - `auth_failures_total > 10 in 5 minutes` → potential brute force
     - `active_connections > 80` → approaching connection limit
     - `rate_limit_hits_total > 50 in 5 minutes` → potential DoS
   - Include sample Grafana dashboard JSON.

**Acceptance criteria:**
- `/metrics` endpoint returns Prometheus-formatted metrics.
- Security events logged with consistent structured format.
- Alerting rules document complete with thresholds.

---

### 3.4 Extend log retention and audit trail

**Problem:**
Logs are retained for only 7 days (Serilog configuration in both
SignalingServer and ScreenSenderApp). For security auditing and compliance,
production systems typically need 90+ days.

**Tasks:**

1. **Update log retention in `appsettings.Production.json`:**
   - Set `retainedFileCountLimit` to 90 (days).
   - Consider adding a separate audit log sink for security events only
     (authentication, authorization, rate limiting).

2. **Add audit log category:**
   - Create a dedicated `Audit` log category in Serilog.
   - Route security events to a separate file: `logs/audit-.log`.
   - Audit log should include: timestamp, event type, IP, client ID,
     server ID, result (success/failure), reason.

3. **Document log rotation and archival strategy.**

**Acceptance criteria:**
- Production logs retained for 90 days.
- Audit log file contains only security-relevant events.
- Log files are rotated daily with clear naming convention.

---

### 3.5 Update project documentation

**Problem:**
Several documentation files are outdated or inconsistent with the actual
state of the codebase.

**Tasks:**

1. **Update `README.md`:**
   - Test count: 148 → 323.
   - Add PWA support mention.
   - Add HMAC authentication to security features.
   - Update phase status indicators.

2. **Update `docs/HMAC-Implementation-TODO.md`:**
   - Mark all 7 tasks as completed (they are all implemented).
   - Add "Implementation completed" header with date.

3. **Update `docs/security-best-practices.md`:**
   - Add sections for new Phase 1 features (rate limiting, WebSocket auth,
     HTTPS enforcement).
   - Update authorization dialog section to reflect actual implementation.

4. **Create `docs/production-checklist.md`:**
   - Consolidate all production requirements into a single checklist.
   - Include pre-deployment verification steps.
   - Include rollback procedures.

**Acceptance criteria:**
- All documentation reflects the current state of the codebase.
- Production checklist covers all three phases.
- No outdated test counts, phase statuses, or TODO markers.

---

## Summary

| Phase | Focus | Items | Estimated effort |
|-------|-------|-------|------------------|
| **Phase 1** | Security critical fixes | 5 tasks | ~40 hours |
| **Phase 2** | Hardening + CI/CD | 6 tasks | ~30 hours |
| **Phase 3** | Operations + docs | 5 tasks | ~20 hours |
| **Total** | | **16 tasks** | **~90 hours** |

### Risk if skipped

| Phase | Risk if deployed without it |
|-------|----------------------------|
| **Phase 1** | Credentials intercepted, sessions hijacked, unauthorized remote control. **Do not deploy.** |
| **Phase 2** | Brute force attacks succeed, no CI/CD = manual error-prone deployments, resource exhaustion possible. **Risky.** |
| **Phase 3** | Difficult to diagnose production issues, manual deployments, inconsistent documentation. **Inconvenient but survivable.** |

---

*Generated: 2026-04-16 | Project: DeskShare (RemoteDesktopNet) | Tests: 323/323 passing*
