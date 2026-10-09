/**
 * Service Worker for DeskShare PWA
 *
 * Provides offline caching for static assets so the app shell loads
 * even without a network connection. WebRTC streaming still requires
 * a live connection, but the UI is available immediately.
 *
 * For junior developers:
 * - A service worker is a script that runs in the background, separate from the page.
 * - It intercepts network requests and can serve cached responses.
 * - The "install" event fires when the service worker is first registered.
 * - The "activate" event fires after install, used to clean up old caches.
 * - The "fetch" event fires for every network request the page makes.
 */

const CACHE_NAME = 'deskshare-v2';

// Files to cache during install (the "app shell")
const APP_SHELL_FILES = [
    '/webclient/index.html',
    '/webclient/client.js',
    '/webclient/remote-control.js',
    '/webclient/mobile.css',
    '/webclient/manifest.json'
];

/**
 * Install event: cache the app shell files.
 * waitUntil() keeps the service worker in "installing" state
 * until all files are cached.
 */
self.addEventListener('install', (event) => {
    console.log('[ServiceWorker] Installing...');
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => {
                console.log('[ServiceWorker] Caching app shell');
                return cache.addAll(APP_SHELL_FILES);
            })
            .catch((error) => {
                // Don't fail install if caching fails (e.g., files not yet served)
                console.warn('[ServiceWorker] Cache addAll failed, continuing:', error);
            })
    );
    // Activate immediately without waiting for old service worker to stop
    self.skipWaiting();
});

/**
 * Activate event: clean up old caches from previous versions.
 * This runs after install, when the new service worker takes control.
 */
self.addEventListener('activate', (event) => {
    console.log('[ServiceWorker] Activating...');
    event.waitUntil(
        caches.keys().then((cacheNames) => {
            return Promise.all(
                cacheNames
                    .filter((name) => name !== CACHE_NAME)
                    .map((name) => {
                        console.log('[ServiceWorker] Deleting old cache:', name);
                        return caches.delete(name);
                    })
            );
        })
    );
    // Take control of all pages immediately (don't wait for reload)
    self.clients.claim();
});

/**
 * Fetch event: serve from cache first, fall back to network.
 * "Cache-first" strategy is good for static assets that rarely change.
 *
 * We skip caching for:
 * - WebSocket connections (ws:// or wss://) - these are real-time
 * - API endpoints (/health, /statistics, /authenticate, etc.)
 * - Non-GET requests (POST, PUT, DELETE)
 */
self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Skip non-GET requests (API calls, form submissions)
    if (event.request.method !== 'GET') {
        return;
    }

    // Skip WebSocket upgrade requests
    if (event.request.headers.get('Upgrade') === 'websocket') {
        return;
    }

    // Skip API endpoints - these need fresh data
    const apiPaths = ['/health', '/statistics', '/signal', '/register', '/authenticate', '/servers'];
    if (apiPaths.some((path) => url.pathname.startsWith(path))) {
        return;
    }

    // Cache-first strategy for static assets
    // Network first: a cache-first strategy kept serving old client code after every deployment.
    // The cache is only an offline fallback.
    event.respondWith(
        fetch(event.request)
            .then((networkResponse) => {
                if (networkResponse && networkResponse.status === 200) {
                    const responseClone = networkResponse.clone();
                    caches.open(CACHE_NAME).then((cache) => cache.put(event.request, responseClone));
                }
                return networkResponse;
            })
            .catch(() => caches.match(event.request).then((cached) =>
                cached || (event.request.headers.get('Accept')?.includes('text/html')
                    ? caches.match('/webclient/index.html')
                    : undefined)))
    );
});
