/**
 * WebRTC Client with Remote Control Support
 *
 * This is a simplified version of client.js with DataChannel and RemoteControl integration.
 * For production, you would merge this with the full client.js implementation.
 */

// WebSocket and WebRTC connection management
let ws = null;
let pc = null;
let dataChannel = null;
let remoteControl = null;
let clientId = null;
let senderId = null;
let statsInterval = null;

const config = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        { urls: 'stun:stun1.l.google.com:19302' }
    ]
};

// UI Elements
const connectBtn = document.getElementById('connectBtn');
const disconnectBtn = document.getElementById('disconnectBtn');
const fullscreenBtn = document.getElementById('fullscreenBtn');
const controlBtn = document.getElementById('controlBtn');
const remoteVideo = document.getElementById('remoteVideo');
const videoContainer = document.getElementById('videoContainer');
const placeholder = document.getElementById('placeholder');
const controlIndicator = document.getElementById('controlIndicator');
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

function updateDataChannelState(state) {
    const elem = document.getElementById('dataChannelState');
    if (elem) {
        elem.textContent = state;
        elem.style.color = state === 'open' ? '#4caf50' : '#f44336';
    }
}

function updateControlStatus(status, authorized = false) {
    const elem = document.getElementById('controlStatus');
    if (elem) {
        elem.textContent = status;
        elem.style.color = authorized ? '#4caf50' : (status === 'Disabled' ? '#666' : '#ff9800');
    }
}

async function connect() {
    const signalingUrl = document.getElementById('signalingUrl').value;
    senderId = document.getElementById('serverId').value.trim();

    if (!signalingUrl) {
        updateStatus('Please enter signaling server URL', 'error');
        return;
    }

    updateStatus('Connecting to signaling server...', 'info');

    try {
        // Connect to WebSocket signaling server
        ws = new WebSocket(signalingUrl);

        ws.onopen = async () => {
            console.log('[WebSocket] Connected');
            updateConnectionState('Connected');
        };

        ws.onmessage = async (event) => {
            const message = JSON.parse(event.data);
            console.log('[WebSocket] Received message:', message.Type);

            switch (message.Type) {
                case 'Identify':
                    clientId = message.TargetId;
                    document.getElementById('clientId').textContent = clientId.substring(0, 8) + '...';
                    updateStatus('Connected! Waiting for offer from server...', 'success');

                    // If no server ID specified, request offer from any server
                    if (!senderId) {
                        sendMessage({ Type: 'RequestOffer', SenderId: clientId });
                    }
                    break;

                case 'Offer':
                    await handleOffer(message);
                    break;

                case 'IceCandidate':
                    await handleIceCandidate(message);
                    break;

                case 'Error':
                    updateStatus(`Error: ${message.ErrorMessage}`, 'error');
                    break;
            }
        };

        ws.onerror = (error) => {
            console.error('[WebSocket] Error:', error);
            updateStatus('WebSocket error occurred', 'error');
        };

        ws.onclose = () => {
            console.log('[WebSocket] Disconnected');
            updateConnectionState('Disconnected');
            updateStatus('Disconnected from signaling server', 'error');
        };

    } catch (error) {
        console.error('[Connection] Error:', error);
        updateStatus(`Connection failed: ${error.message}`, 'error');
    }
}

