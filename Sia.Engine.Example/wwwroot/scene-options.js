const scenes = {
  bunny: { title: 'Stanford Bunny', quality: 'high', camera: 'distance' },
  unlit: { title: 'Unlit', quality: null, camera: null },
};

export function readSceneOptions(pageUrl) {
  const parameters = new URL(pageUrl).searchParams;
  const pipeline = parameters.get('pipeline') ?? 'bunny';
  if (!Object.hasOwn(scenes, pipeline)) throw new Error('Expected pipeline bunny|unlit. Bistro is available in the native example.');
  const scene = scenes[pipeline];
  if (parameters.has('quality') && parameters.get('quality') !== scene.quality)
    throw new Error('Browser Bunny uses High; Unlit has no quality setting.');
  for (const name of ['atmosphere', 'skybox']) {
    if (parameters.has(name)) throw new Error(`${name} is no longer supported. Remove it from the URL.`);
  }
  if (parameters.has('camera')) throw new Error('The browser Bunny uses distance controls instead of a free camera.');
  const featureLevel = parameters.get('feature-level') ?? 'auto';
  if (!['auto', 'core', 'compatibility'].includes(featureLevel))
    throw new Error('Expected feature-level core|compatibility|auto.');
  function dimension(name, fallback) {
    if (!parameters.has(name)) return fallback;
    const value = Number(parameters.get(name));
    if (!Number.isInteger(value) || value < 64 || value > 8192) throw new Error(`Expected ${name}=64..8192.`);
    return value;
  }
  const width = dimension('width', parameters.has('benchmark-frames') ? 1280 : undefined);
  const height = dimension('height', parameters.has('benchmark-frames') ? 720 : undefined);
  const args = ['--pipeline', pipeline];
  if (scene.quality) args.push('--quality', scene.quality);
  const forwarded = [
    'pbr-path', 'surface-data', 'scene-gi', 'probes', 'environment',
    'render-scale', 'width', 'height', 'gpu-timing', 'gpu-traversal', 'target-fps',
    'benchmark-frames', 'benchmark-motion', 'debug', 'distance', 'lod',
  ];
  for (const name of forwarded) {
    if (parameters.has(name)) args.push('--' + name, parameters.get(name));
  }
  return { ...scene, pipeline, parameters, featureLevel, width, height, args };
}
