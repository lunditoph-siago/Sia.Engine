const scope = new URL(self.registration.scope);
const cacheName = 'sia-bistro-assets:' + scope.pathname;
const paths = new Set(['Bistro.siapbr', 'BistroFinest.siapbr'].map(name => new URL('Assets/' + name, scope).pathname));

self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== scope.origin || !paths.has(url.pathname)
      || event.request.headers.has('Range')
      || !/^[a-f0-9]{64}$/i.test(url.searchParams.get('sha256') ?? '')) return;
  const response = loadScene(event.request);
  event.respondWith(response.then(result => result.response));
  event.waitUntil(response.then(result => result.cached));
});

function cachedResponse(response, hit) {
  const headers = new Headers(response.headers);
  headers.set('X-Sia-Asset-Cache', hit ? 'hit' : 'miss');
  const reader = response.body?.getReader();
  let chunk;
  let offset = 0;
  const body = reader ? new ReadableStream({
    async pull(controller) {
      const buffer = new Uint8Array(1024 * 1024);
      let used = 0;
      try {
        while (used < buffer.length) {
          if (!chunk || offset === chunk.length) {
            const next = await reader.read();
            if (next.done) {
              if (used) controller.enqueue(buffer.subarray(0, used));
              controller.close();
              reader.releaseLock();
              return;
            }
            chunk = next.value;
            offset = 0;
          }
          const count = Math.min(buffer.length - used, chunk.length - offset);
          buffer.set(chunk.subarray(offset, offset + count), used);
          used += count;
          offset += count;
        }
        controller.enqueue(buffer);
      } catch (error) {
        reader.releaseLock();
        throw error;
      }
    },
    async cancel(reason) {
      try { await reader.cancel(reason); }
      finally { reader.releaseLock(); }
    }
  }) : null;
  return new Response(body, { status: response.status, statusText: response.statusText, headers });
}

async function loadScene(request) {
  let cache;
  try {
    cache = await caches.open(cacheName);
    const cached = await cache.match(request);
    if (cached) return { response: cachedResponse(cached, true) };
  } catch { return { response: await fetch(request) }; }

  const response = await fetch(request, { cache: 'no-store' });
  if (response.status !== 200 || !response.body) return { response };
  const reader = response.body.getReader();
  const cacheStream = new TransformStream();
  const writer = cacheStream.writable.getWriter();
  let caching = true;
  // Bound the cache branch by its consumer's backpressure. Response.clone()
  // can buffer an entire large scene when one branch reads more slowly.
  const cached = cache.put(request, new Response(cacheStream.readable, response)).then(async () => {
    const path = new URL(request.url).pathname;
    for (const key of await cache.keys()) {
      if (new URL(key.url).pathname === path && key.url !== request.url) await cache.delete(key);
    }
  }).catch(() => {
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
  const headers = new Headers(response.headers);
  headers.set('X-Sia-Asset-Cache', 'miss');
  return { response: new Response(body, { status: response.status, statusText: response.statusText, headers }), cached };
}