async function handleOffer(message) {
    console.log('[WebRTC] Received offer');
    senderId = message.SenderId;

    // Create peer connection
    pc = new RTCPeerConnection(config);

    // Setup data channel handler (for channels created by remote peer)
    pc.ondatachannel = (event) => {
        console.log('[DataChannel] Received from remote peer');
        setupDataChannel(event.channel);
    };

    // Handle incoming tracks (video)
    pc.ontrack = (event) => {
        console.log('[WebRTC] Received track:', event.track.kind);
        if (event.track.kind === 'video') {
            remoteVideo.srcObject = event.streams[0];
            placeholder.style.display = 'none';
            fullscreenBtn.disabled = false;
            controlBtn.disabled = false;
            updateStatus('Video stream connected!', 'success');
        }
    };

    // Handle connection state changes
    pc.onconnectionstatechange = () => {
        console.log('[WebRTC] Connection state:', pc.connectionState);
        updateWebRtcState(pc.connectionState);

        if (pc.connectionState === 'connected') {
            updateStatus('WebRTC connected! Video streaming...', 'success');
            startStatsInterval();
        } else if (pc.connectionState === 'failed' || pc.connectionState === 'closed') {
            updateStatus('WebRTC connection failed', 'error');
            stopStatsInterval();
        }
    };

    // Handle ICE candidates
    pc.onicecandidate = (event) => {
        if (event.candidate) {
            console.log('[ICE] Sending candidate');
            sendMessage({
                Type: 'IceCandidate',
                SenderId: clientId,
                TargetId: senderId,
                Candidate: JSON.stringify({
                    candidate: event.candidate.candidate,
                    sdpMid: event.candidate.sdpMid,
                    sdpMLineIndex: event.candidate.sdpMLineIndex
                }),
                SdpMid: event.candidate.sdpMid,
                SdpMLineIndex: event.candidate.sdpMLineIndex
            });
        }
    };

    // Set remote description (offer)
    await pc.setRemoteDescription({ type: 'offer', sdp: message.Sdp });

    // Create answer
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);

    // Send answer
    sendMessage({
        Type: 'Answer',
        SenderId: clientId,
        TargetId: senderId,
        Sdp: answer.sdp
    });

    console.log('[WebRTC] Answer sent');
    connectBtn.disabled = true;
    disconnectBtn.disabled = false;
}

async function handleIceCandidate(message) {
    if (pc && message.Candidate) {
        try {
            const candidate = JSON.parse(message.Candidate);
            await pc.addIceCandidate(new RTCIceCandidate(candidate));
            console.log('[ICE] Candidate added');
        } catch (error) {
            console.error('[ICE] Error adding candidate:', error);
        }
    }
}

function setupDataChannel(channel) {
    dataChannel = channel;

    dataChannel.onopen = () => {
        console.log('[DataChannel] Opened');
        updateDataChannelState('open');
        updateStatus('Data channel ready! You can enable remote control.', 'success');
    };

    dataChannel.onclose = () => {
        console.log('[DataChannel] Closed');
        updateDataChannelState('closed');
        if (remoteControl) {
            remoteControl.disable();
            updateControlUI(false);
        }
    };

    dataChannel.onerror = (error) => {
        console.error('[DataChannel] Error:', error);
        updateDataChannelState('error');
    };

    dataChannel.onmessage = (event) => {
        try {
            const message = JSON.parse(event.data);
            handleDataChannelMessage(message);
        } catch (error) {
            console.error('[DataChannel] Error parsing message:', error);
        }
    };
}

function handleDataChannelMessage(message) {
    console.log('[DataChannel] Received:', message.type);

    switch (message.type) {
        case 'authorization_response':
            if (remoteControl) {
                remoteControl.handleAuthorizationResponse(message);
                updateControlStatus(
                    message.authorized ? 'Authorized' : 'Denied',
                    message.authorized
                );

                if (message.authorized) {
                    updateStatus('Remote control authorized!', 'success');
                    videoContainer.classList.add('control-active');
                    controlIndicator.classList.add('active');
                } else {
                    updateStatus('Remote control denied by server', 'error');
                }
            }
            break;

        case 'authorization_revoked':
            updateStatus('Remote control revoked by server', 'warning');
            if (remoteControl) {
                remoteControl.disable();
                updateControlUI(false);
            }
            break;
    }
}

function toggleControl() {
    if (!dataChannel || dataChannel.readyState !== 'open') {
        updateStatus('Data channel not ready', 'error');
        return;
    }

    if (!remoteControl) {
        // Create and enable remote control
        remoteControl = new RemoteControl(remoteVideo, dataChannel);
        remoteControl.enable();
        updateControlUI(true);
        updateControlStatus('Requesting...', false);
        updateStatus('Requesting remote control authorization...', 'info');
    } else if (remoteControl.enabled) {
        // Disable remote control
        remoteControl.disable();
        updateControlUI(false);
        updateControlStatus('Disabled', false);
        updateStatus('Remote control disabled', 'info');
    } else {
        // Re-enable
        remoteControl.enable();
        updateControlUI(true);
        updateControlStatus('Requesting...', false);
        updateStatus('Requesting remote control authorization...', 'info');
    }
}

