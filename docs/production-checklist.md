# Production Deployment Checklist

> Consolidated pre-deployment checklist covering all project phases (1-4).
> Review each item before deploying DeskShare to a production environment.

---

## Phase 1: Core WebRTC Streaming

- [ ] **DXGI Desktop Duplication** verified on target Windows version (10/11)
- [ ] **SignalingServer** starts without errors and `/health` endpoint returns 200
- [ ] **WebSocket signaling** (`/signal`) accepts connections and exchanges SDP/ICE
- [ ] **WebClient** loads in target browsers (Chrome, Edge, Firefox)
- [ ] **VP8 encoding** produces valid video stream at configured resolution/FPS
- [ ] **Multi-client mode** tested with expected number of concurrent viewers
- [ ] **Connection Manager** tracks all clients and cleans up on disconnect
- [ ] **Window capture (HWND)** works for target applications (if used)

## Phase 2: Performance Optimization

- [ ] **SIMD pixel converter** enabled (verify AVX2 support on target CPU)
- [ ] **ArrayPool buffer pooling** active (check GC pressure under load)
- [ ] **Adaptive bitrate** configured with appropriate quality thresholds
- [ ] **Target FPS** set in `appsettings.json` (recommended: 30fps for production)
- [ ] **Memory usage** stays under 200MB during sustained streaming
- [ ] **CPU usage** stays under 10% idle, acceptable under load

## Phase 3: Remote Input Control

- [ ] **Input authorization** requires explicit user consent (not auto-approve)
- [ ] **Rate limiting** set to appropriate value (default: 120 inputs/sec)
- [ ] **Session timeout** configured (default: 30 minutes inactivity)
- [ ] **Audit logging** enabled for all input operations
- [ ] **Browser shortcut protection** verified (Ctrl+T, Alt+F4 not captured)
- [ ] **Data channel** established and JSON messages flow bidirectionally

## Phase 4: Production Readiness

### Security

- [ ] **HMAC-SHA256 authentication** enabled for all client connections
- [ ] **Nonce cache** active with automatic cleanup (prevents replay attacks)
- [ ] **WSS (TLS)** configured for signaling server (no plain WS in production)
- [ ] **DTLS/SRTP** verified for media encryption (WebRTC standard)
- [ ] **TURN credentials** changed from defaults (`remoteuser`/`changeme`)
- [ ] **TURN server** accessible and tested for NAT traversal scenarios
- [ ] **Input validation** active on all user-submitted data

### Configuration

- [ ] **`appsettings.json`** reviewed and production values set
- [ ] **`ASPNETCORE_ENVIRONMENT`** set to `Production`
- [ ] **Logging level** set appropriately (Information or Warning, not Debug)
- [ ] **Serilog file sink** configured with log rotation and size limits
- [ ] **Quality preset** selected (Low/Medium/High/Ultra)

### Infrastructure

- [ ] **Docker image** builds successfully (`docker compose build`)
- [ ] **Docker Compose** starts all services (`docker compose up -d`)
- [ ] **Health check** passes (`curl http://localhost:5151/health`)
- [ ] **CoTURN** container running and reachable on expected ports
- [ ] **TLS certificates** mounted in `./certs` directory
- [ ] **Firewall rules** allow required ports (5151, 9090, 3478, 49152-65535)
- [ ] **Log volume** mounted (`./logs:/app/logs`)

### CI/CD

- [ ] **GitHub Actions build-test** workflow passes on main branch
- [ ] **GitHub Actions release** workflow tested with a tag push
- [ ] **Test results** reviewed (323/323 passing, 0 failures)
- [ ] **Release artifact** (ZIP) downloadable and functional

### Monitoring

- [ ] **OpenTelemetry metrics** endpoint accessible
- [ ] **Prometheus scrape target** configured
- [ ] **Grafana dashboard** imported (11 panels)
- [ ] **Alert rules** configured for critical metrics (CPU, memory, packet loss)
- [ ] **Statistics endpoint** (`/statistics`) returns valid JSON

### Documentation

- [ ] **README.md** up to date with current test count and feature list
- [ ] **Security best practices** document reviewed by team
- [ ] **Deployment guide** followed step-by-step on clean environment
- [ ] **Troubleshooting guide** covers known production issues

---

## Quick Smoke Test

After deployment, verify these endpoints respond correctly:

```bash
# Health check
curl -f https://your-server:5151/health

# Statistics
curl -f https://your-server:5151/statistics

# WebSocket signaling (should upgrade to WebSocket)
curl -i https://your-server:5151/signal

# WebClient (static files)
curl -f https://your-server:5151/index.html
```

## Rollback Plan

1. Stop current containers: `docker compose down`
2. Pull previous image tag: `docker compose pull`
3. Restart with previous version: `docker compose up -d`
4. Verify health: `curl -f https://your-server:5151/health`

---

**Last updated:** 2026-04-16
