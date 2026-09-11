using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeskShare.Stun;
using DeskShare.Turn;

namespace DeskShare.IntegrationTests.Turn;

public class TurnServerIntegrationTests : IAsyncLifetime
{
    private IHost? _host;
    private const int TestPort = 13479; // Different port to avoid conflicts
    private TurnServer? _turnServer;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateDefaultBuilder();

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new TurnServerOptions
            {
                Port = TestPort,
                Realm = "test.realm",
                EnableTestUser = true, // Enable test user for integration tests
                MinRelayPort = 50000,
                MaxRelayPort = 50100
            });
            services.AddSingleton<ILogger<RelayEngine>>(sp =>
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<RelayEngine>());
            services.AddHostedService<TurnServer>();
        });

        _host = builder.Build();

        // Start the host
        await _host.StartAsync();

        // Get reference to TURN server
        var hostedServices = _host.Services.GetServices<IHostedService>();
        _turnServer = hostedServices.OfType<TurnServer>().FirstOrDefault();

        // Give server time to start
        await Task.Delay(500);
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task TurnServer_StartsSuccessfully()
    {
        // Assert
        Assert.NotNull(_turnServer);
        var stats = _turnServer.GetStatistics();
        Assert.True(stats.IsRunning);
    }

    [Fact]
    public async Task TurnServer_RespondsToBindingRequest()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        var request = new StunMessage
        {
            MessageType = StunMessageType.BindingRequest,
            TransactionId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }
        };
        var requestBytes = request.ToBytes();

        // Act
        await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);

        // Wait for response with timeout
        var receiveTask = client.ReceiveAsync();
        var timeoutTask = Task.Delay(2000);
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

        // Assert
        Assert.Equal(receiveTask, completedTask); // Ensure we got response, not timeout

        var result = await receiveTask;
        var response = StunMessage.Parse(result.Buffer);
        Assert.Equal(StunMessageType.BindingResponse, response.MessageType);
        Assert.Equal(request.TransactionId, response.TransactionId);
    }

    [Fact]
    public async Task TurnServer_AllocateRequest_CreatesAllocation()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        var request = new StunMessage
        {
            MessageType = (StunMessageType)0x0003, // Allocate Request
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(request.TransactionId);
        var requestBytes = request.ToBytes();

        // Act
        await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);

        var receiveTask = client.ReceiveAsync();
        var timeoutTask = Task.Delay(2000);
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

        // Assert
        Assert.Equal(receiveTask, completedTask);

        var result = await receiveTask;
        var response = StunMessage.Parse(result.Buffer);

        // Should be Allocate Success Response (0x0103)
        Assert.Equal((StunMessageType)0x0103, response.MessageType);
        Assert.Equal(request.TransactionId, response.TransactionId);

        // Should contain XOR-RELAYED-ADDRESS attribute
        var relayedAddress = response.Attributes.FirstOrDefault(a =>
            a.Type == StunAttributeType.XorRelayedAddress);
        Assert.NotNull(relayedAddress);
    }

    [Fact]
    public async Task TurnServer_AllocateRequest_UpdatesStatistics()
    {
        // Arrange
        Assert.NotNull(_turnServer);
        var initialStats = _turnServer.GetStatistics();
        var initialAllocations = initialStats.AllocationsCreated;

        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        var request = new StunMessage
        {
            MessageType = (StunMessageType)0x0003, // Allocate Request
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(request.TransactionId);
        var requestBytes = request.ToBytes();

        // Act
        await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);
        await client.ReceiveAsync(); // Wait for response

        // Give server time to update stats
        await Task.Delay(200);

        var finalStats = _turnServer.GetStatistics();

        // Assert
        Assert.True(finalStats.AllocationsCreated > initialAllocations);
        Assert.True(finalStats.ActiveAllocations >= 1);
    }

    [Fact]
    public async Task TurnServer_MultipleAllocations_AllSucceed()
    {
        // Arrange
        const int allocationCount = 3;
        var clients = new List<UdpClient>();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        try
        {
            // Act - Create multiple allocations
            for (int i = 0; i < allocationCount; i++)
            {
                var client = new UdpClient();
                clients.Add(client);

                var request = new StunMessage
                {
                    MessageType = (StunMessageType)0x0003, // Allocate Request
                    TransactionId = new byte[12]
                };
                Random.Shared.NextBytes(request.TransactionId);
                var requestBytes = request.ToBytes();

                await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);
                var result = await client.ReceiveAsync();
                var response = StunMessage.Parse(result.Buffer);

                // Assert each allocation succeeds
                Assert.Equal((StunMessageType)0x0103, response.MessageType);
            }

            // Assert - Check statistics
            Assert.NotNull(_turnServer);
            await Task.Delay(200);
            var stats = _turnServer.GetStatistics();
            Assert.True(stats.ActiveAllocations >= allocationCount);
        }
        finally
        {
            // Cleanup
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task TurnServer_RefreshRequest_ExtendsAllocation()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        // First, create an allocation
        var allocateRequest = new StunMessage
        {
            MessageType = (StunMessageType)0x0003, // Allocate Request
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(allocateRequest.TransactionId);
        await client.SendAsync(allocateRequest.ToBytes(), allocateRequest.ToBytes().Length, serverEndPoint);
        await client.ReceiveAsync(); // Wait for allocate response

        // Act - Send refresh request
        var refreshRequest = new StunMessage
        {
            MessageType = (StunMessageType)0x0004, // Refresh Request
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(refreshRequest.TransactionId);
        var refreshBytes = refreshRequest.ToBytes();

        await client.SendAsync(refreshBytes, refreshBytes.Length, serverEndPoint);

        var receiveTask = client.ReceiveAsync();
        var timeoutTask = Task.Delay(2000);
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

        // Assert
        Assert.Equal(receiveTask, completedTask);

        var result = await receiveTask;
        var response = StunMessage.Parse(result.Buffer);

        // Should be Refresh Success Response (0x0104)
        Assert.Equal((StunMessageType)0x0104, response.MessageType);
        Assert.Equal(refreshRequest.TransactionId, response.TransactionId);
    }

    [Fact]
    public async Task TurnServer_CreatePermissionRequest_Succeeds()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        // First, create an allocation
        var allocateRequest = new StunMessage
        {
            MessageType = (StunMessageType)0x0003,
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(allocateRequest.TransactionId);
        await client.SendAsync(allocateRequest.ToBytes(), allocateRequest.ToBytes().Length, serverEndPoint);
        await client.ReceiveAsync();

        // Act - Send CreatePermission request
        var permissionRequest = new StunMessage
        {
            MessageType = (StunMessageType)0x0008, // CreatePermission Request
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(permissionRequest.TransactionId);
        var permissionBytes = permissionRequest.ToBytes();

        await client.SendAsync(permissionBytes, permissionBytes.Length, serverEndPoint);

        var receiveTask = client.ReceiveAsync();
        var timeoutTask = Task.Delay(2000);
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

        // Assert
        Assert.Equal(receiveTask, completedTask);

        var result = await receiveTask;
        var response = StunMessage.Parse(result.Buffer);

        // Should be CreatePermission Success Response (0x0108)
        Assert.Equal((StunMessageType)0x0108, response.MessageType);
    }

    [Fact]
    public async Task TurnServer_ChannelBindRequest_Succeeds()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        // First, create an allocation
        var allocateRequest = new StunMessage
        {
            MessageType = (StunMessageType)0x0003,
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(allocateRequest.TransactionId);
        await client.SendAsync(allocateRequest.ToBytes(), allocateRequest.ToBytes().Length, serverEndPoint);
        await client.ReceiveAsync();

        // Act - Send ChannelBind request
        var channelBindRequest = new StunMessage
        {
            MessageType = (StunMessageType)0x0009, // ChannelBind Request
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(channelBindRequest.TransactionId);
        var channelBindBytes = channelBindRequest.ToBytes();

        await client.SendAsync(channelBindBytes, channelBindBytes.Length, serverEndPoint);

        var receiveTask = client.ReceiveAsync();
        var timeoutTask = Task.Delay(2000);
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

        // Assert
        Assert.Equal(receiveTask, completedTask);

        var result = await receiveTask;
        var response = StunMessage.Parse(result.Buffer);

        // Should be ChannelBind Success Response (0x0109)
        Assert.Equal((StunMessageType)0x0109, response.MessageType);
    }

    [Fact]
    public async Task TurnServer_Statistics_TracksRequestsCorrectly()
    {
        // Arrange
        Assert.NotNull(_turnServer);
        var initialStats = _turnServer.GetStatistics();
        var initialRequests = initialStats.RequestsProcessed;

        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        // Act - Send multiple different request types
        var requestTypes = new[]
        {
            StunMessageType.BindingRequest,
            (StunMessageType)0x0003, // Allocate
        };

        foreach (var type in requestTypes)
        {
            var request = new StunMessage
            {
                MessageType = type,
                TransactionId = new byte[12]
            };
            Random.Shared.NextBytes(request.TransactionId);
            await client.SendAsync(request.ToBytes(), request.ToBytes().Length, serverEndPoint);
            await client.ReceiveAsync();
        }

        await Task.Delay(200);
        var finalStats = _turnServer.GetStatistics();

        // Assert
        Assert.True(finalStats.RequestsProcessed >= initialRequests + requestTypes.Length);
    }

    [Fact]
    public async Task TurnServer_ConcurrentAllocations_HandledCorrectly()
    {
        // Arrange
        const int concurrentCount = 5;
        var tasks = new List<Task<bool>>();

        // Act
        for (int i = 0; i < concurrentCount; i++)
        {
            tasks.Add(CreateAllocationAndValidate());
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result));
    }

    private async Task<bool> CreateAllocationAndValidate()
    {
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        var request = new StunMessage
        {
            MessageType = (StunMessageType)0x0003, // Allocate Request
            TransactionId = new byte[12]
        };
        Random.Shared.NextBytes(request.TransactionId);

        await client.SendAsync(request.ToBytes(), request.ToBytes().Length, serverEndPoint);

        var receiveTask = client.ReceiveAsync();
        var timeoutTask = Task.Delay(3000);
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

        if (completedTask == timeoutTask)
            return false;

        var result = await receiveTask;
        var response = StunMessage.Parse(result.Buffer);

        return response.MessageType == (StunMessageType)0x0103; // Allocate Success
    }
}
