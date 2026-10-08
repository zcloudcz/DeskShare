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
