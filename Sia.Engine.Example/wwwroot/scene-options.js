const scenes = {
  bunny: { title: 'Stanford Bunny', quality: 'high' },
  unlit: { title: 'Unlit', quality: null },
};

export function readSceneOptions(pageUrl) {
  const parameters = new URL(pageUrl).searchParams;
  const pipeline = parameters.get('pipeline') ?? 'bunny';
  if (!Object.hasOwn(scenes, pipeline)) throw new Error('Expected pipeline bunny|unlit. Bistro is available in the native example.');
  const scene = scenes[pipeline];
  for (const name of parameters.keys()) {
    if (!['pipeline', 'scene', 'environment', 'probes'].includes(name))
      throw new Error(`Unknown scene option: ${name}.`);
    if (parameters.getAll(name).length !== 1) throw new Error(`Duplicate scene option: ${name}.`);
    if (pipeline === 'unlit' && name !== 'pipeline') throw new Error('Unlit has no external scene or lighting assets.');
  }
  const args = ['--pipeline', pipeline];
  for (const name of ['environment', 'probes']) {
    if (parameters.has(name)) args.push('--' + name, parameters.get(name));
  }
  return { ...scene, pipeline, sceneUrl: parameters.get('scene'), args };
}
