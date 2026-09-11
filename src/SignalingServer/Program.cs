using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using DeskShare.Core.Models;
using DeskShare.SignalingServer.Services;
using DeskShare.Stun;
using DeskShare.Turn;
using Serilog;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/signaling-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// ============================================================
// SERVICE REGISTRATION
// ============================================================

builder.Services.AddSingleton<ConnectionManager>();

// Configure storage backend (InMemory or Azure Table Storage)
var storageType = builder.Configuration.GetValue<string>("Storage:Type", "InMemory");
Log.Information("Storage backend: {StorageType}", storageType);

if (storageType.Equals("AzureTableStorage", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetValue<string>("Storage:AzureTableStorage:ConnectionString");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Log.Fatal("Azure Table Storage connection string not configured");
        throw new InvalidOperationException(
            "Storage:AzureTableStorage:ConnectionString must be set when using Azure Table Storage");
    }

    var requireHmac = builder.Configuration.GetValue<bool>("Security:RequireHmacSignature", false);
    builder.Services.AddSingleton<IServerSessionStorage>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<AzureTableSessionStorage>>();
        return new AzureTableSessionStorage(connectionString, logger, requireHmac);
    });

    Log.Information("Azure Table Storage initialized");
}
else
{
    builder.Services.AddSingleton<IServerSessionStorage, InMemorySessionStorage>();
    Log.Information("In-memory storage initialized");
}

// Add STUN server as hosted service
builder.Services.AddSingleton(new StunServerOptions
{
    Port = builder.Configuration.GetValue<int>("Stun:Port", 3478)
});
builder.Services.AddHostedService<StunServer>();

// Add TURN server as hosted service (only if enabled)
var turnEnabled = builder.Configuration.GetValue<bool>("Turn:Enabled", false);
if (turnEnabled)
{
    builder.Services.AddSingleton(new TurnServerOptions
    {
        Port = builder.Configuration.GetValue<int>("Turn:Port", 3478),
        Realm = builder.Configuration.GetValue<string>("Turn:Realm", "localhost") ?? "localhost",
        EnableTestUser = builder.Configuration.GetValue<bool>("Turn:EnableTestUser", false),
        MinRelayPort = builder.Configuration.GetValue<int>("Turn:MinRelayPort", 49152),
        MaxRelayPort = builder.Configuration.GetValue<int>("Turn:MaxRelayPort", 65535)
    });
    builder.Services.AddHostedService<TurnServer>();
    Log.Information("TURN server is ENABLED");
}
else
{
    Log.Information("TURN server is DISABLED - Only P2P connections will be available");
}

// ============================================================
// CORS — restrict methods and headers in production
// ============================================================
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();

        if (allowedOrigins.Length == 0 && builder.Environment.IsDevelopment())
        {
            Log.Warning("CORS: Allowing any origin (DEVELOPMENT MODE ONLY)");
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            Log.Information("CORS: Restricting to {Count} allowed origin(s)", allowedOrigins.Length);
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST")
                  .WithHeaders("Content-Type", "Authorization", "X-Requested-With")
                  .AllowCredentials();
        }
    });
});

// ============================================================
// RATE LIMITING — protect /authenticate and /register endpoints
// ============================================================
var authPermitLimit = builder.Configuration.GetValue<int>("RateLimiting:Authenticate:PermitLimit", 10);
var authWindowSeconds = builder.Configuration.GetValue<int>("RateLimiting:Authenticate:WindowSeconds", 60);
var regPermitLimit = builder.Configuration.GetValue<int>("RateLimiting:Register:PermitLimit", 5);
var regWindowSeconds = builder.Configuration.GetValue<int>("RateLimiting:Register:WindowSeconds", 60);

