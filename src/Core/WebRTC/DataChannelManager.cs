using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskShare.Core.Auth;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Models;
using DeskShare.Core.Safety;
using Serilog;
using SIPSorcery.Net;

namespace DeskShare.Core.WebRTC;

/// <summary>
/// Manages WebRTC DataChannel for bi-directional communication.
/// Handles incoming input messages and provides statistics.
/// </summary>
/// <remarks>
/// DataChannel provides a reliable, ordered data transport mechanism over WebRTC
/// alongside the video stream. This is perfect for low-latency control messages
/// like mouse and keyboard inputs.
///
/// For junior developers:
/// - DataChannel is like WebSocket but runs over the same WebRTC connection as video
/// - It supports both reliable (TCP-like) and unreliable (UDP-like) delivery
/// - We use reliable mode for input messages to ensure no commands are lost
/// </remarks>
public sealed class DataChannelManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly IInputController? _inputController;

    // The browser sends enum names ("MouseMove", "Left"), the desktop viewer sends numbers.
    // JsonStringEnumConverter accepts both; without it every browser input failed to deserialize.
    private static readonly JsonSerializerOptions InputJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private RTCDataChannel? _dataChannel;
    private OnDataChannelMessageDelegate? _messageHandler;
    private bool _disposed;

    // Rejections logged per message type; after the cap further ones are only counted, so a misbehaving
    // viewer cannot flood the log.
    private const int MaxRejectionLogsPerType = 5;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _rejectionCounts = new();

    // Statistics tracking
    private long _messagesReceived;
    private long _messagesSent;
    private long _bytesReceived;
    private long _bytesSent;
    private DateTime _channelOpenedAt;

    /// <summary>
    /// Capabilities the host grants on top of the always-granted <see cref="SessionCapabilities.View"/>
    /// (for example <see cref="SessionCapabilities.Clipboard"/>). <see cref="SessionCapabilities.Control"/>
    /// is not set here: it follows the input controller's authorization (see <see cref="GrantedCapabilities"/>).
    /// </summary>
    public SessionCapabilities HostGrantedCapabilities { get; set; } = SessionCapabilities.None;

    /// <summary>
    /// Capabilities of the current session. Control is derived from the input controller's
    /// <c>IsEnabled</c> (the "Allow remote control" decision) so there is only one source of truth.
    /// </summary>
    public SessionCapabilities GrantedCapabilities => GrantedFor(_peer);

    // Grants of ONE peer connection. Messages are always judged with the context of the channel they arrived on,
    // never with whatever connection happens to be current when the judging code runs.
    private SessionCapabilities GrantedFor(PeerContext peer) =>
        // "Unverified": until the viewer proved it holds the e2e key, nothing is granted. Only e2e_bind passes
        // (it needs no grant, see DataChannelPolicy), so a man in the middle gets neither input nor clipboard nor files.
        // A retired peer is never verified, so it gets nothing either.
        RequireE2EBinding && !peer.IsVerified
            ? SessionCapabilities.None
            : SessionCapabilities.View
              | HostGrantedCapabilities
              | (_inputController?.IsEnabled == true && OwnsControl(peer) ? SessionCapabilities.Control : SessionCapabilities.None);

    // With ControlApproval the input controller's authorization is global, but it was approved for ONE viewer. Everyone else
    // is denied Control even while the controller is authorized (a stale or late grant can never serve another viewer).
    private PeerContext? _controlPeer;
    private readonly object _controlLock = new();

    private bool OwnsControl(PeerContext peer)
    {
        if (ControlApproval == null) return true; // headless: the host's checkbox decided at start
        lock (_controlLock) return ReferenceEquals(_controlPeer, peer);
    }

    /// <summary>
    /// Checks the viewer's <c>e2e_bind</c> mac and returns the sender's own mac for the reply, or null when the
    /// viewer's mac is wrong. Setting it switches the "unverified until bound" rule on (see <see cref="GrantedCapabilities"/>).
    /// </summary>
    public Func<PeerContext, string, string?>? VerifyViewerBind { get; set; }

    /// <summary>True when peers must pass the e2e binding before they get any capability.</summary>
    public bool RequireE2EBinding => VerifyViewerBind != null;

    // Verification state of the CURRENT peer connection. It is a separate object per connection (see PeerContext), so a
    // check that finishes late for a replaced connection can only touch that connection's own, retired object.
    private volatile PeerContext _peer = new();

    /// <summary>True once the viewer of the current peer connection passed the e2e binding.</summary>
    public bool PeerVerified => _peer.IsVerified;

    /// <summary>A new peer connection took over: the previous context is retired and its channel dropped.</summary>
    public void BeginPeer(PeerContext peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var previous = _peer;
        // In production the video source retires the old context BEFORE it calls us, so "was it verified" is already false
        // here. The session end is therefore tracked on the context itself (PeerContext.EndSession), not derived from IsVerified.
        previous.Retire(); // first: waits for a message handler of the old peer that is still running
        _peer = peer;
        DetachDataChannel();
        EndViewerSession(previous);
    }

    /// <summary>
    /// Unattended agent: asked once per connection, after the viewer's e2e binding verified and BEFORE it gets video or any
    /// capability. True admits it; false (or the <see cref="AdmissionTimeout"/>) refuses it. Null admits everyone.
    /// The token is cancelled when the viewer goes away, so the question on screen can close.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? AdmitViewer { get; set; }

    /// <summary>How long the person at the computer has to answer <see cref="AdmitViewer"/>.</summary>
    public TimeSpan AdmissionTimeout { get; set; } = ControlApprovalGate.DefaultTimeout;

    /// <summary>
    /// Attended app: control needs the host's explicit approval for every session. Set means "viewers may ask"; the control
    /// authorization is then NOT given when sharing starts but only after the person clicked Allow, and it ends with the session.
    /// </summary>
    public ControlApprovalGate? ControlApproval { get; set; }

    /// <summary>Raised when the host refused (or did not answer) the admission question; the session closes the connection.</summary>
    public event EventHandler<PeerContext>? ViewerAdmissionDenied;

    /// <summary>Raised when a verified viewer is gone (its channel closed or another connection replaced it).</summary>
    public event EventHandler<PeerContext>? ViewerEnded;

    /// <summary>
    /// Raised (outside any lock) when the viewer's "recording_state" changed. Read <see cref="PeerContext.Recording"/> for the
    /// current value rather than trusting event order: the effects of two quick messages may run in either order.
    /// </summary>
    public event EventHandler<PeerContext>? ViewerRecordingChanged;

    /// <summary>Raised with the size in bytes of every clipboard message sent to the viewer.</summary>
    public event Action<int>? ClipboardSent;

    private CancellationTokenSource? _admissionCts;

    private void CancelPendingQuestions()
    {
        ControlApproval?.CancelPending();
        try { Interlocked.Exchange(ref _admissionCts, null)?.Cancel(); }
        catch (ObjectDisposedException) { /* the question just finished */ }
    }

    private void EndViewerSession(PeerContext peer)
    {
        var wasActive = peer.EndSession(); // also invalidates a control grant that is still in flight for this peer
        CancelPendingQuestions();
        if (ControlApproval != null)
        {
            // Control approved for this viewer ends with its session: the next one has to be approved again. Only when nobody
            // else owns it now (a late end of an old peer must not take control away from the newer, approved viewer).
            bool revoke;
            lock (_controlLock)
            {
                if (ReferenceEquals(_controlPeer, peer)) _controlPeer = null;
                revoke = _controlPeer == null;
            }
            if (revoke && _inputController?.IsEnabled == true)
                _inputController.RevokeAuthorization();
        }
        if (wasActive)
            ViewerEnded?.Invoke(this, peer);
    }

    /// <summary>Raised with the verified peer's context when the viewer's e2e binding verified.</summary>
    public event EventHandler<PeerContext>? ViewerVerified;

    /// <summary>Raised with the peer's context when it sent a binding that does not verify (wrong key, or a man in the middle).</summary>
    public event EventHandler<PeerContext>? ViewerBindFailed;

    /// <summary>
    /// True when the viewer on the current channel asked for the host's clipboard ("clipboard_subscribe").
    /// The grant alone is not enough: a viewer that never opted in must not receive clipboard text.
    /// Reset whenever the channel is attached, detached or closed.
    /// </summary>
    public bool ClipboardSubscribed => _peer.ClipboardSubscribed;

    /// <summary>
    /// Receives the viewer's files; set by the session when the host allows file receiving. Its transfer is
    /// aborted whenever the channel closes or is replaced, so no half-written file survives a disconnect.
    /// </summary>
    public FileReceiver? FileReceiver { get; set; }

    /// <summary>
    /// Display list and switching; set by the host when it knows its displays. One instance for all peers, so the
    /// rate limit is shared. Without it "switch_display" is ignored and no "displays" message is sent.
    /// </summary>
    public Displays.DisplaySwitchService? DisplaySwitcher { get; set; }

    private PrivacyScreenController? _privacyScreen;

    /// <summary>
    /// Privacy screen of this host; set by the session when the host allows it. It is switched off whenever the
    /// channel closes or is replaced, and its state changes are reported to the viewer ("privacy_screen_state").
    /// </summary>
    public PrivacyScreenController? PrivacyScreen
    {
        get => _privacyScreen;
        set
        {
            if (_privacyScreen != null)
                _privacyScreen.StateChanged -= SendPrivacyState;
            _privacyScreen = value;
            if (value != null)
                value.StateChanged += SendPrivacyState;
        }
    }

    private void SendPrivacyState(bool enabled, string? reason)
    {
        if (IsOpen) // automatic off often happens because the channel is gone: nobody to tell then
            SendJson(new { type = "privacy_screen_state", enabled, reason });
    }

    /// <summary>
    /// Sends a clipboard message to the viewer, only while it is subscribed and the channel is open.
    /// </summary>
    public bool TrySendClipboard(string json)
    {
        var peer = _peer; // one read: subscription check and send concern the same peer connection
        var sent = peer.ClipboardSubscribed && SendOn(peer, json);
        if (sent)
            ClipboardSent?.Invoke(Encoding.UTF8.GetByteCount(json));
        return sent;
    }

    /// <summary>
    /// Event raised when a message is received on the data channel.
    /// </summary>
    public event EventHandler<string>? MessageReceived;

    /// <summary>
    /// Event raised when a clipboard message is received.
    /// </summary>
    public event EventHandler<ClipboardMessage>? ClipboardMessageReceived;

    /// <summary>
    /// Event raised when the data channel opens.
    /// </summary>
    public event EventHandler? ChannelOpened;

    /// <summary>
    /// Event raised when the data channel closes.
    /// </summary>
    public event EventHandler? ChannelClosed;

    /// <summary>
    /// Gets whether the data channel is currently open and ready.
    /// </summary>
    public bool IsOpen => _dataChannel?.readyState == RTCDataChannelState.open;

    /// <summary>
    /// Gets the label (name) of the data channel.
    /// </summary>
    public string? Label => _dataChannel?.label;

    /// <summary>
    /// Initializes a new instance of the DataChannelManager class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <param name="inputController">Optional input controller for processing remote input.</param>
    public DataChannelManager(ILogger logger, IInputController? inputController = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inputController = inputController;
        if (_inputController != null)
            _inputController.AuthorizationStateChanged += OnAuthorizationStateChanged;
        _logger.Information("DataChannelManager initialized");
    }

    /// <summary>
    /// Attaches to an existing data channel created by the peer connection.
    /// This is typically called when the remote peer creates the data channel.
    /// </summary>
    /// <param name="dataChannel">The data channel to attach to.</param>
    public void AttachDataChannel(RTCDataChannel dataChannel)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);

        if (_dataChannel != null)
        {
            _logger.Warning("DataChannel already attached, replacing with new channel");
            DetachDataChannel();
        }

        // The channel belongs to the peer connection that is current NOW; its messages are always judged with that
        // context (captured in the handler below), even if another connection takes over later.
        var peer = _peer;
        if (peer.IsRetired)
        {
            dataChannel.close();
            return;
        }

        _dataChannel = dataChannel;
        _channelOpenedAt = DateTime.UtcNow;
        peer.Channel = dataChannel;
        peer.Revoke(); // a new channel is a new viewer session: it must subscribe again and prove the e2e binding again
        EndViewerSession(peer); // ...and the control approved for the previous channel's viewer does not carry over
        FileReceiver?.Abort();
        _privacyScreen?.Disable("channel_replaced");

        // Subscribe to data channel events
        _dataChannel.onopen += OnDataChannelOpen;
        _dataChannel.onclose += OnDataChannelClose;
        _dataChannel.onerror += OnDataChannelError;
        _messageHandler = (channel, protocol, data) => OnDataChannelMessage(peer, channel, protocol, data);
        _dataChannel.onmessage += _messageHandler;

        _logger.Information("DataChannel attached: Label={Label}, ID={Id}, State={State}",
            dataChannel.label, dataChannel.id, dataChannel.readyState);
    }

    /// <summary>
    /// Creates a new data channel on the peer connection.
    /// This is typically called by the offerer (screen sender) to establish the channel.
    /// </summary>
    /// <param name="peerConnection">The peer connection to create the channel on.</param>
    /// <param name="label">Label (name) for the data channel.</param>
    /// <param name="options">Optional configuration options.</param>
    /// <returns>The created data channel.</returns>
    public async Task<RTCDataChannel> CreateDataChannelAsync(
        RTCPeerConnection peerConnection,
        string label = "input",
        RTCDataChannelInit? options = null)
    {
        ArgumentNullException.ThrowIfNull(peerConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        // Default options for reliable, ordered delivery
        options ??= new RTCDataChannelInit
        {
            ordered = true,  // Messages delivered in order
            // maxRetransmits and maxPacketLifeTime are null for reliable mode
        };

        var dataChannel = await peerConnection.createDataChannel(label, options);
        AttachDataChannel(dataChannel);

        _logger.Information("DataChannel created: Label={Label}, Ordered={Ordered}",
            label, options.ordered);

        return dataChannel;
    }

    /// <summary>
    /// Sends a text message over the data channel.
    /// </summary>
    /// <param name="message">The text message to send.</param>
    /// <returns>True if sent successfully, false otherwise.</returns>
    public bool SendMessage(string message) => SendOn(_peer, message);

    // Sends to ONE peer connection's own channel; the check (alive, verified, open) and the send happen under the
    // peer's lock, so a connection replacement cannot slip in between and redirect the message to the new peer.
    private bool SendOn(PeerContext peer, string message, bool? requireVerified = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DataChannelManager));
        }

        try
        {
            if (!peer.TrySend(channel => channel.send(message), requireVerified: requireVerified ?? RequireE2EBinding))
            {
                _logger.Warning("Cannot send message: DataChannel not open, not verified or replaced");
                return false;
            }

            Interlocked.Increment(ref _messagesSent);
            Interlocked.Add(ref _bytesSent, Encoding.UTF8.GetByteCount(message));

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send message over DataChannel");
            return false;
        }
    }

    /// <summary>
    /// Sends an object as JSON over the data channel.
    /// </summary>
    /// <typeparam name="T">Type of object to send.</typeparam>
    /// <param name="obj">The object to serialize and send.</param>
    /// <returns>True if sent successfully, false otherwise.</returns>
    public bool SendJson<T>(T obj) => SendJsonOn(_peer, obj);

    private bool SendJsonOn<T>(PeerContext peer, T obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        try
        {
            var json = JsonSerializer.Serialize(obj);
            return SendOn(peer, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to serialize object to JSON");
            return false;
        }
    }

    /// <summary>
    /// Gets statistics about data channel operations.
    /// </summary>
    /// <returns>Statistics object.</returns>
    public DataChannelStatistics GetStatistics()
    {
        return new DataChannelStatistics
        {
            IsOpen = IsOpen,
            Label = Label,
            MessagesReceived = Interlocked.Read(ref _messagesReceived),
            MessagesSent = Interlocked.Read(ref _messagesSent),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            BytesSent = Interlocked.Read(ref _bytesSent),
            ChannelOpenedAt = _channelOpenedAt,
            Uptime = DateTime.UtcNow - _channelOpenedAt
        };
    }

    #region Event Handlers

    /// <summary>
    /// Called when the data channel opens and becomes ready.
    /// </summary>
    private void OnDataChannelOpen()
    {
        _channelOpenedAt = DateTime.UtcNow;
        _logger.Information("DataChannel opened: Label={Label}, ID={Id}",
            _dataChannel?.label, _dataChannel?.id);

        ChannelOpened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called when the data channel closes.
    /// </summary>
    private void OnDataChannelClose()
    {
        _logger.Information("DataChannel closed: Label={Label}, ID={Id}",
            _dataChannel?.label, _dataChannel?.id);
        var peer = _peer;
        peer.Revoke();
        FileReceiver?.Abort();
        _privacyScreen?.Disable("channel_closed");
        EndViewerSession(peer);

        ChannelClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called when an error occurs on the data channel.
    /// </summary>
    /// <param name="error">Error message.</param>
    private void OnDataChannelError(string error)
    {
        _logger.Error("DataChannel error: {Error}", error);
    }

    /// <summary>
    /// Called when a message is received on the data channel.
    /// Processes input messages if an input controller is configured.
    /// </summary>
    /// <param name="dc">The data channel that received the message.</param>
    /// <param name="protocol">Protocol type (string or binary).</param>
    /// <param name="data">The message data.</param>
    private void OnDataChannelMessage(PeerContext peer, RTCDataChannel dc, DataChannelPayloadProtocols protocol, byte[] data)
    {
        try
        {
            // A retired peer's messages no longer count. "peer" is the context this channel was attached to (bound in
            // AttachDataChannel), so the whole dispatch below judges the message against ITS state.
            if (peer.IsRetired)
            {
                return;
            }

            Interlocked.Increment(ref _messagesReceived);
            Interlocked.Add(ref _bytesReceived, data.Length);

            // Size gate before decoding: a huge frame must not be turned into a string at all.
            if (data.Length > DataChannelPolicy.MaxMessageBytes)
            {
                LogRejection("<oversize>", $"{data.Length} bytes");
                return;
            }

            // Convert byte array to string
            var message = Encoding.UTF8.GetString(data);

            _logger.Debug("DataChannel message received: Length={Length} bytes", data.Length);

            // Raise event for general message handling
            MessageReceived?.Invoke(this, message);

            // If input controller is configured, try to process as input message.
            // With e2e binding the messages must be processed even without a controller: e2e_bind is handled there.
            if (_inputController != null || RequireE2EBinding)
            {
                ProcessInputMessage(peer, message);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing DataChannel message");
        }
    }

    /// <summary>
    /// Processes a message as an input command or clipboard message.
    /// The message is first checked against the session policy; only allowed messages reach a handler.
    /// </summary>
    /// <param name="message">JSON message containing command.</param>
    internal void ProcessInputMessage(string message) => ProcessInputMessage(_peer, message);

    // Everything here concerns "peer", the connection the message arrived on.
    //
    // Two phases, because nothing may call out (events, sends, the input controller, the file receiver, the privacy
    // screen, other locks) while peer.SyncRoot is held:
    //  1. under the peer's lock: judge the message with THIS peer's state and make the small state changes;
    //     the result is an "effect" (what to do), not the doing.
    //  2. after releasing the lock: run the effect, unless the peer was replaced in between.
    // A message that was valid when it was judged may therefore still be applied a moment after its connection is
    // replaced; that equals the message having arrived a moment earlier. What can never happen is a message being
    // judged with another connection's state.
    internal void ProcessInputMessage(PeerContext peer, string message)
    {
        try
        {
            if (peer.IsRetired)
            {
                return;
            }

            if (message.Length > DataChannelPolicy.MaxMessageBytes)
            {
                LogRejection("<oversize>", $"{message.Length} chars");
                return;
            }

            using var doc = JsonDocument.Parse(message);
            var privacyEpoch = PrivacyScreen?.Epoch ?? 0; // before the policy check: see PrivacyScreenController.Epoch

            Action? effect;
            lock (peer.SyncRoot)
            {
                if (peer.IsRetired)
                {
                    return;
                }

                effect = Judge(peer, message, doc.RootElement, privacyEpoch);
            }

            if (effect != null && !peer.IsRetired)
            {
                effect();
            }
        }
        catch (JsonException)
        {
            // No exception object in the log: its text can echo parts of the payload.
            LogRejection("<malformed>", "invalid JSON");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing message");
        }
    }

    // Phase 1 (runs under the peer's lock): returns what to do, or null for "nothing".
    private Action? Judge(PeerContext peer, string message, JsonElement root, long privacyEpoch)
    {
        // Default deny: an unknown type, or a capability the session lacks, never reaches a handler.
        if (!DataChannelPolicy.TryGetRequiredCapability(root, out var typeName, out var required))
        {
            // One shared bucket for all unknown names: the names come from the peer, so counting them
            // separately would let it grow the dictionary (and the log) without limit.
            LogRejection("<unknown>", $"unknown message type '{typeName}'");
            return null;
        }

        var granted = GrantedFor(peer);
        if ((granted & required) != required)
        {
            // The viewer asks for its capabilities as soon as its own check passed, while the host is still being asked whether
            // it may connect. Tell it why nothing happens (the answer to its request follows when the host decided).
            if (typeName == "authorization_request" && peer.AdmissionStarted)
                return () => SendOn(peer, JsonSerializer.Serialize(new { type = "approval_pending", what = "connection" }), requireVerified: false);

            LogRejection(typeName, $"capability {required} not granted");
            // Tell a viewer that offers a file anyway (stale UI, other client) instead of leaving it waiting.
            if (typeName == "file_offer" && FileReceiver.TryGetId(root) is { } offerId)
            {
                return () => SendJsonOn(peer, new { type = "file_reject", id = offerId, reason = "disabled" });
            }
            return null;
        }

        if (root.TryGetProperty("type", out var typeElement))
        {
            var messageType = typeElement.GetString();

            if (messageType == "e2e_bind")
            {
                return () => HandleViewerBind(peer, root);
            }

            if (messageType == "authorization_request")
            {
                // Without a ControlApproval the host decided with the "Allow remote control" checkbox, which authorized the
                // input controller when sharing started: report that state. The viewer only sends input once it got
                // authorized = true.
                // With a ControlApproval, "interactive" requests (the viewer's user clicked "Enable control") ask the person at
                // the computer; the first, automatic request only learns what is possible ("requestable").
                // Clients that know the protocol always send the field ("interactive": false for the automatic probe); one that omits it
                // is an older viewer that can never ask, so it is told to update instead of silently staying view-only.
                var hasInteractive = root.TryGetProperty("interactive", out var interactiveElement);
                var interactive = hasInteractive && interactiveElement.ValueKind == JsonValueKind.True;
                // THIS peer's grant (not the global controller state): another viewer's authorization that is still finishing
                // must not make this one believe it has control.
                bool authorized = (granted & SessionCapabilities.Control) != 0;
                var approval = ControlApproval;
                _logger.Information("Remote control requested by viewer, authorized: {Authorized}, interactive: {Interactive}", authorized, interactive);
                if (approval != null && interactive && !authorized)
                {
                    return () =>
                    {
                        SendOn(peer, JsonSerializer.Serialize(new { type = "approval_pending", what = "control" }));
                        _ = RunControlApprovalAsync(peer, approval);
                    };
                }
                var legacyViewer = approval != null && !hasInteractive && !authorized;
                var requestable = approval != null && !authorized && !legacyViewer;
                return () => SendAuthorizationResponse(peer, authorized, granted, requestable, legacyViewer ? "client_update_required" : null);
            }

            if (messageType == "switch_display")
            {
                // Passed the policy: this peer holds Control. The epoch is taken now, in the judging phase, so a revoke
                // that happens while the new capturer starts cancels the switch (see SwitchDisplayAsync).
                var request = root.Clone();
                var epoch = Volatile.Read(ref _controlEpoch);
                return () => _ = SwitchDisplayAsync(peer, request, epoch);
            }

            if (messageType == "privacy_screen")
            {
                // Passed the policy: PrivacyScreen and Control are both granted. The state goes back through StateChanged.
                var on = root.TryGetProperty("enabled", out var onElement) && onElement.ValueKind == JsonValueKind.True;
                return () => PrivacyScreen?.Set(on, privacyEpoch, () => (GrantedFor(peer) & required) == required);
            }

            if (messageType == "recording_state")
            {
                // Passed the policy: View is granted. A repeat of the same value is not an event (no duplicate audit record).
                var on = root.TryGetProperty("on", out var recordingOn) && recordingOn.ValueKind == JsonValueKind.True;
                if (peer.Recording == on) return null;
                peer.Recording = on;
                return () => ViewerRecordingChanged?.Invoke(this, peer);
            }

            if (messageType == "clipboard_subscribe")
            {
                // Already passed the policy: only sessions with the Clipboard grant get here.
                peer.ClipboardSubscribed = root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
                _logger.Information("Viewer clipboard subscription: {Subscribed}", peer.ClipboardSubscribed);
                return null;
            }

            if (messageType is "file_offer" or "file_chunk" or "file_end" or "file_cancel")
            {
                // Passed the policy: only sessions with the FileTransfer grant (and so a receiver) get here.
                // Replies go to THIS peer's transport, whatever connection is current when they are sent.
                // The generation is taken now, while the peer is known to be alive: if the peer is replaced before the
                // effect runs, the replacement aborts the receiver, and the receiver then drops this stale message.
                var receiver = FileReceiver;
                var generation = receiver?.Generation;
                return () => receiver?.Handle(messageType, root, json => SendOn(peer, json), generation);
            }

            if (messageType == "clipboard")
            {
                if (root.TryGetProperty("data", out var dataElement))
                {
                    var clipboardMessage = JsonSerializer.Deserialize<ClipboardMessage>(dataElement.GetRawText());
                    if (clipboardMessage != null)
                    {
                        _logger.Information("Received clipboard message: Type={Type}, Size={Size}",
                            clipboardMessage.ContentType, clipboardMessage.SizeBytes);
                        return () => ClipboardMessageReceived?.Invoke(this, clipboardMessage);
                    }
                }

                // Never fall through: a clipboard message without "data" must not be parsed as input.
                return null;
            }
        }

        // Fall back to treating as InputMessage
        var inputMessage = JsonSerializer.Deserialize<InputMessage>(message, InputJsonOptions);

        if (inputMessage == null)
        {
            _logger.Warning("Failed to deserialize message: Result was null");
            return null;
        }

        // Apply input to controller if available
        if (_inputController == null)
        {
            return null;
        }

        return () =>
        {
            if (!_inputController.ApplyInput(inputMessage))
            {
                _logger.Warning("Input message rejected: Type={Type}", inputMessage.Type);
                return;
            }
            Volatile.Write(ref _lastInputTicks, Environment.TickCount64);
        };
    }

    private long _lastInputTicks = long.MinValue / 2;

    /// <summary>When a viewer's mouse or keyboard input was last applied (Environment.TickCount64); tells whether
    /// someone is controlling right now (the browser does not report switching control off).</summary>
    public long LastInputTicks => Volatile.Read(ref _lastInputTicks);

    // The answer to "authorization_request": what the host allows, in the viewer's vocabulary. Also sent unasked once a
    // waiting viewer was admitted or its control request was answered.
    private void SendAuthorizationResponse(PeerContext peer, bool authorized, SessionCapabilities granted, bool requestable = false, string? reason = null)
    {
        // "clipboard" tells the viewer whether the host shares the text clipboard (only then its buttons are usable).
        var clipboard = (granted & SessionCapabilities.Clipboard) != 0;
        // "files" likewise tells the viewer whether it may send files to this host.
        var files = (granted & SessionCapabilities.FileTransfer) != 0;
        // "privacy" says whether the viewer may offer its privacy screen button.
        var privacy = (granted & SessionCapabilities.PrivacyScreen) != 0;
        SendJsonOn(peer, new { type = "authorization_response", authorized, clipboard, files, privacy, requestable, reason, timestamp = DateTime.UtcNow });
        // The display list follows to the same peer; its viewer shows the picker only when it also has control.
        if (DisplaySwitcher is { } displays)
            SendOn(peer, displays.BuildMessage());
    }

    // Runs outside the peer's lock (an effect). Asks the person at the computer and answers the peer that asked.
    private async Task RunControlApprovalAsync(PeerContext peer, ControlApprovalGate approval)
    {
        try
        {
            var outcome = await approval.RequestAsync(peer).ConfigureAwait(false);
            if (outcome == ApprovalOutcome.Ignored || peer.IsRetired)
                return; // the viewer left while the question was open: nothing to grant, nobody to tell

            var authorized = false;
            if (outcome == ApprovalOutcome.Allowed && _inputController != null)
            {
                // The controller may take a while to grant (the Windows one waits about a second). The viewer can leave meanwhile,
                // so remember which session this grant is for and check again, under the same lock the session end takes.
                var epoch = peer.Epoch;
                authorized = await _inputController.RequestAuthorizationAsync("approved-by-host").ConfigureAwait(false);
                var gone = false;
                var rollBack = false;
                lock (_controlLock)
                {
                    gone = peer.IsRetired || peer.Epoch != epoch;
                    if (authorized && !gone)
                        _controlPeer = peer; // from here on only this viewer has Control
                    else
                        rollBack = authorized && _controlPeer == null; // nobody else owns the authorization: take it back
                }
                if (rollBack) _inputController.RevokeAuthorization();
                if (gone) return; // the viewer left: nobody to tell
            }

            SendAuthorizationResponse(peer, authorized, GrantedFor(peer),
                reason: authorized ? null : outcome == ApprovalOutcome.TimedOut ? "timeout" : "denied");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while asking the host for control approval");
        }
    }

    // Unattended agent: the viewer's binding verified, now the person at the computer decides whether it may connect.
    private async Task AdmitThenVerifyAsync(PeerContext peer, Func<CancellationToken, Task<bool>> admit)
    {
        var cts = new CancellationTokenSource(AdmissionTimeout);
        Interlocked.Exchange(ref _admissionCts, cts)?.Cancel(); // an older question is moot
        var allowed = false;
        try
        {
            allowed = await admit(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                _logger.Error(ex, "Admission question failed; the viewer is refused");
        }
        finally
        {
            Interlocked.CompareExchange(ref _admissionCts, null, cts);
            cts.Dispose();
        }

        // The effect runs after the question: judge again whether this connection is still the live one.
        if (peer.IsRetired)
            return;

        if (!allowed)
        {
            SendOn(peer, JsonSerializer.Serialize(new { type = "approval_denied", what = "connection" }), requireVerified: false);
            ViewerAdmissionDenied?.Invoke(this, peer);
            return;
        }

        if (!peer.TryVerify())
            return;

        _logger.Information("Viewer was admitted by the host after the end-to-end binding check");
        ViewerVerified?.Invoke(this, peer);
        var grantedNow = GrantedFor(peer);
        var controlNow = (grantedNow & SessionCapabilities.Control) != 0;
        SendAuthorizationResponse(peer, controlNow, grantedNow, ControlApproval != null && !controlNow);
    }

    /// <summary>
    /// Verifies the viewer's <c>e2e_bind</c>. On success the peer gets its normal grants and receives the sender's own
    /// bind; on failure nothing is sent back (the sender's mac must not be handed to an unverified peer).
    /// </summary>
    private void HandleViewerBind(PeerContext peer, JsonElement message)
    {
        if (VerifyViewerBind == null || peer.IsVerified || (AdmitViewer != null && peer.AdmissionStarted))
        {
            return; // binding not in use, or already done / waiting for the host's answer: a repeated message changes nothing
        }

        string? senderMac = null;
        if (E2EBinding.TryReadBind(message, out var role, out var mac) && role == E2EBinding.ViewerRole)
        {
            senderMac = VerifyViewerBind(peer, mac);
        }

        if (senderMac == null)
        {
            LogRejection("e2e_bind", "binding does not verify");
            ViewerBindFailed?.Invoke(this, peer);
            return;
        }

        // The sender's own bind goes out BEFORE the peer is committed as verified: the viewer must have proven the sender
        // (and be ready to show video) before any frame or grant opens for it. The reply is allowed to an unverified
        // peer because its MAC just verified; TrySend still refuses a retired peer.
        var reply = JsonSerializer.Serialize(new { type = "e2e_bind", v = E2EBinding.Version, role = E2EBinding.SenderRole, mac = senderMac });
        SendOn(peer, reply, requireVerified: false);

        // Unattended confirmation: the peer stays unverified (no video, no grants) until the host says yes.
        if (AdmitViewer is { } admit)
        {
            if (peer.TryBeginAdmission())
                _ = AdmitThenVerifyAsync(peer, admit);
            return;
        }

        // Commits to the captured peer only, and only if it was not retired meanwhile.
        if (!peer.TryVerify())
        {
            return;
        }

        _logger.Information("Viewer passed the end-to-end binding check");
        ViewerVerified?.Invoke(this, peer);
    }

    // Bumped whenever the control authorization changes, so a display switch requested under an earlier grant can be cancelled.
    private int _controlEpoch;

    private void OnAuthorizationStateChanged(object? sender, InputAuthorizationState state)
    {
        Interlocked.Increment(ref _controlEpoch);
        if (ControlApproval == null || state is InputAuthorizationState.Authorized or InputAuthorizationState.Pending)
            return;

        // The authorization ended (host revoked it, timed out, rollback): whoever owned control loses it and is told.
        PeerContext? owner;
        lock (_controlLock)
        {
            owner = _controlPeer;
            _controlPeer = null;
        }
        // On another thread: this event may be raised while the controller holds its own lock, and sending takes the peer's lock.
        if (owner != null && !owner.IsRetired)
            _ = Task.Run(() => SendOn(owner, JsonSerializer.Serialize(new { type = "authorization_revoked" })));
    }

    // Runs outside the peer's lock (an effect). The answer goes to the peer that asked.
    private async Task SwitchDisplayAsync(PeerContext peer, JsonElement message, int epoch)
    {
        try
        {
            if (DisplaySwitcher is not { } displays)
                return;

            if (!Displays.DisplaySwitchService.TryReadIndex(message, out var index))
            {
                LogRejection("switch_display", "missing or invalid index");
                return;
            }

            // Control is checked again when the switch is about to take effect: it may have been revoked while the new capturer started.
            var result = await displays.SwitchAsync(index,
                () => !peer.IsRetired && epoch == Volatile.Read(ref _controlEpoch) && (GrantedFor(peer) & SessionCapabilities.Control) != 0);
            if (result is Displays.DisplaySwitchResult.InvalidIndex or Displays.DisplaySwitchResult.RateLimited or Displays.DisplaySwitchResult.Failed)
                LogRejection("switch_display", result.ToString());

            // Always answer with the current state: after a refusal it puts the viewer's picker back.
            SendOn(peer, displays.BuildMessage());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error switching display");
        }
    }

    /// <summary>
    /// Logs a rejected message. Only the type name is logged, never the content (it may hold keystrokes or
    /// clipboard text), and only the first few per bucket.
    /// </summary>
    /// <param name="bucket">A known type name or a fixed category such as "&lt;unknown&gt;"; never a raw peer
    /// value, so the number of buckets stays finite.</param>
    private void LogRejection(string bucket, string reason)
    {
        var count = _rejectionCounts.AddOrUpdate(bucket, 1, (_, c) => c + 1);
        if (count <= MaxRejectionLogsPerType)
        {
            _logger.Warning("DataChannel message rejected: Type={Type}, Reason={Reason}", bucket, reason);
        }
        else if (count == MaxRejectionLogsPerType + 1)
        {
            _logger.Warning("DataChannel: further rejections of Type={Type} will not be logged", bucket);
        }
    }

    /// <summary>Number of rejection buckets; exposed for tests that check it stays bounded.</summary>
    internal int RejectionBucketCount => _rejectionCounts.Count;

    #endregion

    #region Cleanup

    /// <summary>
    /// Detaches from the current data channel and unsubscribes from events.
    /// </summary>
    internal void DetachDataChannel()
    {
        _peer.Revoke(); // also when no channel is attached: verification never outlives its peer

        if (_dataChannel == null)
        {
            return;
        }

        _dataChannel.onopen -= OnDataChannelOpen;
        _dataChannel.onclose -= OnDataChannelClose;
        _dataChannel.onerror -= OnDataChannelError;
        _dataChannel.onmessage -= _messageHandler;
        _messageHandler = null;

        _dataChannel = null;
        _peer.Revoke();
        FileReceiver?.Abort();
        _privacyScreen?.Disable("channel_closed");
    }

    /// <summary>
    /// Disposes the data channel manager and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_inputController != null)
            _inputController.AuthorizationStateChanged -= OnAuthorizationStateChanged;
        DetachDataChannel();
        _disposed = true;

        _logger.Information("DataChannelManager disposed. Final statistics: Received={Received}, Sent={Sent}",
            _messagesReceived, _messagesSent);
    }

    #endregion
}

/// <summary>
/// Contains statistics about data channel operations.
/// </summary>
public sealed class DataChannelStatistics
{
    /// <summary>
    /// Whether the data channel is currently open.
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>
    /// Label (name) of the data channel.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Total number of messages received.
    /// </summary>
    public long MessagesReceived { get; set; }

    /// <summary>
    /// Total number of messages sent.
    /// </summary>
    public long MessagesSent { get; set; }

    /// <summary>
    /// Total bytes received.
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// Total bytes sent.
    /// </summary>
    public long BytesSent { get; set; }

    /// <summary>
    /// Timestamp when channel was opened.
    /// </summary>
    public DateTime ChannelOpenedAt { get; set; }

    /// <summary>
    /// Duration since channel was opened.
    /// </summary>
    public TimeSpan Uptime { get; set; }
}
