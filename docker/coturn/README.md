# coturn TURN Server Setup

Docker setup pro coturn TURN server pro DeskShare.

## 🎯 Co je TURN server?

TURN (Traversal Using Relays around NAT) server slouží jako fallback relay pro WebRTC komunikaci, když přímé P2P spojení není možné kvůli firewallu nebo NAT.

**Kdy je potřeba?**
- Klient je za symetrickým NAT
- Firemní firewall blokuje UDP
- Nelze vytvořit přímé P2P spojení

## 🚀 Rychlý start

### 1. Konfigurace

Editujte `turnserver.conf`:

```bash
# Nastavte vaši veřejnou IP
external-ip=YOUR_PUBLIC_IP_HERE

# Změňte výchozí hesla (DŮLEŽITÉ pro produkci!)
user=youruser:SecurePassword123!
cli-password=AdminPassword456!
```

### 2. Spuštění

```bash
# Start TURN serveru
docker-compose up -d

# Zobrazit logy
docker-compose logs -f

# Zastavit
docker-compose down
```

### 3. Ověření

Test připojení:

```bash
# Install turnutils (na klientovi)
sudo apt-get install coturn-utils

# Test TURN
turnutils_uclient -v -u remoteuser -w RemotePass123! YOUR_SERVER_IP
```

Očekávaný výstup:
```
0: Total connect time is 0
0: start_mclient: msz=2, tot_send_msgs=0, tot_recv_msgs=0, tot_send_bytes ~ 0, tot_recv_bytes ~ 0
...
Total lost packets 0 (0.000000%), total send dropped 0 (0.000000%)
```

## 📊 Monitoring

### Logy

```bash
# Live logs
docker-compose logs -f coturn

# Nebo v souboru
tail -f logs/turnserver.log
```

### CLI Admin interface

```bash
# Připojit se k CLI
telnet localhost 5766
# Password: AdminPass123!

# Příkazy:
# ps - Show sessions
# u - Show users
# help - Show all commands
```

### Metriky (pokud povoleno Prometheus)

```bash
curl http://localhost:9641/metrics
```

## 🔧 Konfigurace pro WebClient

Aktualizujte `src/WebClient/client.js`:

```javascript
const config = {
    iceServers: [
        // STUN servers (zdarma)
        { urls: 'stun:stun.l.google.com:19302' },

        // Váš TURN server
        {
            urls: 'turn:YOUR_SERVER_IP:3478',
            username: 'remoteuser',
            credential: 'RemotePass123!'
        },
        // TLS/TURNS (doporučeno pro produkci)
        {
            urls: 'turns:YOUR_SERVER_IP:5349',
            username: 'remoteuser',
            credential: 'RemotePass123!'
        }
    ]
};
```

## 🔒 Bezpečnost

### Produkční checklist

- [ ] Změnit výchozí user/password
- [ ] Povolit pouze TLS/TURNS (zakázat plain TURN)
- [ ] Nastavit TLS certifikáty (Let's Encrypt)
- [ ] Omezit allowed-peer-ip na známé rozsahy
- [ ] Povolit firewall pravidla pouze pro potřebné porty
- [ ] Použít Redis pro dynamické credentials
- [ ] Nastavit rate limiting (user-quota, max-bps)
- [ ] Povolit monitoring (Prometheus + Grafana)

### Firewall konfigurace

```bash
# Ubuntu/Debian (ufw)
sudo ufw allow 3478/tcp   # TURN
sudo ufw allow 3478/udp   # TURN
sudo ufw allow 5349/tcp   # TURNS
sudo ufw allow 5349/udp   # TURNS
sudo ufw allow 49152:65535/udp  # Media relay

# Or firewalld (CentOS/RHEL)
sudo firewall-cmd --permanent --add-port=3478/tcp
sudo firewall-cmd --permanent --add-port=3478/udp
sudo firewall-cmd --permanent --add-port=5349/tcp
sudo firewall-cmd --permanent --add-port=5349/udp
sudo firewall-cmd --permanent --add-port=49152-65535/udp
sudo firewall-cmd --reload
```

### TLS certifikáty (Let's Encrypt)

```bash
# Install certbot
sudo apt-get install certbot

# Get certificate
sudo certbot certonly --standalone -d turn.yourdomain.com

# Copy to coturn
sudo cp /etc/letsencrypt/live/turn.yourdomain.com/fullchain.pem ./cert.pem
sudo cp /etc/letsencrypt/live/turn.yourdomain.com/privkey.pem ./key.pem

# Uncomment v turnserver.conf:
# cert=/etc/coturn/cert.pem
# pkey=/etc/coturn/key.pem
# fingerprint

# Restart
docker-compose restart
```

## 📈 Performance tuning

Pro high-load scénáře:

```conf
# V turnserver.conf

# Limit per-session bandwidth (1 Mbps)
max-bps=1000000

# Total capacity (unlimited)
bps-capacity=0

# Max concurrent sessions
total-quota=1000

# Max sessions per user
user-quota=10

# Optimalizace TCP
# tcp-relay-connections-per-endpoint=10

# Worker threads (CPU cores)
# relay-threads=4
```

## 🐛 Troubleshooting

### TURN connection failed

```bash
# Check if server is listening
netstat -tuln | grep 3478

# Test from external network
turnutils_uclient -v -u remoteuser -w RemotePass123! PUBLIC_IP
```

### Permission denied (ports)

```bash
# Pokud běží na portu <1024, potřebujete sudo/CAP_NET_BIND_SERVICE
docker run --cap-add=NET_BIND_SERVICE ...
```

### High CPU usage

```bash
# Check active sessions
docker exec remotedesktop-coturn turnadmin -l

# Reduce verbosity v turnserver.conf
# Zakomentovat: verbose
```

## 📚 Reference

- [coturn Documentation](https://github.com/coturn/coturn)
- [STUN/TURN RFC](https://tools.ietf.org/html/rfc5766)
- [WebRTC ICE](https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Connectivity)

---

**Next**: [Run Guide](../../docs/run-guide.md) | [README](../../README.md)
