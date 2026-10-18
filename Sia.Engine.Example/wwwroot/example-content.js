export async function defaultScene(pageUrl, fetchContent = fetch) {
  const descriptor = new URL('Assets/default-scenes.json', pageUrl);
  const catalog = await readCatalog(descriptor, fetchContent);
  const path = /^Content\/[a-f0-9]{64}\/[a-f0-9]{32}\.siascene$/;
  if (!catalog || typeof catalog !== 'object' || Array.isArray(catalog)
      || Object.keys(catalog).length !== 1
      || !catalog.bunny || typeof catalog.bunny !== 'object' || Array.isArray(catalog.bunny)
      || Object.keys(catalog.bunny).some(name => !['scene', 'reflectionCaptures'].includes(name))
      || 'reflectionCaptures' in catalog.bunny && (!Array.isArray(catalog.bunny.reflectionCaptures) || catalog.bunny.reflectionCaptures.length !== 0)
      || typeof catalog.bunny.scene !== 'string' || !path.test(catalog.bunny.scene)
      || catalog.bunny.scene.endsWith('/' + '0'.repeat(32) + '.siascene')) {
    throw new Error('Expected a browser content package containing only Bunny.');
  }
  return new URL(catalog.bunny.scene, descriptor).href;
}

async function readCatalog(descriptor, fetchContent) {
  const response = await fetchContent(descriptor, { cache: 'no-store' });
  if (!response.ok || !response.body) throw new Error('The default example content package is missing.');
  const reader = response.body.getReader();
  const bytes = new Uint8Array(4096);
  let size = 0;
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      if (size + value.byteLength > bytes.length) throw new Error('The default scene catalog exceeds 4 KiB.');
      bytes.set(value, size);
      size += value.byteLength;
    }
  } catch (error) {
    await reader.cancel(error);
    throw error;
  } finally { reader.releaseLock(); }
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, size)));
}
