# Azure Deployment Guide - SignalingServer s Azure Table Storage

Praktický návod pro nasazení DeskShare SignalingServer do Azure s perzistentním úložištěm.

## Obsah

1. [Přehled řešení](#přehled-řešení)
2. [Rychlý start (15 minut)](#rychlý-start-15-minut)
3. [Detailní konfigurace](#detailní-konfigurace)
4. [Cenové úvahy](#cenové-úvahy)
5. [Monitoring](#monitoring)
6. [Troubleshooting](#troubleshooting)

---

## Přehled řešení

### Co jsme implementovali

SignalingServer nyní podporuje **2 storage backendy**:

1. **In-Memory** (výchozí) - Data v paměti, ztrácí se při restartu
2. **Azure Table Storage** - Perzistentní, sdílené mezi instancemi

### Architektura v Azure

```
┌─────────────────────┐
│  Screen Sender App  │
│  (Desktop/Konzole)  │
└──────────┬──────────┘
           │ HTTPS POST /register
           │ (Passkey každých 5 min)
           ▼
┌─────────────────────────────────────┐
│   Azure App Service / VM            │
│   SignalingServer (ASP.NET Core)    │
│                                     │
│   ┌─────────────────────────────┐  │
│   │ IServerSessionStorage        │  │
│   │  (Pluggable Backend)         │  │
│   └─────────────┬───────────────┘  │
│                 │                   │
│   ┌─────────────▼────────────────┐ │
│   │ AzureTableSessionStorage     │ │
│   └─────────────┬────────────────┘ │
└─────────────────┼──────────────────┘
                  │ SDK
                  ▼
     ┌────────────────────────────┐
     │  Azure Table Storage       │
     │                            │
     │  Tables:                   │
     │  • ServerSessions          │
     │  • TrustedClients          │
     └────────────────────────────┘

           ▲
           │ WebSocket /signal
           │
┌──────────┴──────────┐
│  Web Client         │
│  (Browser/Viewer)   │
└─────────────────────┘
```

### Výhody Azure Table Storage

✅ **Perzistence**: Data přežijí restart serveru
✅ **Multi-instance**: Více serverů sdílí stejná data
✅ **Škálovatelnost**: Tisíce operací/sekundu
✅ **Nízké náklady**: ~€2/měsíc pro běžné použití
✅ **Zero code changes**: Stačí změnit konfiguraci

---

## Rychlý start (15 minut)

### Krok 1: Vytvoření Azure Storage Account (5 min)

#### Varianta A: Azure Portal (GUI)

1. Přihlaste se na https://portal.azure.com
2. Klikněte **Create a resource** → **Storage account**
3. Vyplňte:
   - **Resource group**: `rg-remotedesktop` (nebo vytvořte novou)
   - **Storage account name**: `stremotedesktop` (musí být globálně unikátní!)
   - **Region**: `West Europe` (nebo nejbližší region)
   - **Performance**: `Standard`
   - **Redundancy**: `LRS` (Locally Redundant Storage - nejlevnější)
4. Klikněte **Review + Create** → **Create**
5. Počkejte ~1 minutu na vytvoření

#### Varianta B: Azure CLI (Terminal)

```bash
# Přihlášení
az login

# Vytvoření resource group (pokud neexistuje)
az group create \
  --name rg-remotedesktop \
  --location westeurope

# Vytvoření storage account
az storage account create \
  --name stremotedesktop \
  --resource-group rg-remotedesktop \
  --location westeurope \
  --sku Standard_LRS \
  --kind StorageV2
```

### Krok 2: Získání Connection String (2 min)

#### Varianta A: Azure Portal

1. V Azure Portal otevřete vytvořený Storage Account
2. V levém menu klikněte **Access keys**
3. U **key1** klikněte **Show** a zkopírujte **Connection string**

#### Varianta B: Azure CLI

```bash
az storage account show-connection-string \
  --name stremotedesktop \
  --resource-group rg-remotedesktop \
  --query connectionString -o tsv
```

Výstup vypadá takto:
```
DefaultEndpointsProtocol=https;AccountName=stremotedesktop;AccountKey=xxxxx...;EndpointSuffix=core.windows.net
```

### Krok 3: Konfigurace SignalingServer (3 min)

Otevřete `src/SignalingServer/appsettings.json` a upravte:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Cors": {
    "AllowedOrigins": [
      "https://localhost:5001",
      "https://localhost:7001",
      "https://your-domain.com"
    ]
  },
  "Storage": {
    "Type": "AzureTableStorage",
    "AzureTableStorage": {
      "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=stremotedesktop;AccountKey=xxxxx...;EndpointSuffix=core.windows.net"
    }
  },
  "Stun": {
    "Port": 3478
  },
  "Turn": {
    "Enabled": false,
    "Port": 3479,
    "Realm": "DeskShare",
    "EnableTestUser": false,
    "MinRelayPort": 49152,
    "MaxRelayPort": 65535
  }
}
```

⚠️ **BEZPEČNOST**: Pro produkci **NIKDY** neukládejte connection string přímo do `appsettings.json`!
Použijte místo toho:

#### Option 1: Environment Variables (DOPORUČENO)

```bash
# Windows
set Storage__AzureTableStorage__ConnectionString="DefaultEndpointsProtocol=https;..."

# Linux/macOS
export Storage__AzureTableStorage__ConnectionString="DefaultEndpointsProtocol=https;..."
```

Nebo v `appsettings.Production.json`:
```json
{
  "Storage": {
    "Type": "AzureTableStorage",
    "AzureTableStorage": {
      "ConnectionString": ""  // Prázdné, přečte se z env variable
    }
  }
}
```

#### Option 2: Azure Key Vault (PRO PRODUKCI)

```bash
# Vytvoření Key Vault
az keyvault create \
  --name kv-remotedesktop \
  --resource-group rg-remotedesktop \
  --location westeurope

# Uložení connection string do Key Vault
az keyvault secret set \
  --vault-name kv-remotedesktop \
  --name "StorageConnectionString" \
  --value "DefaultEndpointsProtocol=https;..."
```

V kódu (`Program.cs`):
```csharp
// Přidat NuGet: Azure.Identity, Azure.Security.KeyVault.Secrets
var keyVaultUrl = "https://kv-remotedesktop.vault.azure.net/";
var client = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
var connectionString = await client.GetSecretAsync("StorageConnectionString");
```

### Krok 4: Testování lokálně (5 min)

```bash
cd src/SignalingServer
dotnet run
```

Očekávaný výstup:
```
[12:34:56 INF] Storage backend: AzureTableStorage
[12:34:56 INF] Azure Table Storage initialized (tables: ServerSessions, TrustedClients)
[12:34:56 INF] In-memory storage initialized
[12:34:56 INF] TURN server is DISABLED - Only P2P connections will be available
[12:34:57 INF] Now listening on: http://localhost:5151
```

Otevřete browser na `http://localhost:5151/health`:
```json
{
  "Status": "Healthy",
  "Connections": 0,
  "Timestamp": "2025-01-17T12:35:00Z"
}
```

Zkontrolujte Azure Portal → Storage Account → Tables:
- Měly by se automaticky vytvořit 2 tabulky:
  - `ServerSessions`
  - `TrustedClients`

---

## Detailní konfigurace

### Konfigurace pro různá prostředí

#### Development (Lokální Azure Storage Emulator)

**Windows**: Nainstalujte [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite)

```bash
npm install -g azurite
azurite --silent
```

`appsettings.Development.json`:
```json
{
  "Storage": {
    "Type": "AzureTableStorage",
    "AzureTableStorage": {
      "ConnectionString": "UseDevelopmentStorage=true"
    }
  }
}
```

#### Staging

`appsettings.Staging.json`:
```json
{
  "Storage": {
    "Type": "AzureTableStorage",
    "AzureTableStorage": {
      "ConnectionString": ""  // Z environment variable
    }
  },
  "Cors": {
    "AllowedOrigins": [
      "https://staging.yourdomain.com"
    ]
  }
}
```

#### Production

`appsettings.Production.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Error"
    }
  },
  "Storage": {
    "Type": "AzureTableStorage",
    "AzureTableStorage": {
      "ConnectionString": ""  // Z Key Vault nebo env variable
    }
  },
  "Cors": {
    "AllowedOrigins": [
      "https://yourdomain.com",
      "https://www.yourdomain.com"
    ]
  }
}
```

### Nasazení do Azure

#### Option 1: Azure App Service (DOPORUČENO)

**Výhody**: Jednoduchý deployment, automatické SSL, integrované logging

```bash
# 1. Vytvoření App Service Plan
az appservice plan create \
  --name plan-remotedesktop \
  --resource-group rg-remotedesktop \
  --sku B1 \
  --is-linux false

# 2. Vytvoření Web App
az webapp create \
  --name app-remotedesktop-signaling \
  --resource-group rg-remotedesktop \
  --plan plan-remotedesktop \
  --runtime "DOTNET|8.0"

# 3. Nastavení connection string jako App Setting
az webapp config appsettings set \
  --name app-remotedesktop-signaling \
  --resource-group rg-remotedesktop \
  --settings \
    Storage__AzureTableStorage__ConnectionString="<YOUR_CONNECTION_STRING>" \
    Storage__Type="AzureTableStorage"

# 4. Deployment (z Visual Studio nebo CLI)
cd src/SignalingServer
dotnet publish -c Release

# Zip publikovaných souborů
cd bin/Release/net8.0/publish
Compress-Archive -Path * -DestinationPath ../../../../../deploy.zip

# Deploy do Azure
az webapp deployment source config-zip \
  --resource-group rg-remotedesktop \
  --name app-remotedesktop-signaling \
  --src deploy.zip
```

URL: `https://app-remotedesktop-signaling.azurewebsites.net`

#### Option 2: Azure Container Instances (Levnější)

```dockerfile
# Dockerfile (v root projektu)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/SignalingServer/RemoteDesktop.SignalingServer.csproj", "SignalingServer/"]
COPY ["src/Core/RemoteDesktop.Core.csproj", "Core/"]
COPY ["src/Stun/RemoteDesktop.Stun.csproj", "Stun/"]
COPY ["src/Turn/RemoteDesktop.Turn.csproj", "Turn/"]
RUN dotnet restore "SignalingServer/RemoteDesktop.SignalingServer.csproj"
COPY src/ .
WORKDIR "/src/SignalingServer"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RemoteDesktop.SignalingServer.dll"]
```

```bash
# Build Docker image
docker build -t signaling-server:latest .

# Push do Azure Container Registry
az acr create --name acrremotedesktop --resource-group rg-remotedesktop --sku Basic
az acr login --name acrremotedesktop
docker tag signaling-server:latest acrremotedesktop.azurecr.io/signaling-server:latest
docker push acrremotedesktop.azurecr.io/signaling-server:latest

# Deploy do ACI
az container create \
  --resource-group rg-remotedesktop \
  --name aci-signaling-server \
  --image acrremotedesktop.azurecr.io/signaling-server:latest \
  --dns-name-label signaling-remotedesktop \
  --ports 80 \
  --cpu 1 --memory 1.5 \
  --environment-variables \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__Type=AzureTableStorage \
    Storage__AzureTableStorage__ConnectionString="<CONNECTION_STRING>"
```

URL: `http://signaling-remotedesktop.westeurope.azurecontainer.io`

#### Option 3: Azure VM (Nejvíc kontroly)

```bash
# Vytvoření VM
az vm create \
  --resource-group rg-remotedesktop \
  --name vm-signaling-server \
  --image UbuntuLTS \
  --size Standard_B2s \
  --admin-username azureuser \
  --generate-ssh-keys

# Otevření portu 80
az vm open-port --port 80 --resource-group rg-remotedesktop --name vm-signaling-server

# SSH do VM
ssh azureuser@<VM_PUBLIC_IP>

# Instalace .NET 8
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0

# Upload aplikace (pomocí SCP)
scp -r src/SignalingServer/bin/Release/net8.0/publish/* azureuser@<VM_IP>:/home/azureuser/signaling/

# Vytvoření systemd service
sudo nano /etc/systemd/system/signaling.service
```

`/etc/systemd/system/signaling.service`:
```ini
[Unit]
Description=RemoteDesktop SignalingServer
After=network.target

[Service]
Type=notify
WorkingDirectory=/home/azureuser/signaling
ExecStart=/home/azureuser/.dotnet/dotnet RemoteDesktop.SignalingServer.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=signaling-server
User=azureuser
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Storage__Type=AzureTableStorage
Environment=Storage__AzureTableStorage__ConnectionString=<CONNECTION_STRING>

[Install]
WantedBy=multi-user.target
```

```bash
# Start service
sudo systemctl daemon-reload
sudo systemctl enable signaling.service
sudo systemctl start signaling.service
sudo systemctl status signaling.service
```

---

## Cenové úvahy

### Měsíční náklady (odhad pro 100 aktivních uživatelů)

| Služba | Tier | Cena/měsíc (EUR) | Poznámka |
|--------|------|------------------|----------|
| **Azure Table Storage** | Standard LRS | €2 | 100k transakcí + 5GB úložiště |
| **Azure App Service B1** | Basic | €50 | 1 core, 1.75GB RAM, always-on |
| **Azure ACI** | 1 vCPU, 1.5GB | €30 | Pay-per-second billing |
| **Azure VM B2s** | Standard | €35 | 2 vCPU, 4GB RAM, Linux |
| **CELKEM (App Service)** | | **€52** | |
| **CELKEM (ACI)** | | **€32** | Nejlevnější varianta |
| **CELKEM (VM)** | | **€37** | Nejvíce kontroly |

### Detailní breakdown - Azure Table Storage

Pro 100 uživatelů s průměrným použitím:

**Operace za měsíc:**
- Registrace serveru: 100 serverů × 12 registrací/hod × 24 hod × 30 dní = **864,000 write operací**
- Autentizace klientů: 100 klientů × 2 auth/den × 30 dní = **6,000 read operací**
- Online status checks: 100 klientů × 120 checks/hod × 24 hod × 30 dní = **8,640,000 read operací**
- Cleanup: 1 cleanup/5min × 12/hod × 24 hod × 30 dní = **8,640 scan operací**

**Celkem**: ~9.5M operací/měsíc

**Cena Azure Table Storage:**
- První 10M operací: ZDARMA
- **Úložiště**: 5GB × €0.05/GB = **€0.25/měsíc**
- **Celkem**: **€0.25/měsíc**

🎉 **Téměř zdarma pro malé až střední nasazení!**

### Optimalizace nákladů

1. **Použijte ACI místo App Service**: Úspora ~€20/měsíc
2. **Zvažte VM spot instances**: Úspora až 90% (ale může být odebrána)
3. **Table Storage je levnější než CosmosDB**: Úspora €100+/měsíc
4. **In-memory mode pro testing**: €0 (ale bez perzistence)

---

## Monitoring

### Application Insights (DOPORUČENO)

```bash
# Vytvoření Application Insights
az monitor app-insights component create \
  --app appinsights-remotedesktop \
  --location westeurope \
  --resource-group rg-remotedesktop \
  --application-type web

# Získání instrumentation key
az monitor app-insights component show \
  --app appinsights-remotedesktop \
  --resource-group rg-remotedesktop \
  --query instrumentationKey -o tsv
```

`appsettings.Production.json`:
```json
{
  "ApplicationInsights": {
    "InstrumentationKey": "<YOUR_KEY>",
    "EnableAdaptiveSampling": true
  }
}
```

`Program.cs` (přidat):
```csharp
builder.Services.AddApplicationInsightsTelemetry();
```

### Užitečné Kusto queries (Application Insights)

**1. Server registrations v posledních 24 hodinách:**
```kusto
traces
| where message contains "Server registered"
| project timestamp, serverId = tostring(customDimensions.ServerId), validTo = tostring(customDimensions.ValidTo)
| order by timestamp desc
| take 100
```

**2. Authentication failures:**
```kusto
traces
| where severityLevel >= 2 and message contains "Authentication failed"
| summarize count() by bin(timestamp, 5m), reason = tostring(customDimensions.Reason)
| render timechart
```

**3. Active sessions:**
```kusto
traces
| where message contains "Client authenticated"
| summarize activeClients = dcount(tostring(customDimensions.ClientId)) by bin(timestamp, 1h)
| render timechart
```

**4. Azure Table Storage performance:**
```kusto
dependencies
| where type == "Azure Table"
| summarize
    avgDuration = avg(duration),
    p95Duration = percentile(duration, 95),
    operations = count()
  by name, bin(timestamp, 5m)
| render timechart
```

### Health Check Endpoint

SignalingServer již obsahuje `/health` endpoint:

```bash
curl http://localhost:5151/health
```

```json
{
  "Status": "Healthy",
  "Connections": 5,
  "Timestamp": "2025-01-17T14:30:00Z"
}
```

Nastavte Azure Monitor alert:
```bash
az monitor metrics alert create \
  --name "SignalingServer Down" \
  --resource-group rg-remotedesktop \
  --scopes /subscriptions/<sub-id>/resourceGroups/rg-remotedesktop/providers/Microsoft.Web/sites/app-remotedesktop-signaling \
  --condition "avg Percentage CPU > 80" \
  --window-size 5m \
  --evaluation-frequency 1m \
  --action <action-group-id>
```

---

## Troubleshooting

### Problém: "Storage:AzureTableStorage:ConnectionString not configured"

**Příčina**: Connection string není nastaven nebo má špatný formát.

**Řešení**:
```bash
# Zkontrolujte environment variables
echo $Storage__AzureTableStorage__ConnectionString  # Linux/macOS
echo %Storage__AzureTableStorage__ConnectionString%  # Windows

# Nebo zkontrolujte appsettings.json
cat src/SignalingServer/appsettings.json | grep ConnectionString
```

### Problém: "Failed to create table: Forbidden"

**Příčina**: Storage Account má firewall nebo connection string nemá správná oprávnění.

**Řešení**:
```bash
# Azure Portal → Storage Account → Networking
# Přidejte svou IP nebo povolte "Allow Azure services"

# Nebo použijte SAS token místo connection string
az storage account generate-sas \
  --account-name stremotedesktop \
  --services t \
  --resource-types sco \
  --permissions rwdlac \
  --expiry 2026-01-01T00:00:00Z \
  --https-only
```

### Problém: "Table already exists" při spuštění

**Příčina**: Tabulky `ServerSessions` a `TrustedClients` již existují z předchozího běhu.

**Řešení**: To je normální! `CreateIfNotExists()` je idempotentní. Pokud chcete začít znovu:

```bash
# Azure Portal → Storage Account → Tables → ServerSessions → Delete
# Nebo CLI:
az storage table delete --name ServerSessions --account-name stremotedesktop
az storage table delete --name TrustedClients --account-name stremotedesktop
```

### Problém: Slow performance / timeout

**Příčina**: Storage Account je v jiném regionu než aplikace.

**Řešení**:
```bash
# Zkontrolujte region
az storage account show --name stremotedesktop --query location

# Přesuňte App Service do stejného regionu
# (Nebo vytvořte nový Storage Account ve správném regionu)
```

### Problém: Vysoké náklady

**Příčina**: Příliš mnoho read operací (např. online status check každou sekundu).

**Řešení**: Upravte frekvenci v `OnlineStatusMonitor.cs`:
```csharp
// Bylo: 30 sekund
await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

// Nově: 2 minuty (4× méně operací)
await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
```

### Debugging lokálně

```bash
# Spusťte s detailním loggingem
cd src/SignalingServer
ASPNETCORE_ENVIRONMENT=Development dotnet run --verbosity detailed

# Nebo použijte Azure Storage Explorer
# Download: https://azure.microsoft.com/en-us/products/storage/storage-explorer/
# Připojte se pomocí connection string a prohlížejte tabulky
```

---

## Další kroky

### Po úspěšném nasazení:

1. ✅ **Nastavte custom domain** (např. `signaling.yourdomain.com`)
   ```bash
   az webapp config hostname add \
     --webapp-name app-remotedesktop-signaling \
     --resource-group rg-remotedesktop \
     --hostname signaling.yourdomain.com
   ```

2. ✅ **Zapněte SSL/HTTPS** (automatické s App Service managed certificate)
   ```bash
   az webapp config ssl bind \
     --name app-remotedesktop-signaling \
     --resource-group rg-remotedesktop \
     --certificate-thumbprint auto \
     --ssl-type SNI
   ```

3. ✅ **Nastavte backup policy**
   ```bash
   az backup policy create \
     --resource-group rg-remotedesktop \
     --vault-name vault-remotedesktop \
     --name daily-backup \
     --backup-management-type AzureStorage
   ```

4. ✅ **Dokumentujte URL pro klienty**
   - Aktualizujte `ScreenSenderService` configuration
   - Aktualizujte WebClient signaling URL
   - Otestujte end-to-end připojení

---

## Doporučené nastavení pro produkci

### appsettings.Production.json (finální verze)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Error",
      "RemoteDesktop": "Information"
    }
  },
  "AllowedHosts": "signaling.yourdomain.com",
  "Cors": {
    "AllowedOrigins": [
      "https://yourdomain.com",
      "https://www.yourdomain.com"
    ]
  },
  "Storage": {
    "Type": "AzureTableStorage",
    "AzureTableStorage": {
      "ConnectionString": ""
    }
  },
  "Stun": {
    "Port": 3478
  },
  "Turn": {
    "Enabled": true,
    "Port": 3479,
    "Realm": "yourdomain.com",
    "EnableTestUser": false,
    "MinRelayPort": 49152,
    "MaxRelayPort": 65535
  },
  "ApplicationInsights": {
    "InstrumentationKey": "<FROM_KEY_VAULT>",
    "EnableAdaptiveSampling": true
  }
}
```

### Environment variables (Azure App Service Configuration)

```bash
Storage__Type=AzureTableStorage
Storage__AzureTableStorage__ConnectionString=<FROM_KEY_VAULT>
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:80
ApplicationInsights__InstrumentationKey=<FROM_KEY_VAULT>
```

---

## Závěr

Nyní máte plně funkční SignalingServer s Azure Table Storage backendem, který je:

- ✅ **Škálovatelný**: Zvládne stovky až tisíce současných uživatelů
- ✅ **Spolehlivý**: Data přežijí restart, multi-instance ready
- ✅ **Levný**: ~€32-52/měsíc pro malé až střední nasazení
- ✅ **Monitorovaný**: Application Insights, health checks, alerting
- ✅ **Bezpečný**: HTTPS, CORS, Key Vault pro secrets

### Potřebujete pomoc?

- **Dokumentace**: [Azure Table Storage](https://learn.microsoft.com/en-us/azure/storage/tables/)
- **GitHub Issues**: https://github.com/your-repo/issues
- **Azure Support**: https://portal.azure.com/#blade/Microsoft_Azure_Support/HelpAndSupportBlade

---

**Happy Deploying! 🚀**
