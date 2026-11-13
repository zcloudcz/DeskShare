// UI state mirrors existing connection controls; this module never grants permissions.
DeskShareI18n.init();
const form = document.getElementById('connectForm');
form.addEventListener('submit', event => { event.preventDefault(); userDisconnected = false; connect(); });
const passwordMode = document.getElementById('passwordMode');
function updateSecretMode() {
    const field = document.getElementById('passkey');
    field.type = passwordMode.checked ? 'password' : 'text';
    field.autocapitalize = passwordMode.checked ? 'none' : 'characters';
    field.placeholder = passwordMode.checked ? '' : 'ABC-234-XYZ';
    document.getElementById('codeHint').textContent = t(passwordMode.checked ? 'web.password_hint' : 'web.code_hint');
}
passwordMode.addEventListener('change', updateSecretMode);
// Format only a nine-digit device number. Never rewrite a secret: the authentication
// code decides between the existing code and permanent-password branches.
document.getElementById('passkey').addEventListener('blur', event => {
    if (!passwordMode.checked && looksLikePasskey(event.target.value)) {
        event.target.value = event.target.value.replace(/[-\s]/g, '').toUpperCase().match(/.{3}/g).join('-');
    }
});
document.getElementById('serverId').addEventListener('blur', event => {
    const digits = event.target.value.replace(/[\s-]/g, '');
    if (/^\d{9}$/.test(digits)) event.target.value = digits.match(/.{3}/g).join(' ');
});
function syncSessionUi() {
    const connected = !disconnectBtn.disabled || !fullscreenBtn.disabled;
    document.body.classList.toggle('connected', connected);
    if (!connected) setFocus(false);
}
new MutationObserver(syncSessionUi).observe(document.querySelector('.controls'), { attributes:true, subtree:true, attributeFilter:['disabled'] });
syncSessionUi();
window.addEventListener('deskshare-language', () => {
    updateSecretMode();
    if (currentStatusMessage) statusText.textContent = t(currentStatusMessage.key, currentStatusMessage.params);
    updateConnectionState(currentConnectionState);
    updateWebRtcState(currentRtcState);
    if (bindFailed) placeholder.textContent = t('web.bind_failed');
    else if (placeholder.style.display !== 'none' && !fullscreenBtn.disabled) placeholder.textContent = t('connect.pending');
    if (fallbackField) {
        if (fallbackField.tagName === 'INPUT') fallbackField.placeholder = t('web.paste');
        else fallbackField.title = t('web.manualcopy');
    }
    updateControlButton();
    updateRecordingIndicator();
    updatePrivacyButton();
    fullscreenBtn.textContent = t(document.fullscreenElement ? 'web.exitfull' : 'view.fullscreen');
});
if ('serviceWorker' in navigator) navigator.serviceWorker.register('service-worker.js').catch(error => console.warn('[PWA]', error));
