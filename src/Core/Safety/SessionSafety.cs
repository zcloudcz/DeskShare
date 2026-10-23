using DeskShare.Core.Auth;
using DeskShare.Core.Interfaces;
using DeskShare.Core.Platforms.Windows;
using DeskShare.Core.WebRTC;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace DeskShare.Core.Safety;

/// <summary>
/// Glue between the sender's session and the three "anti-scam" features: the approval of control and of unattended
/// connections (decided in <see cref="DataChannelManager"/>), the on-screen session banner and the audit log.
/// The decisions themselves live in small testable classes (<see cref="ControlApprovalGate"/>, <see cref="AdmissionPolicy"/>,
/// <see cref="SessionStateModel"/>, <see cref="AuditEvents"/>); this class only connects events to them.
/// </summary>
internal sealed class SessionSafety : IDisposable
{
    private readonly WebRTCSessionWithInput _session;
    private readonly IAuditSink _audit;
    private readonly SessionStateModel _model = new();
    private IDisposable? _banner;
    private PeerContext? _active; // the verified viewer we count as "the session"
    private string _sessionId = "";
    private bool _recording; // recording state of the active session (guarded by _recordingLock)
    private long _recordingSeq; // numbers the recording decisions in the order they were made (guarded by _recordingLock)
    private readonly object _recordingLock = new();

    /// <param name="controlPrompt">Asks the person at the computer for control (attended app). Without one a requested control is always denied.</param>
    /// <param name="admission">Unattended agent: asks before a viewer may connect. Null admits every verified viewer.</param>
    public SessionSafety(
        WebRTCSessionWithInput session,
        ScreenSenderConfiguration config,
        IAuditSink audit,
        ILogger logger,
        Func<CancellationToken, Task<bool>>? controlPrompt,
        Func<CancellationToken, Task<bool>>? admission)
    {
        _session = session;
        _audit = audit;
        var dataChannel = session.DataChannel;

        if (config.EnableRemoteControl && config.RequireControlApproval)
        {
            // No prompt means nobody can say yes, so control stays off (fail closed).
            var gate = new ControlApprovalGate(controlPrompt ?? (_ => Task.FromResult(false)));
            gate.Resolved += outcome =>
            {
                if (outcome == ApprovalOutcome.Denied) _audit.Write(AuditEvents.ControlDenied("the person at the computer clicked Deny"));
                else if (outcome == ApprovalOutcome.TimedOut) _audit.Write(AuditEvents.ControlDenied("nobody answered in time"));
            };
            dataChannel.ControlApproval = gate;
        }
        dataChannel.AdmitViewer = admission;

        dataChannel.ViewerVerified += (_, peer) => OnViewerStarted(peer);
        dataChannel.ViewerEnded += (_, peer) => OnViewerEnded(peer);
        dataChannel.ViewerRecordingChanged += (_, peer) => OnRecordingChanged(peer);
        dataChannel.ClipboardMessageReceived += (_, message) => _audit.Write(AuditEvents.ClipboardTransfer(toHost: true, message.SizeBytes));
        dataChannel.ClipboardSent += size => _audit.Write(AuditEvents.ClipboardTransfer(toHost: false, size));
        if (dataChannel.PrivacyScreen is { } privacy)
            privacy.StateChanged += (on, reason) => _audit.Write(AuditEvents.PrivacyScreen(on, reason));
        session.FileReceived += (_, file) => _audit.Write(AuditEvents.FileReceived(file.SavedAs, file.SizeBytes));

        if (session.InputController is { } input)
        {
            input.AuthorizationStateChanged += (_, state) => OnControlStateChanged(state);
            _model.ControlChanged(input.AuthorizationState == InputAuthorizationState.Authorized);
        }

        if (!config.ShowSessionBanner)
        {
            // The host opted out (for example a headless server nobody sits at).
        }
        else if (OperatingSystem.IsWindows())
        {
            var banner = new SessionBanner(StopControl, EndSession);
            _banner = banner;
            // The check is repeated inside the lambda because the analyzer does not carry the one above into it.
            _model.Changed += _ => { if (OperatingSystem.IsWindows()) { var s = _model.Snapshot; banner.Update(s.State, s.Recording, s.Version); } };
        }
        else
        {
            logger.LogInformation("The session banner is only available on Windows; no banner is shown.");
        }
    }

