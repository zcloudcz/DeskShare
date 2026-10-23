// Race tests for the web viewer: src/WebClient/bind-gate.js (SessionHost/PeerSession) and the real client.js code that
// uses it. client.js is loaded into a vm with a stubbed DOM/WebRTC; the data channels are recording fakes.
// Scenario in all tests: connection A is working on something (MAC check, clipboard read, file transfer) when a new
// offer replaces it with connection B (a malicious signaling server can race one in). Nothing of A may reach B.
// Run: node tests/js/bind-gate.test.mjs
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';

const read = (p) => fs.readFileSync(new URL(p, import.meta.url), 'utf8');

// A DOM element stand-in: any property read gives a harmless function/stub, any write is accepted.
function stub() {
    const store = {};
    return new Proxy(function () {}, {
        get: (_, k) => (k in store ? store[k] : k === 'style' ? (store.style = {}) : k === 'textContent' ? '' : k === 'then' ? undefined : stub()),
        set: (_, k, v) => { store[k] = v; return true; },
        apply: () => stub()
    });
}

function fakeChannel() {
    return { readyState: 'open', bufferedAmount: 0, sent: [], send(m) { this.sent.push(JSON.parse(m)); }, close() {}, addEventListener() {}, removeEventListener() {} };
}

function load() {
    let readText = () => Promise.resolve('');
    const sandbox = {
        console: { log() {}, warn() {}, error() {} },
        document: { getElementById: () => stub(), addEventListener() {}, body: stub() },
        navigator: { clipboard: { readText: () => readText() } },
        crypto: webcrypto, TextEncoder, TextDecoder, setTimeout, clearTimeout, setInterval, clearInterval,
        btoa, atob, URL, fetch: async () => ({}), WebSocket: class {}, RTCPeerConnection: class {},
        window: { addEventListener() {} }, location: { protocol: 'https:', host: 'x' }
    };
    sandbox.globalThis = sandbox;
    const ctx = vm.createContext(sandbox);
    vm.runInContext(read('../../src/WebClient/bind-gate.js').replace(/if \(typeof module[^\n]*\n?/, ''), ctx);
    vm.runInContext('class RemoteControl { constructor(v, c) { this.c = c; } disable() {} }', ctx);
    const exports = vm.runInContext(
        read('../../src/WebClient/client.js') +
        '\n;({ sessions, sendClipboard, sendFile, setClipboardAllowed, setFilesAllowed, getFileTransfer: () => fileTransfer })', ctx);
    return { ...exports, setReadText: (f) => { readText = f; } };
}

function connection(sessions, { verified = true } = {}) {
    const session = sessions.begin({});
    session.channel = fakeChannel();
    session.verified = verified;
    session.clipboardAllowed = true;
    session.filesAllowed = true;
    return session;
}

const tick = () => new Promise((r) => setImmediate(r));

// 1. A late (valid) MAC result for A must not verify B, and nothing is sent on B.
{
    const { sessions } = load();
    const A = connection(sessions, { verified: false });
    let finishA;
    const sentOnB = [];
    const check = new Promise((r) => { finishA = r; });
    check.then((ok) => sessions.completeVerification(A, ok,
        () => sessions.send(A, { type: 'authorization_request' }), () => { throw new Error('must not fail'); }));

    const B = connection(sessions, { verified: false }); // new offer replaces A while A's check is in flight
    finishA(true);
    await tick();

    assert.equal(B.verified, false);
    assert.equal(A.verified, false);
    assert.deepEqual(B.channel.sent, sentOnB);
    assert.deepEqual(A.channel.sent, []);
}

// 2. A late FAILURE of A must not kill B.
{
    const { sessions } = load();
    const A = connection(sessions, { verified: false });
    let failed = false;
    const check = Promise.resolve(false);
    connection(sessions, { verified: false });
    await check.then((ok) => sessions.completeVerification(A, ok, () => {}, () => { failed = true; }));
    assert.equal(failed, false);
}

// 3. Before verification only the bind may be sent.
{
    const { sessions } = load();
    const A = connection(sessions, { verified: false });
    assert.equal(sessions.send(A, { type: 'clipboard' }), false);
    assert.equal(sessions.send(A, { type: 'e2e_bind' }, { bind: true }), true);
    assert.deepEqual(A.channel.sent, [{ type: 'e2e_bind' }]);
}

// 4. Real sendClipboard(): the connection is replaced while the browser reads the clipboard -> nothing is sent to B.
{
    const app = load();
    const A = connection(app.sessions);
    let finishRead;
    app.setReadText(() => new Promise((r) => { finishRead = r; }));

    const pending = app.sendClipboard();
    const B = connection(app.sessions); // verified B, open channel: the worst case
    finishRead('secret text of the user');
    await pending;

    assert.deepEqual(B.channel.sent, []);
    assert.deepEqual(A.channel.sent, []);
}

// 4b. Control: without replacement the text IS sent (the test above would pass trivially if sending never worked).
{
    const app = load();
    const A = connection(app.sessions);
    app.setReadText(() => Promise.resolve('hello'));
    await app.sendClipboard();
    assert.equal(A.channel.sent.length, 1);
    assert.equal(A.channel.sent[0].type, 'clipboard');
}

// 5. Real sendFile(): the connection is replaced during the transfer -> transfer cancelled, nothing sent to B.
{
    const app = load();
    const A = connection(app.sessions);
    const file = { name: 'a.txt', size: 10, slice: () => ({ arrayBuffer: async () => new Uint8Array(10).buffer }) };

    const transfer = app.sendFile(file); // sends file_offer, then waits for the host's answer
    await tick();
    assert.equal(A.channel.sent[0]?.type, 'file_offer');
    const offered = A.channel.sent.length;
    const t = A.transfer;

    const B = connection(app.sessions); // replaces A while the transfer waits
    await transfer;

    assert.equal(t.cancelled, true);
    assert.deepEqual(B.channel.sent, []);
    assert.equal(A.channel.sent.length, offered); // not even a file_cancel went anywhere after the replacement
    assert.equal(app.getFileTransfer(), null);
}

console.log('bind gate OK');
