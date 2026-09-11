# Telemetry & Monitoring Guide

Complete guide to monitoring DeskShare with OpenTelemetry, Prometheus, and Grafana.

## Table of Contents

1. [Overview](#overview)
2. [Quick Start](#quick-start)
3. [Available Metrics](#available-metrics)
4. [Prometheus Setup](#prometheus-setup)
5. [Grafana Setup](#grafana-setup)
6. [Dashboard Configuration](#dashboard-configuration)
7. [Alerting Rules](#alerting-rules)
8. [Troubleshooting](#troubleshooting)

---

## Overview

DeskShare exposes comprehensive metrics via OpenTelemetry, compatible with Prometheus and Grafana for production monitoring.

**Architecture:**
```
┌──────────────────┐
│ ScreenSenderApp  │
│  (Metrics)       │──> http://localhost:9090/metrics (Prometheus format)
└──────────────────┘            │
                                 ▼
                        ┌─────────────────┐
                        │   Prometheus    │ (Scrapes every 15s)
                        │   (Time-series  │
                        │    Database)    │
                        └─────────────────┘
                                 │
                                 ▼
                        ┌─────────────────┐
                        │    Grafana      │ (Visualizes metrics)
                        │   (Dashboard)   │
                        └─────────────────┘
```

---

## Quick Start

### 1. Enable Metrics in ScreenSenderApp

Metrics are enabled by default. Configuration in `appsettings.json`:

```json
{
  "OpenTelemetry": {
    "Enabled": true,
    "PrometheusPort": 9090,
    "PrometheusEndpoint": "/metrics"
  }
}
```

Start ScreenSenderApp:
```bash
cd src/ScreenSenderApp
dotnet run
```

Verify metrics endpoint:
```bash
curl http://localhost:9090/metrics
```

Expected output:
```
# HELP capture_frames_total Total number of frames captured from screen
# TYPE capture_frames_total counter
capture_frames_total 1250

# HELP webrtc_connections_active Number of currently active WebRTC connections
# TYPE webrtc_connections_active gauge
webrtc_connections_active 2
...
```

### 2. Install Prometheus

**Windows (using Chocolatey):**
```powershell
choco install prometheus
```

**Linux:**
```bash
wget https://github.com/prometheus/prometheus/releases/download/v2.48.0/prometheus-2.48.0.linux-amd64.tar.gz
tar xvfz prometheus-2.48.0.linux-amd64.tar.gz
cd prometheus-2.48.0.linux-amd64
```

**macOS:**
```bash
brew install prometheus
```

### 3. Configure Prometheus

Create `prometheus.yml`:

```yaml
global:
  scrape_interval: 15s
  evaluation_interval: 15s

scrape_configs:
  - job_name: 'remotedesktop'
    static_configs:
      - targets: ['localhost:9090']
        labels:
          instance: 'screen-sender-001'
          environment: 'development'
```

Start Prometheus:
```bash
prometheus --config.file=prometheus.yml
```

Access Prometheus UI: http://localhost:9091

### 4. Install Grafana

**Windows (using Chocolatey):**
```powershell
choco install grafana
```

**Linux:**
```bash
sudo apt-get install -y software-properties-common
sudo add-apt-repository "deb https://packages.grafana.com/oss/deb stable main"
sudo apt-get update
sudo apt-get install grafana
sudo systemctl start grafana-server
```

**macOS:**
```bash
brew install grafana
brew services start grafana
```

Access Grafana UI: http://localhost:3000 (default credentials: admin/admin)

### 5. Add Prometheus Data Source in Grafana

1. Login to Grafana (http://localhost:3000)
2. Go to **Configuration** → **Data Sources**
3. Click **Add data source**
4. Select **Prometheus**
5. Set URL: `http://localhost:9091`
6. Click **Save & Test**

---

## Available Metrics

### Capture Pipeline Metrics

| Metric Name | Type | Description | Unit |
|-------------|------|-------------|------|
| `capture_frames_total` | Counter | Total frames captured from screen | frames |
| `capture_frames_dropped` | Counter | Frames dropped due to backlog | frames |
| `capture_time_ms` | Histogram | Time to capture single frame | ms |
| `encoding_time_ms` | Histogram | Time to encode frame to VP8 | ms |
| `capture_fps` | Gauge | Current frames per second | fps |

**Example Queries:**

```promql
# Current capture FPS
capture_fps

# Capture rate (frames/second)
rate(capture_frames_total[1m])

# Frame drop rate
rate(capture_frames_dropped[1m]) / rate(capture_frames_total[1m]) * 100

# Average capture time (p95)
histogram_quantile(0.95, rate(capture_time_ms_bucket[5m]))

# Average encoding time (p50)
histogram_quantile(0.50, rate(encoding_time_ms_bucket[5m]))
```

### WebRTC Connection Metrics

| Metric Name | Type | Description | Unit |
|-------------|------|-------------|------|
| `webrtc_connections_active` | Gauge | Currently active connections | connections |
| `webrtc_connections_total` | Counter | Total connections established | connections |
| `webrtc_disconnections_total` | Counter | Total disconnections | connections |
| `webrtc_connection_duration_seconds` | Histogram | Connection duration | seconds |
| `webrtc_bitrate_kbps` | Histogram | Video bitrate | kbps |
| `webrtc_packet_loss_percent` | Histogram | Packet loss percentage | % |
| `webrtc_latency_ms` | Histogram | Round-trip time (RTT) | ms |

**Example Queries:**

```promql
# Active connections
webrtc_connections_active

# Connection rate (new connections/minute)
rate(webrtc_connections_total[1m]) * 60

# Average connection duration
avg(webrtc_connection_duration_seconds)

# Current bitrate (p95)
histogram_quantile(0.95, rate(webrtc_bitrate_kbps_bucket[5m]))

# Packet loss percentage (average)
avg(rate(webrtc_packet_loss_percent_sum[5m]) / rate(webrtc_packet_loss_percent_count[5m]))

# Latency (p95)
histogram_quantile(0.95, rate(webrtc_latency_ms_bucket[5m]))
```

### Remote Control Metrics

| Metric Name | Type | Description | Unit |
|-------------|------|-------------|------|
| `remote_control_mouse_moves` | Counter | Mouse move events processed | events |
| `remote_control_mouse_clicks` | Counter | Mouse click events processed | events |
| `remote_control_keyboard_keys` | Counter | Keyboard events processed | events |
| `remote_control_inputs_rejected` | Counter | Inputs rejected (rate limit/validation) | events |
| `remote_control_authorization_requested` | Counter | Authorization requests | requests |
| `remote_control_authorization_granted` | Counter | Authorizations granted | grants |
| `remote_control_authorization_denied` | Counter | Authorizations denied | denials |
| `remote_control_sessions_authorized` | Gauge | Currently authorized sessions | sessions |

**Example Queries:**

```promql
# Input event rate (events/second)
rate(remote_control_mouse_moves[1m]) + rate(remote_control_mouse_clicks[1m]) + rate(remote_control_keyboard_keys[1m])

# Input rejection rate
rate(remote_control_inputs_rejected[5m])

# Authorization success rate
rate(remote_control_authorization_granted[5m]) / rate(remote_control_authorization_requested[5m]) * 100

# Active authorized sessions
remote_control_sessions_authorized
```

### System Resource Metrics

| Metric Name | Type | Description | Unit |
|-------------|------|-------------|------|
| `system_memory_usage` | Gauge | Process memory usage | bytes |
| `system_cpu_usage` | Gauge | Process CPU usage | % |

**Example Queries:**

```promql
# Memory usage (MB)
system_memory_usage / 1024 / 1024

# CPU usage percentage
system_cpu_usage

# Memory growth rate (MB/minute)
delta(system_memory_usage[5m]) / 1024 / 1024 / 5
```

---

## Prometheus Setup

### Production Configuration

**prometheus.yml:**

```yaml
global:
  scrape_interval: 15s
  evaluation_interval: 15s
  external_labels:
    cluster: 'production'
    datacenter: 'us-east-1'

# Alertmanager configuration
alerting:
  alertmanagers:
    - static_configs:
        - targets: ['localhost:9093']

# Load alerting rules
rule_files:
  - 'alerts.yml'

# Scrape configurations
scrape_configs:
  - job_name: 'remotedesktop'
    static_configs:
      - targets: ['server1:9090', 'server2:9090', 'server3:9090']
        labels:
          environment: 'production'

    scrape_interval: 10s
    scrape_timeout: 5s

  - job_name: 'remotedesktop-staging'
    static_configs:
      - targets: ['staging-server:9090']
        labels:
          environment: 'staging'
```

### Service Discovery (Kubernetes)

```yaml
scrape_configs:
  - job_name: 'remotedesktop-k8s'
    kubernetes_sd_configs:
      - role: pod
        namespaces:
          names:
            - remotedesktop

    relabel_configs:
      - source_labels: [__meta_kubernetes_pod_label_app]
        action: keep
        regex: screen-sender

      - source_labels: [__meta_kubernetes_pod_name]
        target_label: instance
```

---

## Grafana Setup

### Import Dashboard

1. Download dashboard JSON: [remotedesktop-dashboard.json](#dashboard-json)
2. In Grafana, click **+** → **Import**
3. Upload JSON file or paste JSON content
4. Select Prometheus data source
5. Click **Import**

### Dashboard Panels

**Overview Row:**
- Active Connections (Gauge)
- Capture FPS (Graph)
- Memory Usage (Graph)
- CPU Usage (Graph)

**Capture Pipeline Row:**
- Frames Captured/Sec (Graph)
- Frame Drop Rate (Graph)
- Capture Time p95 (Graph)
- Encoding Time p95 (Graph)

**WebRTC Performance Row:**
- Bitrate (Graph)
- Packet Loss % (Graph)
- Latency (RTT) (Graph)
- Connection Duration (Heatmap)

**Remote Control Row:**
- Input Events/Sec (Graph)
- Rejection Rate (Graph)
- Authorization Success Rate (Graph)
- Active Authorized Sessions (Stat)

---

## Dashboard Configuration

### Dashboard JSON

```json
{
  "dashboard": {
    "title": "DeskShare Monitoring",
    "panels": [
      {
        "id": 1,
        "title": "Active WebRTC Connections",
        "type": "stat",
        "targets": [
          {
            "expr": "webrtc_connections_active",
            "legendFormat": "Connections"
          }
        ]
      },
      {
        "id": 2,
        "title": "Capture FPS",
        "type": "graph",
        "targets": [
          {
            "expr": "capture_fps",
            "legendFormat": "FPS"
          }
        ],
        "yaxes": [
          {
            "label": "Frames/sec",
            "min": 0,
            "max": 60
          }
        ]
      },
      {
        "id": 3,
        "title": "Frame Drop Rate",
        "type": "graph",
        "targets": [
          {
            "expr": "rate(capture_frames_dropped[1m]) / rate(capture_frames_total[1m]) * 100",
            "legendFormat": "Drop Rate %"
          }
        ],
        "alert": {
          "conditions": [
            {
              "evaluator": {
                "type": "gt",
                "params": [5]
              },
              "query": {
                "params": ["A", "5m", "now"]
              }
            }
          ],
          "name": "High Frame Drop Rate"
        }
      },
      {
        "id": 4,
        "title": "WebRTC Latency (p95)",
        "type": "graph",
        "targets": [
          {
            "expr": "histogram_quantile(0.95, rate(webrtc_latency_ms_bucket[5m]))",
            "legendFormat": "p95 Latency"
          }
        ]
      },
      {
        "id": 5,
        "title": "Memory Usage",
        "type": "graph",
        "targets": [
          {
            "expr": "system_memory_usage / 1024 / 1024",
            "legendFormat": "Memory (MB)"
          }
        ]
      }
    ]
  }
}
```

Save as `grafana-dashboard.json` and import into Grafana.

---

## Alerting Rules

### Prometheus Alert Rules

Create `alerts.yml`:

```yaml
groups:
  - name: remotedesktop_alerts
    interval: 30s
    rules:
      # High frame drop rate
      - alert: HighFrameDropRate
        expr: rate(capture_frames_dropped[5m]) / rate(capture_frames_total[5m]) * 100 > 5
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "High frame drop rate detected"
          description: "Frame drop rate is {{ $value }}% (threshold: 5%)"

      # Low FPS
      - alert: LowFPS
        expr: capture_fps < 20
        for: 1m
        labels:
          severity: warning
        annotations:
          summary: "FPS below threshold"
          description: "Current FPS: {{ $value }} (expected: >20)"

      # High packet loss
      - alert: HighPacketLoss
        expr: avg(rate(webrtc_packet_loss_percent_sum[5m]) / rate(webrtc_packet_loss_percent_count[5m])) > 5
        for: 3m
        labels:
          severity: critical
        annotations:
          summary: "High WebRTC packet loss"
          description: "Packet loss: {{ $value }}% (threshold: 5%)"

      # High latency
      - alert: HighLatency
        expr: histogram_quantile(0.95, rate(webrtc_latency_ms_bucket[5m])) > 200
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "High WebRTC latency"
          description: "p95 Latency: {{ $value }}ms (threshold: 200ms)"

      # High CPU usage
      - alert: HighCPUUsage
        expr: system_cpu_usage > 80
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "High CPU usage"
          description: "CPU usage: {{ $value }}% (threshold: 80%)"

      # High memory usage
      - alert: HighMemoryUsage
        expr: system_memory_usage / 1024 / 1024 > 500
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "High memory usage"
          description: "Memory usage: {{ $value }}MB (threshold: 500MB)"

      # High input rejection rate
      - alert: HighInputRejectionRate
        expr: rate(remote_control_inputs_rejected[5m]) > 10
        for: 2m
        labels:
          severity: warning
        annotations:
          summary: "High remote control input rejection rate"
          description: "{{ $value }} inputs/sec rejected (possible attack or misconfiguration)"

      # Authorization denial spike
      - alert: AuthorizationDenialSpike
        expr: rate(remote_control_authorization_denied[5m]) > 5
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: "Spike in authorization denials"
          description: "{{ $value }} denials/sec (possible unauthorized access attempts)"
```

### Alertmanager Configuration

Create `alertmanager.yml`:

```yaml
global:
  resolve_timeout: 5m

route:
  group_by: ['alertname', 'cluster']
  group_wait: 10s
  group_interval: 10s
  repeat_interval: 12h
  receiver: 'email-notifications'
  routes:
    - match:
        severity: critical
      receiver: 'pagerduty'

receivers:
  - name: 'email-notifications'
    email_configs:
      - to: 'ops-team@example.com'
        from: 'prometheus@example.com'
        smarthost: 'smtp.gmail.com:587'
        auth_username: 'prometheus@example.com'
        auth_password: 'your-password'

  - name: 'pagerduty'
    pagerduty_configs:
      - service_key: 'your-pagerduty-key'
```

---

## Troubleshooting

### Metrics Not Showing in Prometheus

**Problem:** Prometheus cannot scrape metrics from ScreenSenderApp.

**Solutions:**

1. **Check metrics endpoint:**
   ```bash
   curl http://localhost:9090/metrics
   ```
   Should return Prometheus-formatted metrics.

2. **Verify ScreenSenderApp is running:**
   ```bash
   netstat -an | findstr 9090
   ```
   Should show listener on port 9090.

3. **Check Prometheus targets:**
   - Open http://localhost:9091/targets
   - Verify target status is "UP"
   - If "DOWN", check firewall rules

4. **Verify configuration:**
   ```json
   {
     "OpenTelemetry": {
       "Enabled": true  // Must be true
     }
   }
   ```

### Grafana Dashboard Shows "No Data"

**Problem:** Dashboard panels show "No data".

**Solutions:**

1. **Verify Prometheus data source:**
   - Configuration → Data Sources → Prometheus
   - Click "Save & Test"
   - Should show "Data source is working"

2. **Check time range:**
   - Ensure dashboard time range is set correctly
   - Try "Last 5 minutes" or "Last 15 minutes"

3. **Run query in Prometheus:**
   - Open http://localhost:9091
   - Execute query manually (e.g., `capture_fps`)
   - Verify data exists

4. **Check metric names:**
   - Metric names are case-sensitive
   - Use autocomplete in Grafana query editor

### High Memory Usage

**Problem:** ScreenSenderApp memory usage grows over time.

**Diagnostic:**

```promql
# Memory growth rate (MB/hour)
delta(system_memory_usage[1h]) / 1024 / 1024
```

**Solutions:**

1. **Reduce capture FPS:**
   ```json
   {
     "Capture": {
       "TargetFps": 15  // Instead of 30 or 60
     }
   }
   ```

2. **Use pooled converter:**
   ```json
   {
     "Capture": {
       "ConverterType": "Pooled"
     }
   }
   ```

3. **Check for memory leaks:**
   - Review code for undisposed objects
   - Use dotMemory profiler for analysis

### Metrics Delayed or Stale

**Problem:** Metrics update slowly or show stale data.

**Solutions:**

1. **Reduce scrape interval:**
   ```yaml
   global:
     scrape_interval: 5s  // Instead of 15s
   ```

2. **Check ScreenSenderApp performance:**
   - High CPU usage may delay metric collection
   - Reduce workload or scale horizontally

3. **Verify clock sync:**
   - Ensure server clocks are synchronized (NTP)
   - Timestamp mismatches cause data gaps

---

## Best Practices

### Production Deployment

✅ **DO:**
- Set scrape_interval to 10-15 seconds (balance accuracy vs overhead)
- Use service discovery (Kubernetes, Consul) for dynamic scaling
- Enable Prometheus remote storage for long-term retention
- Set up Alertmanager for critical alerts (packet loss, high latency)
- Use Grafana variables for multi-instance dashboards
- Export dashboards as JSON for version control

❌ **DON'T:**
- Scrape too frequently (<5s) - increases load without benefit
- Store Prometheus data on slow disks - use SSD
- Ignore alerts - review and tune thresholds regularly
- Expose metrics endpoint publicly - use authentication/firewall
- Query Prometheus directly from applications - use Grafana or dedicated tools

### Metric Collection

- **Counters** for cumulative values (frames captured, connections total)
- **Gauges** for point-in-time values (active connections, current FPS)
- **Histograms** for distributions (latency, bitrate, encoding time)

### Retention Policy

- **Development:** 7 days
- **Staging:** 30 days
- **Production:** 90 days (hot) + 1 year (cold storage)

---

## References

- **OpenTelemetry Specification:** https://opentelemetry.io/docs/
- **Prometheus Documentation:** https://prometheus.io/docs/
- **Grafana Documentation:** https://grafana.com/docs/
- **PromQL Guide:** https://prometheus.io/docs/prometheus/latest/querying/basics/

---

**Last Updated:** November 2025
**Version:** 1.0
**Maintainer:** DeskShare Team
