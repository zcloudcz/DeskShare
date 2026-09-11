using System.Collections.Concurrent;

namespace DeskShare.Core.Auth;

/// <summary>
/// Thread-safe cache for tracking used nonces.
/// Prevents replay attacks by ensuring each nonce is used only once.
/// Automatically cleans up expired nonces to prevent memory leaks.
/// </summary>
/// <remarks>
/// Purpose:
/// Even with HMAC signatures, an attacker could capture a valid signed request
/// and replay it within the signature validity window. The nonce cache prevents
/// this by tracking which nonces have been used.
///
/// How it works:
/// 1. Client generates unique nonce (Guid.NewGuid())
/// 2. Server checks if nonce has been seen before
/// 3. If not seen: accept request and store nonce
/// 4. If seen: reject request (replay attack)
/// 5. Automatic cleanup removes expired nonces periodically
///
/// Thread safety:
/// Uses ConcurrentDictionary for lock-free thread-safe operations.
/// Safe to use from multiple threads simultaneously.
/// </remarks>
public sealed class NonceCache : IDisposable
{
    private readonly ConcurrentDictionary<string, DateTime> _usedNonces;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _nonceTtl;
    private readonly int _maxCacheSize;
    private bool _disposed;

    // Statistics for monitoring
    private long _totalNoncesProcessed;
    private long _totalReplayAttemptsBlocked;
    private long _totalCleanupRuns;

    /// <summary>
    /// Initializes nonce cache with automatic cleanup.
    /// </summary>
    /// <param name="nonceTtl">How long to keep nonces before expiring (default: 60 seconds)</param>
    /// <param name="cleanupInterval">How often to run cleanup (default: 30 seconds)</param>
    /// <param name="maxCacheSize">Maximum number of nonces to store (default: 10000)</param>
    /// <remarks>
    /// Default settings:
    /// - TTL: 60 seconds (matches RequestSigningService.SignatureValiditySeconds)
    /// - Cleanup: Every 30 seconds
    /// - Max size: 10,000 nonces (~1 MB memory)
    ///
    /// Memory usage estimation:
    /// - Each nonce: ~100 bytes (GUID string + DateTime + overhead)
    /// - 10,000 nonces: ~1 MB
    /// - Should handle 100+ requests/second sustained load
    /// </remarks>
    public NonceCache(
        TimeSpan? nonceTtl = null,
        TimeSpan? cleanupInterval = null,
        int maxCacheSize = 10000)
    {
        _usedNonces = new ConcurrentDictionary<string, DateTime>();
        _nonceTtl = nonceTtl ?? TimeSpan.FromSeconds(RequestSigningService.SignatureValiditySeconds);
        _maxCacheSize = maxCacheSize;

        // Setup periodic cleanup timer
        var interval = cleanupInterval ?? TimeSpan.FromSeconds(30);
        _cleanupTimer = new Timer(
            CleanupExpiredNonces,
            state: null,
            dueTime: interval,
            period: interval);
    }

    /// <summary>
    /// Checks if nonce has been used and marks it as used if not.
    /// This is an atomic operation - thread safe.
    /// </summary>
    /// <param name="nonce">Nonce to check and register</param>
    /// <returns>True if nonce is fresh (not previously used), false if replay detected</returns>
    /// <exception cref="ArgumentNullException">If nonce is null or empty</exception>
    /// <exception cref="InvalidOperationException">If cache is full (DoS protection)</exception>
    /// <remarks>
    /// This method performs two operations atomically:
    /// 1. Check if nonce exists
    /// 2. If not, add it to cache
    ///
    /// Returns:
    /// - True: Nonce is fresh, request is valid
    /// - False: Nonce already used, replay attack detected
    ///
    /// Example:
    /// <code>
    /// if (nonceCache.TryUseNonce(request.Nonce))
    /// {
    ///     // Process request
    /// }
    /// else
    /// {
    ///     // Reject - replay attack
    /// }
    /// </code>
    /// </remarks>
    public bool TryUseNonce(string nonce)
    {
        if (string.IsNullOrEmpty(nonce))
            throw new ArgumentNullException(nameof(nonce));

        if (_disposed)
            throw new ObjectDisposedException(nameof(NonceCache));

        // Check cache size limit (DoS protection)
        if (_usedNonces.Count >= _maxCacheSize)
        {
            // Force immediate cleanup before rejecting
            CleanupExpiredNonces(null);

            // Still full after cleanup: reject this request instead of throwing.
            // An exception here would propagate into the auth path and let a flood
            // of bogus nonces knock out authentication for everyone.
            if (_usedNonces.Count >= _maxCacheSize)
            {
                Interlocked.Increment(ref _totalNoncesProcessed);
                return false;
            }
        }

        // Try to add nonce to dictionary
        // TryAdd is atomic - returns false if key already exists
        var now = DateTime.UtcNow;
        bool added = _usedNonces.TryAdd(nonce, now);

        Interlocked.Increment(ref _totalNoncesProcessed);

        if (!added)
        {
            // Nonce already exists - replay attack detected!
            Interlocked.Increment(ref _totalReplayAttemptsBlocked);
        }

        return added;
    }

    /// <summary>
    /// Checks if a nonce has been used without marking it as used.
    /// Useful for testing or diagnostics.
    /// </summary>
    /// <param name="nonce">Nonce to check</param>
    /// <returns>True if nonce has been used, false otherwise</returns>
    public bool IsNonceUsed(string nonce)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;

