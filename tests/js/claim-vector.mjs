// Parity check for the "My devices" claim proof (src/Core/Auth/DeviceClaimProof.cs): the same construction in
// WebCrypto must give the bytes the C# test (tests/UnitTests/Auth/DeviceClaimProofTests.cs) asserts, so a JS client
// (e.g. a future mobile or web host) can make proofs the signaling server accepts. Run: node tests/js/claim-vector.mjs
import assert from 'node:assert/strict';

const enc = new TextEncoder();

// Same constants as DeviceClaimProofTests.cs.
const OWNER_SECRET = 'owner-secret-vector';
const SERVER_ID = 'server-123456789';
const ACCOUNT_KEY = '00000000-1111-2222-3333-444444444444';
const TIMESTAMP = 1792756800;
const NONCE = 'nonce-0123456789abcdef';
const PROOF = 'zL6YNCE6EK+ssHWysJOtl+XSrJOfnBtajzffPDYjF8I=';

// Key = SHA-256(owner secret): the value the server keeps per Server ID.
const claimKey = new Uint8Array(await crypto.subtle.digest('SHA-256', enc.encode(OWNER_SECRET)));
const hmacKey = await crypto.subtle.importKey('raw', claimKey, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
const message = `DeskShare-claim-v1|${SERVER_ID}|${ACCOUNT_KEY}|${TIMESTAMP}|${NONCE}`;
const proof = Buffer.from(await crypto.subtle.sign('HMAC', hmacKey, enc.encode(message))).toString('base64');

assert.equal(proof, PROOF);
console.log('claim vector OK');
