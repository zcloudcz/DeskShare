// Per-peer-connection state of the web viewer, and the single gate every outgoing data channel message passes.
//
// Why: the end-to-end check (verifying the sender's MAC), a clipboard read and a file transfer all take time. A new
// offer can replace the peer connection meanwhile (a malicious signaling server can race one in). If this state were
// shared (global flags), a late result for connection A could verify B, or a transfer started on A could keep sending
// into B. So every peer connection gets its own PeerSession; replacing it kills the old one, and nothing is ever
// sent except through SessionHost.send(), which checks that the session is still the current, live, verified one.
class PeerSession {
    constructor(generation, pc) {
        this.generation = generation;
        this.pc = pc;
        this.channel = null;
        this.verified = false;       // the sender's e2e_bind verified
        this.dead = false;           // replaced by a newer connection
        this.clipboardAllowed = false;
        this.filesAllowed = false;
        this.transfer = null;        // file transfer in progress on THIS connection
        this.bindSent = false;
        this.pendingStream = null;   // video held back until verified
    }

    /** The connection was replaced: nothing of it may act any more. */
    kill() {
        this.dead = true;
        this.verified = false;
        this.clipboardAllowed = false;
        this.filesAllowed = false;
        if (this.transfer) this.transfer.cancelled = true; // its loop stops at the next check and wakes via onKilled
        this.pendingStream = null;
        if (this.onKilled) this.onKilled();
    }
}

class SessionHost {
    constructor() {
        this.generation = 0;
        this.current = null;
    }

    /** A new peer connection (or none, with pc = null): the previous session dies. */
    begin(pc) {
        if (this.current) this.current.kill();
        this.current = pc ? new PeerSession(++this.generation, pc) : null;
        return this.current;
    }

    isCurrent(session) {
        return session !== null && session === this.current && !session.dead;
    }

    /**
     * The only way to put a message on the data channel. A verified session may send anything; before verification
     * only the e2e_bind message (`bind: true`) may go out. Returns false when nothing was sent.
     */
    send(session, message, { bind = false } = {}) {
        if (!this.isCurrent(session)) return false;
        if (!bind && !session.verified) return false;
        const channel = session.channel;
        if (!channel || channel.readyState !== 'open') return false;
        channel.send(typeof message === 'string' ? message : JSON.stringify(message));
        return true;
    }

    /** Applies the outcome of an async MAC check only to the session it started on. Returns false when stale. */
    completeVerification(session, ok, onVerified, onFailed) {
        if (!this.isCurrent(session)) return false;
        if (ok) {
            session.verified = true;
            onVerified();
        } else {
            onFailed();
        }
        return true;
    }
}

if (typeof module !== 'undefined') module.exports = { PeerSession, SessionHost };
