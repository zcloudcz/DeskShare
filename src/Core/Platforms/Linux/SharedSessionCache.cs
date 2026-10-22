namespace DeskShare.Core.Platforms.Linux;

/// <summary>
/// Reference-counted holder for the one portal session that the capturer and the input controller share.
/// </summary>
/// <remarks>
/// Two rules beyond plain reference counting:
/// <list type="bullet">
/// <item>A session is kept for <c>keepAfterOpen</c> after it was opened even when the count drops to zero. The
/// sender probes the screen size with a throw-away capturer and then creates the real one; without this the
/// portal would ask the user twice on backends that cannot restore a session from a token.</item>
/// <item><see cref="Invalidate"/> drops a session the portal closed (the user pressed "Stop sharing"), so the next
/// <see cref="Acquire"/> opens a fresh one instead of handing out a dead session. Late releases of the dropped
/// session are ignored.</item>
/// </list>
/// </remarks>
internal sealed class SharedSessionCache<T> where T : class, IDisposable
{
    private readonly TimeSpan _keepAfterOpen;
    private readonly object _lock = new();
    private T? _current;        // guarded by _lock
    private int _refCount;      // guarded by _lock
    private DateTime _openedAt; // guarded by _lock
    private Timer? _closeTimer; // guarded by _lock

    public SharedSessionCache(TimeSpan keepAfterOpen) => _keepAfterOpen = keepAfterOpen;

    /// <summary>The live session, or null; for tests and diagnostics.</summary>
    internal T? Current { get { lock (_lock) return _current; } }

    /// <summary>
    /// Returns the shared session, calling <paramref name="open"/> when there is none. Pair every successful
    /// call with <see cref="Release"/>. Opening runs under the lock on purpose: a second caller waits for the
    /// consent dialog of the first instead of opening a second dialog.
    /// </summary>
    public T Acquire(Func<T> open)
    {
        lock (_lock)
        {
            _closeTimer?.Dispose();
            _closeTimer = null;
            if (_current == null)
            {
                _current = open();
                _openedAt = DateTime.UtcNow;
            }
            _refCount++;
            return _current;
        }
    }

    /// <summary>Drops one reference to <paramref name="session"/>; the last one closes it (after the keep time).</summary>
    public void Release(T session)
    {
        T? toClose = null;
        lock (_lock)
        {
            if (!ReferenceEquals(session, _current) || _refCount == 0)
                return; // already invalidated (and disposed) or over-released
            if (--_refCount > 0)
                return;

            var keep = _openedAt + _keepAfterOpen - DateTime.UtcNow;
            if (keep <= TimeSpan.Zero)
            {
                toClose = _current;
                _current = null;
            }
            else
            {
                _closeTimer = new Timer(_ => CloseIfUnused(session), null, keep, Timeout.InfiniteTimeSpan);
            }
        }
        toClose?.Dispose();
    }

    /// <summary>Forgets <paramref name="session"/> (closed by the portal) and disposes it.</summary>
    public void Invalidate(T session)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(session, _current))
                return;
            _current = null;
            _refCount = 0;
            _closeTimer?.Dispose();
            _closeTimer = null;
        }
        session.Dispose();
    }

    private void CloseIfUnused(T session)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(session, _current) || _refCount > 0)
                return; // re-acquired in the meantime
            _current = null;
            _closeTimer?.Dispose();
            _closeTimer = null;
        }
        session.Dispose();
    }
}