builder.Services.AddRateLimiter(options =>
{
    // Rate limit policy for /authenticate: max N requests per window per IP
    options.AddPolicy("authenticate", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermitLimit,
                Window = TimeSpan.FromSeconds(authWindowSeconds),
                QueueLimit = 0
            }));

    // Rate limit policy for /register: max N requests per window per IP
    options.AddPolicy("register", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = regPermitLimit,
                Window = TimeSpan.FromSeconds(regWindowSeconds),
                QueueLimit = 0
            }));

    // Return 429 with Retry-After header when rate limited
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
            ? retryAfterValue.TotalSeconds
            : 60;
        context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter).ToString();

        var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = context.HttpContext.Request.Path;
        Log.Warning("Rate limit exceeded | IP: {IP} | Endpoint: {Path}", ip, path);

        // Record rate limit hit in ConnectionManager statistics
        var connMgr = context.HttpContext.RequestServices.GetService<ConnectionManager>();
        connMgr?.RecordRateLimitHit();

        await context.HttpContext.Response.WriteAsJsonAsync(
            new { Error = "Too many requests. Please try again later." },
            cancellationToken);
    };
});

// ============================================================
// REQUEST SIZE LIMITS via Kestrel
// ============================================================
builder.WebHost.ConfigureKestrel(options =>
{
    // Limit request body size to 64 KB (auth messages are small)
    options.Limits.MaxRequestBodySize = 64 * 1024;
});

// ============================================================
// ENVIRONMENT WARNING
// ============================================================
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsProduction())
{
    Log.Warning("ASPNETCORE_ENVIRONMENT is set to '{Env}' — expected 'Production' or 'Development'",
        builder.Environment.EnvironmentName);
}

var app = builder.Build();

// ============================================================
// MIDDLEWARE PIPELINE
// ============================================================

// HTTPS redirect — ensures all HTTP requests are redirected to HTTPS
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Security headers — applied to every response
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

    // HSTS — only in production (browsers remember this for 1 year)
    if (!app.Environment.IsDevelopment())
    {
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }

    // CSP — allow self and WebSocket connections
    headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self' wss: ws:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline';";

    await next();
});

app.UseCors();
app.UseRateLimiter();

// Serve WebClient static files from /webclient path
var webClientPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "WebClient");
if (Directory.Exists(webClientPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
            Path.GetFullPath(webClientPath)),
        RequestPath = "/webclient"
    });
    Log.Information("Serving WebClient static files from {Path}", Path.GetFullPath(webClientPath));
}
else
{
    Log.Warning("WebClient directory not found at {Path} - static file serving disabled", webClientPath);
}

// ============================================================
// CONFIGURE CONNECTION MANAGER
// ============================================================
var connectionManager = app.Services.GetRequiredService<ConnectionManager>();
connectionManager.Configure(
    maxConnections: builder.Configuration.GetValue<int>("WebSocket:MaxConnections", 100),
    idleTimeoutSeconds: builder.Configuration.GetValue<int>("WebSocket:IdleTimeoutSeconds", 300),
    tokenExpirationSeconds: builder.Configuration.GetValue<int>("WebSocket:TokenExpirationSeconds", 30),
    requireHmacSignature: builder.Configuration.GetValue<bool>("Security:RequireHmacSignature", false));

var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);

// ============================================================
// GRACEFUL SHUTDOWN
// ============================================================
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStopping.Register(async () =>
{
    Log.Information("Server shutting down — notifying connected clients...");

    var cm = app.Services.GetRequiredService<ConnectionManager>();
    var stats = cm.GetStatistics();

    var disconnectTasks = stats.Connections
        .Select(c => cm.DisconnectClientAsync(c.ClientId, "Server shutting down"))
        .ToArray();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        await Task.WhenAll(disconnectTasks).WaitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        Log.Warning("Graceful shutdown timed out after 10 seconds");
    }

    Log.Information("Graceful shutdown complete — {Count} clients notified", disconnectTasks.Length);
});