        return _usedNonces.ContainsKey(nonce);
    }

    /// <summary>
    /// Removes expired nonces from cache.
    /// Called automatically by timer, but can be called manually for testing.
    /// </summary>
    /// <param name="state">Timer state (unused)</param>
    /// <remarks>
    /// Cleanup algorithm:
    /// 1. Iterate through all nonces
    /// 2. Check if older than TTL
    /// 3. Remove expired nonces
    /// 4. Track statistics
    ///
    /// Performance:
    /// - O(n) where n = number of nonces in cache
    /// - Typical: ~100-1000 nonces, takes &lt; 1ms
    /// - Worst case: 10,000 nonces, takes ~10ms
    /// </remarks>
    public void CleanupExpiredNonces(object? state)
    {
        if (_disposed)
            return;

        try
        {
            var now = DateTime.UtcNow;
            var expiredNonces = new List<string>();

            // Find expired nonces
            foreach (var kvp in _usedNonces)
            {
                var age = now - kvp.Value;
                if (age > _nonceTtl)
                {
                    expiredNonces.Add(kvp.Key);
                }
            }

            // Remove expired nonces
            int removedCount = 0;
            foreach (var nonce in expiredNonces)
            {
                if (_usedNonces.TryRemove(nonce, out _))
                {
                    removedCount++;
                }
            }

            Interlocked.Increment(ref _totalCleanupRuns);

            // Log cleanup stats (caller can access via GetStatistics())
            if (removedCount > 0)
            {
                // Could log here, but we don't have ILogger dependency in Core
                // Caller can check statistics and log if needed
            }
        }
        catch
        {
            // Swallow exceptions in cleanup to prevent timer from stopping
            // In production, consider logging this
        }
    }

    /// <summary>
    /// Gets current cache statistics for monitoring and diagnostics.
    /// </summary>
    /// <returns>Statistics snapshot</returns>
    /// <remarks>
    /// Use this for:
    /// - Monitoring cache health
    /// - Detecting DoS attacks (high replay count)
    /// - Capacity planning (cache size trends)
    /// - Performance tuning (cleanup effectiveness)
    /// </remarks>
    public NonceCacheStatistics GetStatistics()
    {
        var nonces = _usedNonces.ToArray();

        return new NonceCacheStatistics
        {
            TotalNonces = nonces.Length,
            MaxCacheSize = _maxCacheSize,
            NonceTtl = _nonceTtl,
            TotalNoncesProcessed = Interlocked.Read(ref _totalNoncesProcessed),
            TotalReplayAttemptsBlocked = Interlocked.Read(ref _totalReplayAttemptsBlocked),
            TotalCleanupRuns = Interlocked.Read(ref _totalCleanupRuns),
            OldestNonce = nonces.Length > 0 ? nonces.Min(n => n.Value) : (DateTime?)null,
            NewestNonce = nonces.Length > 0 ? nonces.Max(n => n.Value) : (DateTime?)null,
            CacheUtilization = nonces.Length / (double)_maxCacheSize
        };
    }

    /// <summary>
    /// Clears all nonces from cache.
    /// Use with caution - only for testing or emergency situations.
    /// </summary>
    public void Clear()
    {
        _usedNonces.Clear();
    }

    /// <summary>
    /// Disposes the nonce cache and stops cleanup timer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Stop and dispose timer
        _cleanupTimer?.Dispose();

        // Clear cache
        _usedNonces.Clear();
    }
}

/// <summary>
/// Statistics about nonce cache health and performance.
/// </summary>
public sealed class NonceCacheStatistics
{
    /// <summary>
    /// Current number of nonces in cache.
    /// </summary>
    public int TotalNonces { get; set; }

    /// <summary>
    /// Maximum number of nonces allowed in cache.
    /// </summary>
    public int MaxCacheSize { get; set; }

    /// <summary>
    /// Time-to-live for nonces.
    /// </summary>
    public TimeSpan NonceTtl { get; set; }

    /// <summary>
    /// Total nonces processed since cache creation (lifetime counter).
    /// </summary>
    public long TotalNoncesProcessed { get; set; }

    /// <summary>
    /// Total replay attempts blocked (lifetime counter).
    /// High value indicates potential attack.
    /// </summary>
    public long TotalReplayAttemptsBlocked { get; set; }

    /// <summary>
    /// Total cleanup runs executed (lifetime counter).
    /// </summary>
    public long TotalCleanupRuns { get; set; }

    /// <summary>
    /// Timestamp of oldest nonce in cache (null if empty).
    /// </summary>
    public DateTime? OldestNonce { get; set; }

    /// <summary>
    /// Timestamp of newest nonce in cache (null if empty).
    /// </summary>
    public DateTime? NewestNonce { get; set; }

    /// <summary>
    /// Cache utilization as percentage (0.0 to 1.0).
    /// Values approaching 1.0 indicate cache may need larger size.
    /// </summary>
    public double CacheUtilization { get; set; }

    /// <summary>
    /// Replay attack rate (percentage of requests that are replays).
    /// </summary>
    public double ReplayRate => TotalNoncesProcessed > 0
        ? TotalReplayAttemptsBlocked / (double)TotalNoncesProcessed
        : 0.0;
}
