# Azure Deployment Guide - SignalingServer

Kompletní návod pro nasazení DeskShare SignalingServer jako Azure Function.

## Obsah

1. [Úvod](#úvod)
2. [Architektura řešení](#architektura-řešení)
3. [Předpoklady](#předpoklady)
4. [Vytvoření Azure Function projektu](#vytvoření-azure-function-projektu)
5. [Migrace kódu](#migrace-kódu)
6. [Konfigurace Azure Resources](#konfigurace-azure-resources)
7. [Nasazení](#nasazení)
8. [Cenové úvahy](#cenové-úvahy)
9. [Monitoring a Diagnostika](#monitoring-a-diagnostika)
10. [Alternativní řešení](#alternativní-řešení)

---

## Úvod

SignalingServer je klíčová součást DeskShare, která zajišťuje:
- WebSocket komunikaci mezi klienty
- WebRTC signaling (SDP exchange, ICE candidates)
- Autentizaci a správu sessions
- Koordinaci P2P spojení

**Proč Azure Functions?**
- ✅ Automatické škálování
- ✅ Pay-per-execution model (levnější než neustále běžící VM)
- ✅ Integrovaný monitoring (Application Insights)
- ✅ Globální dostupnost (Azure CDN)
- ⚠️ **Ale:** WebSocket podporuje pouze Premium/Dedicated plány

---

## Architektura řešení

### Současná architektura (ASP.NET Core)

```
┌─────────────────┐         WebSocket          ┌──────────────────┐
│  Server (Host)  │◄──────────────────────────►│ SignalingServer  │
│  Screen Sender  │    /signal?clientId=xxx    │   (ASP.NET)      │
└─────────────────┘                             └──────────────────┘
                                                         ▲
                                                         │ WebSocket
                                                         │ /signal?clientId=yyy
                                                         │
                                                ┌────────┴────────┐
                                                │  Client (View)  │
                                                │  Browser/App    │
                                                └─────────────────┘
```

### Azure Architecture

```
┌─────────────────┐                            ┌──────────────────────────┐
│  Server (Host)  │                            │   Azure Function App     │
│  Screen Sender  │                            │   (Premium/Dedicated)    │
│                 │                            │                          │
│  Sends passkey  │────HTTP POST──────────────►│ /api/register           │
│  registration   │                            │  (HTTP Trigger)         │
└─────────────────┘                            └──────────────────────────┘
                                                         │
                    WebSocket                            │ Stores session
┌─────────────────┐ /api/signal           ┌─────────────▼──────────────┐
│  Server/Client  │◄─────────────────────►│  Azure SignalR Service     │
│  WebSocket      │                        │  (Managed WebSocket)       │
└─────────────────┘                        └────────────────────────────┘
                                                         ▲
                    WebSocket                            │
┌─────────────────┐ /api/signal                         │
│  Client (View)  │◄────────────────────────────────────┘
│  Browser/App    │
└─────────────────┘

                            Storage
                    ┌───────────────────┐
                    │ Azure Table Store │
                    │  or CosmosDB      │
                    │                   │
                    │ - Server Sessions │
                    │ - Trusted Clients │
                    │ - Statistics      │
                    └───────────────────┘
```

**Klíčové změny:**
1. **Azure SignalR Service** namísto přímých WebSocket spojení
2. **Azure Table Storage** nebo **CosmosDB** pro perzistenci session dat
3. **HTTP Triggers** pro /register a /authenticate endpointy
4. **SignalR Bindings** pro WebSocket komunikaci

---

## Předpoklady

### 1. Azure Subscription
- Aktivní Azure předplatné
- Resource Group pro projekt

### 2. Vývojové nástroje
```bash
# .NET 8 SDK
dotnet --version  # 8.0 nebo vyšší

# Azure Functions Core Tools
npm install -g azure-functions-core-tools@4 --unsafe-perm true

# Azure CLI
az --version
az login
```

### 3. Visual Studio / VS Code
- Visual Studio 2022 s Azure Development workload, nebo
- VS Code s rozšířením "Azure Functions"

---

## Vytvoření Azure Function projektu

### Krok 1: Vytvoření projektu

```bash
# Navigace do složky projektu
cd C:\GIT\DeskShare\src

# Vytvoření nového Azure Functions projektu
func init SignalingServerAzure --worker-runtime dotnet-isolated --target-framework net8.0

cd SignalingServerAzure
```

### Krok 2: Přidání NuGet balíčků

```bash
# SignalR Service binding
dotnet add package Microsoft.Azure.WebJobs.Extensions.SignalRService

# Azure Storage pro session management
dotnet add package Azure.Data.Tables

# Logging
dotnet add package Microsoft.Extensions.Logging.ApplicationInsights

# Shared Core project
dotnet add reference ../Core/RemoteDesktop.Core.csproj
```

Upravte `SignalingServerAzure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="1.21.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="1.17.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.1.0" />
    <PackageReference Include="Microsoft.Azure.WebJobs.Extensions.SignalRService" Version="1.13.0" />
    <PackageReference Include="Azure.Data.Tables" Version="12.8.3" />
    <PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.22.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Core\RemoteDesktop.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="host.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Update="local.settings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>Never</CopyToPublishDirectory>
    </None>
  </ItemGroup>
</Project>
```

---

## Migrace kódu

### 1. Struktura projektu

```
SignalingServerAzure/
├── Functions/
│   ├── RegisterFunction.cs          # POST /api/register
│   ├── AuthenticateFunction.cs      # POST /api/authenticate
│   ├── SignalRNegotiateFunction.cs  # GET /api/signal/negotiate
│   ├── OnConnectedFunction.cs       # SignalR OnConnected
│   ├── OnDisconnectedFunction.cs    # SignalR OnDisconnected
│   ├── SendMessageFunction.cs       # SignalR message handling
│   └── HealthFunction.cs            # GET /api/health
├── Services/
│   ├── SessionStorageService.cs     # Azure Table Storage
│   ├── ConnectionService.cs         # Wrapper nad SignalR
│   └── StatisticsService.cs         # Metrics tracking
├── Models/
│   └── TableEntities.cs             # Azure Table entity models
├── Program.cs
├── host.json
├── local.settings.json
└── SignalingServerAzure.csproj
```

### 2. SessionStorageService.cs

```csharp
using Azure.Data.Tables;
using RemoteDesktop.Core.Auth;

namespace SignalingServerAzure.Services;

/// <summary>
/// Manages server sessions and trusted clients using Azure Table Storage.
/// </summary>
public class SessionStorageService
{
    private readonly TableClient _sessionsTable;
    private readonly TableClient _trustedClientsTable;
    private readonly ILogger<SessionStorageService> _logger;

    public SessionStorageService(
        TableServiceClient tableServiceClient,
        ILogger<SessionStorageService> logger)
    {
        _logger = logger;

        // Create tables if they don't exist
        _sessionsTable = tableServiceClient.GetTableClient("ServerSessions");
        _trustedClientsTable = tableServiceClient.GetTableClient("TrustedClients");

        _sessionsTable.CreateIfNotExists();
        _trustedClientsTable.CreateIfNotExists();
    }

    public async Task<ServerSessionEntity?> GetSessionAsync(string serverId)
    {
        try
        {
            var response = await _sessionsTable.GetEntityAsync<ServerSessionEntity>(
                partitionKey: "sessions",
                rowKey: serverId);

            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task UpsertSessionAsync(ServerSessionEntity session)
    {
        session.PartitionKey = "sessions";
        session.RowKey = session.ServerId;
        await _sessionsTable.UpsertEntityAsync(session);

        _logger.LogInformation("Session upserted: {ServerId}", session.ServerId);
    }

    public async Task DeleteSessionAsync(string serverId)
    {
        await _sessionsTable.DeleteEntityAsync("sessions", serverId);
        _logger.LogInformation("Session deleted: {ServerId}", serverId);
    }

    public async Task<bool> IsTrustedClientAsync(string serverId, string clientId)
    {
        var key = $"{serverId}:{clientId}";

        try
        {
            var response = await _trustedClientsTable.GetEntityAsync<TrustedClientEntity>(
                partitionKey: serverId,
                rowKey: clientId);

            // Update last used timestamp
            var entity = response.Value;
            entity.LastUsed = DateTime.UtcNow;
            await _trustedClientsTable.UpsertEntityAsync(entity);

            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task TrustClientAsync(string serverId, string clientId)
    {
        var entity = new TrustedClientEntity
        {
            PartitionKey = serverId,
            RowKey = clientId,
            ServerId = serverId,
            ClientId = clientId,
            EstablishedAt = DateTime.UtcNow,
            LastUsed = DateTime.UtcNow
        };

        await _trustedClientsTable.UpsertEntityAsync(entity);
        _logger.LogInformation("Client trusted: {ServerId}/{ClientId}", serverId, clientId);
    }

    /// <summary>
    /// Cleanup expired sessions (run periodically via Timer Trigger)
    /// </summary>
    public async Task CleanupExpiredSessionsAsync()
    {
        var now = DateTime.UtcNow;
        var expiredSessions = new List<string>();

        await foreach (var session in _sessionsTable.QueryAsync<ServerSessionEntity>())
        {
            if (session.ValidTo < now)
            {
                expiredSessions.Add(session.ServerId);
                await DeleteSessionAsync(session.ServerId);
            }
        }

        if (expiredSessions.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions", expiredSessions.Count);
        }
    }
}

// Table Entities
public class ServerSessionEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "sessions";
    public string RowKey { get; set; } = string.Empty; // ServerId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string ServerId { get; set; } = string.Empty;
    public string Passkey { get; set; } = string.Empty;
    public DateTime ValidTo { get; set; }
    public bool RemoteControlEnabled { get; set; }
    public bool TrustClientPermanent { get; set; }
    public string? ConnectedClientId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
}

public class TrustedClientEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // ServerId
    public string RowKey { get; set; } = string.Empty; // ClientId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string ServerId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTime EstablishedAt { get; set; }
    public DateTime LastUsed { get; set; }
}
```

### 3. RegisterFunction.cs

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.Text.Json;
using RemoteDesktop.Core.Auth;
using SignalingServerAzure.Services;

namespace SignalingServerAzure.Functions;

public class RegisterFunction
{
    private readonly SessionStorageService _sessionStorage;
    private readonly ILogger<RegisterFunction> _logger;

    public RegisterFunction(
        SessionStorageService sessionStorage,
        ILogger<RegisterFunction> logger)
    {
        _sessionStorage = sessionStorage;
        _logger = logger;
    }

    [Function("Register")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "register")]
        HttpRequestData req)
    {
        try
        {
            var requestBody = await req.ReadAsStringAsync();
            var registration = JsonSerializer.Deserialize<ServerRegistrationMessage>(requestBody!);

            if (registration == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid registration data");
            }

            // Store session in Azure Table Storage
            var sessionEntity = new ServerSessionEntity
            {
                ServerId = registration.ServerId,
                Passkey = registration.Passkey,
                ValidTo = registration.ValidTo,
                RemoteControlEnabled = registration.RemoteControlEnabled,
                TrustClientPermanent = registration.TrustClientPermanent,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            await _sessionStorage.UpsertSessionAsync(sessionEntity);

            _logger.LogInformation(
                "Server registered | ServerId: {ServerId} | ValidTo: {ValidTo}",
                registration.ServerId,
                registration.ValidTo);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new ServerRegistrationResponse
            {
                Success = true,
                ServerId = registration.ServerId
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing registration");
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Registration failed");
        }
    }

    private async Task<HttpResponseData> CreateErrorResponse(
        HttpRequestData req,
        HttpStatusCode statusCode,
        string message)
    {
        var response = req.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { Error = message });
        return response;
    }
}
```

### 4. AuthenticateFunction.cs

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.Text.Json;
using RemoteDesktop.Core.Auth;
using SignalingServerAzure.Services;

namespace SignalingServerAzure.Functions;

public class AuthenticateFunction
{
    private readonly SessionStorageService _sessionStorage;
    private readonly ILogger<AuthenticateFunction> _logger;

    public AuthenticateFunction(
        SessionStorageService sessionStorage,
        ILogger<AuthenticateFunction> logger)
    {
        _sessionStorage = sessionStorage;
        _logger = logger;
    }

    [Function("Authenticate")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "authenticate")]
        HttpRequestData req)
    {
        try
        {
            var requestBody = await req.ReadAsStringAsync();
            var authRequest = JsonSerializer.Deserialize<ClientAuthenticationMessage>(requestBody!);

            if (authRequest == null)
            {
                return await CreateUnauthorizedResponse(req, "Invalid authentication data");
            }

            // Get session from storage
            var session = await _sessionStorage.GetSessionAsync(authRequest.ServerId);

            if (session == null)
            {
                _logger.LogWarning("Authentication failed - server not found: {ServerId}", authRequest.ServerId);
                return await CreateUnauthorizedResponse(req, "Server not found");
            }

            // Check if session expired
            if (session.ValidTo < DateTime.UtcNow)
            {
                _logger.LogWarning("Authentication failed - session expired: {ServerId}", authRequest.ServerId);
                return await CreateUnauthorizedResponse(req, "Session expired");
            }

            // Check if client is already connected
            if (!string.IsNullOrEmpty(session.ConnectedClientId) &&
                session.ConnectedClientId != authRequest.ClientId)
            {
                _logger.LogWarning(
                    "Authentication failed - another client connected: {ServerId}/{ClientId}",
                    authRequest.ServerId, session.ConnectedClientId);
                return await CreateUnauthorizedResponse(req, "Another client already connected");
            }

            // Check if client is trusted
            bool isTrusted = await _sessionStorage.IsTrustedClientAsync(
                authRequest.ServerId,
                authRequest.ClientId);

            // Validate passkey if not trusted
            if (!isTrusted)
            {
                if (!AuthenticationService.ValidatePasskey(
                    authRequest.ServerId,
                    authRequest.Passkey,
                    DateTime.UtcNow))
                {
                    _logger.LogWarning(
                        "Authentication failed - invalid passkey: {ServerId}/{ClientId}",
                        authRequest.ServerId, authRequest.ClientId);
                    return await CreateUnauthorizedResponse(req, "Invalid passkey");
                }
            }

            // Mark client as connected
            session.ConnectedClientId = authRequest.ClientId;
            session.LastActivity = DateTime.UtcNow;
            await _sessionStorage.UpsertSessionAsync(session);

            // Trust client if permanent trust enabled
            if (session.TrustClientPermanent && !isTrusted)
            {
                await _sessionStorage.TrustClientAsync(authRequest.ServerId, authRequest.ClientId);
            }

            _logger.LogInformation(
                "Client authenticated | ServerId: {ServerId} | ClientId: {ClientId} | Trusted: {Trusted}",
                authRequest.ServerId, authRequest.ClientId, isTrusted);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new ClientAuthenticationResponse
            {
                Success = true,
                RemoteControlEnabled = session.RemoteControlEnabled
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing authentication");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { Error = "Authentication failed" });
            return response;
        }
    }

    private async Task<HttpResponseData> CreateUnauthorizedResponse(
        HttpRequestData req,
        string message)
    {
        var response = req.CreateResponse(HttpStatusCode.Unauthorized);
        await response.WriteAsJsonAsync(new ClientAuthenticationResponse
        {
            Success = false,
            ErrorMessage = message
        });
        return response;
    }
}
```

### 5. SignalRNegotiateFunction.cs

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;

namespace SignalingServerAzure.Functions;

public class SignalRNegotiateFunction
{
    [Function("negotiate")]
    public SignalRConnectionInfo Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "signal/negotiate")]
        HttpRequestData req,
        [SignalRConnectionInfoInput(HubName = "signaling", UserId = "{query.clientId}")]
        SignalRConnectionInfo connectionInfo)
    {
        // Azure SignalR automatically handles WebSocket negotiation
        return connectionInfo;
    }
}
```

### 6. SendMessageFunction.cs

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using System.Text.Json;
using RemoteDesktop.Core.Models;

namespace SignalingServerAzure.Functions;

public class SendMessageFunction
{
    private readonly ILogger<SendMessageFunction> _logger;

    public SendMessageFunction(ILogger<SendMessageFunction> logger)
    {
        _logger = logger;
    }

    [Function("messages")]
    [SignalROutput(HubName = "signaling")]
    public async Task<SignalRMessageAction> Run(
        [SignalRTrigger("signaling", "messages", "SendMessage")]
        SignalRInvocationContext invocationContext,
        string message)
    {
        try
        {
            var signalingMessage = JsonSerializer.Deserialize<SignalingMessage>(message);

            if (signalingMessage == null)
            {
                _logger.LogWarning("Invalid message format from {ConnectionId}", invocationContext.ConnectionId);
                return new SignalRMessageAction("error");
            }

            // Set sender from connection context
            signalingMessage.SenderId = invocationContext.UserId ?? invocationContext.ConnectionId;
            signalingMessage.Timestamp = DateTime.UtcNow;

            _logger.LogInformation(
                "Message received | Type: {Type} | From: {Sender} | To: {Target}",
                signalingMessage.Type,
                signalingMessage.SenderId,
                signalingMessage.TargetId ?? "broadcast");

            // Route to specific client or broadcast
            if (!string.IsNullOrWhiteSpace(signalingMessage.TargetId))
            {
                // Send to specific user
                return new SignalRMessageAction("ReceiveMessage")
                {
                    UserId = signalingMessage.TargetId,
                    Arguments = new[] { JsonSerializer.Serialize(signalingMessage) }
                };
            }
            else
            {
                // Broadcast to all except sender
                return new SignalRMessageAction("ReceiveMessage")
                {
                    Arguments = new[] { JsonSerializer.Serialize(signalingMessage) },
                    GroupName = "all"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            return new SignalRMessageAction("error");
        }
    }
}
```

### 7. Program.cs

```csharp
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SignalingServerAzure.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Azure Table Storage for session management
        var storageConnectionString = context.Configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage not configured");

        services.AddSingleton(new TableServiceClient(storageConnectionString));
        services.AddSingleton<SessionStorageService>();
        services.AddSingleton<StatisticsService>();

        // Logging
        services.AddLogging();
    })
    .Build();

await host.RunAsync();
```

### 8. host.json

```json
{
  "version": "2.0",
  "logging": {
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true,
        "maxTelemetryItemsPerSecond": 20
      }
    }
  },
  "extensions": {
    "http": {
      "routePrefix": "api"
    },
    "signalR": {
      "connectionTimeout": "00:02:00"
    }
  }
}
```

### 9. local.settings.json

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureSignalRConnectionString": "<your-signalr-connection-string>"
  }
}
```

---

## Konfigurace Azure Resources

### 1. Vytvoření Resource Group

```bash
az login

# Vytvoření resource group
az group create \
  --name rg-remotedesktop-prod \
  --location westeurope

# Nebo pomocí Azure Portal:
# https://portal.azure.com > Create Resource Group
```

### 2. Azure SignalR Service

```bash
# Vytvoření SignalR Service (Free tier pro vývoj)
az signalr create \
  --name signalr-remotedesktop \
  --resource-group rg-remotedesktop-prod \
  --sku Free_F1 \
  --unit-count 1 \
  --service-mode Default

# Pro produkci použijte Standard tier:
# --sku Standard_S1 --unit-count 1

# Získání connection string
az signalr key list \
  --name signalr-remotedesktop \
  --resource-group rg-remotedesktop-prod \
  --query primaryConnectionString -o tsv
```

**Cenová tier:**
- **Free F1**: 20 souběžných spojení, vhodné pro dev/test
- **Standard S1**: 1000 spojení, €42/měsíc
- **Premium P1**: 1000 spojení + Redis cache, €420/měsíc

### 3. Storage Account (pro session data)

```bash
# Vytvoření storage account
az storage account create \
  --name stremotedesktop \
  --resource-group rg-remotedesktop-prod \
  --location westeurope \
  --sku Standard_LRS \
  --kind StorageV2

# Získání connection string
az storage account show-connection-string \
  --name stremotedesktop \
  --resource-group rg-remotedesktop-prod \
  --query connectionString -o tsv
```

### 4. Function App (Premium Plan pro WebSocket)

```bash
# Vytvoření App Service Plan (Premium EP1)
az functionapp plan create \
  --name plan-remotedesktop-functions \
  --resource-group rg-remotedesktop-prod \
  --location westeurope \
  --sku EP1 \
  --is-linux false

# Vytvoření Function App
az functionapp create \
  --name func-remotedesktop-signaling \
  --resource-group rg-remotedesktop-prod \
  --plan plan-remotedesktop-functions \
  --storage-account stremotedesktop \
  --runtime dotnet-isolated \
  --runtime-version 8 \
  --functions-version 4

# Konfigurace Application Settings
az functionapp config appsettings set \
  --name func-remotedesktop-signaling \
  --resource-group rg-remotedesktop-prod \
  --settings \
    "AzureSignalRConnectionString=<connection-string-from-step-2>" \
    "APPINSIGHTS_INSTRUMENTATIONKEY=<application-insights-key>"
```

**Cenový plán:**
- **Consumption**: Nepodporuje WebSocket ❌
- **Premium EP1**: 1 vCore, 3.5 GB RAM, ~€125/měsíc ✅
- **Dedicated (App Service)**: Začíná na ~€50/měsíc (B1), podporuje WebSocket ✅

### 5. Application Insights

```bash
# Vytvoření Application Insights
az monitor app-insights component create \
  --app appinsights-remotedesktop \
  --location westeurope \
  --resource-group rg-remotedesktop-prod \
  --application-type web

# Získání instrumentation key
az monitor app-insights component show \
  --app appinsights-remotedesktop \
  --resource-group rg-remotedesktop-prod \
  --query instrumentationKey -o tsv
```

---

## Nasazení

### Metoda 1: Visual Studio

1. **Publish profil:**
   - Pravý klik na projekt → **Publish**
   - **Target**: Azure → Azure Function App (Windows)
   - Vyberte `func-remotedesktop-signaling`
   - **Deployment type**: Publish (generates pubxml)

2. **Deploy:**
   - Klikněte **Publish**
   - VS automaticky build & deploy

### Metoda 2: Azure Functions Core Tools

```bash
# Build projektu
dotnet build --configuration Release

# Publikace do Azure
cd src/SignalingServerAzure
func azure functionapp publish func-remotedesktop-signaling
```

### Metoda 3: GitHub Actions CI/CD

Vytvořte `.github/workflows/azure-functions-deploy.yml`:

```yaml
name: Deploy SignalingServer to Azure Functions

on:
  push:
    branches:
      - main
    paths:
      - 'src/SignalingServerAzure/**'
      - 'src/Core/**'

env:
  AZURE_FUNCTIONAPP_NAME: func-remotedesktop-signaling
  AZURE_FUNCTIONAPP_PACKAGE_PATH: 'src/SignalingServerAzure'
  DOTNET_VERSION: '8.0.x'

jobs:
  build-and-deploy:
    runs-on: windows-latest

    steps:
    - name: 'Checkout GitHub Action'
      uses: actions/checkout@v3

    - name: Setup .NET ${{ env.DOTNET_VERSION }}
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: ${{ env.DOTNET_VERSION }}

    - name: 'Restore dependencies'
      run: dotnet restore
      working-directory: ${{ env.AZURE_FUNCTIONAPP_PACKAGE_PATH }}

    - name: 'Build project'
      run: dotnet build --configuration Release --no-restore
      working-directory: ${{ env.AZURE_FUNCTIONAPP_PACKAGE_PATH }}

    - name: 'Run tests'
      run: dotnet test --no-restore --verbosity normal

    - name: 'Publish project'
      run: dotnet publish --configuration Release --no-build --output ./output
      working-directory: ${{ env.AZURE_FUNCTIONAPP_PACKAGE_PATH }}

    - name: 'Deploy to Azure Functions'
      uses: Azure/functions-action@v1
      with:
        app-name: ${{ env.AZURE_FUNCTIONAPP_NAME }}
        package: ${{ env.AZURE_FUNCTIONAPP_PACKAGE_PATH }}/output
        publish-profile: ${{ secrets.AZURE_FUNCTIONAPP_PUBLISH_PROFILE }}
```

**Získání Publish Profile:**
```bash
az functionapp deployment list-publishing-profiles \
  --name func-remotedesktop-signaling \
  --resource-group rg-remotedesktop-prod \
  --xml
```

Uložte jako GitHub Secret: `AZURE_FUNCTIONAPP_PUBLISH_PROFILE`

---

## Cenové úvahy

### Měsíční náklady (odhad pro 100 aktivních uživatelů)

| Služba | Tier | Cena/měsíc | Poznámka |
|--------|------|------------|----------|
| **Azure Functions** | Premium EP1 | €125 | Always-on, WebSocket support |
| **Azure SignalR** | Standard S1 | €42 | 1000 connections, 1M messages/day |
| **Storage Account** | Standard LRS | €2 | 100GB transactions + 5GB storage |
| **Application Insights** | Pay-as-you-go | €5-10 | 5GB data/month |
| **Bandwidth (Egress)** | Standard | €10-20 | První 100GB zdarma, pak €0.081/GB |
| **CELKEM** | | **€184-199** | |

### Optimalizace nákladů:

1. **Použít Consumption Plan + Azure Container Instances pro WebSocket**
   - Functions na Consumption (~€5/měsíc)
   - ACI s vlastním SignalR serverem (~€30/měsíc)
   - **Úspora: ~€90/měsíc**

2. **Použít Azure App Service místo Functions**
   - B1 Basic tier (~€50/měsíc) podporuje WebSocket
   - **Úspora: ~€75/měsíc**
   - Ale: Žádné auto-scaling

3. **Self-hosted SignalingServer na VM**
   - B2s VM (2 vCPU, 4GB RAM) = €35/měsíc
   - Vyžaduje správu, monitoring, updates
   - **Úspora: ~€150/měsíc**

### Doporučení:
- **Pro vývoj:** Free tier všude = €0
- **Pro malý provoz (<50 users):** App Service B1 = ~€50-70/měsíc
- **Pro produkci (100+ users):** Functions Premium + SignalR = ~€180/měsíc
- **Pro enterprise:** Zvažte self-hosted řešení na VM

---

## Monitoring a Diagnostika

### 1. Application Insights Queries

**Sledování session registrations:**
```kusto
traces
| where message contains "Server registered"
| project timestamp, serverId = tostring(customDimensions.ServerId), validTo = tostring(customDimensions.ValidTo)
| order by timestamp desc
```

**Autentizační failures:**
```kusto
traces
| where severityLevel >= 2 and message contains "Authentication failed"
| summarize count() by bin(timestamp, 5m), reason = tostring(customDimensions.Reason)
| render timechart
```

**SignalR connection statistics:**
```kusto
dependencies
| where type == "Azure SignalR"
| summarize
    connections = count(),
    avgDuration = avg(duration),
    failures = countif(success == false)
  by bin(timestamp, 1h)
| render timechart
```

### 2. Health Endpoint

Vytvořte `HealthFunction.cs`:

```csharp
[Function("Health")]
public async Task<HttpResponseData> Run(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")]
    HttpRequestData req)
{
    var response = req.CreateResponse(HttpStatusCode.OK);
    await response.WriteAsJsonAsync(new
    {
        Status = "Healthy",
        Timestamp = DateTime.UtcNow,
        Version = "1.0.0",
        Environment = Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT")
    });
    return response;
}
```

**Azure Monitor Alert:**
```bash
az monitor metrics alert create \
  --name "SignalingServer Health Check" \
  --resource-group rg-remotedesktop-prod \
  --scopes /subscriptions/<sub-id>/resourceGroups/rg-remotedesktop-prod/providers/Microsoft.Web/sites/func-remotedesktop-signaling \
  --condition "avg Percentage CPU > 80" \
  --window-size 5m \
  --evaluation-frequency 1m \
  --action <action-group-id>
```

### 3. Logging Best Practices

```csharp
// Strukturovaný logging
_logger.LogInformation(
    "Client {Action} | ServerId: {ServerId} | ClientId: {ClientId} | Duration: {Duration}ms",
    "authenticated",
    serverId,
    clientId,
    stopwatch.ElapsedMilliseconds);

// Custom metrics
var telemetryClient = serviceProvider.GetService<TelemetryClient>();
telemetryClient.TrackMetric("SessionsActive", activeSessionsCount);
telemetryClient.TrackEvent("ClientConnected", new Dictionary<string, string>
{
    { "ServerId", serverId },
    { "ClientId", clientId },
    { "IsTrusted", isTrusted.ToString() }
});
```

---

## Alternativní řešení

### Řešení 1: Azure Container Instances + ASP.NET Core

**Výhody:**
- Přímá migrace současného kódu (minimal changes)
- Plná kontrola nad WebSocket implementací
- Levnější než Functions Premium (~€30/měsíc)

**Postup:**
1. Dockerizace SignalingServer:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/SignalingServer/RemoteDesktop.SignalingServer.csproj", "SignalingServer/"]
COPY ["src/Core/RemoteDesktop.Core.csproj", "Core/"]
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

2. Nasazení do ACI:

```bash
az container create \
  --resource-group rg-remotedesktop-prod \
  --name aci-signaling-server \
  --image <your-acr>.azurecr.io/signaling-server:latest \
  --dns-name-label signaling-remotedesktop \
  --ports 80 443 \
  --cpu 1 --memory 1.5 \
  --environment-variables \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Storage="<storage-connection-string>"
```

3. URL: `http://signaling-remotedesktop.westeurope.azurecontainer.io`

**Nevýhody:**
- Manuální škálování (single instance)
- Žádná built-in load balancing

---

### Řešení 2: Azure Kubernetes Service (AKS)

Pro velmi velký provoz (1000+ concurrent users):

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: signaling-server
spec:
  replicas: 3
  selector:
    matchLabels:
      app: signaling-server
  template:
    metadata:
      labels:
        app: signaling-server
    spec:
      containers:
      - name: signaling-server
        image: <acr>.azurecr.io/signaling-server:latest
        ports:
        - containerPort: 80
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        resources:
          requests:
            cpu: 500m
            memory: 512Mi
          limits:
            cpu: 1000m
            memory: 1Gi
---
apiVersion: v1
kind: Service
metadata:
  name: signaling-server-service
spec:
  type: LoadBalancer
  ports:
  - port: 80
    targetPort: 80
  selector:
    app: signaling-server
```

**Výhody:**
- Auto-scaling (HPA based on CPU/memory)
- High availability (multiple replicas)
- Azure Monitor integration

**Nevýhody:**
- Složitější setup
- Dražší (~€150+/měsíc pro malý cluster)

---

### Řešení 3: Hybrid (Self-hosted + Azure)

**Architektura:**
- SignalingServer na vlastním serveru/VM (levný VPS ~€5/měsíc)
- Azure CDN pro static assets (WebClient)
- Azure DNS pro custom domain
- Cloudflare jako reverse proxy (free WebSocket proxying)

**Výhody:**
- Velmi nízké náklady (<€10/měsíc)
- Plná kontrola

**Nevýhody:**
- Vyžaduje DevOps znalosti
- Žádná SLA od Azure

---

## Závěr

### Doporučené řešení podle use-case:

| Scénář | Řešení | Měsíční náklady |
|--------|--------|-----------------|
| **Vývoj/Testing** | Azure Functions Free + SignalR Free | €0 |
| **Malý projekt (<50 users)** | Azure App Service B1 | €50-70 |
| **Střední projekt (50-200 users)** | Functions Premium + SignalR Standard | €180-200 |
| **Velký projekt (200+ users)** | AKS + SignalR Premium | €300-500 |
| **Budget řešení** | Azure Container Instances | €30-50 |
| **Self-hosted** | VPS + Cloudflare | €5-15 |

### Next Steps:

1. ✅ Vytvořte Azure account a resource group
2. ✅ Implementujte SessionStorageService a Functions
3. ✅ Otestujte lokálně s Azure Storage Emulator
4. ✅ Nasaďte do Azure (začněte s Free tier)
5. ✅ Nastavte monitoring (Application Insights)
6. ✅ Optimalizujte náklady podle skutečného provozu

---

**Dokumentace:**
- [Azure Functions .NET Isolated](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)
- [Azure SignalR Service](https://learn.microsoft.com/en-us/azure/azure-signalr/)
- [Azure Table Storage](https://learn.microsoft.com/en-us/azure/storage/tables/)
- [Application Insights](https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview)

**Podpora:**
- GitHub Issues: https://github.com/your-repo/issues
- Azure Support: https://portal.azure.com/#blade/Microsoft_Azure_Support/HelpAndSupportBlade
