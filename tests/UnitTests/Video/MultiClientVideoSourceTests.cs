using DeskShare.Core.Models;
using DeskShare.Core.Video;
using SIPSorcery.Net;

namespace DeskShare.UnitTests.Video;

/// <summary>
/// Unit tests for MultiClientVideoSource.
/// Tests multi-client broadcasting functionality, client management,
/// and proper resource cleanup.
/// </summary>
public sealed class MultiClientVideoSourceTests : IDisposable
{
    private readonly MultiClientVideoSource _videoSource = new();

    /// <summary>
    /// Tests that Initialize with valid parameters succeeds.
    /// This is the basic happy path test.
    /// </summary>
    [Fact]
    public void Initialize_ValidParameters_Success()
    {
        // Arrange
        var width = 1920;
        var height = 1080;
        var frameRate = 30;

        // Act
        var result = _videoSource.Initialize(width, height, frameRate);

        // Assert
        Assert.True(result);
        Assert.True(_videoSource.IsInitialized);
    }

    /// <summary>
    /// Tests that Initialize with odd width throws ArgumentException.
    /// I420 format requires even dimensions.
    /// </summary>
    [Fact]
    public void Initialize_OddWidth_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => _videoSource.Initialize(1921, 1080, 30));
        Assert.Contains("must be positive and even", exception.Message);
    }

    /// <summary>
    /// Tests that Initialize with odd height throws ArgumentException.
    /// I420 format requires even dimensions.
    /// </summary>
    [Fact]
    public void Initialize_OddHeight_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => _videoSource.Initialize(1920, 1081, 30));
        Assert.Contains("must be positive and even", exception.Message);
    }

    /// <summary>
    /// Tests that Initialize with invalid frame rate throws ArgumentException.
    /// Frame rate must be between 1 and 120.
    /// </summary>
    [Fact]
    public void Initialize_InvalidFrameRate_ThrowsArgumentException()
    {
        // Act & Assert - Frame rate too high
        Assert.Throws<ArgumentException>(
            () => _videoSource.Initialize(1920, 1080, 121));

        // Act & Assert - Frame rate zero
        Assert.Throws<ArgumentException>(
            () => _videoSource.Initialize(1920, 1080, 0));

        // Act & Assert - Frame rate negative
        Assert.Throws<ArgumentException>(
            () => _videoSource.Initialize(1920, 1080, -1));
    }

    /// <summary>
    /// Tests that Initialize can only be called once.
    /// Second call should throw InvalidOperationException.
    /// </summary>
    [Fact]
    public void Initialize_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => _videoSource.Initialize(1920, 1080, 30));
    }

    /// <summary>
    /// Tests that AddClient creates a peer connection for new client.
    /// Each client should get their own dedicated peer connection.
    /// Note: ConnectedClientCount only counts clients in "connected" state,
    /// not just added clients. WebRTC negotiation is needed for connection.
    /// </summary>
    [Fact]
    public void AddClient_AfterInitialize_ReturnsPeerConnection()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act
        var peerConnection = _videoSource.AddClient("client-1");

        // Assert
        Assert.NotNull(peerConnection);
        // Note: ConnectedClientCount will be 0 until WebRTC connection is established
        // We just verify peer connection was created successfully
    }

    /// <summary>
    /// Tests that AddClient before Initialize throws InvalidOperationException.
    /// VideoSource must be initialized before adding clients.
    /// </summary>
    [Fact]
    public void AddClient_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => _videoSource.AddClient("client-1"));
    }

    /// <summary>
    /// Tests that adding the same client twice throws ArgumentException.
    /// Each client ID must be unique.
    /// </summary>
    [Fact]
    public void AddClient_DuplicateClientId_ThrowsArgumentException()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);
        _videoSource.AddClient("client-1");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(
            () => _videoSource.AddClient("client-1"));
        Assert.Contains("already exists", exception.Message);
    }

    /// <summary>
    /// Tests that multiple clients can be added successfully.
    /// VideoSource should support multiple concurrent clients.
    /// </summary>
    [Fact]
    public void AddClient_MultipleClients_AllSucceed()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act - Add 5 clients
        for (var i = 1; i <= 5; i++)
        {
            var peerConnection = _videoSource.AddClient($"client-{i}");
            Assert.NotNull(peerConnection);
        }

        // Assert - We don't check ConnectedClientCount here because
        // clients need WebRTC negotiation to be "connected"
    }

    /// <summary>
    /// Tests that RemoveClient removes a client successfully.
    /// This tests proper cleanup of client resources.
    /// </summary>
    [Fact]
    public void RemoveClient_ExistingClient_Success()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);
        _videoSource.AddClient("client-1");

        // Act
        _videoSource.RemoveClient("client-1");

        // Assert - Client should be removed (can add again)
        var peerConnection = _videoSource.AddClient("client-1");
        Assert.NotNull(peerConnection);
    }

    /// <summary>
    /// Tests that RemoveClient with non-existent client is safe (no exception).
    /// Removing a non-existent client should be a no-op.
    /// </summary>
    [Fact]
    public void RemoveClient_NonExistentClient_NoException()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act & Assert - Should not throw
        _videoSource.RemoveClient("non-existent-client");
    }

    /// <summary>
    /// Tests that GetClientPeerConnection returns correct peer connection.
    /// This allows retrieving a specific client's peer connection.
    /// </summary>
    [Fact]
    public void GetClientPeerConnection_ExistingClient_ReturnsPeerConnection()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);
        var originalPeerConnection = _videoSource.AddClient("client-1");

        // Act
        var retrievedPeerConnection = _videoSource.GetClientPeerConnection("client-1");

        // Assert
        Assert.NotNull(retrievedPeerConnection);
        Assert.Same(originalPeerConnection, retrievedPeerConnection);
    }

    /// <summary>
    /// Tests that GetClientPeerConnection returns null for non-existent client.
    /// This is the expected behavior for missing clients.
    /// </summary>
    [Fact]
    public void GetClientPeerConnection_NonExistentClient_ReturnsNull()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act
        var peerConnection = _videoSource.GetClientPeerConnection("non-existent-client");

        // Assert
        Assert.Null(peerConnection);
    }

    /// <summary>
    /// Tests that PushFrame before Initialize throws InvalidOperationException.
    /// VideoSource must be initialized before pushing frames.
    /// </summary>
    [Fact]
    public void PushFrame_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // Arrange
        var frame = CreateTestVideoFrame(1920, 1080);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => _videoSource.PushFrame(frame));
    }

    /// <summary>
    /// Tests that PushFrame with null frame throws ArgumentNullException.
    /// Frame parameter must not be null.
    /// </summary>
    [Fact]
    public void PushFrame_NullFrame_ThrowsArgumentNullException()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => _videoSource.PushFrame(null!));
    }

    /// <summary>
    /// Tests that PushFrame with no clients returns false.
    /// When no clients are connected, frames should be dropped.
    /// </summary>
    [Fact]
    public void PushFrame_NoClients_ReturnsFalse()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);
        var frame = CreateTestVideoFrame(1920, 1080);

        // Act
        var result = _videoSource.PushFrame(frame);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Tests that GetStatistics returns valid statistics.
    /// This verifies that statistics tracking works correctly.
    /// </summary>
    [Fact]
    public void GetStatistics_AfterInitialize_ReturnsValidStatistics()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act
        var stats = _videoSource.GetStatistics();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0, stats.FramesPushed);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.CurrentFps);
    }

    /// <summary>
    /// Tests that GetClientStatistics returns empty dictionary when no clients.
    /// This is the expected behavior with zero clients.
    /// </summary>
    [Fact]
    public void GetClientStatistics_NoClients_ReturnsEmptyDictionary()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act
        var clientStats = _videoSource.GetClientStatistics();

        // Assert
        Assert.NotNull(clientStats);
        Assert.Empty(clientStats);
    }

    /// <summary>
    /// Tests that GetClientStatistics returns statistics for all clients.
    /// Each client should have individual statistics tracked.
    /// </summary>
    [Fact]
    public void GetClientStatistics_MultipleClients_ReturnsAllClientStats()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);
        _videoSource.AddClient("client-1");
        _videoSource.AddClient("client-2");
        _videoSource.AddClient("client-3");

        // Act
        var clientStats = _videoSource.GetClientStatistics();

        // Assert
        Assert.Equal(3, clientStats.Count);
        Assert.True(clientStats.ContainsKey("client-1"));
        Assert.True(clientStats.ContainsKey("client-2"));
        Assert.True(clientStats.ContainsKey("client-3"));
    }

    /// <summary>
    /// Tests that IsActive returns false when no clients are added.
    /// VideoSource is only active when it has clients.
    /// </summary>
    [Fact]
    public void IsActive_NoClients_ReturnsFalse()
    {
        // Arrange
        _videoSource.Initialize(1920, 1080, 30);

        // Act
        var isActive = _videoSource.IsActive;

        // Assert
        Assert.False(isActive);
    }

    /// <summary>
    /// Tests that Dispose can be called multiple times safely.
    /// Multiple dispose should be idempotent (no errors).
    /// </summary>
    [Fact]
    public void Dispose_MultipleTimes_NoException()
    {
        // Arrange
        var videoSource = new MultiClientVideoSource();
        videoSource.Initialize(1920, 1080, 30);

        // Act & Assert
        videoSource.Dispose();
        videoSource.Dispose(); // Second dispose should be safe
    }

    /// <summary>
    /// Tests that operations after Dispose throw ObjectDisposedException.
    /// Once disposed, the VideoSource should not be usable.
    /// </summary>
    [Fact]
    public void PushFrame_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var videoSource = new MultiClientVideoSource();
        videoSource.Initialize(1920, 1080, 30);
        var frame = CreateTestVideoFrame(1920, 1080);
        videoSource.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(
            () => videoSource.PushFrame(frame));
    }

    /// <summary>
    /// Helper method to create a test VideoFrame.
    /// Creates a properly formatted I420 frame with specified dimensions.
    /// </summary>
    /// <param name="width">Frame width (must be even).</param>
    /// <param name="height">Frame height (must be even).</param>
    /// <returns>A test VideoFrame with gray pixels.</returns>
    private static VideoFrame CreateTestVideoFrame(int width, int height)
    {
        var yPlaneSize = width * height;
        var uvPlaneSize = (width / 2) * (height / 2);

        var yPlane = new byte[yPlaneSize];
        var uPlane = new byte[uvPlaneSize];
        var vPlane = new byte[uvPlaneSize];

        // Fill with gray (Y=128, U=128, V=128)
        Array.Fill(yPlane, (byte)128);
        Array.Fill(uPlane, (byte)128);
        Array.Fill(vPlane, (byte)128);

        return new VideoFrame(
            width,
            height,
            yPlane,
            uPlane,
            vPlane,
            width,       // yStride
            width / 2,   // uStride
            width / 2,   // vStride
            DateTime.UtcNow);
    }

    /// <summary>
    /// Cleanup method called after each test.
    /// Ensures proper disposal of resources.
    /// </summary>
    public void Dispose()
    {
        _videoSource.Dispose();
    }
}
