/**
 * RemoteControl Module
 *
 * Handles mouse and keyboard event capture from the video element
 * and sends them to the remote desktop via WebRTC DataChannel.
 *
 * For junior developers:
 * - Mouse coordinates are normalized to 0-1 range for resolution independence
 * - We capture events on the video element, not the whole page
 * - preventDefault() stops normal browser behavior (like context menu)
 * - DataChannel must be open before sending messages
 */

class RemoteControl {
    /**
     * Creates a new RemoteControl instance
     * @param {HTMLVideoElement} videoElement - The video element to capture events from
     * @param {RTCDataChannel} dataChannel - The WebRTC data channel for sending commands
     */
    constructor(videoElement, dataChannel) {
        this.videoElement = videoElement;
        this.dataChannel = dataChannel;
        this.enabled = false;
        this.authorized = false;

        // Statistics tracking
        this.stats = {
            mouseMoveSent: 0,
            mouseClicksSent: 0,
            keysSent: 0,
            messagesDropped: 0,
            lastError: null
        };

        // Track pressed modifier keys
        this.modifierKeys = {
            shift: false,
            control: false,
            alt: false
        };

        console.log('[RemoteControl] Initialized');
    }

    /**
     * Enables remote control by attaching event listeners.
     * Supports both mouse (desktop) and touch (mobile/tablet) input.
     */
    enable() {
        if (this.enabled) {
            console.warn('[RemoteControl] Already enabled');
            return;
        }

        // Request authorization from server
        this.requestAuthorization();

        // Mouse events (desktop)
        this.videoElement.addEventListener('mousemove', this.handleMouseMove);
        this.videoElement.addEventListener('mousedown', this.handleMouseDown);
        this.videoElement.addEventListener('mouseup', this.handleMouseUp);
        this.videoElement.addEventListener('wheel', this.handleMouseWheel);
        this.videoElement.addEventListener('contextmenu', this.handleContextMenu);

        // Touch events (mobile/tablet) - mapped to mouse events for the remote desktop
        this.videoElement.addEventListener('touchstart', this.handleTouchStart, { passive: false });
        this.videoElement.addEventListener('touchmove', this.handleTouchMove, { passive: false });
        this.videoElement.addEventListener('touchend', this.handleTouchEnd, { passive: false });

        // Keyboard events
        // Note: We listen on document for keyboard, as video element needs focus
        document.addEventListener('keydown', this.handleKeyDown);
        document.addEventListener('keyup', this.handleKeyUp);

        // Make video element focusable so it can receive keyboard events
        this.videoElement.setAttribute('tabindex', '0');
        this.videoElement.focus();

        this.enabled = true;
        console.log('[RemoteControl] Enabled - mouse, touch and keyboard events are now captured');
    }

    /**
     * Disables remote control by removing event listeners
     */
    disable() {
        if (!this.enabled) {
            return;
        }

        // Mouse events
        this.videoElement.removeEventListener('mousemove', this.handleMouseMove);
        this.videoElement.removeEventListener('mousedown', this.handleMouseDown);
        this.videoElement.removeEventListener('mouseup', this.handleMouseUp);
        this.videoElement.removeEventListener('wheel', this.handleMouseWheel);
        this.videoElement.removeEventListener('contextmenu', this.handleContextMenu);

        // Touch events
        this.videoElement.removeEventListener('touchstart', this.handleTouchStart);
        this.videoElement.removeEventListener('touchmove', this.handleTouchMove);
        this.videoElement.removeEventListener('touchend', this.handleTouchEnd);

        // Keyboard events
        document.removeEventListener('keydown', this.handleKeyDown);
        document.removeEventListener('keyup', this.handleKeyUp);

        this.enabled = false;
        this.authorized = false;
        console.log('[RemoteControl] Disabled');
    }

    /**
     * Requests authorization from the server to enable input control
     */
    requestAuthorization() {
        const message = {
            type: 'authorization_request',
            timestamp: new Date().toISOString()
        };

        this.sendMessage(message);
        console.log('[RemoteControl] Authorization requested');
    }

    /**
     * Handles authorization response from server
     * @param {Object} response - Authorization response message
     */
    handleAuthorizationResponse(response) {
        this.authorized = response.authorized === true;

        if (this.authorized) {
            console.log('[RemoteControl] Authorization GRANTED - remote control is active');
        } else {
            console.warn('[RemoteControl] Authorization DENIED');
            this.disable();
        }
    }

    /**
     * Normalizes mouse coordinates to 0-1 range based on video element size
     * @param {MouseEvent} event - Mouse event
     * @returns {{x: number, y: number}} Normalized coordinates
     */
    normalizeCoordinates(event) {
        const rect = this.videoElement.getBoundingClientRect();

        // Calculate position relative to video element
        const x = (event.clientX - rect.left) / rect.width;
        const y = (event.clientY - rect.top) / rect.height;

        // Clamp to 0-1 range
        return {
            x: Math.max(0, Math.min(1, x)),
            y: Math.max(0, Math.min(1, y))
        };
    }

