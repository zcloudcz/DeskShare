// Decides whether the result of an asynchronous end-to-end check may still be applied.
// Why: verifying the sender's MAC takes a few async WebCrypto steps. If a new offer replaces the peer connection
// meanwhile (a malicious signaling server can race one in), the late "valid" result of the OLD connection must
// not mark the NEW one as verified. Every peer connection replacement calls reset(); results carry a token.
class BindGate {
    constructor() {
        this.generation = 0;
        this.pc = null;
        this.verified = false;
    }

    /** A new (or no) peer connection: all earlier tokens become stale and nothing is verified. */
    reset(pc) {
        this.generation++;
        this.pc = pc;
        this.verified = false;
    }

    /** Capture before starting async work. */
    token() {
        return { generation: this.generation, pc: this.pc };
    }

    isCurrent(token) {
        return token.generation === this.generation && token.pc === this.pc;
    }

    /** Applies the outcome only for the connection the check started on. Returns false when it was stale. */
    complete(token, ok, onVerified, onFailed) {
        if (!this.isCurrent(token)) return false;
        if (ok) {
            this.verified = true;
            onVerified();
        } else {
            onFailed();
        }
        return true;
    }
}

if (typeof module !== 'undefined') module.exports = { BindGate };
