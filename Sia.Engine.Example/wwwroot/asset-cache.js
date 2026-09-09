const scope = new URL(self.registration.scope);
const cacheName = 'sia-bistro-assets:' + scope.pathname;
const paths = new Set(['Bistro.siapbr', 'BistroFinest.siapbr'].map(name => new URL('Assets/' + name, scope).pathname));

self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== scope.origin || !paths.has(url.pathname)
      || !/^[a-f0-9]{64}$/i.test(url.searchParams.get('sha256') ?? '')) return;
  event.respondWith(loadScene(event.request));
});

function cachedResponse(response, hit) {
  const headers = new Headers(response.headers);
  headers.set('X-Sia-Asset-Cache', hit ? 'hit' : 'miss');
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

async function loadScene(request) {
  let cache;
  try {
    cache = await caches.open(cacheName);
    const cached = await cache.match(request);
    if (cached) return cachedResponse(cached, true);
  } catch { return fetch(request); }

  const response = await fetch(request, { cache: 'no-store' });
  if (response.status !== 200) return response;
  try {
    await cache.put(request, response);
    const stored = await cache.match(request);
    if (!stored) return fetch(request, { cache: 'no-store' });
    const path = new URL(request.url).pathname;
    for (const key of await cache.keys()) {
      if (new URL(key.url).pathname === path && key.url !== request.url) await cache.delete(key);
    }
    return cachedResponse(stored, false);
  } catch { return fetch(request, { cache: 'no-store' }); }
}
