using SIPSorcery.Net;

namespace DeskShare.Core.Auth;

/// <summary>
/// The end-to-end verification state of ONE peer connection (see <see cref="E2EBinding"/>), together with that
/// connection's own transport (<see cref="Connection"/>, <see cref="Channel"/>).
/// </summary>
/// <remarks>
/// Why an object per peer connection and not a flag on the long-lived session: verifying a MAC takes time, and a new
/// peer connection can replace the old one meanwhile (a malicious signaling server can race one in). If the state were
/// shared, the late "valid" result of connection A could mark connection B as verified. With one object per connection
/// a late result can only ever reach A's own object, and A's object is <see cref="Retire">retired</see> the moment it is
/// replaced, so it can no longer become verified at all.
///
/// The rule for all code: read the current context ONCE into a local, then check and use only that context and ITS
/// transport. Never check a context and then send through "the current channel" (it may have changed in between).
/// <see cref="TrySend"/> does the check and the send under the same lock that <see cref="Retire"/> needs, so a send
/// cannot interleave with the replacement.
/// </remarks>
public sealed class PeerContext
{
    private readonly object _lock = new();
    private volatile bool _verified;
    private volatile bool _dead;

    /// <summary>The peer connection this context belongs to (null in unit tests).</summary>
    public RTCPeerConnection? Connection { get; }

    /// <summary>The data channel of this connection (set when it is attached).</summary>
    public volatile RTCDataChannel? Channel;

    /// <summary>True while the viewer of this connection asked for the host's clipboard.</summary>
    public volatile bool ClipboardSubscribed;

    /// <summary>
    /// True while the viewer of this connection says it records the session ("recording_state"). Purely informational:
    /// a viewer decodes the video itself, so the host can neither detect nor prevent recording. It only lets an honest
    /// viewer tell the person at the computer. Per peer so a replaced connection can never leave the flag behind.
    /// </summary>
    public volatile bool Recording;

    /// <summary>Test seam: treat the channel as open (a real RTCDataChannel cannot be opened without a network).</summary>
    internal bool ChannelOpenForTests;

    public PeerContext(RTCPeerConnection? connection = null) => Connection = connection;

    /// <summary>Held while a message of this peer is being processed and while something is sent to it.</summary>
    public object SyncRoot => _lock;

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
            _sessionActive = true;
            return true;
        }
    }

    private bool _sessionActive;
    private int _epoch;

    /// <summary>
    /// Counts how many viewer sessions of this connection ended (<see cref="EndSession"/>). A control grant that is still being
    /// made remembers the value it started with and is rolled back if it changed: the viewer it was for is gone.
    /// </summary>
    public int Epoch => Volatile.Read(ref _epoch);

    /// <summary>
    /// Ends the viewer session of this connection, whatever its verification state is now (a retired connection is already
    /// unverified, so "was it verified?" cannot tell). Returns true only the first time after a session started.
    /// </summary>
    public bool EndSession()
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _epoch);
            var was = _sessionActive;
            _sessionActive = false;
            return was;
        }
    }

    private volatile bool _admissionStarted;

    /// <summary>True once the host was asked whether this viewer may connect (unattended confirmation); it is asked once per connection.</summary>
    public bool AdmissionStarted => _admissionStarted;

    /// <summary>Marks the admission question as started. Returns false when it already was, or the connection was retired.</summary>
    public bool TryBeginAdmission()
    {
        lock (_lock)
        {
            if (_dead || _admissionStarted) return false;
            _admissionStarted = true;
            return true;
        }
    }

    /// <summary>
    /// Runs <paramref name="send"/> on THIS connection's data channel, but only if the context is alive, (optionally)
    /// verified and the channel is open. Check and send happen under the lock <see cref="Retire"/> needs.
    /// </summary>
    public bool TrySend(Action<RTCDataChannel> send, bool requireVerified)
    {
        lock (_lock)
        {
            if (_dead || (requireVerified && !_verified)) return false;
            var channel = Channel;
            if (channel == null || (!ChannelOpenForTests && channel.readyState != RTCDataChannelState.open)) return false;
            send(channel);
            return true;
        }
    }

    /// <summary>The connection was replaced: it can no longer be verified, and nothing more is sent to it or taken from it.</summary>
    public void Retire()
    {
        lock (_lock)
        {
            _dead = true;
            _verified = false;
            ClipboardSubscribed = false;
            Recording = false;
        }
    }

    /// <summary>Back to unverified on the same connection (for example a new data channel on it).</summary>
    public void Revoke()
    {
        lock (_lock)
        {
            _verified = false;
            ClipboardSubscribed = false;
            Recording = false;
        }
    }
}