    /**
     * Maps mouse button number to our enum
     * @param {number} button - Mouse button number (0=left, 1=middle, 2=right, 3/4=extra)
     * @returns {string} Button name
     */
    getMouseButtonName(button) {
        switch (button) {
            case 0: return 'Left';
            case 1: return 'Middle';
            case 2: return 'Right';
            case 3: return 'Extra1';
            case 4: return 'Extra2';
            default: return 'Left';
        }
    }

    /**
     * Handles mouse move events
     */
    handleMouseMove = (event) => {
        if (!this.authorized) return;

        const coords = this.normalizeCoordinates(event);

        const message = {
            Type: 'MouseMove',
            X: coords.x,
            Y: coords.y,
            Timestamp: new Date().toISOString()
        };

        this.sendInputMessage(message);
        this.stats.mouseMoveSent++;
    }

    /**
     * Handles mouse button down events
     */
    handleMouseDown = (event) => {
        if (!this.authorized) return;

        event.preventDefault();

        const message = {
            Type: 'MouseDown',
            Button: this.getMouseButtonName(event.button),
            Shift: event.shiftKey,
            Control: event.ctrlKey,
            Alt: event.altKey,
            Timestamp: new Date().toISOString()
        };

        this.sendInputMessage(message);
        this.stats.mouseClicksSent++;
    }

    /**
     * Handles mouse button up events
     */
    handleMouseUp = (event) => {
        if (!this.authorized) return;

        event.preventDefault();

        const message = {
            Type: 'MouseUp',
            Button: this.getMouseButtonName(event.button),
            Shift: event.shiftKey,
            Control: event.ctrlKey,
            Alt: event.altKey,
            Timestamp: new Date().toISOString()
        };

        this.sendInputMessage(message);
    }

    /**
     * Handles mouse wheel events
     */
    handleMouseWheel = (event) => {
        if (!this.authorized) return;

        event.preventDefault();

        // Normalize wheel delta (different browsers use different scales)
        // Standard is 120 units per "notch"
        const delta = event.deltaY > 0 ? -120 : 120;

        const message = {
            Type: 'MouseWheel',
            WheelDelta: delta,
            Shift: event.shiftKey,
            Control: event.ctrlKey,
            Alt: event.altKey,
            Timestamp: new Date().toISOString()
        };

        this.sendInputMessage(message);
    }

    /**
     * Prevents context menu from showing
     */
    handleContextMenu = (event) => {
        if (this.authorized) {
            event.preventDefault();
        }
    }

    /**
     * Handles keyboard key down events
     */
    handleKeyDown = (event) => {
        if (!this.authorized) return;

        // Don't capture certain browser shortcuts (Ctrl+T, Alt+F4, etc.)
        if (this.shouldIgnoreKey(event)) {
            return;
        }

        event.preventDefault();

        // Update modifier key state
        this.updateModifiers(event);

        const message = {
            Type: 'KeyDown',
            KeyCode: event.keyCode,
            ScanCode: 0,  // Browser doesn't provide scan codes
            Shift: event.shiftKey,
            Control: event.ctrlKey,
            Alt: event.altKey,
            Timestamp: new Date().toISOString()
        };

        this.sendInputMessage(message);
        this.stats.keysSent++;
    }

    /**
     * Handles keyboard key up events
     */
    handleKeyUp = (event) => {
        if (!this.authorized) return;

        if (this.shouldIgnoreKey(event)) {
            return;
        }

        event.preventDefault();

        // Update modifier key state
        this.updateModifiers(event);

        const message = {
            Type: 'KeyUp',
            KeyCode: event.keyCode,
            ScanCode: 0,
            Shift: event.shiftKey,
            Control: event.ctrlKey,
            Alt: event.altKey,
            Timestamp: new Date().toISOString()
        };

        this.sendInputMessage(message);
    }

    /**
     * Updates the tracked state of modifier keys
     */
    updateModifiers(event) {
        this.modifierKeys.shift = event.shiftKey;
        this.modifierKeys.control = event.ctrlKey;
        this.modifierKeys.alt = event.altKey;
    }

    /**
     * Determines if a key event should be ignored (browser shortcuts)
     */
    shouldIgnoreKey(event) {
        // Allow F5 (refresh) if Ctrl is not pressed
        if (event.keyCode === 116 && !event.ctrlKey) {
            return true;
        }

        // Allow Ctrl+T (new tab), Ctrl+W (close tab), Ctrl+R (refresh)
        if (event.ctrlKey) {
            if (event.keyCode === 84 || event.keyCode === 87 || event.keyCode === 82) {
                return true;
            }
        }

        // Allow Alt+F4 (close window)
        if (event.altKey && event.keyCode === 115) {
            return true;
        }

        return false;
    }

    // ====================================================================
    // TOUCH EVENT HANDLERS (Mobile/Tablet)
    //
    // Touch events are mapped to mouse events for the remote desktop:
    // - Single finger tap = left click
    // - Single finger drag = mouse move
    // - Two finger tap = right click
    // - Two finger pinch/spread = mouse wheel (zoom)
    // ====================================================================

