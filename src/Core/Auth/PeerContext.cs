namespace DeskShare.Core.Auth;

/// <summary>
/// The end-to-end verification state of ONE peer connection (see <see cref="E2EBinding"/>).
/// </summary>
/// <remarks>
/// Why an object per peer connection and not a flag on the long-lived session: verifying a MAC takes time, and a new
/// peer connection can replace the old one meanwhile (a malicious signaling server can race one in). If the state were
/// shared, the late "valid" result of connection A could mark connection B as verified. With one object per connection
/// a late result can only ever reach A's own object, and A's object is <see cref="Retire">retired</see> the moment it is
/// replaced, so it can no longer become verified at all.
/// </remarks>
public sealed class PeerContext
{
    private readonly object _lock = new();
    private volatile bool _verified;
    private volatile bool _dead;

    /// <summary>The peer connection this context belongs to (null in unit tests).</summary>
    public SIPSorcery.Net.RTCPeerConnection? Connection { get; }

    public PeerContext(SIPSorcery.Net.RTCPeerConnection? connection = null) => Connection = connection;

    /// <summary>True once verified and while the connection has not been replaced.</summary>
    public bool IsVerified => _verified && !_dead;

    /// <summary>True after the connection was replaced; such a context never becomes verified.</summary>
    public bool IsRetired => _dead;

    /// <summary>
    /// Marks the connection verified, atomically with the "still the current one" check.
    /// Returns false when it was retired meanwhile (the caller must then do nothing for this peer).
    /// </summary>
    public bool TryVerify()
    {
        lock (_lock)
        {
            if (_dead) return false;
            _verified = true;
            return true;
        }
    }

    /// <summary>The connection was replaced: it can no longer be verified.</summary>
    public void Retire()
    {
        lock (_lock)
        {
            _dead = true;
            _verified = false;
        }
    }

    /// <summary>Back to unverified on the same connection (for example a new data channel on it).</summary>
    public void Revoke()
    {
        lock (_lock) { _verified = false; }
    }
}