function updateControlUI(enabled) {
    if (enabled) {
        controlBtn.textContent = 'Disable Control';
        controlBtn.classList.remove('warning');
        controlBtn.classList.add('success');
    } else {
        controlBtn.textContent = 'Enable Control';
        controlBtn.classList.remove('success');
        controlBtn.classList.add('warning');
        videoContainer.classList.remove('control-active');
        controlIndicator.classList.remove('active');
    }
}

function disconnect() {
    if (remoteControl) {
        remoteControl.disable();
        remoteControl = null;
    }

    if (dataChannel) {
        dataChannel.close();
        dataChannel = null;
    }

    if (pc) {
        pc.close();
        pc = null;
    }

    if (ws) {
        ws.close();
        ws = null;
    }

    stopStatsInterval();

    remoteVideo.srcObject = null;
    placeholder.style.display = 'block';
    connectBtn.disabled = false;
    disconnectBtn.disabled = true;
    fullscreenBtn.disabled = true;
    controlBtn.disabled = true;

    updateConnectionState('Disconnected');
    updateWebRtcState('Closed');
    updateDataChannelState('closed');
    updateControlStatus('Disabled', false);
    updateStatus('Disconnected', 'info');
}

function toggleFullscreen() {
    if (!document.fullscreenElement) {
        videoContainer.requestFullscreen().catch(err => {
            console.error('Error entering fullscreen:', err);
        });
    } else {
        document.exitFullscreen();
    }
}

function sendMessage(message) {
    if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(message));
    } else {
        console.error('[WebSocket] Cannot send message: not connected');
    }
}

function startStatsInterval() {
    stopStatsInterval();
    statsInterval = setInterval(updateStats, 1000);
}

function stopStatsInterval() {
    if (statsInterval) {
        clearInterval(statsInterval);
        statsInterval = null;
    }
}

async function updateStats() {
    if (!pc) return;

    try {
        const stats = await pc.getStats();
        let bitrate = 0;
        let fps = 0;
        let packetsLost = 0;
        let packetsReceived = 0;
        let rtt = 0;

        stats.forEach(report => {
            if (report.type === 'inbound-rtp' && report.kind === 'video') {
                if (report.bytesReceived) {
                    bitrate = Math.round(report.bytesReceived * 8 / 1000);
                }
                if (report.framesPerSecond) {
                    fps = Math.round(report.framesPerSecond);
                }
                if (report.packetsLost !== undefined && report.packetsReceived !== undefined) {
                    packetsLost = report.packetsLost;
                    packetsReceived = report.packetsReceived;
                }
            }
            if (report.type === 'candidate-pair' && report.state === 'succeeded') {
                if (report.currentRoundTripTime !== undefined) {
                    rtt = Math.round(report.currentRoundTripTime * 1000);
                }
            }
        });

        document.getElementById('bitrate').textContent = `${bitrate} Kbps`;
        document.getElementById('fps').textContent = `${fps} fps`;

        const packetLossPercent = packetsReceived > 0
            ? ((packetsLost / (packetsLost + packetsReceived)) * 100).toFixed(2)
            : '0.00';
        document.getElementById('packetLoss').textContent = `${packetLossPercent}%`;
        document.getElementById('latency').textContent = `${rtt} ms`;

        // Update remote control stats
        if (remoteControl) {
            const controlStats = remoteControl.getStatistics();
            document.getElementById('mouseMoveSent').textContent = controlStats.mouseMoveSent;
            document.getElementById('mouseClicksSent').textContent = controlStats.mouseClicksSent;
            document.getElementById('keysSent').textContent = controlStats.keysSent;
            document.getElementById('messagesDropped').textContent = controlStats.messagesDropped;
        }

    } catch (error) {
        console.error('[Stats] Error:', error);
    }
}

// Initialize
console.log('[Client] Ready');
updateStatus('Ready to connect', 'info');