    /**
     * Normalizes touch coordinates to 0-1 range, same as mouse coordinates.
     * @param {Touch} touch - A single Touch object from the touch event
     * @returns {{x: number, y: number}} Normalized coordinates
     */
    normalizeTouchCoordinates(touch) {
        const rect = this.videoElement.getBoundingClientRect();
        const x = (touch.clientX - rect.left) / rect.width;
        const y = (touch.clientY - rect.top) / rect.height;
        return {
            x: Math.max(0, Math.min(1, x)),
            y: Math.max(0, Math.min(1, y))
        };
    }

    /**
     * Handles touch start - maps to mouse down.
     * Single finger = left click, two fingers = right click.
     */
    handleTouchStart = (event) => {
        if (!this.authorized) return;
        event.preventDefault();

        const touch = event.touches[0];
        const coords = this.normalizeTouchCoordinates(touch);

        // First move the cursor to the touch position
        this.sendInputMessage({
            Type: 'MouseMove',
            X: coords.x,
            Y: coords.y,
            Timestamp: new Date().toISOString()
        });
        this.stats.mouseMoveSent++;

        // Determine button: 2 fingers = right click, 1 finger = left click
        const button = event.touches.length >= 2 ? 'Right' : 'Left';

        this.sendInputMessage({
            Type: 'MouseDown',
            Button: button,
            Shift: false,
            Control: false,
            Alt: false,
            Timestamp: new Date().toISOString()
        });
        this.stats.mouseClicksSent++;

        // Store touch start info for gesture detection (pinch zoom)
        this._lastTouchDistance = null;
        if (event.touches.length === 2) {
            this._lastTouchDistance = this._getTouchDistance(event.touches[0], event.touches[1]);
        }
    }

    /**
     * Handles touch move - maps to mouse move.
     * Two finger pinch/spread is mapped to mouse wheel (zoom).
     */
    handleTouchMove = (event) => {
        if (!this.authorized) return;
        event.preventDefault();

        // Two-finger pinch/spread → mouse wheel (zoom)
        if (event.touches.length === 2 && this._lastTouchDistance !== null) {
            const currentDistance = this._getTouchDistance(event.touches[0], event.touches[1]);
            const delta = currentDistance - this._lastTouchDistance;

            // Only send wheel event if the pinch distance changed significantly
            if (Math.abs(delta) > 5) {
                // Spread (fingers apart) = scroll up (zoom in), pinch = scroll down (zoom out)
                const wheelDelta = delta > 0 ? 120 : -120;
                this.sendInputMessage({
                    Type: 'MouseWheel',
                    WheelDelta: wheelDelta,
                    Shift: false,
                    Control: false,
                    Alt: false,
                    Timestamp: new Date().toISOString()
                });
                this._lastTouchDistance = currentDistance;
            }
            return;
        }

        // Single finger drag → mouse move
        const touch = event.touches[0];
        const coords = this.normalizeTouchCoordinates(touch);

        this.sendInputMessage({
            Type: 'MouseMove',
            X: coords.x,
            Y: coords.y,
            Timestamp: new Date().toISOString()
        });
        this.stats.mouseMoveSent++;
    }

    /**
     * Handles touch end - maps to mouse up.
     */
    handleTouchEnd = (event) => {
        if (!this.authorized) return;
        event.preventDefault();

        // Use the button that was pressed in touchstart
        const button = event.changedTouches.length >= 2 ? 'Right' : 'Left';

        this.sendInputMessage({
            Type: 'MouseUp',
            Button: button,
            Shift: false,
            Control: false,
            Alt: false,
            Timestamp: new Date().toISOString()
        });

        this._lastTouchDistance = null;
    }

    /**
     * Calculates distance between two touch points (for pinch detection).
     * @param {Touch} touch1 - First touch point
     * @param {Touch} touch2 - Second touch point
     * @returns {number} Distance in pixels
     */
    _getTouchDistance(touch1, touch2) {
        const dx = touch1.clientX - touch2.clientX;
        const dy = touch1.clientY - touch2.clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }

    /**
     * Sends an input message via DataChannel
     */
    sendInputMessage(message) {
        // Check if data channel is open
        if (!this.dataChannel || this.dataChannel.readyState !== 'open') {
            this.stats.messagesDropped++;
            if (this.stats.messagesDropped % 100 === 0) {
                console.warn('[RemoteControl] DataChannel not open, dropped', this.stats.messagesDropped, 'messages');
            }
            return;
        }

        this.sendMessage(message);
    }

    /**
     * Sends a generic message via DataChannel
     */
    sendMessage(message) {
        try {
            const json = JSON.stringify(message);
            this.dataChannel.send(json);
        } catch (error) {
            console.error('[RemoteControl] Failed to send message:', error);
            this.stats.lastError = error.message;
            this.stats.messagesDropped++;
        }
    }

    /**
     * Gets current statistics
     */
    getStatistics() {
        return {
            ...this.stats,
            enabled: this.enabled,
            authorized: this.authorized,
            dataChannelOpen: this.dataChannel?.readyState === 'open'
        };
    }
}

// Export for use in main client
if (typeof module !== 'undefined' && module.exports) {
    module.exports = RemoteControl;
}
