// WebSocket and WebRTC connection management
let ws = null;
let pc = null;
let clientId = null;
let senderId = null;
let resumeToken = null; // Single-use token from /authenticate or /resume; lets a reconnect skip the (rotated) passkey
let pendingIceCandidates = []; // Queue for ICE candidates that arrive before offer
let statsInterval = null; // Interval for polling WebRTC stats
let pingInterval = null; // Interval for sending ping messages
let lastPongTime = Date.now(); // Timestamp of last received pong
let reconnectAttempts = 0; // Number of reconnection attempts
let reconnectTimeout = null; // Timeout for reconnection delay
let isReconnecting = false; // Flag to prevent multiple simultaneous reconnections
let dataChannel = null; // Created by the sender when the host allowed remote control
let userDisconnected = false; // Set by disconnect(); unexpected socket closes may reconnect
let remoteControl = null; // RemoteControl (remote-control.js) bound to the video element

const PING_INTERVAL_MS = 5000; // Send ping every 5 seconds
const PING_TIMEOUT_MS = 15000; // Consider server dead after 15 seconds without pong
const MAX_RECONNECT_ATTEMPTS = 5; // Maximum number of reconnection attempts
const INITIAL_RECONNECT_DELAY_MS = 1000; // Initial delay before reconnecting (1 second)
const MAX_RECONNECT_DELAY_MS = 30000; // Maximum delay before reconnecting (30 seconds)

let config = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' }
    ]
};

// UI Elements
const connectBtn = document.getElementById('connectBtn');
const disconnectBtn = document.getElementById('disconnectBtn');
const fullscreenBtn = document.getElementById('fullscreenBtn');
const remoteVideo = document.getElementById('remoteVideo');
const videoContainer = document.getElementById('videoContainer');
const placeholder = document.getElementById('placeholder');
const statusDiv = document.getElementById('status');
const statusText = document.getElementById('statusText');
const controlBtn = document.getElementById('controlBtn');
const ctrlAltDelBtn = document.getElementById('ctrlAltDelBtn');
const sendClipboardBtn = document.getElementById('sendClipboardBtn');
const copyHostClipboardBtn = document.getElementById('copyHostClipboardBtn');
const sendFileBtn = document.getElementById('sendFileBtn');
const privacyBtn = document.getElementById('privacyBtn');
const recordBtn = document.getElementById('recordBtn');
const recIndicator = document.getElementById('recIndicator');
const displayPicker = document.getElementById('displayPicker');
const displaySelect = document.getElementById('displaySelect');
const fileInput = document.getElementById('fileInput');

function updateStatus(message, type = 'info') {
    statusDiv.style.display = 'block';
    statusDiv.className = `status ${type}`;
    statusText.textContent = message;
    console.log(`[${type.toUpperCase()}] ${message}`);

    // The status line is in the panel that "Hide panels" hides; an error must never stay invisible.
    if (type === 'error') {
        setFocus(false);
    }
}

function updateConnectionState(state) {
    document.getElementById('connectionState').textContent = state;
}

function updateWebRtcState(state) {
    document.getElementById('webrtcState').textContent = state;
}

function getBaseUrl() {
    const signalingUrl = document.getElementById('signalingUrl').value;
    const url = new URL(signalingUrl);
    return `${url.protocol === 'wss:' ? 'https:' : 'http:'}//${url.host}`;
}

