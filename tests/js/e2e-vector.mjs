// Parity check for the end-to-end binding: the web client (src/WebClient/client.js) derives keys and MACs with
// WebCrypto; this script recomputes a fixed vector with the same calls and compares it with the constants that the
// C# test (tests/UnitTests/Auth/E2EBindingTests.cs) asserts. Run: node tests/js/e2e-vector.mjs
// If the C# and JS implementations ever drift apart, a web viewer could no longer connect to a .NET sender.
import assert from 'node:assert/strict';

const enc = new TextEncoder();
const hex = (buf) => Buffer.from(buf).toString('hex');
const fp = (first) => Array.from({ length: 32 }, (_, i) => (first + i).toString(16).padStart(2, '0').toUpperCase()).join(':');

// Same constants as E2EBindingTests.cs
const PASSKEY = 'A3F7K9M2P';
const SERVER_ID = 'server-123456789';
const AUTH_KEY = '4-ne--Ifkq6P2q0hI9jVHJ-pxGxoY8wXA0YCydFSHvE';
const E2E_KEY_HEX = '059dbe1a8b432ce2897c184963de77db35752ae20d7b7074042482ef8160d4d5';
const SENDER_FP = fp(0x00); // 00:01:...:1F
const VIEWER_FP = fp(0xe0); // E0:E1:...:FF
const SENDER_MAC = '1898840887343059f95c5934f3194eaebe5aae504bdfb3f7dfe53cefefdaf1a6';
const VIEWER_MAC = '8f2c9fb1dda05ed5f38edbf5d3508486cc51cc89f03cce4aff94a2b0ea1659af';

async function derive(info) {
    const ikm = await crypto.subtle.importKey('raw', enc.encode(PASSKEY), 'HKDF', false, ['deriveBits']);
    return new Uint8Array(await crypto.subtle.deriveBits(
        { name: 'HKDF', hash: 'SHA-256', salt: enc.encode('DeskShare/v1/' + SERVER_ID), info: enc.encode(info) }, ikm, 256));
}

async function mac(e2eKey, role, senderFp, viewerFp) {
    const key = await crypto.subtle.importKey('raw', e2eKey, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    return hex(await crypto.subtle.sign('HMAC', key, enc.encode(`DeskShare-bind-v1|${role}|${senderFp}|${viewerFp}`)));
}

const auth = await derive('auth');
const e2e = await derive('e2e');
assert.equal(Buffer.from(auth).toString('base64url'), AUTH_KEY);
assert.equal(hex(e2e), E2E_KEY_HEX);
assert.equal(await mac(e2e, 'sender', SENDER_FP, VIEWER_FP), SENDER_MAC);
assert.equal(await mac(e2e, 'viewer', SENDER_FP, VIEWER_FP), VIEWER_MAC);
console.log('e2e vector OK');
