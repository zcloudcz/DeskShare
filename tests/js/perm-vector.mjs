// Parity check for the permanent password: the web client (src/WebClient/client.js, derivePermanentKeys) derives
// the keys with WebCrypto PBKDF2 + HKDF; this script repeats the same calls for a fixed vector and compares with the
// constants that the C# test (tests/UnitTests/Auth/PermanentPasswordTests.cs) asserts. Run: node tests/js/perm-vector.mjs
// If they drift apart, a web viewer can no longer connect to an unattended host with its password.
import assert from 'node:assert/strict';

const enc = new TextEncoder();

// Same constants as PermanentPasswordTests.cs. Non-ASCII on purpose: covers UTF-8 and NFC normalization.
const PASSWORD = 'Žluťoučký kůň 2026';
const SERVER_ID = 'server-123456789';
const AUTH_KEY = 'JHEeLJCuuOig-SzfCVF1yovJDLH-yQas8TlWd0gJ5y8';
const E2E_KEY_HEX = '66325114af86ffca1b9bfe6d612c7d763d10c7ed509909362993ca2d29d295ec';

async function derive(password) {
    const base = await crypto.subtle.importKey('raw', enc.encode(password.normalize('NFC')), 'PBKDF2', false, ['deriveBits']);
    const master = await crypto.subtle.deriveBits(
        { name: 'PBKDF2', hash: 'SHA-256', salt: enc.encode('DeskShare/v1/perm/' + SERVER_ID), iterations: 600000 }, base, 256);
    const ikm = await crypto.subtle.importKey('raw', new Uint8Array(master), 'HKDF', false, ['deriveBits']);
    const hkdf = async (info) => new Uint8Array(await crypto.subtle.deriveBits(
        { name: 'HKDF', hash: 'SHA-256', salt: enc.encode('DeskShare/v1/' + SERVER_ID), info: enc.encode(info) }, ikm, 256));
    return { auth: Buffer.from(await hkdf('auth')).toString('base64url'), e2e: Buffer.from(await hkdf('e2e')).toString('hex') };
}

const keys = await derive(PASSWORD);
assert.equal(keys.auth, AUTH_KEY);
assert.equal(keys.e2e, E2E_KEY_HEX);
// The same text typed as decomposed characters (macOS keyboards) must give the same keys.
assert.deepEqual(await derive(PASSWORD.normalize('NFD')), keys);
console.log('perm vector OK');
