/**
 * Optional "ZCLOUD Login" sign-in and "My devices" for the web viewer.
 *
 * For junior developers:
 * - Nothing happens unless the signaling server answers GET /accounts/config (it returns 404 while
 *   Accounts:Enabled is false). Only then is MSAL.js loaded and the "Sign in" button shown.
 * - MSAL.js (Microsoft's browser library for Entra / External ID) runs the sign-in as a full-page redirect to
 *   ZCLOUD Login and back to this page; MFA, if configured in the user flow, happens there.
 *   It is vendored as one UMD file (vendor/msal-browser.min.js) because this client has no build step.
 * - Signing in only lists your devices and fills the Server ID. Connecting still needs the passkey or password:
 *   the end-to-end binding of the session is derived from that secret, an account can never replace it.
 * - Uses getBaseUrl() from client.js (the server the viewer connects to).
 */
(function () {
    'use strict';

    let msalApp = null;
    let config = null;

    const $ = (id) => document.getElementById(id);

    function loadScript(src) {
        return new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = src;
            s.onload = resolve;
            s.onerror = () => reject(new Error('Could not load ' + src));
            document.head.appendChild(s);
        });
    }

    function setStatus(text) {
        $('myDevicesStatus').textContent = text;
    }

    function currentAccount() {
        return msalApp ? msalApp.getActiveAccount() || msalApp.getAllAccounts()[0] || null : null;
    }

    async function getToken() {
        const account = currentAccount();
        if (!account) throw new Error('Not signed in');
        try {
            return (await msalApp.acquireTokenSilent({ scopes: [config.scope], account })).accessToken;
        } catch (e) {
            if (e instanceof msal.InteractionRequiredAuthError) {
                // Session at ZCLOUD Login ended: go there again (leaves the page).
                await msalApp.acquireTokenRedirect({ scopes: [config.scope], account });
            }
            throw e;
        }
    }

    async function api(method, path) {
        const response = await fetch(getBaseUrl() + path, {
            method,
            headers: { Authorization: 'Bearer ' + await getToken() }
        });
        if (!response.ok && response.status !== 404) {
            let message = `${response.status} ${response.statusText}`;
            try { message = (await response.json()).error || message; } catch { /* not JSON */ }
            throw new Error(message);
        }
        return response;
    }

    function formatServerId(serverId) {
        const digits = serverId.replace(/^server-/, '');
        return digits.replace(/(\d{3})(\d{3})(\d{3})/, '$1 $2 $3');
    }

    async function loadDevices() {
        setStatus('Loading...');
        const list = $('myDevicesList');
        try {
            const data = await (await api('GET', '/me/devices')).json();
            list.replaceChildren();
            for (const d of data.devices) {
                // Built with textContent only: device names come from users and must never be parsed as HTML.
                const li = document.createElement('li');
                const pick = document.createElement('button');
                pick.className = 'device-pick';
                pick.title = 'Fill in this Server ID';
                const name = document.createElement('strong');
                name.textContent = d.name;
                const detail = document.createElement('span');
                detail.textContent = ` ${formatServerId(d.serverId)}${d.unattended ? ' (unattended)' : ''} - ` +
                    (d.online ? 'online' : 'last seen ' + new Date(d.lastSeen).toLocaleString());
                detail.className = d.online ? 'device-online' : 'device-offline';
                pick.append(name, detail);
                pick.onclick = () => {
                    // Server ID only; the passkey/password must still be typed.
                    $('serverId').value = formatServerId(d.serverId);
                    $('passkey').focus();
                };
                const remove = document.createElement('button');
                remove.textContent = 'Remove';
                remove.onclick = async () => {
                    try { await api('DELETE', '/me/devices/' + encodeURIComponent(d.serverId)); await loadDevices(); }
                    catch (e) { setStatus('Not removed: ' + e.message); }
                };
                li.append(pick, remove);
                list.appendChild(li);
            }
            setStatus(data.devices.length === 0
                ? 'No devices yet. Add a computer from the DeskShare desktop app (Server Mode, "Add this computer to my devices").'
                : 'Click a device to fill in its Server ID. The passkey or password is still needed.');
        } catch (e) {
            setStatus('Could not load your devices: ' + e.message);
        }
    }

    function updateUi() {
        const account = currentAccount();
        $('accountBar').hidden = false;
        $('accountName').textContent = account ? (account.username || account.name || '') : '';
        $('signInBtn').textContent = account ? 'Sign out' : 'Sign in';
        $('myDevicesPanel').hidden = !account;
        if (account) loadDevices();
    }

    async function onSignInClick() {
        if (currentAccount()) {
            // Clears this page's tokens and ends the session at ZCLOUD Login, then comes back here.
            await msalApp.logoutRedirect({ account: currentAccount(), postLogoutRedirectUri: redirectUri() });
        } else {
            await msalApp.loginRedirect({ scopes: [config.scope] });
        }
    }

    // One fixed URI (registered in the SPA app registration), whatever path the viewer was opened under.
    function redirectUri() {
        return location.origin + '/webclient/index.html';
    }

    async function init() {
        if (!location.protocol.startsWith('http')) return; // opened from disk: no server to ask
        let response;
        try { response = await fetch(getBaseUrl() + '/accounts/config'); } catch { return; }
        if (!response.ok) return; // accounts off: no account UI at all
        config = await response.json();
        if (!config.clientIdWeb) return;

        await loadScript('vendor/msal-browser.min.js');
        msalApp = new msal.PublicClientApplication({
            auth: {
                clientId: config.clientIdWeb,
                authority: config.authority,
                knownAuthorities: config.knownAuthorities,
                redirectUri: redirectUri(),
                postLogoutRedirectUri: redirectUri(),
                navigateToLoginRequestUrl: false
            },
            // Per tab and gone when it closes; nothing of the session stays on a shared computer.
            cache: { cacheLocation: 'sessionStorage' }
        });
        await msalApp.initialize();
        try {
            // Finishes a sign-in when we come back from ZCLOUD Login (and removes the response from the URL).
            const result = await msalApp.handleRedirectPromise();
            if (result && result.account) msalApp.setActiveAccount(result.account);
        } catch (e) {
            console.warn('[Accounts] Sign-in failed:', e);
        }
        $('signInBtn').onclick = () => onSignInClick().catch((e) => setStatus('Sign-in failed: ' + e.message));
        $('refreshDevicesBtn').onclick = loadDevices;
        updateUi();
    }

    init().catch((e) => console.warn('[Accounts] Not available:', e));
})();
