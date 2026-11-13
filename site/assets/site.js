// Explicit paths stay in their language. Only the Czech root auto-detects.
(() => {
  const supported = ['cs', 'sk', 'en', 'de', 'pl', 'uk', 'fr', 'es', 'it', 'pt', 'ru', 'tr', 'ar', 'zh', 'ja', 'ko', 'id'];
  const normalize = value => {
    const code = String(value || '').toLowerCase().split('-')[0];
    return supported.includes(code) ? code : null;
  };
  const automatic = () => (navigator.languages || [navigator.language]).map(normalize).find(Boolean) || 'en';
  const path = code => code === 'cs' ? '/' : `/${code}/`;
  const readSaved = () => { try { return normalize(localStorage.getItem('deskshare.lang')); } catch { return null; } };
  const save = code => { try { code ? localStorage.setItem('deskshare.lang', code) : localStorage.removeItem('deskshare.lang'); } catch { /* Navigation works without storage. */ } };
  const url = new URL(location.href);
  const query = normalize(url.searchParams.get('lang'));
  const isLanding = location.pathname === '/' || /^\/(sk|en|de|pl|uk|fr|es|it|pt|ru|tr|ar|zh|ja|ko|id)\/$/.test(location.pathname);
  if (isLanding && query) {
    save(query);
    url.searchParams.delete('lang');
    url.pathname = path(query);
    if (url.href !== location.href) location.replace(url.href);
  } else if (location.pathname === '/') {
    const chosen = readSaved() || automatic();
    if (chosen !== 'cs') {
      url.pathname = path(chosen);
      location.replace(url.href);
    }
  }
  document.querySelectorAll('[data-lang]').forEach(link => {
    link.addEventListener('click', event => {
      event.preventDefault();
      const code = link.dataset.lang;
      save(code === 'auto' ? null : code);
      // Drop stale ?lang while retaining unrelated parameters and anchors.
      const target = new URL(location.href);
      target.searchParams.delete('lang');
      target.pathname = path(code === 'auto' ? automatic() : code);
      location.assign(target.href);
    });
  });

  const download = document.getElementById('hero-dl');
  if (!download) return;
  const ua = navigator.userAgent;
  const platform = navigator.userAgentData?.platform || navigator.platform || '';
  const mobile = /Android|iPhone|iPad|iPod|CrOS/i.test(ua) || (/Mac/i.test(platform) && navigator.maxTouchPoints > 1);
  if (mobile) {
    // A browser viewer cannot share this device's desktop. Keep the two roles distinct.
    download.href = '#download';
    download.textContent = document.querySelector('.sub-link').textContent;
    return;
  }
  const os = /Mac/i.test(platform) ? 'mac' : /Linux/i.test(platform) ? 'linux' : 'windows';
  const assets = {mac: 'DeskShare-osx-Setup.pkg', linux: 'DeskShare.AppImage', windows: 'DeskShare-win-Setup.exe'};
  download.href = 'https://github.com/zcloudcz/DeskShare/releases/latest/download/' + assets[os];
  document.getElementById('hero-os').textContent = {mac: 'macOS', linux: 'Linux', windows: 'Windows'}[os];
})();
