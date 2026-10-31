# Azure Deployment Guide - SignalingServer with Azure Table Storage

A practical guide for deploying the DeskShare SignalingServer to Azure with persistent storage.

## Contents

1. [Solution overview](#solution-overview)
2. [Quick start (15 minutes)](#quick-start-15-minutes)
3. [Detailed configuration](#detailed-configuration)
4. [Cost considerations](#cost-considerations)
5. [Monitoring](#monitoring)
6. [Troubleshooting](#troubleshooting)

---

## Solution overview

### What we implemented

SignalingServer now supports **2 storage backends**:

1. **In-Memory** (default) - Data kept in memory, lost on restart
2. **Azure Table Storage** - Persistent, shared between instances

### Architecture in Azure

```
┌─────────────────────┐
│  Screen Sender App  │
│  (Desktop/Console)  │
└──────────┬──────────┘
           │ HTTPS POST /register
           │ (Passkey every 5 min)
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

### Benefits of Azure Table Storage

✅ **Persistence**: Data survives a server restart
✅ **Multi-instance**: Multiple servers share the same data
✅ **Scalability**: Thousands of operations/second
✅ **Low cost**: ~€2/month for typical usage
✅ **Zero code changes**: Just change the configuration

---

## Quick start (15 minutes)

### Step 1: Create an Azure Storage Account (5 min)

#### Option A: Azure Portal (GUI)

1. Sign in at https://portal.azure.com
2. Click **Create a resource** → **Storage account**
3. Fill in:
   - **Resource group**: `rg-remotedesktop` (or create a new one)
   - **Storage account name**: `stremotedesktop` (must be globally unique!)
   - **Region**: `West Europe` (or the nearest region)
   - **Performance**: `Standard`
   - **Redundancy**: `LRS` (Locally Redundant Storage - cheapest)
4. Click **Review + Create** → **Create**
5. Wait ~1 minute for it to be created

#### Option B: Azure CLI (Terminal)

```bash
# Sign in
az login

# Create the resource group (if it does not exist)
az group create \
  --name rg-remotedesktop \
  --location westeurope

# Create the storage account
az storage account create \
  --name stremotedesktop \
  --resource-group rg-remotedesktop \
  --location westeurope \
  --sku Standard_LRS \
  --kind StorageV2
```

### Step 2: Get the Connection String (2 min)

#### Option A: Azure Portal

1. In the Azure Portal, open the storage account you created
2. Click **Access keys** in the left menu
3. Under **key1** click **Show** and copy the **Connection string**

#### Option B: Azure CLI

```bash
az storage account show-connection-string \
  --name stremotedesktop \
  --resource-group rg-remotedesktop \
  --query connectionString -o tsv
```

The output looks like this:
```
DefaultEndpointsProtocol=https;AccountName=stremotedesktop;AccountKey=xxxxx...;EndpointSuffix=core.windows.net
```

### Step 3: Configure SignalingServer (3 min)

Open `src/SignalingServer/appsettings.json` and edit:

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

⚠️ **SECURITY**: In production, **NEVER** store the connection string directly in `appsettings.json`!
Use one of these instead:

#### Option 1: Environment Variables (RECOMMENDED)

```bash
# Windows
set Storage__AzureTableStorage__ConnectionString="DefaultEndpointsProtocol=https;..."

# Linux/macOS
export Storage__AzureTableStorage__ConnectionString="DefaultEndpointsProtocol=https;..."
```

Or in `appsettings.Production.json`:
```json
{
  "Storage": {
    "Type": "AzureTableStorage",
    "AzureTableStorage": {
      "ConnectionString": ""  // Empty, read from the env variable
    }
  }
}
```

#### Option 2: Azure Key Vault (FOR PRODUCTION)

```bash
# Create the Key Vault
az keyvault create \
  --name kv-remotedesktop \
  --resource-group rg-remotedesktop \
  --location westeurope

# Store the connection string in the Key Vault
az keyvault secret set \
  --vault-name kv-remotedesktop \
  --name "StorageConnectionString" \
  --value "DefaultEndpointsProtocol=https;..."
```

In code (`Program.cs`):
```csharp
// Add NuGet: Azure.Identity, Azure.Security.KeyVault.Secrets
var keyVaultUrl = "https://kv-remotedesktop.vault.azure.net/";
var client = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
var connectionString = await client.GetSecretAsync("StorageConnectionString");
```

### Step 4: Test locally (5 min)

```bash
cd src/SignalingServer
dotnet run
```

Expected output:
```
[12:34:56 INF] Storage backend: AzureTableStorage
[12:34:56 INF] Azure Table Storage initialized (tables: ServerSessions, TrustedClients)
[12:34:56 INF] In-memory storage initialized
[12:34:56 INF] TURN server is DISABLED - Only P2P connections will be available
[12:34:57 INF] Now listening on: http://localhost:5151
```

Open the browser at `http://localhost:5151/health`:
```json
{
  "Status": "Healthy",
  "Connections": 0,
  "Timestamp": "2025-01-17T12:35:00Z"
}
```

Check Azure Portal → Storage Account → Tables:
- 2 tables should be created automatically:
  - `ServerSessions`
  - `TrustedClients`

---

## Detailed configuration

### Configuration for different environments

#### Development (Local Azure Storage Emulator)

**Windows**: Install [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite)

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
      "ConnectionString": ""  // From an environment variable
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
      "ConnectionString": ""  // From Key Vault or an environment variable
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

### Deploying to Azure

#### Option 1: Azure App Service (RECOMMENDED)

**Benefits**: Simple deployment, automatic SSL, integrated logging

```bash
# 1. Create the App Service Plan
az appservice plan create \
  --name plan-remotedesktop \
  --resource-group rg-remotedesktop \
  --sku B1 \
  --is-linux false

# 2. Create the Web App
az webapp create \
  --name app-remotedesktop-signaling \
  --resource-group rg-remotedesktop \
  --plan plan-remotedesktop \
  --runtime "DOTNET|8.0"

# 3. Set the connection string as an App Setting
az webapp config appsettings set \
  --name app-remotedesktop-signaling \
  --resource-group rg-remotedesktop \
  --settings \
    Storage__AzureTableStorage__ConnectionString="<YOUR_CONNECTION_STRING>" \
    Storage__Type="AzureTableStorage"

# 4. Deployment (from Visual Studio or the CLI)
cd src/SignalingServer
dotnet publish -c Release

# Zip the published files
cd bin/Release/net8.0/publish
Compress-Archive -Path * -DestinationPath ../../../../../deploy.zip

# Deploy to Azure
az webapp deployment source config-zip \
  --resource-group rg-remotedesktop \
  --name app-remotedesktop-signaling \
  --src deploy.zip
```

URL: `https://app-remotedesktop-signaling.azurewebsites.net`

#### Option 2: Azure Container Instances (Cheaper)

```dockerfile
# Dockerfile (in the project root)
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

# Push to Azure Container Registry
az acr create --name acrremotedesktop --resource-group rg-remotedesktop --sku Basic
az acr login --name acrremotedesktop
docker tag signaling-server:latest acrremotedesktop.azurecr.io/signaling-server:latest
docker push acrremotedesktop.azurecr.io/signaling-server:latest

# Deploy to ACI
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

#### Option 3: Azure VM (Most control)

```bash
# Create the VM
az vm create \
  --resource-group rg-remotedesktop \
  --name vm-signaling-server \
  --image UbuntuLTS \
  --size Standard_B2s \
  --admin-username azureuser \
  --generate-ssh-keys

# Open port 80
az vm open-port --port 80 --resource-group rg-remotedesktop --name vm-signaling-server

# SSH into the VM
ssh azureuser@<VM_PUBLIC_IP>

# Install .NET 8
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0

# Upload the application (using SCP)
scp -r src/SignalingServer/bin/Release/net8.0/publish/* azureuser@<VM_IP>:/home/azureuser/signaling/

# Create the systemd service
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

## Cost considerations

### Monthly costs (estimate for 100 active users)

| Service | Tier | Price/month (EUR) | Note |
|--------|------|------------------|----------|
| **Azure Table Storage** | Standard LRS | €2 | 100k transactions + 5GB storage |
| **Azure App Service B1** | Basic | €50 | 1 core, 1.75GB RAM, always-on |
| **Azure ACI** | 1 vCPU, 1.5GB | €30 | Pay-per-second billing |
| **Azure VM B2s** | Standard | €35 | 2 vCPU, 4GB RAM, Linux |
| **TOTAL (App Service)** | | **€52** | |
| **TOTAL (ACI)** | | **€32** | Cheapest option |
| **TOTAL (VM)** | | **€37** | Most control |

### Detailed breakdown - Azure Table Storage

For 100 users with average usage:

**Operations per month:**
- Server registration: 100 servers × 12 registrations/hour × 24 hours × 30 days = **864,000 write operations**
- Client authentication: 100 clients × 2 auths/day × 30 days = **6,000 read operations**
- Online status checks: 100 clients × 120 checks/hour × 24 hours × 30 days = **8,640,000 read operations**
- Cleanup: 1 cleanup/5min × 12/hour × 24 hours × 30 days = **8,640 scan operations**

**Total**: ~9.5M operations/month

**Azure Table Storage price:**
- First 10M operations: FREE
- **Storage**: 5GB × €0.05/GB = **€0.25/month**
- **Total**: **€0.25/month**

🎉 **Almost free for small to medium deployments!**

### Cost optimization

1. **Use ACI instead of App Service**: Saves ~€20/month
2. **Consider VM spot instances**: Saves up to 90% (but they can be evicted)
3. **Table Storage is cheaper than CosmosDB**: Saves €100+/month
4. **In-memory mode for testing**: €0 (but no persistence)

---

## Monitoring

### Application Insights (RECOMMENDED)

```bash
# Create Application Insights
az monitor app-insights component create \
  --app appinsights-remotedesktop \
  --location westeurope \
  --resource-group rg-remotedesktop \
  --application-type web

# Get the instrumentation key
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

`Program.cs` (add):
```csharp
builder.Services.AddApplicationInsightsTelemetry();
```

### Useful Kusto queries (Application Insights)

**1. Server registrations in the last 24 hours:**
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

SignalingServer already contains a `/health` endpoint:

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

Set up an Azure Monitor alert:
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

### Problem: "Storage:AzureTableStorage:ConnectionString not configured"

**Cause**: The connection string is not set or has the wrong format.

**Solution**:
```bash
# Check the environment variables
echo $Storage__AzureTableStorage__ConnectionString  # Linux/macOS
echo %Storage__AzureTableStorage__ConnectionString%  # Windows

# Or check appsettings.json
cat src/SignalingServer/appsettings.json | grep ConnectionString
```

### Problem: "Failed to create table: Forbidden"

**Cause**: The storage account has a firewall enabled or the connection string lacks the correct permissions.

**Solution**:
```bash
# Azure Portal → Storage Account → Networking
# Add your IP or allow "Allow Azure services"

# Or use a SAS token instead of the connection string
az storage account generate-sas \
  --account-name stremotedesktop \
  --services t \
  --resource-types sco \
  --permissions rwdlac \
  --expiry 2026-01-01T00:00:00Z \
  --https-only
```

### Problem: "Table already exists" on startup

**Cause**: The `ServerSessions` and `TrustedClients` tables already exist from a previous run.

**Solution**: This is normal! `CreateIfNotExists()` is idempotent. If you want to start over:

```bash
# Azure Portal → Storage Account → Tables → ServerSessions → Delete
# Nebo CLI:
az storage table delete --name ServerSessions --account-name stremotedesktop
az storage table delete --name TrustedClients --account-name stremotedesktop
```

### Problem: Slow performance / timeout

**Cause**: The storage account is in a different region than the application.

**Solution**:
```bash
# Check the region
az storage account show --name stremotedesktop --query location

# Move the App Service to the same region
# (Or create a new storage account in the correct region)
```

### Problem: High costs

**Cause**: Too many read operations (e.g. an online status check every second).

**Solution**: Adjust the frequency in `OnlineStatusMonitor.cs`:
```csharp
// Was: 30 seconds
await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

// Now: 2 minutes (4× fewer operations)
await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
```

### Debugging locally

```bash
# Run with detailed logging
cd src/SignalingServer
ASPNETCORE_ENVIRONMENT=Development dotnet run --verbosity detailed

# Or use Azure Storage Explorer
# Download: https://azure.microsoft.com/en-us/products/storage/storage-explorer/
# Connect using the connection string and browse the tables
```

---

## Next steps

### After a successful deployment:

1. ✅ **Set up a custom domain** (e.g. `signaling.yourdomain.com`)
   ```bash
   az webapp config hostname add \
     --webapp-name app-remotedesktop-signaling \
     --resource-group rg-remotedesktop \
     --hostname signaling.yourdomain.com
   ```

2. ✅ **Enable SSL/HTTPS** (automatic with an App Service managed certificate)
   ```bash
   az webapp config ssl bind \
     --name app-remotedesktop-signaling \
     --resource-group rg-remotedesktop \
     --certificate-thumbprint auto \
     --ssl-type SNI
   ```

3. ✅ **Set up a backup policy**
   ```bash
   az backup policy create \
     --resource-group rg-remotedesktop \
     --vault-name vault-remotedesktop \
     --name daily-backup \
     --backup-management-type AzureStorage
   ```

4. ✅ **Document the URL for clients**
   - Update the `ScreenSenderService` configuration
   - Update the WebClient signaling URL
   - Test the end-to-end connection

---

## Recommended production settings

### appsettings.Production.json (final version)

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

## Conclusion

You now have a fully functional SignalingServer with an Azure Table Storage backend that is:

- ✅ **Scalable**: Handles hundreds to thousands of concurrent users
- ✅ **Reliable**: Data survives a restart, multi-instance ready
- ✅ **Cheap**: ~€32-52/month for small to medium deployments
- ✅ **Monitored**: Application Insights, health checks, alerting
- ✅ **Secure**: HTTPS, CORS, Key Vault for secrets

### Need help?

- **Documentation**: [Azure Table Storage](https://learn.microsoft.com/en-us/azure/storage/tables/)
- **GitHub Issues**: https://github.com/your-repo/issues
- **Azure Support**: https://portal.azure.com/#blade/Microsoft_Azure_Support/HelpAndSupportBlade

---

**Happy Deploying! 🚀**
