const scope = new URL(self.registration.scope);
const cacheName = 'sia-content-assets:' + scope.pathname;
const contentRoot = new URL('Assets/Content/', scope).pathname;

self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
  const request = event.request;
  const url = new URL(request.url);
  if (url.origin !== scope.origin || (request.cache === 'only-if-cached' && request.mode !== 'same-origin')) return;
  const scene = request.method === 'GET' && url.pathname.startsWith(contentRoot) && !request.headers.has('Range')
    && /^[a-f0-9]{64}\/(?:[a-f0-9]{32}\.(?:siaasset|siascene)|[A-F0-9]{64}\.chunk)$/.test(url.pathname.slice(contentRoot.length))
    && !url.search;
  const response = scene ? loadScene(request) : fetch(request).then(response => ({ response }));
  event.respondWith(response.then(result => isolatedResponse(result.response)));
  event.waitUntil(response.then(result => result.cached));
});

function isolatedResponse(response) {
  if (response.type === 'opaque' || response.status === 0) return response;
  const headers = new Headers(response.headers);
  headers.set('Cross-Origin-Opener-Policy', 'same-origin');
  headers.set('Cross-Origin-Embedder-Policy', 'require-corp');
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

async function loadScene(request) {
  let cache;
  try {
    cache = await caches.open(cacheName);
    const cached = await cache.match(request);
    if (cached) return { response: cached };
  } catch { return { response: await fetch(request) }; }

  const response = await fetch(request, { cache: 'no-store' });
  if (response.status !== 200 || !response.body) return { response };
  const reader = response.body.getReader();
  const cacheStream = new TransformStream();
  const writer = cacheStream.writable.getWriter();
  let caching = true;
  // Bound the cache branch by its consumer's backpressure. Response.clone()
  // can buffer an entire large scene when one branch reads more slowly.
  const cached = cache.put(request, new Response(cacheStream.readable, response)).catch(() => {
    caching = false;
    void writer.abort().catch(() => {});
  });
  const body = new ReadableStream({
    async pull(controller) {
      try {
        const { value, done } = await reader.read();
        if (done) {
          if (caching) await writer.close().catch(() => {});
          reader.releaseLock();
          controller.close();
          return;
        }
        if (caching) await writer.write(value).catch(() => { caching = false; });
        controller.enqueue(value);
      } catch (error) {
        void writer.abort(error).catch(() => {});
        reader.releaseLock();
        controller.error(error);
      }
    },
    async cancel(reason) {
      void writer.abort(reason).catch(() => {});
      try { await reader.cancel(reason); }
      finally { reader.releaseLock(); }
    }
  });
  return { response: new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers }), cached };
}
