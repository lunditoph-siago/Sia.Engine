const scenes = {
  bunny: { title: 'Stanford Bunny', quality: 'high', camera: 'distance' },
  pbr: { title: 'Bistro', quality: 'low', camera: 'free' },
  unlit: { title: 'Unlit', quality: null, camera: null },
};

export function readSceneOptions(pageUrl) {
  const parameters = new URL(pageUrl).searchParams;
  const pipeline = parameters.get('pipeline') ?? 'pbr';
  if (!Object.hasOwn(scenes, pipeline)) throw new Error('Expected pipeline bunny|pbr|unlit.');
  const scene = scenes[pipeline];
  if (parameters.has('quality') && parameters.get('quality') !== scene.quality)
    throw new Error('Browser Bistro supports Low; browser Bunny uses High.');
  for (const name of ['atmosphere', 'skybox']) {
    if (parameters.has(name)) throw new Error(`${name} is no longer supported. Remove it from the URL.`);
  }
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
    'benchmark-frames', 'benchmark-motion', 'debug', 'distance', 'lod', 'camera',
  ];
  for (const name of forwarded) {
    if (parameters.has(name)) args.push('--' + name, parameters.get(name));
  }
  return { ...scene, pipeline, parameters, featureLevel, width, height, args };
}
