using DeskShare.Core.Auth;
using Xunit;

namespace DeskShare.UnitTests.Auth;

/// <summary>
/// Tests for nonce cache replay attack prevention.
/// Ensures nonces are tracked correctly and duplicates are detected.
/// </summary>
public class NonceCacheTests
{
    [Fact]
    public void TryUseNonce_WithNewNonce_ReturnsTrue()
    {
        // Arrange
        using var cache = new NonceCache();
        var nonce = Guid.NewGuid().ToString();

        // Act
        var result = cache.TryUseNonce(nonce);

        // Assert
        Assert.True(result, "Fresh nonce should be accepted");
    }

    [Fact]
    public void TryUseNonce_WithDuplicateNonce_ReturnsFalse()
    {
        // Arrange
        using var cache = new NonceCache();
        var nonce = Guid.NewGuid().ToString();

        // Act
        var firstUse = cache.TryUseNonce(nonce);
        var secondUse = cache.TryUseNonce(nonce);

        // Assert
        Assert.True(firstUse, "First use should succeed");
        Assert.False(secondUse, "Duplicate nonce should be rejected (replay attack)");
    }

    [Fact]
    public void TryUseNonce_WithMultipleDifferentNonces_ReturnsTrue()
    {
        // Arrange
        using var cache = new NonceCache();

        // Act & Assert - all unique nonces should be accepted
        for (int i = 0; i < 100; i++)
        {
            var nonce = Guid.NewGuid().ToString();
            Assert.True(cache.TryUseNonce(nonce), $"Nonce {i} should be accepted");
        }
    }

