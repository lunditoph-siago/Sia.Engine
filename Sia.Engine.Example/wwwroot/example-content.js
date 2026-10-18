export async function defaultScene(pageUrl, fetchContent = fetch) {
  const descriptor = new URL('Assets/default-scenes.json', pageUrl);
  const catalog = await readCatalog(descriptor, fetchContent);
  const path = /^Content\/[a-f0-9]{64}\/[a-f0-9]{32}\.siascene$/;
  if (!catalog || typeof catalog !== 'object' || Array.isArray(catalog)
      || Object.keys(catalog).length !== 1
      || typeof catalog.bunny !== 'string' || !path.test(catalog.bunny)
      || catalog.bunny.endsWith('/' + '0'.repeat(32) + '.siascene')) {
    throw new Error('Expected a browser content package containing only Bunny.');
  }
  return new URL(catalog.bunny, descriptor).href;
}

async function readCatalog(descriptor, fetchContent) {
  const response = await fetchContent(descriptor, { cache: 'no-store' });
  if (!response.ok || !response.body) throw new Error('The default example content package is missing.');
  const reader = response.body.getReader();
  const chunks = []; let size = 0;
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > 4096) throw new Error('The default scene catalog exceeds 4 KiB.');
      chunks.push(value);
    }
  } catch (error) {
    await reader.cancel(error);
    throw error;
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}