// Mirrors RequestSigningService.SignRequest on the server: HMAC-SHA256(passkey, "serverId|timestamp|nonce").
// The server re-formats the parsed timestamp with .NET's round-trip format ("O"), which has 7 fractional
// digits, so the 3-digit JS ISO string is padded with "0000" before "Z" to produce the same bytes.
// crypto.subtle only exists in secure contexts (https/localhost); elsewhere we fall back to unsigned (legacy).
async function signAuthRequest(serverId, passkey, timestamp, nonce) {
    if (!crypto.subtle || !passkey) return null;
    const enc = new TextEncoder();
    const key = await crypto.subtle.importKey('raw', enc.encode(passkey), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    const message = `${serverId}|${timestamp.replace('Z', '0000Z')}|${nonce}`;
    const sig = await crypto.subtle.sign('HMAC', key, enc.encode(message));
    return btoa(String.fromCharCode(...new Uint8Array(sig)));
}

// ---- End-to-end binding (mirrors E2EBinding.cs; tests/js/e2e-vector.mjs checks both give the same bytes) ----
// The signaling server relays the SDP (and so the DTLS fingerprints) and sees what we send it, so the passkey itself
// never goes there: we send authKey, derived from it. e2eKey, derived from the same passkey, stays in this page and
// is used to prove over the data channel that both sides see the same fingerprints (no man in the middle).
const PLACEHOLDER_IDLE = placeholder.textContent;
const BIND_TIMEOUT_MS = 10000;
const BIND_FAILED_MESSAGE = 'Secure connection check failed. The connection may be intercepted. Do not continue.';
let e2eKey = null; // { serverId, bytes }; memory only. A page reload needs the passkey again.
// All per-connection state (verified, clipboard/file permission, transfer, held-back video) lives in the PeerSession of
// the current peer connection (bind-gate.js). Its messages go out only through sessions.send().
const sessions = new SessionHost();
let bindTimer = null;
let bindFailed = false; // keeps the red error on screen when the socket close handler would overwrite it

function toHex(buf) {
    return Array.from(new Uint8Array(buf), (b) => b.toString(16).padStart(2, '0')).join('');
}

// Returns { authKey (base64url), e2eKey (Uint8Array) }: HKDF-SHA256(passkey, salt "DeskShare/v1/"+serverId, info "auth"/"e2e").
async function deriveKeys(passkey, serverId) {
    return hkdfKeys(new TextEncoder().encode(passkey.replace(/[-\s]/g, '').toUpperCase()), serverId);
}

// The one input field takes the passkey or the host's permanent password (mirrors E2EBinding.DeriveFromUserInput).
const PERMANENT_PASSWORD_MIN_LENGTH = 12;
const looksLikePasskey = (input) => /^[A-Za-z0-9]{9}$/.test(input.replace(/[-\s]/g, ''));

// Permanent password (mirrors E2EBinding.FromPermanentPassword): master = PBKDF2-SHA256(NFC(password),
// "DeskShare/v1/perm/" + serverId, 600000 iterations), then the same HKDF step as a passkey. Takes 0.5-1 s on purpose:
// that is what makes guessing the password from the authKey the server sees expensive.
async function derivePermanentKeys(password, serverId) {
    const enc = new TextEncoder();
    const base = await crypto.subtle.importKey('raw', enc.encode(password.normalize('NFC')), 'PBKDF2', false, ['deriveBits']);
    const master = await crypto.subtle.deriveBits(
        { name: 'PBKDF2', hash: 'SHA-256', salt: enc.encode('DeskShare/v1/perm/' + serverId), iterations: 600000 }, base, 256);
    return hkdfKeys(new Uint8Array(master), serverId);
}

async function hkdfKeys(secret, serverId) {
    const enc = new TextEncoder();
    const ikm = await crypto.subtle.importKey('raw', secret, 'HKDF', false, ['deriveBits']);
    const salt = enc.encode('DeskShare/v1/' + serverId);
    const derive = async (info) => new Uint8Array(await crypto.subtle.deriveBits(
        { name: 'HKDF', hash: 'SHA-256', salt, info: enc.encode(info) }, ikm, 256));
    const auth = await derive('auth');
    return {
        authKey: btoa(String.fromCharCode(...auth)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''),
        e2eKey: await derive('e2e')
    };
}

// The sha-256 DTLS fingerprint of an SDP (uppercase, colons), or null when there is none or the SDP names two
// different ones (we could not tell which certificate DTLS uses).
function getFingerprint(sdp) {
    let found = null;
    const re = /^a=fingerprint:sha-256[ \t]+((?:[0-9A-Fa-f]{2}:){31}[0-9A-Fa-f]{2})[ \t]*$/gm;
    for (const m of (sdp || '').replace(/\r/g, '').matchAll(re)) {
        const fp = m[1].toUpperCase();
        if (found && found !== fp) return null;
        found = fp;
    }
    return found;
}

async function bindKey(usage) {
    return crypto.subtle.importKey('raw', e2eKey.bytes, { name: 'HMAC', hash: 'SHA-256' }, false, [usage]);
}

function bindData(role, senderFp, viewerFp) {
    return new TextEncoder().encode(`DeskShare-bind-v1|${role}|${senderFp}|${viewerFp}`);
}

// Fingerprints as this side sees them: the sender's is in the remote description, ours in the local one.
// Always for an explicit peer connection (never the global pc, which a new offer may have replaced).
function bindFingerprints(peer) {
    return {
        senderFp: getFingerprint(peer?.remoteDescription?.sdp),
        viewerFp: getFingerprint(peer?.localDescription?.sdp)
    };
}

async function sendBind(session) {
    const { senderFp, viewerFp } = bindFingerprints(session.pc);
    if (!e2eKey || !senderFp || !viewerFp) return false;
    const mac = toHex(await crypto.subtle.sign('HMAC', await bindKey('sign'), bindData('viewer', senderFp, viewerFp)));
    // The session is re-checked inside send(): replaced meanwhile means nothing is sent, and that is not a failure.
    if (!sessions.send(session, { type: 'e2e_bind', v: 1, role: 'viewer', mac }, { bind: true })) return !sessions.isCurrent(session);
    session.bindSent = true;
    return true;
}

// crypto.subtle.verify compares in constant time.
async function verifySenderBind(message, peer) {
    const { senderFp, viewerFp } = bindFingerprints(peer);
    if (!e2eKey || !senderFp || !viewerFp || message.v !== 1 || message.role !== 'sender'
        || typeof message.mac !== 'string' || !/^[0-9a-fA-F]{64}$/.test(message.mac)) return false;
    const mac = Uint8Array.from(message.mac.match(/../g), (h) => parseInt(h, 16));
    return crypto.subtle.verify('HMAC', await bindKey('verify'), mac, bindData('sender', senderFp, viewerFp));
}

function showVideo(session) {
    if (!session.pendingStream || !session.verified || !sessions.isCurrent(session)) return;
    remoteVideo.srcObject = session.pendingStream;
    placeholder.style.display = 'none';
    remoteVideo.style.display = 'block';
    fullscreenBtn.disabled = false;
    recordBtn.hidden = false; // only a verified session shows video, so recording is offered only then
    updateStatus('Receiving video stream', 'success');
}

function failBinding() {
    console.warn('End-to-end check failed'); // no keys or fingerprints in the console
    bindFailed = true;
    disconnect();
    updateStatus(BIND_FAILED_MESSAGE, 'error');
}

function startBindTimer(session) {
    clearTimeout(bindTimer);
    if (session.verified) return;
    bindTimer = setTimeout(() => {
        // Only the session that was waiting counts, not a newer one created by a reconnect.
        if (!session.verified && sessions.isCurrent(session)) failBinding();
    }, BIND_TIMEOUT_MS);
}

// Mirrors ServerIdGenerator.NormalizeServerIdInput: "123 456 789", "123-456-789", "123456789" and
// "server-123456789" all become "server-123456789". Anything else is only trimmed so legacy IDs still work.
function normalizeServerId(input) {
    const trimmed = (input || '').trim();
    const rest = /^server-/i.test(trimmed) ? trimmed.slice('server-'.length) : trimmed;
    const digits = rest.replace(/[ -]/g, '');
    return /^[1-9]\d{8}$/.test(digits) ? `server-${digits}` : trimmed;
}

// Mirrors ServerIdGenerator.FormatServerIdForDisplay: "server-123456789" -> "123 456 789".
function formatServerId(serverId) {
    const m = /^server-([1-9]\d{2})(\d{3})(\d{3})$/.exec(serverId);
    return m ? `${m[1]} ${m[2]} ${m[3]}` : serverId;
}

async function authenticate() {
    const baseUrl = getBaseUrl();
    const serverId = normalizeServerId(document.getElementById('serverId').value);
    // Kept as typed: a permanent password must not lose its dashes (deriveKeys normalizes a passkey itself).
    const passkey = document.getElementById('passkey')?.value || '';

    if (!serverId) {
        updateStatus('Server ID is required', 'error');
        return null;
    }

    if (!crypto.subtle) {
        updateStatus('A secure context (https) is required to connect.', 'error');
        return null;
    }

    clientId = clientId || crypto.randomUUID();

    // The server gets authKey instead of the passkey. Without a typed passkey (a trusted client reconnecting) there
    // is nothing to derive: we keep the e2eKey of this page session if it belongs to the same server, otherwise the
    // end-to-end check will fail and the passkey has to be entered again.
    let authKey = '';
    if (passkey) {
        let keys;
        if (looksLikePasskey(passkey)) {
            keys = await deriveKeys(passkey, serverId);
        } else if (passkey.length >= PERMANENT_PASSWORD_MIN_LENGTH) {
            updateStatus('Checking password...', 'info');
            keys = await derivePermanentKeys(passkey, serverId);
        } else {
            updateStatus(`Enter the 9-character passkey or the permanent password (at least ${PERMANENT_PASSWORD_MIN_LENGTH} characters).`, 'error');
            return null;
        }
        authKey = keys.authKey;
        e2eKey = { serverId, bytes: keys.e2eKey };
    } else if (e2eKey && e2eKey.serverId !== serverId) {
        e2eKey = null;
    }

    const timestamp = new Date().toISOString();
    const nonce = crypto.randomUUID();
    const body = {
        ServerId: serverId,
        Passkey: authKey,
        ClientId: clientId,
        Timestamp: timestamp,
        Nonce: nonce,
        Signature: await signAuthRequest(serverId, authKey, timestamp, nonce)
    };

    updateStatus('Authenticating...', 'info');

    const response = await fetch(`${baseUrl}/authenticate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
    });

    if (!response.ok) {
        const errorText = response.status === 401 ? 'Invalid passkey, password or server ID'
            : response.status === 429 ? 'Too many attempts. Wait a few minutes and try again.'
            : `Authentication failed (${response.status})`;
        updateStatus(errorText, 'error');
        return null;
    }

    const result = await response.json();
    resumeToken = result.resumeToken || null;
    return result;
}

/**
 * Trades the stored resume token for a new WebSocket token (POST /resume), so a reconnect works
 * even though the passkey the user typed has long since rotated.
 * Returns null when the server refuses the token (expired, used, sender gone); the caller then falls back to /authenticate.
 */
async function resume() {
    const response = await fetch(`${getBaseUrl()}/resume`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ResumeToken: resumeToken })
    });

    if (response.status === 401) {
        // The server burns a token on first use, so a refused one is worthless.
        resumeToken = null;
        return null;
    }
    if (!response.ok) {
        // Network/server trouble: keep the token (the server may not have consumed it) and let the retry loop try again.
        throw new Error(`Resume failed (${response.status})`);
    }

    const result = await response.json();
    resumeToken = result.resumeToken || null; // rotated: the old token is spent
    return result;
}

/**
 * Opens the signaling WebSocket. With tryResume (reconnects only) it uses the resume token first;
 * a manual connect always authenticates with the typed passkey.
 * Returns true when a WebSocket was created, false when authentication or setup failed.
 */
async function connect(tryResume = false) {
    bindFailed = false;
    try {
        let authResult = null;
        if (tryResume && resumeToken) {
            updateStatus('Resuming session...', 'info');
            authResult = await resume();
        }
        if (!authResult) authResult = await authenticate();
        if (!authResult) return false;

        // HTTP JSON from the minimal API is camelCase (WebSocket signaling messages stay PascalCase).
        if (authResult.iceServers && authResult.iceServers.length > 0) {
            config = { iceServers: authResult.iceServers };
        }

        const signalingUrl = document.getElementById('signalingUrl').value;
        const wsUrl = new URL(signalingUrl);
        wsUrl.searchParams.set('token', authResult.webSocketToken);

        updateStatus('Connecting to signaling server...', 'info');
        updateConnectionState('Connecting');

        ws = new WebSocket(wsUrl.toString());

        ws.onopen = () => {
            updateStatus('Connected to signaling server', 'success');
            updateConnectionState('Connected');
            connectBtn.disabled = true;
            disconnectBtn.disabled = false;
        };

        ws.onmessage = async (event) => {
            console.log('Raw WebSocket message received:', event.data);

            try {
                const message = JSON.parse(event.data);
                console.log('Parsed message:', message);
                console.log('Message Type:', message.Type);

                switch (message.Type) {
                case 0: // Offer
                    senderId = message.SenderId;
                    await handleOffer(message);
                    break;

                case 1: // Answer
                    console.log('Received answer (unexpected for client role)');
                    break;

                case 2: // IceCandidate
                    await handleIceCandidate(message);
                    break;

                case 3: // Identify
                    clientId = message.TargetId;
                    document.getElementById('clientId').textContent = clientId.substring(0, 8) + '...';
                    updateStatus('Connected, requesting connection to server...', 'info');

                    // After getting our ID, request connection to the server
                    const serverId = normalizeServerId(document.getElementById('serverId').value);
                    if (serverId) {
                        sendConnectionRequest(serverId);
                        // Start monitoring server liveness
                        startPingMonitoring(serverId);
                    }
                    break;

                case 4: // Error
                    updateStatus(`Error: ${message.ErrorMessage}`, 'error');

                    // If the error is about server disconnecting, clean up
                    if (message.ErrorMessage && message.ErrorMessage.includes('disconnected')) {
                        // The sender's signaling socket can drop briefly (it reconnects by itself) while the
                        // peer-to-peer video keeps running; only give up when the media is gone as well.
                        if (message.SenderId === senderId && !isMediaConnected()) {
                            console.log('Screen sender disconnected, cleaning up...');
                            disconnect();
                        }
                    }
                    break;

                case 5: // ConnectionRequest
                    console.log('Received connection request (unexpected for client role)');
                    break;

                case 6: // Ping
                    // Server is checking if we're alive - respond with pong
                    sendMessage({
                        Type: 7, // Pong
                        TargetId: message.SenderId
                    });
                    break;

                case 7: // Pong
                    // Server responded to our ping - it's alive
                    lastPongTime = Date.now();
                    break;

                default:
                    console.warn('Unknown message type:', message.Type, message);
                    break;
            }
            } catch (error) {
                console.error('Error parsing message:', error, 'Raw data:', event.data);
            }
        };

        ws.onerror = (error) => {
            console.error('WebSocket error:', error);
            updateStatus('WebSocket error occurred', 'error');
        };

        ws.onclose = (event) => {
            console.log('WebSocket closed:', event.code, event.reason);
            stopPingMonitoring();
            ws = null;

            // Media flows peer-to-peer (or via TURN), not through the signaling server. While it is up, a
            // signaling outage (e.g. a server deployment) must not end the session; reconnect only when needed.
            if (!userDisconnected && isMediaConnected()) {
                updateConnectionState('Signaling offline');
                updateStatus('Signaling server offline - video and control continue', 'info');
                return;
            }

            updateConnectionState('Disconnected');
            connectBtn.disabled = false;
            disconnectBtn.disabled = true;
            cleanup();

            // Attempt reconnection if it wasn't a manual disconnect
            // A server shutdown closes cleanly too, so only the user's own Disconnect stops reconnecting.
            if (!userDisconnected && reconnectAttempts < MAX_RECONNECT_ATTEMPTS) {
                attemptReconnection();
            } else if (reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
                updateStatus('Max reconnection attempts reached. Please reconnect manually.', 'error');
            } else {
                if (!bindFailed) updateStatus('Disconnected from signaling server', 'info');
            }
        };

        return true;
    } catch (error) {
        console.error('Connection error:', error);
        updateStatus(`Connection failed: ${error.message}`, 'error');
        updateConnectionState('Error');
        return false;
    }
}

async function handleOffer(message) {
    try {
        updateStatus('Received offer, creating peer connection...', 'info');

        // A new offer replaces the connection: detach and close the old one first, so none of its late events or
        // callbacks can act on the new one.
        stopRecording(); // the old peer's recording ends here (and is still offered as a file)
        recordBtn.hidden = true;
        if (remoteControl) { remoteControl.disable(); remoteControl = null; }
        if (dataChannel) {
            dataChannel.onopen = dataChannel.onclose = dataChannel.onmessage = null;
            dataChannel.close();
            dataChannel = null;
        }
        if (pc) {
            pc.ondatachannel = pc.ontrack = pc.onicecandidate = pc.onconnectionstatechange = pc.oniceconnectionstatechange = null;
            pc.close();
        }
        clearTimeout(bindTimer);

        // Create peer connection
        pc = new RTCPeerConnection(config);
        // A new session per peer connection (the old one dies: its checks, clipboard reads and file transfers are
        // void), and every peer connection must pass the end-to-end check again.
        const session = sessions.begin(pc);
        hostAllowsControl = false;
        hostAllowsPrivacy = false;
        privacyOn = false;
        hostDisplays = []; // the previous connection's display list does not carry over
        setClipboardAllowed(false);
        setFilesAllowed(false);
        placeholder.textContent = 'Verifying secure connection...';

        // The sender always opens the "input" data channel: the end-to-end check runs over it.
        pc.ondatachannel = (event) => setupDataChannel(session, event.channel);

        // Handle incoming tracks. The video is held back until the sender is verified (see showVideo).
        pc.ontrack = (event) => {
            console.log('Received remote track:', event.track.kind);
            if (event.streams && event.streams[0]) {
                session.pendingStream = event.streams[0];
                showVideo(session); // only shows once the session is verified
            }
        };

        // Handle ICE candidates
        pc.onicecandidate = (event) => {
            if (event.candidate) {
                console.log('Sending ICE candidate to sender');
                sendMessage({
                    Type: 2, // IceCandidate (0=Offer, 1=Answer, 2=IceCandidate, 3=Identify, 4=Error, 5=ConnectionRequest)
                    TargetId: senderId,
                    Candidate: JSON.stringify(event.candidate.toJSON()),
                    SdpMLineIndex: event.candidate.sdpMLineIndex,
                    SdpMid: event.candidate.sdpMid
                });
            }
        };

        // Handle connection state changes
        pc.onconnectionstatechange = () => {
            if (session.dead) return; // event of a replaced connection
            console.log('Connection state:', pc.connectionState);
            updateWebRtcState(pc.connectionState);

            // The sender drops a viewer whose binding does not verify. Losing the connection after we sent our
            // binding but before the sender's arrived is treated as a failed check, not as a network glitch to retry.
            if (session.bindSent && !session.verified && !userDisconnected && ['failed', 'closed', 'disconnected'].includes(pc.connectionState)) {
                failBinding();
                return;
            }

            if (pc.connectionState === 'connected') {
                updateStatus('WebRTC connection established', 'success');
                startBindTimer(session);
                startStatsMonitoring(); // Start collecting statistics
            } else if (pc.connectionState === 'failed' || pc.connectionState === 'closed') {
                stopRecordingOf(session); // cleanup() may not run while signaling is still connected; the file is still offered
                updateStatus('WebRTC connection failed', 'error');
                stopStatsMonitoring(); // Stop collecting statistics
                // Media lost while the signaling socket was already gone: start the normal reconnect path.
                if (pc.connectionState === 'failed' && !ws && !userDisconnected) {
                    attemptReconnection();
                }
            }
        };

        pc.oniceconnectionstatechange = () => {
            console.log('ICE connection state:', pc.iceConnectionState);
        };

        // Set remote description
        await pc.setRemoteDescription({
            type: 'offer',
            sdp: message.Sdp
        });

        // Create answer
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);

        // Send answer to sender
        updateStatus('Sending answer...', 'info');
        sendMessage({
            Type: 1, // Answer (0=Offer, 1=Answer, 2=IceCandidate, 3=Identify, 4=Error, 5=ConnectionRequest)
            TargetId: senderId,
            Sdp: answer.sdp
        });

        // Process any queued ICE candidates that arrived before the peer connection was created
        if (pendingIceCandidates.length > 0) {
            console.log(`Processing ${pendingIceCandidates.length} queued ICE candidates`);
            for (const candidateMessage of pendingIceCandidates) {
                await handleIceCandidate(candidateMessage);
            }
            pendingIceCandidates = [];
        }

    } catch (error) {
        console.error('Error handling offer:', error);
        updateStatus(`Failed to handle offer: ${error.message}`, 'error');
    }
}

async function handleIceCandidate(message) {
    if (!pc) {
        console.log('Queuing ICE candidate (peer connection not ready yet)');
        pendingIceCandidates.push(message);
        return;
    }

    try {
        const candidateData = JSON.parse(message.Candidate);

        // Ensure candidate string has proper format
        let candidateStr = candidateData.candidate;
        if (!candidateStr.startsWith('candidate:')) {
            candidateStr = 'candidate:' + candidateStr;
        }

        const candidate = new RTCIceCandidate({
            candidate: candidateStr,
            sdpMLineIndex: candidateData.sdpMLineIndex,
            sdpMid: candidateData.sdpMid
        });

        await pc.addIceCandidate(candidate);
        console.log('Added ICE candidate');
    } catch (error) {
        console.error('Error adding ICE candidate:', error);
    }
}

function sendMessage(message) {
    if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(message));
    } else {
        console.error('WebSocket is not open, cannot send message');
    }
}

function sendConnectionRequest(serverId) {
    updateStatus(`Requesting connection to ${formatServerId(serverId)}...`, 'info');
    // Send a connection request message to the server
    sendMessage({
        Type: 5, // ConnectionRequest (we'll need to add this type)
        TargetId: serverId
    });
    console.log(`Sent connection request to server: ${serverId}`);
}

function isMediaConnected() {
    return pc !== null && pc.connectionState === 'connected';
}

function disconnect() {
    userDisconnected = true; // explicit disconnect: do not auto-reconnect
    updateStatus('Disconnecting...', 'info');

    // Cancel any pending reconnection attempts
    if (reconnectTimeout) {
        clearTimeout(reconnectTimeout);
        reconnectTimeout = null;
    }
    reconnectAttempts = 0;
    isReconnecting = false;
    resumeToken = null; // a manual disconnect ends the session; the next Connect must use the passkey
    e2eKey = null; // ...which also derives the end-to-end key again

    if (ws) {
        ws.close();
        ws = null;
    }

    cleanup();
}

/**
 * Attempts to reconnect to the signaling server with exponential backoff.
 * Delay doubles with each attempt: 1s, 2s, 4s, 8s, 16s, up to 30s max.
 */
function attemptReconnection() {
    if (isReconnecting) {
        return; // Already attempting to reconnect
    }

    isReconnecting = true;
    reconnectAttempts++;

    // Calculate delay using exponential backoff: delay = initialDelay * 2^(attempts-1)
    const delay = Math.min(
        INITIAL_RECONNECT_DELAY_MS * Math.pow(2, reconnectAttempts - 1),
        MAX_RECONNECT_DELAY_MS
    );

    updateStatus(`Connection lost. Reconnecting in ${Math.round(delay / 1000)}s (attempt ${reconnectAttempts}/${MAX_RECONNECT_ATTEMPTS})...`, 'info');

    reconnectTimeout = setTimeout(async () => {
        try {
            isReconnecting = false;
            console.log(`Reconnection attempt ${reconnectAttempts}/${MAX_RECONNECT_ATTEMPTS}`);
            // connect() reports failures (server still down, token refused) by returning false instead of throwing.
            // No WebSocket exists then, so ws.onclose will never fire; schedule the next attempt here.
            if (!await connect(true)) {
                if (reconnectAttempts < MAX_RECONNECT_ATTEMPTS) {
                    attemptReconnection();
                } else {
                    updateStatus('Max reconnection attempts reached. Please reconnect manually.', 'error');
                }
                return;
            }
            // Success - reset reconnect attempts
            reconnectAttempts = 0;
            updateStatus('Reconnected successfully!', 'success');
        } catch (error) {
            isReconnecting = false;
            console.error('Reconnection failed:', error);
            // The ws.onclose handler will trigger another reconnection attempt if needed
        }
    }, delay);
}

function cleanup() {
    // Stop statistics and ping monitoring
    stopStatsMonitoring();
    stopPingMonitoring();
    stopRecording(); // before the session is dropped, so the host still hears "recording off"
    recordBtn.hidden = true;
    sessions.begin(null); // no live session any more: nothing can be sent, no check or transfer can finish

    if (remoteControl) {
        remoteControl.disable();
        remoteControl = null;
    }
    if (dataChannel) {
        dataChannel.close();
        dataChannel = null;
    }
    hostAllowsPrivacy = false;
    privacyOn = false;
    hostDisplays = [];
    updateControlButton();
    setClipboardAllowed(false);
    setFilesAllowed(false);

    if (pc) {
        pc.close();
        pc = null;
    }

    if (remoteVideo.srcObject) {
        remoteVideo.srcObject.getTracks().forEach(track => track.stop());
        remoteVideo.srcObject = null;
    }

    clearTimeout(bindTimer);

    remoteVideo.style.display = 'none';
    placeholder.textContent = bindFailed ? BIND_FAILED_MESSAGE : PLACEHOLDER_IDLE;
    placeholder.style.display = 'block';
    fullscreenBtn.disabled = true; // Disable fullscreen button

    clientId = null;
    senderId = null;
    pendingIceCandidates = [];
    document.getElementById('clientId').textContent = '-';
    updateWebRtcState('Closed');
}

// ---- Remote control (mouse, keyboard, touch) over the WebRTC data channel ----

function setupDataChannel(session, channel) {
    if (!sessions.isCurrent(session)) { channel.close(); return; }
    session.channel = channel;
    dataChannel = channel;
    // The remote control gets a gated view of the channel: it can send only while its session is the current,
    // live and verified one, however long it lives.
    const gatedChannel = {
        get readyState() { return session.verified && sessions.isCurrent(session) ? channel.readyState : 'closed'; },
        send: (json) => sessions.send(session, json)
    };
    channel.onopen = () => {
        if (!sessions.isCurrent(session)) return;
        remoteControl = new RemoteControl(remoteVideo, gatedChannel);
        // First message on the channel is our e2e binding. The authorization_request follows only after the
        // sender's binding verified (below); nothing else is sent before that.
        sendBind(session).then((sent) => { if (!sent && sessions.isCurrent(session)) failBinding(); },
            () => { if (sessions.isCurrent(session)) failBinding(); });
        updateControlButton();
        // The channel also exists for clipboard-only sessions, so "control available" is only said once the
        // host's authorization_response confirms it.
    };
    channel.onclose = () => {
        if (!sessions.isCurrent(session)) return; // an old channel closing must not tear down the current one
        stopRecordingOf(session);
        if (remoteControl) remoteControl.disable();
        remoteControl = null;
        dataChannel = null;
        hostAllowsControl = false;
        hostAllowsPrivacy = false;
        privacyOn = false;
        hostDisplays = [];
        updateControlButton();
        setClipboardAllowed(false);
        setFilesAllowed(false);
    };
    channel.onmessage = (event) => {
        if (!sessions.isCurrent(session)) return; // message of a replaced connection
        let message;
        try { message = JSON.parse(event.data); } catch { return; }
        if (message.type === 'e2e_bind') {
            if (session.verified) return;
            // Success AND failure only count for the session the check started on.
            verifySenderBind(message, session.pc).then((ok) => sessions.completeVerification(session, ok, () => {
                clearTimeout(bindTimer);
                showVideo(session);
                // The host's authorization_response also tells us whether it shares the clipboard.
                sessions.send(session, { type: 'authorization_request', interactive: false, timestamp: new Date().toISOString() });
            }, failBinding), () => { if (sessions.isCurrent(session)) failBinding(); });
            return;
        }
        // Until the sender's binding verified, whatever else arrives is ignored (it could come from a man in the middle).
        if (!session.verified) return;
        if (message.type === 'authorization_response') {
            setClipboardAllowed(message.clipboard === true);
            setFilesAllowed(message.files === true);
            hostAllowsPrivacy = message.privacy === true;
            // Before the user clicked "Enable control" the answer only tells what the host allows
            // (control and clipboard); afterwards it is the answer to the control request.
            if (!(remoteControl && remoteControl.enabled)) {
                // "requestable": the host asks its user to approve control when we click "Enable control".
                hostAllowsControl = message.authorized === true || message.requestable === true;
                if (hostAllowsControl) updateStatus('Remote control is available. Click "Enable control".', 'success');
                updateControlButton();
            } else {
                remoteControl.handleAuthorizationResponse(message);
                updateStatus(message.authorized ? 'Remote control active'
                    : message.reason === 'timeout' ? 'Nobody at the computer answered the control request'
                    : 'The host did not allow remote control',
                    message.authorized ? 'success' : 'error');
                updateControlButton();
            }
        } else if (message.type === 'approval_pending') {
            // The host's user is being asked (to allow the connection, or the control we requested).
            updateStatus(message.what === 'connection'
                ? 'Waiting for the person at the computer to allow the connection...'
                : 'Waiting for the person at the computer to allow control...', 'info');
        } else if (message.type === 'approval_denied') {
            updateStatus('The person at the computer did not allow the connection', 'error');
        } else if (message.type === 'displays') {
            onDisplays(message);
        } else if (message.type === 'privacy_screen_state') {
            onPrivacyState(message);
        } else if (message.type === 'clipboard') {
            receiveHostClipboard(message);
        } else if (['file_accept', 'file_reject', 'file_done', 'file_error', 'file_cancel'].includes(message.type)) {
            onHostFileMessage(message);
        } else if (message.type === 'authorization_revoked' && remoteControl) {
            remoteControl.disable();
            updateStatus('The host revoked remote control', 'error');
            updateControlButton();
        }
    };
}

function toggleControl() {
    if (!remoteControl) return;
    if (remoteControl.enabled) {
        remoteControl.disable();
        updateStatus('Remote control disabled', 'info');
    } else {
        remoteControl.enable(); // sends authorization_request; input flows after authorization_response
        updateStatus('Requesting remote control...', 'info');
    }
    updateControlButton();
}

// Whether the host allows remote control at all (from its authorization_response).
let hostAllowsControl = false;

function updateControlButton() {
    if (!controlBtn) return;
    controlBtn.disabled = !remoteControl || (!hostAllowsControl && !remoteControl.enabled);
    controlBtn.textContent = remoteControl && remoteControl.enabled ? 'Disable control' : 'Enable control';
    // Only while the host has authorized control: the host rejects the message otherwise.
    if (ctrlAltDelBtn) ctrlAltDelBtn.disabled = !(remoteControl && remoteControl.enabled && remoteControl.authorized);
    updatePrivacyButton();
    updateDisplayPicker();
}

// ---- Session recording (video only, in the browser; the file is offered as a download when it stops) ----
// The host is told ("recording_state") so its banner can say "(recording)". That is honesty towards the person at the host,
// not protection: a viewer decodes the video itself, so recording cannot be prevented.
let recording = null; // { recorder, chunks, session, serverId, startedAt, timer }

function pickRecordingMime() {
    const wanted = ['video/webm;codecs=vp9', 'video/webm;codecs=vp8'];
    return wanted.find((m) => MediaRecorder.isTypeSupported(m)) || ''; // '' = the browser's default
}

function recordingFileName(serverId, now) {
    const p = (n) => String(n).padStart(2, '0');
    const stamp = `${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`;
    return `deskshare-${serverId}-${stamp}.webm`;
}

function toggleRecording() {
    if (recording) { stopRecording(); return; }
    const stream = remoteVideo.srcObject;
    const session = sessions.current;
    if (!stream || !session || !session.verified || typeof MediaRecorder === 'undefined') {
        updateStatus('Recording is not available in this browser or connection', 'error');
        return;
    }
    // Build and start the recorder BEFORE telling the host: if the browser throws here, the host was never told "on",
    // so no "off" is owed. (Nothing can throw after the message below.)
    let recorder;
    const rec = { chunks: [], session, serverId: (e2eKey && e2eKey.serverId) || 'unknown', startedAt: Date.now(), timer: null };
    try {
        // Video tracks only: this app never plays or records audio.
        const videoOnly = new MediaStream(stream.getVideoTracks());
        const mimeType = pickRecordingMime();
        recorder = new MediaRecorder(videoOnly, mimeType ? { mimeType } : undefined);
        rec.recorder = recorder;
        recorder.ondataavailable = (e) => { if (e.data && e.data.size > 0) rec.chunks.push(e.data); };
        // onstop also fires when the tracks end by themselves (disconnect): the file is offered in every case.
        recorder.onstop = () => { if (recording === rec) stopRecording(); else finishRecording(rec); };
        recorder.start(1000);
    } catch (err) {
        updateStatus('Could not start recording: ' + err.message, 'error');
        return;
    }
    // Tell the host: if that does not work (connection gone), do not keep a recording behind its back.
    if (!sessions.send(session, { type: 'recording_state', on: true })) {
        rec.finished = true; // discard: no file for a recording the host was not told about
        recorder.stop();
        updateStatus('Could not tell the host about the recording; not recording', 'error');
        return;
    }
    recording = rec;
    rec.timer = setInterval(updateRecordingIndicator, 500);
    updateRecordingIndicator();
}

function updateRecordingIndicator() {
    if (!recording) {
        recIndicator.hidden = true;
        recordBtn.textContent = 'Record';
        return;
    }
    const s = Math.floor((Date.now() - recording.startedAt) / 1000);
    recIndicator.textContent = `● REC ${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
    recIndicator.hidden = false;
    recordBtn.textContent = 'Stop recording';
}

// Stops the recording only if it belongs to this session (a late event of a replaced connection must not stop a newer one).
function stopRecordingOf(session) {
    if (recording && recording.session === session) stopRecording();
}

// Safe to call anytime (disconnect, peer replacement): does nothing when not recording.
function stopRecording() {
    const rec = recording;
    if (!rec) return;
    recording = null; // first: a second call must not run this twice
    clearInterval(rec.timer);
    updateRecordingIndicator();
    // Tell the host (a no-op when the session is already gone: the host clears the flag itself then).
    sessions.send(rec.session, { type: 'recording_state', on: false });
    if (rec.recorder.state !== 'inactive') rec.recorder.stop(); // onstop -> finishRecording
    else finishRecording(rec);
}

function finishRecording(rec) {
    if (rec.finished) return;
    rec.finished = true;
    if (rec.chunks.length === 0) return;
    const url = URL.createObjectURL(new Blob(rec.chunks, { type: 'video/webm' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = recordingFileName(rec.serverId, new Date(rec.startedAt));
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    updateStatus(`Recording saved as ${a.download}`, 'success');
}

// ---- Privacy screen (the host's local monitor shows black; offered only when the host allowed it and control is active) ----
let hostAllowsPrivacy = false;
let privacyOn = false;

function updatePrivacyButton() {
    if (!privacyBtn) return;
    const controlActive = !!(remoteControl && remoteControl.enabled && remoteControl.authorized);
    if (!controlActive && privacyOn) {
        sessions.send(sessions.current, { type: 'privacy_screen', enabled: false }); // the host needs Control for this message
    }
    if (!controlActive) privacyOn = false;
    privacyBtn.hidden = !(hostAllowsPrivacy && controlActive);
    privacyBtn.textContent = privacyOn ? 'Privacy screen: on' : 'Privacy screen';
}

function togglePrivacy() {
    // The button follows the host's privacy_screen_state answer, not the click. Goes through the session gate like
    // everything else: only to the current, live, verified connection.
    sessions.send(sessions.current, { type: 'privacy_screen', enabled: !privacyOn });
}

function onPrivacyState(message) {
    privacyOn = message.enabled === true;
    updatePrivacyButton();
    if (message.reason && !privacyOn) updateStatus('Privacy screen is off: ' + message.reason, 'info');
}

// ---- Display picker (shown when the host has several displays and control is active; the host refuses a switch without control) ----
let hostDisplays = [];
let hostCurrentDisplay = 0;

function onDisplays(message) {
    hostDisplays = Array.isArray(message.items) ? message.items : [];
    hostCurrentDisplay = Number.isInteger(message.current) ? message.current : 0;
    updateDisplayPicker();
}

function updateDisplayPicker() {
    if (!displayPicker || !displaySelect) return;
    const controlActive = !!(remoteControl && remoteControl.enabled && remoteControl.authorized);
    displayPicker.hidden = !(controlActive && hostDisplays.length > 1);
    displaySelect.replaceChildren(...hostDisplays.map(d => new Option(d.name, d.index, false, d.index === hostCurrentDisplay)));
}

function switchDisplay(index) {
    // The picker follows the host's "displays" answer, so a refused switch (too soon, display gone) puts it back.
    sessions.send(sessions.current, { type: 'switch_display', index: Number(index) });
}

// Ctrl+Alt+Del cannot be typed through the browser; the Windows host injects it as a secure attention sequence.
function sendCtrlAltDel() {
    if (remoteControl && remoteControl.authorized) {
        remoteControl.sendInputMessage({ Type: 'SecureAttention', Timestamp: new Date().toISOString() });
    }
}

// ---- Clipboard sharing (text only, only when the host allowed it) ----

const CLIPBOARD_MAX_BYTES = 60000; // keep in sync with ClipboardSync.MaxTextBytes
const MAX_MESSAGE_BYTES = 64 * 1024; // the host drops larger data channel messages
let hostClipboardText = null;
let clipboardSequence = 0;

// The permission belongs to the current session; the buttons only mirror it.
function setClipboardAllowed(allowed) {
    const session = sessions.current;
    // Telling the host we want its clipboard text is harmless: it only reaches the OS clipboard on a click.
    if (session && allowed && !session.clipboardAllowed) {
        sessions.send(session, { type: 'clipboard_subscribe', enabled: true });
    }
    if (session) session.clipboardAllowed = allowed;
    sendClipboardBtn.hidden = !allowed;
    copyHostClipboardBtn.hidden = !allowed;
    if (!allowed) {
        hostClipboardText = null;
        hideFallbackField();
    }
    copyHostClipboardBtn.disabled = hostClipboardText === null;
}

// Needs a user gesture (the button click), otherwise the browser refuses to read the clipboard.
async function sendClipboard() {
    // Captured before the first await: the text read now is for THIS connection only. If another one replaced it
    // while the browser was reading, sendClipboardText drops it.
    const session = sessions.current;
    if (!session || !session.verified) return;
    let text;
    try {
        text = await navigator.clipboard.readText();
    } catch {
        // No clipboard-read permission (or API missing): let the user paste into a small field instead.
        showFallbackField('input');
        return;
    }
    sendClipboardText(text, session);
}

function sendClipboardText(text, session = sessions.current) {
    if (!session || !session.verified || !sessions.isCurrent(session)) return;
    if (!text) {
        updateStatus('Your clipboard has no text', 'info');
        return;
    }
    const bytes = new TextEncoder().encode(text).length;
    if (bytes > CLIPBOARD_MAX_BYTES) {
        updateStatus(`Clipboard text is too large (${bytes} bytes, max ${CLIPBOARD_MAX_BYTES}); not sent`, 'error');
        return;
    }
    // Same shape as the .NET ClipboardMessage (ContentType 0 = Text).
    const json = JSON.stringify({
        type: 'clipboard',
        data: { ContentType: 0, TextContent: text, SizeBytes: bytes, Timestamp: new Date().toISOString(), SequenceNumber: ++clipboardSequence }
    });
    if (new TextEncoder().encode(json).length > MAX_MESSAGE_BYTES) {
        updateStatus('Clipboard text is too large once encoded; not sent', 'error');
        return;
    }
    if (!sessions.send(session, json)) return;
    updateStatus(`Clipboard sent to the host (${bytes} bytes)`, 'success');
}

function receiveHostClipboard(message) {
    const data = message.data;
    if (copyHostClipboardBtn.hidden || !data || data.ContentType !== 0 || typeof data.TextContent !== 'string' || !data.TextContent) return;
    if (new TextEncoder().encode(data.TextContent).length > CLIPBOARD_MAX_BYTES) {
        updateStatus('The host sent clipboard text that is too large; ignored', 'error');
        return;
    }
    hostClipboardText = data.TextContent;
    copyHostClipboardBtn.disabled = false;
    updateStatus('The host sent clipboard text. Click "Copy host clipboard".', 'info');
}

async function copyHostClipboard() {
    if (hostClipboardText === null) return;
    try {
        await navigator.clipboard.writeText(hostClipboardText);
        hostClipboardText = null;
        copyHostClipboardBtn.disabled = true;
        updateStatus('Host clipboard copied to your clipboard', 'success');
    } catch {
        // writeText refused: show the text selected so the user can press Ctrl+C.
        showFallbackField('output');
    }
}

// Manual fallbacks when the Clipboard API is refused. The fields handle their own events (no document
// listener) and stop key events from bubbling, so remote-control key capture never sees the typing.
let fallbackField = null;

function hideFallbackField() {
    if (!fallbackField) return;
    const field = fallbackField;
    fallbackField = null; // before remove(): removing a focused element fires blur
    field.remove();
}

function showFallbackField(kind) {
    hideFallbackField();
    const field = document.createElement(kind === 'input' ? 'input' : 'textarea');
    if (kind === 'input') {
        field.type = 'text';
        field.placeholder = 'Paste here (Ctrl+V) to send';
        field.addEventListener('paste', (event) => {
            event.preventDefault();
            const text = event.clipboardData ? event.clipboardData.getData('text/plain') : '';
            hideFallbackField();
            sendClipboardText(text);
        });
    } else {
        field.readOnly = true;
        field.rows = 2;
        field.title = 'Press Ctrl+C to copy';
        field.value = hostClipboardText;
        field.addEventListener('copy', (event) => {
            // Put the text on the clipboard ourselves: hiding the field below would otherwise remove the
            // selection before the browser's default copy runs.
            event.clipboardData.setData('text/plain', field.value);
            event.preventDefault();
            hostClipboardText = null;
            copyHostClipboardBtn.disabled = true;
            hideFallbackField();
            updateStatus('Host clipboard copied to your clipboard', 'success');
        });
        updateStatus('Press Ctrl+C to copy the host clipboard', 'info');
    }
    field.addEventListener('keydown', (event) => {
        event.stopPropagation();
        if (event.key === 'Escape') hideFallbackField();
    });
    field.addEventListener('keyup', (event) => event.stopPropagation());
    field.addEventListener('blur', hideFallbackField);
    fallbackField = field;
    (kind === 'input' ? sendClipboardBtn : copyHostClipboardBtn).after(field);
    field.focus();
    if (kind !== 'input') field.select();
}

// ---- File transfer to the host (viewer -> host only, and only when the host allowed it) ----

const FILE_CHUNK_BYTES = 45000;          // keep in sync with FileReceiver.MaxChunkBytes (base64 must stay < 64 KB)
const FILE_BUFFER_HIGH = 1024 * 1024;    // stop queuing chunks above this many buffered bytes
const FILE_REPLY_TIMEOUT_MS = 30000;
let fileTransfer = null; // { id, replies, waiter, cancelled } while a file is being sent (also on its session)

function setFilesAllowed(allowed) {
    if (sessions.current) sessions.current.filesAllowed = allowed;
    sendFileBtn.hidden = !allowed;
    if (!allowed && fileTransfer) {
        fileTransfer.cancelled = true;
        onHostFileMessage({ type: 'file_error', id: fileTransfer.id, reason: 'connection_closed' }); // wake the sender
    }
}

function onSendFileClick() {
    if (fileTransfer) {
        fileTransfer.cancelled = true; // second click cancels
        updateStatus('Cancelling file transfer...', 'info');
    } else {
        fileInput.click();
    }
}

fileInput.addEventListener('change', () => {
    const file = fileInput.files[0];
    fileInput.value = ''; // allow picking the same file again
    if (file) sendFile(file);
});

// Drop a file on the video area. Always cancel the default, or the browser would open the file in this tab.
videoContainer.addEventListener('dragover', (event) => {
    if (event.dataTransfer && Array.from(event.dataTransfer.types).includes('Files')) event.preventDefault();
});
videoContainer.addEventListener('drop', (event) => {
    if (!event.dataTransfer || event.dataTransfer.files.length === 0) return;
    event.preventDefault();
    sendFile(event.dataTransfer.files[0]);
});

function onHostFileMessage(message) {
    const t = fileTransfer;
    if (!t || message.id !== t.id) return;
    if (t.waiter) {
        const wake = t.waiter;
        t.waiter = null;
        wake(message);
    } else {
        t.replies.push(message);
    }
}

function nextHostReply(t) {
    return new Promise((resolve, reject) => {
        if (t.replies.length) { resolve(t.replies.shift()); return; }
        const timer = setTimeout(() => { t.waiter = null; reject(new Error('The host did not answer in time')); }, FILE_REPLY_TIMEOUT_MS);
        t.waiter = (message) => { clearTimeout(timer); resolve(message); };
    });
}

// bufferedAmountLowThreshold + event, with a short poll as safety net (the event does not fire on a closed channel).
// Waits until at most `limit` bytes are buffered. `stop` runs on every wake-up: it throws to abort (cancel, host error)
// or returns true to leave early. Fails only when the buffer has not shrunk for FILE_REPLY_TIMEOUT_MS (a slow link is fine).
async function waitForChannelDrain(channel, limit, stop) {
    channel.bufferedAmountLowThreshold = FILE_BUFFER_HIGH / 4;
    let lowest = Infinity, lastProgress = Date.now();
    while (channel.readyState === 'open' && channel.bufferedAmount > limit) {
        if (stop()) return;
        if (channel.bufferedAmount < lowest) {
            lowest = channel.bufferedAmount;
            lastProgress = Date.now();
        } else if (Date.now() - lastProgress >= FILE_REPLY_TIMEOUT_MS) {
            throw new Error('The connection to the host stalled');
        }
        await new Promise((resolve) => {
            const done = () => { channel.removeEventListener('bufferedamountlow', done); resolve(); };
            channel.addEventListener('bufferedamountlow', done, { once: true });
            setTimeout(done, 200);
        });
    }
}

function newGuid() {
    const b = crypto.getRandomValues(new Uint8Array(16));
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

function bytesToBase64(bytes) {
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
}

async function sendFile(file) {
    // The whole transfer belongs to this session: when a new connection replaces it, the transfer is cancelled
    // (PeerSession.kill) and every step below re-checks it before it sends.
    const session = sessions.current;
    if (!session || !session.verified || !session.filesAllowed || !session.channel || session.channel.readyState !== 'open') {
        updateStatus('The host does not accept files', 'error');
        return;
    }
    if (fileTransfer) {
        updateStatus('A file is already being sent', 'info');
        return;
    }

    const t = fileTransfer = session.transfer = { id: newGuid(), replies: [], waiter: null, cancelled: false };
    session.onKilled = () => onHostFileMessage({ type: 'file_error', id: t.id, reason: 'connection_closed' }); // wake the loop
    const send = (obj) => { if (!sessions.send(session, obj)) throw new Error('The connection to the host is closed'); };
    const abortIfStopped = () => {
        if (t.cancelled) throw new Error('cancelled');
        if (t.replies.length) throw new Error(`The host stopped the transfer (${t.replies[0].reason || t.replies[0].type})`);
        if (!sessions.isCurrent(session) || session.channel.readyState !== 'open') throw new Error('The connection to the host is closed');
    };

    try {
        send({ type: 'file_offer', id: t.id, name: file.name, size: file.size });
        updateStatus(`Offering ${file.name} to the host...`, 'info');
        const answer = await nextHostReply(t);
        if (answer.type !== 'file_accept') throw new Error(`The host refused ${file.name}: ${answer.reason || answer.type}`);

        const hash = new Sha256();
        let seq = 0, sent = 0, lastPercent = -1;
        for (let offset = 0; offset < file.size; offset += FILE_CHUNK_BYTES) {
            abortIfStopped();
            await waitForChannelDrain(session.channel, FILE_BUFFER_HIGH, abortIfStopped);
            abortIfStopped();
            const bytes = new Uint8Array(await file.slice(offset, offset + FILE_CHUNK_BYTES).arrayBuffer());
            hash.update(bytes);
            send({ type: 'file_chunk', id: t.id, seq: seq++, data: bytesToBase64(bytes) });
            sent += bytes.length;
            const percent = Math.floor(sent * 100 / file.size);
            if (percent !== lastPercent) {
                lastPercent = percent;
                updateStatus(`Sending ${file.name} ${percent}% (click "Send file" to cancel)`, 'info');
            }
        }

        abortIfStopped();
        send({ type: 'file_end', id: t.id, sha256: hash.hex() });
        // The reply clock starts only once file_end has left; a host reply (done or error) ends the wait early.
        await waitForChannelDrain(session.channel, 0, () => { if (t.replies.length) return true; abortIfStopped(); });
        const done = await nextHostReply(t);
        if (done.type !== 'file_done') throw new Error(`The host could not save ${file.name}: ${done.reason || done.type}`);
        updateStatus(`Sent ${file.name} (saved on the host as ${done.savedAs})`, 'success');
    } catch (error) {
        try { send({ type: 'file_cancel', id: t.id }); } catch { /* connection went away or was replaced: nothing to tell */ }
        if (error.message === 'cancelled') updateStatus(`Cancelled sending ${file.name}`, 'info');
        else updateStatus(error.message, 'error');
    } finally {
        if (fileTransfer === t) fileTransfer = null;
        session.transfer = null;
    }
}

// Incremental SHA-256 (WebCrypto can only hash a whole buffer, which would mean holding a 2 GB file in memory).
class Sha256 {
    static K = new Uint32Array([
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2]);

    constructor() {
        this.h = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
        this.block = new Uint8Array(64);
        this.blockLength = 0;
        this.total = 0;
        this.w = new Uint32Array(64);
    }

    update(bytes) {
        this.total += bytes.length;
        let i = 0;
        if (this.blockLength > 0) {
            const take = Math.min(64 - this.blockLength, bytes.length);
            this.block.set(bytes.subarray(0, take), this.blockLength);
            this.blockLength += take;
            i = take;
            if (this.blockLength === 64) { this.compress(this.block, 0); this.blockLength = 0; }
        }
        for (; i + 64 <= bytes.length; i += 64) this.compress(bytes, i);
        if (i < bytes.length) {
            this.block.set(bytes.subarray(i), 0);
            this.blockLength = bytes.length - i;
        }
    }

    compress(data, pos) {
        const w = this.w, K = Sha256.K, h = this.h;
        for (let t = 0; t < 16; t++, pos += 4) {
            w[t] = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
        }
        for (let t = 16; t < 64; t++) {
            const a = w[t - 15], b = w[t - 2];
            const s0 = ((a >>> 7) | (a << 25)) ^ ((a >>> 18) | (a << 14)) ^ (a >>> 3);
            const s1 = ((b >>> 17) | (b << 15)) ^ ((b >>> 19) | (b << 13)) ^ (b >>> 10);
            w[t] = (w[t - 16] + s0 + w[t - 7] + s1) | 0;
        }
        let a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (let t = 0; t < 64; t++) {
            const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
            const ch = (e & f) ^ (~e & g);
            const t1 = (hh + S1 + ch + K[t] + w[t]) | 0;
            const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
            const maj = (a & b) ^ (a & c) ^ (b & c);
            const t2 = (S0 + maj) | 0;
            hh = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }

    hex() {
        const bits = this.total * 8;
        const pad = new Uint8Array(((this.blockLength < 56) ? 64 : 128) - this.blockLength);
        pad[0] = 0x80;
        const view = new DataView(pad.buffer);
        view.setUint32(pad.length - 8, Math.floor(bits / 0x100000000));
        view.setUint32(pad.length - 4, bits >>> 0);
        const total = this.total;
        this.update(pad);
        this.total = total;
        return Array.from(this.h, (x) => (x >>> 0).toString(16).padStart(8, '0')).join('');
    }
}

// Handle page unload
window.addEventListener('beforeunload', () => {
    disconnect();
});

/**
 * Starts monitoring WebRTC statistics.
 * Polls getStats() API every second to track:
 * - Bitrate (video data rate)
 * - Framerate (FPS)
 * - Packet loss
 * - Jitter (network stability)
 * - Round-trip time (latency)
 */
function startStatsMonitoring() {
    if (statsInterval) {
        clearInterval(statsInterval);
    }

    let lastBytesReceived = 0;
    let lastTimestamp = Date.now();
    let lastFramesDecoded = 0;

    statsInterval = setInterval(async () => {
        if (!pc || pc.connectionState !== 'connected') {
            stopStatsMonitoring();
            return;
        }

        try {
            const stats = await pc.getStats();
            let bitrateKbps = 0;
            let fps = 0;
            let packetLoss = 0;
            let jitter = 0;
            let rtt = 0;
            let resolution = '-';
            let selectedPairId = null;
            const candidates = {};
            const pairs = {};

            stats.forEach(report => {
                // Inbound RTP stream stats (video we're receiving)
                if (report.type === 'inbound-rtp' && report.kind === 'video') {
                    const now = Date.now();
                    const timeDelta = (now - lastTimestamp) / 1000; // seconds

                    if (report.bytesReceived && lastBytesReceived > 0) {
                        const bytesDelta = report.bytesReceived - lastBytesReceived;
                        bitrateKbps = Math.round((bytesDelta * 8) / (timeDelta * 1000));
                    }

                    if (report.framesDecoded && lastFramesDecoded > 0) {
                        const framesDelta = report.framesDecoded - lastFramesDecoded;
                        fps = Math.round(framesDelta / timeDelta);
                    }

                    if (report.packetsLost && report.packetsReceived) {
                        const totalPackets = report.packetsLost + report.packetsReceived;
                        packetLoss = (report.packetsLost / totalPackets * 100).toFixed(2);
                    }

                    if (report.jitter) {
                        jitter = (report.jitter * 1000).toFixed(2); // Convert to ms
                    }

                    if (report.frameWidth && report.frameHeight) {
                        resolution = `${report.frameWidth}x${report.frameHeight}`;
                    }

                    lastBytesReceived = report.bytesReceived || lastBytesReceived;
                    lastFramesDecoded = report.framesDecoded || lastFramesDecoded;
                    lastTimestamp = now;
                }

                // Candidate pair stats (for RTT/latency)
                if (report.type === 'candidate-pair' && report.state === 'succeeded') {
                    if (report.currentRoundTripTime) {
                        rtt = Math.round(report.currentRoundTripTime * 1000); // Convert to ms
                    }
                }

                // Collected so the route (direct vs relay) can be resolved after the loop.
                if (report.type === 'local-candidate' || report.type === 'remote-candidate') {
                    candidates[report.id] = report;
                }
                if (report.type === 'candidate-pair') {
                    pairs[report.id] = report;
                }
                if (report.type === 'transport' && report.selectedCandidatePairId) {
                    selectedPairId = report.selectedCandidatePairId;
                }
            });

            // Firefox has no transport.selectedCandidatePairId; it marks the pair with the non-standard
            // `selected` flag instead. Several pairs can be nominated, so that is only the last resort.
            const selectedPair = pairs[selectedPairId] ||
                Object.values(pairs).find(p => p.selected) ||
                Object.values(pairs).find(p => p.nominated && p.state === 'succeeded');
            let connectionType = '-';
            if (selectedPair) {
                const local = candidates[selectedPair.localCandidateId];
                const remote = candidates[selectedPair.remoteCandidateId];
                const isRelay = (local && local.candidateType === 'relay') ||
                    (remote && remote.candidateType === 'relay');
                // The relay protocol (udp/tcp/tls) is reported on the local candidate.
                const relayProtocol = local && local.relayProtocol ? ` (${local.relayProtocol})` : '';
                connectionType = isRelay ? `relay${relayProtocol}` : 'direct';
            }

            // Update UI with statistics
            updateStatsDisplay(bitrateKbps, fps, packetLoss, jitter, rtt);
            document.getElementById('connectionType').textContent = connectionType;
            document.getElementById('resolution').textContent = resolution;
        } catch (error) {
            console.error('Error getting WebRTC stats:', error);
        }
    }, 1000); // Update every second
}

/**
 * Stops monitoring WebRTC statistics.
 */
function stopStatsMonitoring() {
    if (statsInterval) {
        clearInterval(statsInterval);
        statsInterval = null;
    }

    // Reset stats display
    document.getElementById('bitrate').textContent = '- Kbps';
    document.getElementById('fps').textContent = '- fps';
    document.getElementById('packetLoss').textContent = '-%';
    document.getElementById('latency').textContent = '- ms';
    document.getElementById('connectionType').textContent = '-';
    document.getElementById('resolution').textContent = '-';
}

/**
 * Updates the statistics display in the UI.
 */
function updateStatsDisplay(bitrate, fps, packetLoss, jitter, rtt) {
    document.getElementById('bitrate').textContent = bitrate > 0 ? `${bitrate} Kbps` : '- Kbps';
    document.getElementById('fps').textContent = fps > 0 ? `${fps} fps` : '- fps';
    document.getElementById('packetLoss').textContent = packetLoss > 0 ? `${packetLoss}%` : '0%';
    document.getElementById('latency').textContent = rtt > 0 ? `${rtt} ms` : '- ms';
}

/**
 * Starts monitoring server liveness by sending periodic ping messages.
 * If server doesn't respond with pong within timeout, disconnects.
 * @param {string} serverId - ID of the server to ping
 */
function startPingMonitoring(serverId) {
    if (pingInterval) {
        clearInterval(pingInterval);
    }

    lastPongTime = Date.now(); // Reset last pong time

    pingInterval = setInterval(() => {
        // Check if we've received a pong recently
        const timeSinceLastPong = Date.now() - lastPongTime;

        if (timeSinceLastPong > PING_TIMEOUT_MS) {
            // Server is not responding - consider it dead
            console.error('Server ping timeout - no response');
            if (isMediaConnected()) {
                // Keep the running video; closing the dead socket lets ws.onclose take the signaling-offline path.
                stopPingMonitoring();
                if (ws) ws.close();
                return;
            }
            updateStatus('Server not responding - disconnecting', 'error');
            disconnect();
            return;
        }

        // Send ping to server
        sendMessage({
            Type: 6, // Ping
            TargetId: serverId
        });
    }, PING_INTERVAL_MS);
}

/**
 * Stops monitoring server liveness.
 */
function stopPingMonitoring() {
    if (pingInterval) {
        clearInterval(pingInterval);
        pingInterval = null;
    }
}

/**
 * Toggles fullscreen mode for the video container.
 * Uses the Fullscreen API which is supported in all modern browsers.
 */
/**
 * "Hide panels" / "Show panels": with the panels hidden only the title and the shared screen stay
 * (see body.focus in index.html).
 */
function setFocus(on) {
    document.body.classList.toggle('focus', on);
}

function toggleFullscreen() {
    if (!document.fullscreenElement) {
        // Enter fullscreen
        if (videoContainer.requestFullscreen) {
            videoContainer.requestFullscreen();
        } else if (videoContainer.webkitRequestFullscreen) { // Safari
            videoContainer.webkitRequestFullscreen();
        } else if (videoContainer.msRequestFullscreen) { // IE11
            videoContainer.msRequestFullscreen();
        }
        fullscreenBtn.textContent = 'Exit Fullscreen';
    } else {
        // Exit fullscreen
        if (document.exitFullscreen) {
            document.exitFullscreen();
        } else if (document.webkitExitFullscreen) { // Safari
            document.webkitExitFullscreen();
        } else if (document.msExitFullscreen) { // IE11
            document.msExitFullscreen();
        }
        fullscreenBtn.textContent = 'Fullscreen';
    }
}

// Listen for fullscreen changes (ESC key or F11)
document.addEventListener('fullscreenchange', () => {
    if (document.fullscreenElement) {
        fullscreenBtn.textContent = 'Exit Fullscreen';
    } else {
        fullscreenBtn.textContent = 'Fullscreen';
    }
});

// When served by the SignalingServer itself (not opened from disk), use that host and hide the field: users of the
// hosted viewer never need another server, the field only matters for a local copy opened from disk (development).
if (location.protocol.startsWith('http')) {
    const wsScheme = location.protocol === 'https:' ? 'wss' : 'ws';
    const signalingInput = document.getElementById('signalingUrl');
    signalingInput.value = `${wsScheme}://${location.host}/signal`;
    signalingInput.closest('.control-group').style.display = 'none'; // not .hidden: mobile.css sets display: flex
}
