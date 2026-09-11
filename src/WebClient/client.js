// WebSocket and WebRTC connection management
let ws = null;
let pc = null;
let clientId = null;
let senderId = null;
let pendingIceCandidates = []; // Queue for ICE candidates that arrive before offer
let statsInterval = null; // Interval for polling WebRTC stats
let pingInterval = null; // Interval for sending ping messages
let lastPongTime = Date.now(); // Timestamp of last received pong
let reconnectAttempts = 0; // Number of reconnection attempts
let reconnectTimeout = null; // Timeout for reconnection delay
let isReconnecting = false; // Flag to prevent multiple simultaneous reconnections

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

function updateStatus(message, type = 'info') {
    statusDiv.style.display = 'block';
    statusDiv.className = `status ${type}`;
    statusText.textContent = message;
    console.log(`[${type.toUpperCase()}] ${message}`);
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

async function authenticate() {
    const baseUrl = getBaseUrl();
    const serverId = document.getElementById('serverId').value;
    const passkey = (document.getElementById('passkey')?.value || '').replace(/-/g, '');

    if (!serverId) {
        updateStatus('Server ID is required', 'error');
        return null;
    }

    clientId = clientId || crypto.randomUUID();

    const timestamp = new Date().toISOString();
    const nonce = crypto.randomUUID();
    const body = {
        ServerId: serverId,
        Passkey: passkey,
        ClientId: clientId,
        Timestamp: timestamp,
        Nonce: nonce,
        Signature: await signAuthRequest(serverId, passkey, timestamp, nonce)
    };

    updateStatus('Authenticating...', 'info');

    const response = await fetch(`${baseUrl}/authenticate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
    });

    if (!response.ok) {
        const errorText = response.status === 401 ? 'Invalid passkey or server ID' : `Authentication failed (${response.status})`;
        updateStatus(errorText, 'error');
        return null;
    }

    return await response.json();
}

async function connect() {
    try {
        const authResult = await authenticate();
        if (!authResult) return;

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
                    const serverId = document.getElementById('serverId').value;
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
                        if (message.SenderId === senderId) {
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
            updateConnectionState('Disconnected');
            connectBtn.disabled = false;
            disconnectBtn.disabled = true;
            cleanup();

            // Attempt reconnection if it wasn't a manual disconnect
            if (!event.wasClean && reconnectAttempts < MAX_RECONNECT_ATTEMPTS) {
                attemptReconnection();
            } else if (reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
                updateStatus('Max reconnection attempts reached. Please reconnect manually.', 'error');
            } else {
                updateStatus('Disconnected from signaling server', 'info');
            }
        };

    } catch (error) {
        console.error('Connection error:', error);
        updateStatus(`Connection failed: ${error.message}`, 'error');
        updateConnectionState('Error');
    }
}

async function handleOffer(message) {
    try {
        updateStatus('Received offer, creating peer connection...', 'info');

        // Create peer connection
        pc = new RTCPeerConnection(config);

        // Handle incoming tracks
        pc.ontrack = (event) => {
            console.log('Received remote track:', event.track.kind);
            if (event.streams && event.streams[0]) {
                remoteVideo.srcObject = event.streams[0];
                placeholder.style.display = 'none';
                remoteVideo.style.display = 'block';
                fullscreenBtn.disabled = false; // Enable fullscreen button
                updateStatus('Receiving video stream', 'success');
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
            console.log('Connection state:', pc.connectionState);
            updateWebRtcState(pc.connectionState);

            if (pc.connectionState === 'connected') {
                updateStatus('WebRTC connection established', 'success');
                startStatsMonitoring(); // Start collecting statistics
            } else if (pc.connectionState === 'failed' || pc.connectionState === 'closed') {
                updateStatus('WebRTC connection failed', 'error');
                stopStatsMonitoring(); // Stop collecting statistics
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
    updateStatus(`Requesting connection to ${serverId}...`, 'info');
    // Send a connection request message to the server
    sendMessage({
        Type: 5, // ConnectionRequest (we'll need to add this type)
        TargetId: serverId
    });
    console.log(`Sent connection request to server: ${serverId}`);
}

function disconnect() {
    updateStatus('Disconnecting...', 'info');

    // Cancel any pending reconnection attempts
    if (reconnectTimeout) {
        clearTimeout(reconnectTimeout);
        reconnectTimeout = null;
    }
    reconnectAttempts = 0;
    isReconnecting = false;

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
            await connect();
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

    if (pc) {
        pc.close();
        pc = null;
    }

    if (remoteVideo.srcObject) {
        remoteVideo.srcObject.getTracks().forEach(track => track.stop());
        remoteVideo.srcObject = null;
    }

    remoteVideo.style.display = 'none';
    placeholder.style.display = 'block';
    fullscreenBtn.disabled = true; // Disable fullscreen button

    clientId = null;
    senderId = null;
    pendingIceCandidates = [];
    document.getElementById('clientId').textContent = '-';
    updateWebRtcState('Closed');
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
            });

            // Update UI with statistics
            updateStatsDisplay(bitrateKbps, fps, packetLoss, jitter, rtt);
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