    /// <summary>For the display switch callback of the service (the switch happens outside the data channel manager).</summary>
    public void DisplaySwitched(string displayName) => _audit.Write(AuditEvents.DisplaySwitched(displayName));

    // Session identity and the recording flag change together under _recordingLock: a "recording" callback is checked against
    // the CURRENT session and applied in the same critical section, so it can never publish into a replacement session.
    // Only leaf calls happen inside the lock (the audit sink); the banner is updated after it, with a sequence number
    // (and the model's version) that make a delayed call harmless.
    private void OnViewerStarted(PeerContext peer)
    {
        // A new id per viewer connection, short enough to read in the Event Viewer.
        var id = Guid.NewGuid().ToString("N")[..8];
        lock (_recordingLock)
        {
            _sessionId = id;
            _active = peer;
            _recording = false; // a new session starts without recording until its viewer says so
        }
        _audit.Write(AuditEvents.SessionStarted(id, RemoteAddressOf(peer), endToEndVerified: true));
        _model.ViewerStarted();

        // The data channel usually reports the end, but a connection that dies without closing its channel only changes state.
        if (peer.Connection is { } connection)
        {
            connection.onconnectionstatechange += state =>
            {
                if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed)
                    OnViewerEnded(peer);
            };
        }
    }

    private void OnViewerEnded(PeerContext peer)
    {
        string sessionId;
        long sequence = 0;
        lock (_recordingLock)
        {
            // Both the channel and the connection can report the end of the same session: count it once.
            if (!ReferenceEquals(_active, peer)) return;
            _active = null;
            sessionId = _sessionId;
            if (_recording)
            {
                // The flag is per peer (PeerContext.Recording is cleared when it is retired); log the stop before the end.
                _recording = false;
                sequence = ++_recordingSeq;
                _audit.Write(AuditEvents.Recording(false));
            }
        }
        if (sequence != 0) _model.RecordingChanged(false, sequence);
        _audit.Write(AuditEvents.SessionEnded(sessionId));
        _model.ViewerEnded();
    }

    private void OnRecordingChanged(PeerContext peer)
    {
        bool on;
        long sequence;
        lock (_recordingLock)
        {
            // Only the verified viewer we count as the session may change the banner.
            if (!ReferenceEquals(_active, peer)) return;
            on = peer.Recording; // the peer's current word, not the order the callbacks happened to run in
            if (on == _recording) return;
            _recording = on;
            sequence = ++_recordingSeq;
            _audit.Write(AuditEvents.Recording(on)); // inside the lock: the audit trail keeps the order of the decisions
        }
        _model.RecordingChanged(on, sequence);
    }


    private void OnControlStateChanged(InputAuthorizationState state)
    {
        _model.ControlChanged(state == InputAuthorizationState.Authorized);
        switch (state)
        {
            case InputAuthorizationState.Authorized: _audit.Write(AuditEvents.ControlGranted()); break;
            case InputAuthorizationState.Revoked: _audit.Write(AuditEvents.ControlRevoked()); break;
        }
    }

    // Banner button: the viewer keeps watching, but its mouse and keyboard stop working (and it is told).
    private void StopControl() => _session.RevokeInputAuthorization();

    // Banner button: hang up on the viewer. Its next connection attempt starts the whole check (and approval) again.
    private void EndSession()
    {
        try { _active?.Connection?.close(); }
        catch (Exception) { /* already closed */ }
    }

    private static string? RemoteAddressOf(PeerContext peer)
    {
        try { return peer.Connection?.VideoDestinationEndPoint?.Address.ToString(); }
        catch (Exception) { return null; }
    }

    public void Dispose() => _banner?.Dispose();
}
