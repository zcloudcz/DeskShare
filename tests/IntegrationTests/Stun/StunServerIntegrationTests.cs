using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeskShare.Stun;

namespace DeskShare.IntegrationTests.Stun;

public class StunServerIntegrationTests : IAsyncLifetime
{
    private IHost? _host;
    private const int TestPort = 13478; // Different port to avoid conflicts
    private StunServer? _stunServer;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateDefaultBuilder();

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new StunServerOptions
            {
                Port = TestPort
            });
            services.AddHostedService<StunServer>();
        });

        _host = builder.Build();

        // Start the host
        await _host.StartAsync();

        // Get reference to STUN server
        var hostedServices = _host.Services.GetServices<IHostedService>();
        _stunServer = hostedServices.OfType<StunServer>().FirstOrDefault();

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
    public async Task StunServer_StartsSuccessfully()
    {
        // Assert
        Assert.NotNull(_stunServer);
        var stats = _stunServer.GetStatistics();
        Assert.True(stats.IsRunning);
    }

    [Fact]
    public async Task StunServer_RespondsToBindingRequest()
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
        Assert.NotNull(result.Buffer);
        Assert.True(result.Buffer.Length >= StunMessage.HeaderSize);

        // Verify it's a valid STUN message
        var magicCookie = BinaryPrimitives.ReadUInt32BigEndian(result.Buffer.AsSpan(4, 4));
        Assert.Equal(StunMessage.MagicCookie, magicCookie);

        // Parse response
        var response = StunMessage.Parse(result.Buffer);
        Assert.Equal(StunMessageType.BindingResponse, response.MessageType);
        Assert.Equal(request.TransactionId, response.TransactionId);
    }

    [Fact]
    public async Task StunServer_ResponseContainsXorMappedAddress()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        var request = new StunMessage
        {
            MessageType = StunMessageType.BindingRequest,
            TransactionId = new byte[12]
        };
        var requestBytes = request.ToBytes();

        // Act
        await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);
        var result = await client.ReceiveAsync();
        var response = StunMessage.Parse(result.Buffer);

        // Assert
        var xorMappedAddress = response.Attributes.FirstOrDefault(a =>
            a.Type == StunAttributeType.XorMappedAddress);
        Assert.NotNull(xorMappedAddress);
        Assert.NotNull(xorMappedAddress.Value);
        Assert.True(xorMappedAddress.Value.Length >= 8); // IPv4
    }

    [Fact]
    public async Task StunServer_MultipleRequests_AllGetResponses()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);
        const int requestCount = 5;
        var transactionIds = new List<byte[]>();

        // Act - Send multiple requests
        for (int i = 0; i < requestCount; i++)
        {
            var transactionId = new byte[12];
            Random.Shared.NextBytes(transactionId);
            transactionIds.Add(transactionId);

            var request = new StunMessage
            {
                MessageType = StunMessageType.BindingRequest,
                TransactionId = transactionId
            };
            var requestBytes = request.ToBytes();
            await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);
        }

        // Receive all responses
        var responses = new List<StunMessage>();
        for (int i = 0; i < requestCount; i++)
        {
            var result = await client.ReceiveAsync();
            var response = StunMessage.Parse(result.Buffer);
            responses.Add(response);
        }

        // Assert
        Assert.Equal(requestCount, responses.Count);

        // Verify all transaction IDs match
        foreach (var response in responses)
        {
            Assert.Contains(response.TransactionId, transactionIds,
                new ByteArrayEqualityComparer());
        }
    }

    [Fact]
    public async Task StunServer_Statistics_UpdatesAfterRequests()
    {
        // Arrange
        Assert.NotNull(_stunServer);
        var initialStats = _stunServer.GetStatistics();
        var initialRequests = initialStats.RequestsProcessed;
        var initialResponses = initialStats.ResponsesSent;

        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        var request = new StunMessage
        {
            MessageType = StunMessageType.BindingRequest,
            TransactionId = new byte[12]
        };
        var requestBytes = request.ToBytes();

        // Act
        await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);
        await client.ReceiveAsync(); // Wait for response

        // Give server time to update stats
        await Task.Delay(100);

        var finalStats = _stunServer.GetStatistics();

        // Assert
        Assert.True(finalStats.RequestsProcessed > initialRequests);
        Assert.True(finalStats.ResponsesSent > initialResponses);
    }

    [Fact]
    public async Task StunServer_InvalidMessage_DoesNotCrash()
    {
        // Arrange
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);
        var invalidData = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        // Act - Send invalid data
        await client.SendAsync(invalidData, invalidData.Length, serverEndPoint);

        // Wait a bit
        await Task.Delay(200);

        // Assert - Server should still be running
        Assert.NotNull(_stunServer);
        var stats = _stunServer.GetStatistics();
        Assert.True(stats.IsRunning);
    }

    [Fact]
    public async Task StunServer_ConcurrentRequests_HandlesCorrectly()
    {
        // Arrange
        const int concurrentClients = 10;
        var tasks = new List<Task<bool>>();

        // Act - Send requests from multiple clients concurrently
        for (int i = 0; i < concurrentClients; i++)
        {
            tasks.Add(SendBindingRequestAndValidateResponse());
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result));
    }

    private async Task<bool> SendBindingRequestAndValidateResponse()
    {
        using var client = new UdpClient();
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);

        var transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);

        var request = new StunMessage
        {
            MessageType = StunMessageType.BindingRequest,
            TransactionId = transactionId
        };
        var requestBytes = request.ToBytes();

        await client.SendAsync(requestBytes, requestBytes.Length, serverEndPoint);

        var receiveTask = client.ReceiveAsync();
        var timeoutTask = Task.Delay(3000);
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

        if (completedTask == timeoutTask)
            return false;

        var result = await receiveTask;
        var response = StunMessage.Parse(result.Buffer);

        return response.MessageType == StunMessageType.BindingResponse &&
               response.TransactionId.SequenceEqual(transactionId);
    }

    private class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x == null || y == null)
                return x == y;
            return x.SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            return obj.Aggregate(0, (acc, b) => acc ^ b.GetHashCode());
        }
    }
}