    [Fact]
    public void TryUseNonce_WithNullNonce_ThrowsArgumentNullException()
    {
        // Arrange
        using var cache = new NonceCache();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => cache.TryUseNonce(null!));
    }

    [Fact]
    public void TryUseNonce_WithEmptyNonce_ThrowsArgumentNullException()
    {
        // Arrange
        using var cache = new NonceCache();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => cache.TryUseNonce(string.Empty));
    }

    [Fact]
    public void IsNonceUsed_WithUsedNonce_ReturnsTrue()
    {
        // Arrange
        using var cache = new NonceCache();
        var nonce = Guid.NewGuid().ToString();

        cache.TryUseNonce(nonce);

        // Act
        var isUsed = cache.IsNonceUsed(nonce);

        // Assert
        Assert.True(isUsed);
    }

    [Fact]
    public void IsNonceUsed_WithUnusedNonce_ReturnsFalse()
    {
        // Arrange
        using var cache = new NonceCache();
        var nonce = Guid.NewGuid().ToString();

        // Act
        var isUsed = cache.IsNonceUsed(nonce);

        // Assert
        Assert.False(isUsed);
    }

    [Fact]
    public async Task CleanupExpiredNonces_RemovesOldNonces()
    {
        // Arrange - very short TTL for testing
        using var cache = new NonceCache(nonceTtl: TimeSpan.FromMilliseconds(100));

        var nonce = Guid.NewGuid().ToString();
        cache.TryUseNonce(nonce);

        // Act - wait for nonce to expire
        await Task.Delay(150);
        cache.CleanupExpiredNonces(null);

        // Assert - old nonce should be cleaned up
        var stats = cache.GetStatistics();
        Assert.Equal(0, stats.TotalNonces);
    }

    [Fact]
    public async Task CleanupExpiredNonces_KeepsRecentNonces()
    {
        // Arrange
        using var cache = new NonceCache(nonceTtl: TimeSpan.FromSeconds(5));

        var nonce = Guid.NewGuid().ToString();
        cache.TryUseNonce(nonce);

        // Act - cleanup immediately (nonce not expired yet)
        await Task.Delay(10);
        cache.CleanupExpiredNonces(null);

        // Assert - recent nonce should still be there
        var stats = cache.GetStatistics();
        Assert.Equal(1, stats.TotalNonces);
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        using var cache = new NonceCache();

        // Act - add 3 nonces
        cache.TryUseNonce("nonce1");
        cache.TryUseNonce("nonce2");
        cache.TryUseNonce("nonce3");
        cache.TryUseNonce("nonce1"); // duplicate (should be rejected)

        var stats = cache.GetStatistics();

        // Assert
        Assert.Equal(3, stats.TotalNonces);
        Assert.Equal(4, stats.TotalNoncesProcessed); // 3 accepted + 1 rejected
        Assert.Equal(1, stats.TotalReplayAttemptsBlocked);
    }

    [Fact]
    public void GetStatistics_TracksReplayAttempts()
    {
        // Arrange
        using var cache = new NonceCache();
        var nonce = Guid.NewGuid().ToString();

        // Act - use same nonce multiple times
        cache.TryUseNonce(nonce);
        cache.TryUseNonce(nonce); // replay
        cache.TryUseNonce(nonce); // replay

        var stats = cache.GetStatistics();

        // Assert
        Assert.Equal(2, stats.TotalReplayAttemptsBlocked);
        Assert.Equal(3, stats.TotalNoncesProcessed);
    }

    [Fact]
    public void GetStatistics_CalculatesReplayRate()
    {
        // Arrange
        using var cache = new NonceCache();

        // Act - 2 unique, 2 duplicates
        cache.TryUseNonce("nonce1");
        cache.TryUseNonce("nonce2");
        cache.TryUseNonce("nonce1"); // replay
        cache.TryUseNonce("nonce2"); // replay

        var stats = cache.GetStatistics();

        // Assert
        Assert.Equal(0.5, stats.ReplayRate); // 2 replays out of 4 total = 50%
    }

    [Fact]
    public void GetStatistics_ShowsCacheUtilization()
    {
        // Arrange
        using var cache = new NonceCache(maxCacheSize: 100);

        // Act - add 25 nonces
        for (int i = 0; i < 25; i++)
        {
            cache.TryUseNonce($"nonce{i}");
        }

        var stats = cache.GetStatistics();

        // Assert
        Assert.Equal(0.25, stats.CacheUtilization); // 25/100 = 25%
    }

    [Fact]
    public void TryUseNonce_WhenCacheFull_ReturnsFalse()
    {
        // Arrange - very small cache for testing
        using var cache = new NonceCache(maxCacheSize: 5, nonceTtl: TimeSpan.FromHours(1));

        // Fill cache
        for (int i = 0; i < 5; i++)
        {
            cache.TryUseNonce($"nonce{i}");
        }

        // Act & Assert - 6th nonce is rejected (cache full, nothing to cleanup), no exception
        Assert.False(cache.TryUseNonce("nonce-overflow"));
    }

    [Fact]
    public async Task TryUseNonce_ConcurrentAccess_ThreadSafe()
    {
        // Arrange
        using var cache = new NonceCache();
        var nonce = Guid.NewGuid().ToString();
        var successCount = 0;

        // Act - try to use same nonce from 10 concurrent threads
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            if (cache.TryUseNonce(nonce))
            {
                Interlocked.Increment(ref successCount);
            }
        }));

        await Task.WhenAll(tasks);

        // Assert - only ONE thread should succeed (nonce used only once)
        Assert.Equal(1, successCount);
    }

    [Fact]
    public async Task TryUseNonce_ConcurrentDifferentNonces_AllSucceed()
    {
        // Arrange
        using var cache = new NonceCache();
        var successCount = 0;

        // Act - 100 concurrent requests with unique nonces
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            var nonce = $"nonce-{i}";
            if (cache.TryUseNonce(nonce))
            {
                Interlocked.Increment(ref successCount);
            }
        }));

        await Task.WhenAll(tasks);

        // Assert - all should succeed (all nonces are unique)
        Assert.Equal(100, successCount);
    }

    [Fact]
    public void Clear_RemovesAllNonces()
    {
        // Arrange
        using var cache = new NonceCache();

        cache.TryUseNonce("nonce1");
        cache.TryUseNonce("nonce2");
        cache.TryUseNonce("nonce3");

        // Act
        cache.Clear();

        // Assert
        var stats = cache.GetStatistics();
        Assert.Equal(0, stats.TotalNonces);

        // Previously used nonce should be accepted again after clear
        Assert.True(cache.TryUseNonce("nonce1"));
    }

    [Fact]
    public void Dispose_StopsCleanupTimer()
    {
        // Arrange
        var cache = new NonceCache(
            nonceTtl: TimeSpan.FromMilliseconds(10),
            cleanupInterval: TimeSpan.FromMilliseconds(10));

        cache.TryUseNonce("nonce1");

        // Act
        cache.Dispose();

        // Assert - after disposal, should throw on access
        Assert.Throws<ObjectDisposedException>(() => cache.TryUseNonce("nonce2"));
    }

    [Fact]
    public async Task AutomaticCleanup_RunsPeriodically()
    {
        // Arrange - short TTL and cleanup interval for testing
        using var cache = new NonceCache(
            nonceTtl: TimeSpan.FromMilliseconds(50),
            cleanupInterval: TimeSpan.FromMilliseconds(30));

        // Add nonce
        cache.TryUseNonce("old-nonce");

        var statsBefore = cache.GetStatistics();
        Assert.Equal(0, statsBefore.TotalCleanupRuns);

        // Act - wait for automatic cleanup to run
        await Task.Delay(200);

        // Assert - cleanup should have run at least once
        var statsAfter = cache.GetStatistics();
        Assert.True(statsAfter.TotalCleanupRuns > 0, "Automatic cleanup should have run");
        Assert.Equal(0, statsAfter.TotalNonces); // Old nonce should be removed
    }
}
