export async function defaultScene(pipeline, pageUrl, fetchContent = fetch) {
  const descriptor = new URL('Assets/default-scenes.json', pageUrl);
  const catalog = await readCatalog(descriptor, fetchContent);
  const keys = ['bunny', 'bistroExterior', 'bistroInterior', 'bistroWine'];
  const path = /^Content\/[a-f0-9]{64}\/[a-f0-9]{32}\.siascene$/;
  if (!catalog || typeof catalog !== 'object' || Array.isArray(catalog)
      || Object.keys(catalog).length !== keys.length
      || keys.some(key => typeof catalog[key] !== 'string' || !path.test(catalog[key])
        || catalog[key].endsWith('/' + '0'.repeat(32) + '.siascene'))) {
    throw new Error('Invalid default scene catalog.');
  }
  return new URL(pipeline === 'bunny' ? catalog.bunny : catalog.bistroExterior, descriptor).href;
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
