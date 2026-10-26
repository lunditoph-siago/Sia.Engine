const scenes = {
  bunny: { title: 'Stanford Bunny' },
  unlit: { title: 'Unlit' },
};

export function readSceneOptions(pageUrl) {
  const parameters = new URL(pageUrl).searchParams;
  const pipeline = parameters.get('pipeline') ?? 'bunny';
  if (!Object.hasOwn(scenes, pipeline)) throw new Error('Expected pipeline bunny|unlit.');
  const scene = scenes[pipeline];
  for (const name of parameters.keys()) {
    if (!['pipeline', 'quality', 'scene', 'environment', 'probes', 'exposure-compensation', 'auto-exposure'].includes(name))
      throw new Error(`Unknown scene option: ${name}.`);
    if (parameters.getAll(name).length !== 1) throw new Error(`Duplicate scene option: ${name}.`);
    if (pipeline === 'unlit' && name !== 'pipeline') throw new Error('Unlit does not use PBR scene or lighting options.');
  }
  const quality = pipeline === 'bunny' ? parameters.get('quality') ?? 'high' : null;
  if (quality !== null && !['high', 'medium', 'low'].includes(quality)) throw new Error('Expected quality high|medium|low.');
  const args = ['--pipeline', pipeline];
  if (quality) args.push('--quality', quality);
  for (const name of ['environment', 'probes']) {
    if (parameters.has(name)) args.push('--' + name, parameters.get(name));
  }
  if (parameters.has('exposure-compensation')) {
    const value = parameters.get('exposure-compensation').trim();
    const stops = Math.fround(Number(value));
    if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(value)
        || !Number.isFinite(stops) || stops < -16 || stops > 16)
      throw new Error('Expected exposure-compensation stops in -16..16.');
    args.push('--exposure-compensation', value);
  }
  if (parameters.has('auto-exposure')) {
    const value = parameters.get('auto-exposure');
    if (value !== '0' && value !== '1') throw new Error('Expected auto-exposure 0|1.');
    if (value === '1') args.push('--auto-exposure');
  }
  return { ...scene, pipeline, quality, sceneUrl: parameters.get('scene'), args };
}
