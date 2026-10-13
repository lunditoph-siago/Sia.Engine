// Fixed example entry points. Mesh identities and payloads remain in their records.
export async function defaultContent(pipeline, pageUrl, fetchContent = fetch) {
  let descriptor = new URL('Assets/default-scenes.json', pageUrl);
  let catalog = await readCatalog(descriptor, fetchContent);
  // A thin Pages site can delegate once to the full package on a CORS-enabled host.
  if (catalog && typeof catalog === 'object' && Object.keys(catalog).length === 1
      && typeof catalog.catalog === 'string') {
    descriptor = new URL(catalog.catalog);
    if (descriptor.protocol !== 'https:' || descriptor.username || descriptor.password) {
      throw new Error('External example content requires an HTTPS catalog URL.');
    }
    catalog = await readCatalog(descriptor, fetchContent);
  }
  const keys = ['bunny', 'bistroExterior', 'bistroInterior', 'bistroWine'];
  const path = suffix => new RegExp('^Content/[a-f0-9]{64}/[a-f0-9]{32}\\.' + suffix + '$');
  const validPath = (value, suffix) => typeof value === 'string' && path(suffix).test(value)
    && !value.endsWith('/' + '0'.repeat(32) + '.' + suffix);
  const validEntry = entry => entry && typeof entry === 'object' && !Array.isArray(entry)
    && Object.keys(entry).every(key => key === 'scene' || key === 'lightmaps')
    && validPath(entry.scene, 'siascene')
    && (entry.lightmaps == null || validPath(entry.lightmaps, 'siaasset')
      && entry.lightmaps.slice(0, entry.lightmaps.lastIndexOf('/')) === entry.scene.slice(0, entry.scene.lastIndexOf('/')));
  if (!catalog || typeof catalog !== 'object' || Array.isArray(catalog)
      || Object.keys(catalog).length !== keys.length
      || keys.some(key => !validEntry(catalog[key]))) {
    throw new Error('Invalid default scene catalog.');
  }
  const entry = pipeline === 'bunny' ? catalog.bunny : catalog.bistroExterior;
  return { scene: new URL(entry.scene, descriptor).href,
    lightmaps: entry.lightmaps == null ? null : new URL(entry.lightmaps, descriptor).href };
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
