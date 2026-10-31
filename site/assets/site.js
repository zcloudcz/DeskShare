// Fills in the latest release version and date from GitHub so the page never shows a stale number.
(async () => {
  const el = document.getElementById('release-meta');
  if (!el) return;
  try {
    const res = await fetch('https://api.github.com/repos/zcloudcz/DeskShare/releases/latest', { headers: { Accept: 'application/vnd.github+json' } });
    if (!res.ok) return;
    const rel = await res.json();
    const date = new Date(rel.published_at).toLocaleDateString(document.documentElement.lang, { year: 'numeric', month: 'long', day: 'numeric' });
    el.textContent = `${el.dataset.label} ${rel.tag_name} · ${date}`;
  } catch {
    // Offline or rate-limited: keep the static text.
  }
})();

// Points the hero download at the visitor's OS. The static HTML stays Windows, so crawlers and
// visitors without JS get the main build. Browsers do not reliably tell Apple Silicon from Intel
// (Safari reports "Intel" on every Mac), so Macs get the Apple Silicon build plus an Intel link.
(() => {
  const dl = document.getElementById('hero-dl');
  if (!dl) return;
  const ua = navigator.userAgent;
  const platform = navigator.userAgentData?.platform || navigator.platform || '';
  // iPadOS Safari pretends to be a Mac; touch points give it away.
  const mobile = /Android|iPhone|iPad|iPod|CrOS/i.test(ua) || (/Mac/i.test(platform) && navigator.maxTouchPoints > 1);
  const os = mobile ? 'mobile' : /Mac/i.test(platform) ? 'mac' : /Linux/i.test(platform) ? 'linux' : null;
  if (!os) return;

  if (os === 'mobile') {
    // Desktop installers are useless on a phone: lead with the web viewer instead.
    dl.remove();
    document.getElementById('hero-web').className = 'btn primary';
    return;
  }
  const base = 'https://github.com/zcloudcz/DeskShare/releases/latest/download/';
  dl.href = base + (os === 'mac' ? 'DeskShare-osx-Setup.pkg' : 'DeskShare.AppImage');
  for (const id of ['hero-dl-label', 'hero-title']) {
    const el = document.getElementById(id);
    el.textContent = el.dataset[os];
  }
  if (os === 'mac') document.getElementById('hero-intel').hidden = false;
})();