// ============================================================
// IDLE CLIENT CLEANUP — runs alongside session cleanup
// ============================================================
var idleCleanupTimer = new Timer(async _ =>
{
    try
    {
        var cm = app.Services.GetRequiredService<ConnectionManager>();
        var idleClients = cm.GetIdleClients();

        foreach (var clientId in idleClients)
        {
            Log.Information("Disconnecting idle client | ClientId: {ClientId}", clientId);
            await cm.DisconnectClientAsync(clientId, "Idle timeout");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error during idle client cleanup");
    }
}, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

lifetime.ApplicationStopping.Register(() => idleCleanupTimer.Dispose());

// ============================================================
// WEBSOCKET SIGNALING ENDPOINT — requires token from /authenticate
// ============================================================
app.Map("/signal", async (HttpContext context, ConnectionManager cm) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    string clientId;
    var allowLegacy = builder.Configuration.GetValue<bool>("Security:AllowLegacyConnections", false);

    if (context.Request.Query.TryGetValue("token", out var tokenValue) &&
        !string.IsNullOrWhiteSpace(tokenValue.ToString()))
    {
        if (!cm.ValidateAndConsumeToken(tokenValue.ToString(), out clientId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    else if (allowLegacy &&
             context.Request.Query.TryGetValue("clientId", out var requestedId) &&
             !string.IsNullOrWhiteSpace(requestedId.ToString()))
    {
        clientId = requestedId.ToString();
        app.Logger.LogWarning(
            "WebSocket connected WITHOUT token (legacy mode) | ClientId: {ClientId} | SECURITY RISK",
            clientId);
    }
    else
    {
        app.Logger.LogWarning("WebSocket connection rejected — no valid token provided");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    // Check connection limits
    if (!cm.CanAcceptConnection())
    {
        app.Logger.LogWarning("Connection limit reached ({Max}), rejecting client {ClientId}",
            cm.MaxConnections, clientId);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    var maxMessageSize = builder.Configuration.GetValue<int>("WebSocket:MaxMessageSize", 16384);
    var buffer = new byte[maxMessageSize];
    var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    WebSocketReceiveResult? receiveResult = null;

    try
    {
        if (!cm.RegisterClient(clientId, webSocket, remoteAddress))
        {
            var errorMessage = new SignalingMessage
            {
                Type = SignalingMessageType.Error,
                SenderId = "server",
                ErrorMessage = $"Client ID '{clientId}' is already in use",
                Timestamp = DateTime.UtcNow
            };
            var errorJson = JsonSerializer.Serialize(errorMessage);
            var errorBytes = Encoding.UTF8.GetBytes(errorJson);
            await webSocket.SendAsync(
                new ArraySegment<byte>(errorBytes),
                WebSocketMessageType.Text,
                true,
                context.RequestAborted);
            return;
        }

        // Send confirmation with client ID
        var identifyMessage = new SignalingMessage
        {
            Type = SignalingMessageType.Identify,
            SenderId = "server",
            TargetId = clientId,
            Timestamp = DateTime.UtcNow
        };

        var identifyJson = JsonSerializer.Serialize(identifyMessage);
        var identifyBytes = Encoding.UTF8.GetBytes(identifyJson);
        await webSocket.SendAsync(
            new ArraySegment<byte>(identifyBytes),
            WebSocketMessageType.Text,
            true,
            context.RequestAborted);

        using var messageStream = new MemoryStream();

        receiveResult = await webSocket.ReceiveAsync(
            new ArraySegment<byte>(buffer),
            context.RequestAborted);

        while (!receiveResult.CloseStatus.HasValue)
        {
            if (receiveResult.MessageType == WebSocketMessageType.Text)
            {
                messageStream.Write(buffer, 0, receiveResult.Count);

                if (receiveResult.EndOfMessage)
                {
                    var json = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                    messageStream.SetLength(0);

                    try
                    {
                        var message = JsonSerializer.Deserialize<SignalingMessage>(json);

                        if (message != null)
                        {
                            cm.RecordMessageReceived(clientId);

                            message.SenderId = clientId;
                            message.Timestamp = DateTime.UtcNow;

                            if (!string.IsNullOrWhiteSpace(message.TargetId))
                            {
                                await cm.SendMessageAsync(message, context.RequestAborted);
                            }
                            else
                            {
                                await cm.BroadcastAsync(clientId, message, context.RequestAborted);
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        app.Logger.LogError(ex, "Error deserializing message from client {ClientId}", clientId);
                    }
                }
            }

            receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                context.RequestAborted);
        }

        await webSocket.CloseAsync(
            receiveResult.CloseStatus.Value,
            receiveResult.CloseStatusDescription,
            context.RequestAborted);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error handling WebSocket for client {ClientId}", clientId);
    }
    finally
    {
        if (clientId != null)
        {
            var disconnectMessage = new SignalingMessage
            {
                Type = SignalingMessageType.Error,
                SenderId = clientId,
                ErrorMessage = $"Client {clientId} disconnected",
                Timestamp = DateTime.UtcNow
            };

            try
            {
                await cm.BroadcastAsync(clientId, disconnectMessage, CancellationToken.None);
            }
            catch
            {
                // Ignore errors during cleanup broadcast
            }

            var reason = receiveResult?.CloseStatusDescription ?? "Connection closed";
            cm.UnregisterClient(clientId, reason);
        }
    }
});

// ============================================================
// ROOT ENDPOINT
// ============================================================
app.MapGet("/", async (HttpContext context) =>
{
    var demoPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "demo.html");
    if (File.Exists(demoPath))
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(demoPath);
    }
    else
    {
        return Results.Ok(new
        {
            Service = "RemoteDesktop Signaling Server",
            Version = "1.0.0",
            Endpoints = new
            {
                WebSocket = "/signal",
                Health = "/health",
                Statistics = "/statistics",
                StunStatistics = "/stun/statistics",
                TurnStatistics = "/turn/statistics"
            },
            Message = "Server is running. Open demo.html in browser for testing interface."
        });
    }
    return Results.Empty;
});

// ============================================================
// REGISTER ENDPOINT — rate limited
// ============================================================
app.MapPost("/register", async (HttpContext context, IServerSessionStorage sessionStorage) =>
{
    try
    {
        var registration = await context.Request.ReadFromJsonAsync<DeskShare.Core.Auth.ServerRegistrationMessage>();

        if (registration == null)
        {
            return Results.BadRequest(new { Error = "Invalid registration data" });
        }

        var response = await sessionStorage.RegisterServerAsync(registration);

        if (response.Success)
        {
            return Results.Ok(response);
        }
        else
        {
            return Results.BadRequest(response);
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error processing server registration");
        return Results.Problem("Failed to process registration");
    }
}).RequireRateLimiting("register");

// ============================================================
// AUTHENTICATE ENDPOINT — rate limited, issues WebSocket token
// ============================================================
app.MapPost("/authenticate", async (HttpContext context, IServerSessionStorage sessionStorage, ConnectionManager cm) =>
{
    try
    {
        var authRequest = await context.Request.ReadFromJsonAsync<DeskShare.Core.Auth.ClientAuthenticationMessage>();

        if (authRequest == null)
        {
            return Results.BadRequest(new { Error = "Invalid authentication data" });
        }

        var response = await sessionStorage.AuthenticateClientAsync(authRequest);

        if (response.Success)
        {
            cm.RecordAuthSuccess();

            var token = cm.IssueWebSocketToken(authRequest.ClientId);

            var iceServers = builder.Configuration.GetSection("IceServers")
                .GetChildren()
                .Select(s => new
                {
                    urls = s["Urls"],
                    username = s["Username"],
                    credential = s["Credential"]
                })
                .Where(s => !string.IsNullOrEmpty(s.urls))
                .Select(s => string.IsNullOrEmpty(s.username)
                    ? (object)new { urls = s.urls }
                    : new { urls = s.urls, username = s.username, credential = s.credential })
                .ToArray();

            return Results.Ok(new
            {
                response.Success,
                response.RemoteControlEnabled,
                WebSocketToken = token,
                IceServers = iceServers
            });
        }
        else
        {
            cm.RecordAuthFailure();
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            Log.Warning("Authentication failed | IP: {IP} | ServerId: {ServerId} | ClientId: {ClientId}",
                ip, authRequest.ServerId, authRequest.ClientId);
            return Results.Unauthorized();
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error processing client authentication");
        return Results.Problem("Failed to process authentication");
    }
}).RequireRateLimiting("authenticate");

// ============================================================
// HEALTH & STATISTICS ENDPOINTS
// ============================================================
app.MapGet("/health", (ConnectionManager cm) => Results.Ok(new
{
    Status = "Healthy",
    Connections = cm.ConnectionCount,
    MaxConnections = cm.MaxConnections,
    Timestamp = DateTime.UtcNow
}));

var diagnosticsApiKey = builder.Configuration.GetValue<string>("Security:DiagnosticsApiKey") ?? "";

IResult ValidateDiagnosticsAccess(HttpContext ctx)
{
    if (string.IsNullOrEmpty(diagnosticsApiKey))
        return null!;

    var providedKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault()
                   ?? ctx.Request.Query["apiKey"].FirstOrDefault();

    if (string.IsNullOrEmpty(providedKey) || providedKey != diagnosticsApiKey)
        return Results.Unauthorized();

    return null!;
}

app.MapGet("/statistics", (HttpContext ctx, ConnectionManager cm) =>
{
    var denied = ValidateDiagnosticsAccess(ctx);
    if (denied != null) return denied;

    var stats = cm.GetStatistics();
    return Results.Ok(stats);
});

app.MapGet("/servers/{serverId}/status", async (HttpContext ctx, string serverId, IServerSessionStorage sessionStorage) =>
{
    var denied = ValidateDiagnosticsAccess(ctx);
    if (denied != null) return denied;

    var status = await sessionStorage.GetServerStatusAsync(serverId);

    if (status == null)
    {
        return Results.NotFound(new
        {
            Error = "Server not found",
            ServerId = serverId,
            Message = "Server is not registered or has been removed",
            Timestamp = DateTime.UtcNow
        });
    }

    return Results.Ok(status);
});

app.MapGet("/servers/status", async (HttpContext ctx, IServerSessionStorage sessionStorage) =>
{
    var denied = ValidateDiagnosticsAccess(ctx);
    if (denied != null) return denied;

    var statuses = await sessionStorage.GetAllServerStatusesAsync();
    return Results.Ok(new
    {
        TotalServers = statuses.Count,
        OnlineServers = statuses.Count(s => s.IsOnline),
        Servers = statuses,
        Timestamp = DateTime.UtcNow
    });
});

app.MapGet("/stun/statistics", (IEnumerable<IHostedService> hostedServices) =>
{
    var stunServer = hostedServices.OfType<StunServer>().FirstOrDefault();
    if (stunServer == null)
        return Results.NotFound(new { Error = "STUN server not found" });

    var stats = stunServer.GetStatistics();
    return Results.Ok(new
    {
        Status = stats.IsRunning ? "Running" : "Stopped",
        Port = 3478,
        RequestsProcessed = stats.RequestsProcessed,
        ResponsesSent = stats.ResponsesSent,
        ErrorsEncountered = stats.ErrorsEncountered,
        Timestamp = DateTime.UtcNow
    });
});

app.MapGet("/turn/statistics", (IEnumerable<IHostedService> hostedServices, IConfiguration configuration) =>
{
    var turnEnabled = configuration.GetValue<bool>("Turn:Enabled", false);

    if (!turnEnabled)
    {
        return Results.Ok(new
        {
            Status = "Disabled",
            Message = "TURN server is disabled. Only P2P connections are available.",
            Timestamp = DateTime.UtcNow
        });
    }

    var turnServer = hostedServices.OfType<TurnServer>().FirstOrDefault();
    if (turnServer == null)
        return Results.NotFound(new { Error = "TURN server not found" });

    var stats = turnServer.GetStatistics();
    return Results.Ok(new
    {
        Status = stats.IsRunning ? "Running" : "Stopped",
        Port = 3478,
        RequestsProcessed = stats.RequestsProcessed,
        AllocationsCreated = stats.AllocationsCreated,
        ActiveAllocations = stats.ActiveAllocations,
        ErrorsEncountered = stats.ErrorsEncountered,
        TotalBytesRelayed = stats.TotalByteRelayed,
        Timestamp = DateTime.UtcNow
    });
});

app.Run();
