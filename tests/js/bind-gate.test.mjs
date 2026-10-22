// Race test for the web viewer's end-to-end check (src/WebClient/bind-gate.js). Run: node tests/js/bind-gate.test.mjs
// Scenario: the MAC of connection A is verified asynchronously; meanwhile a (malicious) signaling server gets a new
// offer through, which replaces the connection with B. A's late result must not verify B.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const { BindGate } = createRequire(import.meta.url)('../../src/WebClient/bind-gate.js');

// Same shape as the handler in client.js, with stubs instead of WebRTC.
function startCheck(gate, deferred, state) {
    const token = gate.token();
    deferred.then((ok) => gate.complete(token, ok,
        () => { state.sentAuthorizationRequestOn.push(token.pc); state.videoShownFor = token.pc; },
        () => { state.failed = true; }));
}

const newState = () => ({ sentAuthorizationRequestOn: [], videoShownFor: null, failed: false });

// A valid late success for A while B is current: B stays unverified, nothing is sent on B.
{
    const gate = new BindGate();
    const a = {}, b = {};
    gate.reset(a);
    let finishA;
    const state = newState();
    startCheck(gate, new Promise((r) => { finishA = r; }), state);

    gate.reset(b); // new offer replaces the connection while A's check is in flight
    finishA(true);
    await new Promise((r) => setImmediate(r));

    assert.equal(gate.verified, false);
    assert.deepEqual(state.sentAuthorizationRequestOn, []);
    assert.equal(state.videoShownFor, null);
}

// A late FAILURE of A must not kill B either.
{
    const gate = new BindGate();
    gate.reset({});
    let finishA;
    const state = newState();
    startCheck(gate, new Promise((r) => { finishA = r; }), state);

    gate.reset({});
    finishA(false);
    await new Promise((r) => setImmediate(r));

    assert.equal(state.failed, false);
}

// Normal case: the check on the current connection applies.
{
    const gate = new BindGate();
    const a = {};
    gate.reset(a);
    const state = newState();
    startCheck(gate, Promise.resolve(true), state);
    await new Promise((r) => setImmediate(r));

    assert.equal(gate.verified, true);
    assert.deepEqual(state.sentAuthorizationRequestOn, [a]);
}

// Same pc object but a reset in between (cleanup / reconnect) is stale too, and reset(null) invalidates everything.
{
    const gate = new BindGate();
    const a = {};
    gate.reset(a);
    const token = gate.token();
    gate.reset(a);
    assert.equal(gate.isCurrent(token), false);
    gate.reset(null);
    assert.equal(gate.verified, false);
}

console.log('bind gate OK');
