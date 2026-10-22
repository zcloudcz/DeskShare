using System.Runtime.InteropServices;
using DeskShare.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging; // CreateLogger<T>() extension
using Serilog;
using Serilog.Extensions.Logging;

// Headless host over DeskShare.Core.ScreenSenderService - the same sender the desktop apps
// run in-process. Intended for machines without a UI (CLI, service, container).
// Any appsettings.json key can be overridden on the command line, e.g.:
//   --Signaling:ServerUrl=wss://host/signal --Capture:TargetFps=15 --Security:EnableRemoteControl=true
//   --config <file.json> (unattended service): extra JSON config; Server ID and owner secret are then kept next to it
//   instead of in the per-user %APPDATA%, because the agent runs as SYSTEM.
var configFile = new ConfigurationBuilder().AddCommandLine(args).Build()["config"];
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile(configFile ?? "unused.json", optional: configFile is null)
    .AddCommandLine(args)
    .Build();

Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();

try
{
    // Defaults come from ScreenSenderConfiguration itself; a missing key keeps them.
    var config = new ScreenSenderConfiguration
    {
        ServerId = configuration["Signaling:ServerId"] ?? string.Empty,
        SignalingServerUrl = configuration["Signaling:ServerUrl"] ?? "wss://app.deskshare.zcloud.cz/signal",
        TargetFps = configuration.GetValue("Capture:TargetFps", 30),
        MaxOutputWidth = configuration.GetValue("Capture:MaxOutputWidth", 0),
        AdapterIndex = configuration.GetValue("Capture:AdapterIndex", 0),
        OutputIndex = configuration.GetValue("Capture:OutputIndex", 0),
        EnableRemoteControl = configuration.GetValue("Security:EnableRemoteControl", false),
        TrustClientPermanent = configuration.GetValue("Security:TrustClientPermanent", false)
    };
    var logger = new SerilogLoggerFactory(Log.Logger).CreateLogger<ScreenSenderService>();
    ScreenSenderService service;
    if (configFile is null)
    {
        config.EnsureServerId(); // empty ServerId -> persisted per-user id
        service = new ScreenSenderService(logger, config);
    }
    else
    {
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(configFile))!;
        var idPath = Path.Combine(dataDir, "server-id");
        if (string.IsNullOrEmpty(config.ServerId))
            config.ServerId = ServerIdGenerator.GetOrCreateServerId(idPath);
        service = new ScreenSenderService(logger, config, idPath, new DeskShare.Core.Auth.ServerOwnerSecretStore(Path.Combine(dataDir, "server-owner.key")));
    }
    using var serviceLifetime = service;

    // Subscribe before StartAsync, otherwise the first passkey would be missed.
    service.PasskeyChanged += (_, e) =>
        Console.WriteLine($"Passkey: {DeskShare.Core.Auth.AuthenticationService.FormatPasskeyForDisplay(e.Passkey)} (valid until {e.ValidTo.ToLocalTime():HH:mm:ss})");
    Console.WriteLine($"Server ID: {service.ServerId}");

    // Cancel the default kill for Ctrl+C / SIGTERM so the service can tear down gracefully
    // (stop the pipeline, close the WebRTC session) before the process exits.
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });

    await service.StartAsync(stop.Token);
    Console.WriteLine("Streaming. Press Ctrl+C to stop.");
    await Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => { }); // completes on cancel, no exception
    await service.StopAsync(CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Screen sender terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
