export function createSceneUI(actions) {
  const element = id => document.getElementById(id);
  const canvas = element('canvas');
  const inspection = element('inspection');
  const settings = element('settings-toggle');
  const loading = element('loading');
  const progress = element('loading-progress');
  const credits = element('credits');
  const controller = element('touch-controller');
  const distance = element('inspection-distance');
  const tour = element('inspection-tour');
  const material = element('inspection-material');
  let scene;
  let phase = 'loading';

  function setSettingsOpen(open) {
    inspection.hidden = !open;
    settings.setAttribute('aria-expanded', String(open));
    const ready = phase === 'ready';
    credits.hidden = open || !ready || !scene?.quality;
    controller.hidden = open || !ready || scene?.camera !== 'free';
    if (open) actions.reset();
    actions.changed();
  }

  function focusCamera() {
    if (phase !== 'ready') return false;
    setSettingsOpen(false);
    canvas.focus();
    return true;
  }

  function hasCameraFocus() {
    return phase === 'ready' && inspection.hidden && document.hasFocus() && document.activeElement === canvas;
  }

  function configure(selected) {
    scene = selected;
    element('loading-title').textContent = scene.title;
    element('scene-title').textContent = scene.title;
    const quality = element('quality-value');
    quality.hidden = !scene.quality;
    quality.textContent = scene.quality ? scene.quality[0].toUpperCase() + scene.quality.slice(1) : '';
    settings.hidden = !scene.quality;
    element('bunny-credit').hidden = scene.pipeline !== 'bunny';
    element('inspection-distance-row').hidden = scene.camera !== 'distance';
    element('camera-speed-row').hidden = scene.camera !== 'free';
    tour.hidden = scene.camera !== 'distance';
    element('inspection-reset').textContent = scene.camera === 'free' ? 'Reset view' : 'Return near';
    if (scene.camera === 'free') {
      element('inspection-help').textContent = 'Touch: left stick to move, drag the right half to look, hold ↑ / ↓ to change height. Keyboard: WASD to move, Q / E down / up, arrows or right mouse drag to look, Shift to move faster, R to reset.';
    } else {
      element('loading-note').textContent = 'Preparing the scene for your device.';
    }
    for (const link of document.querySelectorAll('nav a')) {
      if (new URL(link.href).searchParams.get('pipeline') === scene.pipeline) link.setAttribute('aria-current', 'page');
    }
  }

  function setLoadingState(stage, fraction) {
    if (phase !== 'loading') return;
    element('loading-stage').textContent = stage;
    if (Number.isFinite(fraction)) {
      progress.value = Math.max(0, Math.min(1, fraction));
      element('loading-percent').textContent = Math.round(progress.value * 100) + '%';
    } else {
      progress.removeAttribute('value');
      element('loading-percent').textContent = '';
    }
  }

  function setSceneReady() {
    if (phase !== 'loading') return;
    phase = 'ready';
    loading.hidden = true;
    loading.setAttribute('aria-busy', 'false');
    inspection.disabled = false;
    settings.disabled = false;
    focusCamera();
  }

  function finish(next, title, message) {
    if (phase === 'failed') return;
    phase = next;
    actions.reset();
    setSettingsOpen(false);
    inspection.disabled = true;
    settings.disabled = true;
    loading.hidden = false;
    loading.setAttribute('aria-busy', 'false');
    element('loading-title').textContent = title;
    element('loading-stage').textContent = message;
    element('loading-percent').textContent = '';
    progress.hidden = true;
    element('loading-note').hidden = true;
    const retry = element('retry');
    retry.textContent = next === 'failed' ? 'Try again' : 'Restart scene';
    retry.hidden = false;
  }

  function showError(message) {
    finish('failed', phase === 'ready' ? 'Scene interrupted' : 'Unable to open scene', message);
  }

  function setInspectionStatus(value, flags, description) {
    if (phase === 'failed' || phase === 'stopped') return;
    element('inspection-status').textContent = description;
    distance.value = Math.round(value * 1000);
    tour.textContent = (flags & 1) !== 0 ? 'Pause tour' : 'Resume tour';
    const triangles = (flags & 2) !== 0;
    material.textContent = triangles ? 'Show shaded' : 'Show triangles';
    material.setAttribute('aria-pressed', String(triangles));
  }

  function setSceneAttribution(attribution) {
    const credit = element('pbr-credit');
    credit.textContent = attribution;
    credit.hidden = false;
  }

  settings.addEventListener('click', () => setSettingsOpen(inspection.hidden));
  window.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !inspection.hidden) {
      setSettingsOpen(false);
      settings.focus();
    }
  });
  canvas.addEventListener('pointerdown', focusCamera);
  element('retry').addEventListener('click', () => location.reload());
  distance.addEventListener('input', () => actions.distance(distance.valueAsNumber / 1000));
  tour.addEventListener('click', () => actions.command(1));
  material.addEventListener('click', () => actions.command(2));
  element('inspection-reset').addEventListener('click', () => actions.command(4));
  element('scene-stop').addEventListener('click', () => { actions.reset(); actions.command(512); });
  for (const type of ['resize', 'focus', 'blur']) window.addEventListener(type, actions.changed);
  for (const type of ['focus', 'blur']) canvas.addEventListener(type, actions.changed);

  return { canvas, configure, focusCamera, hasCameraFocus, setLoadingState, setSceneReady,
    setInspectionStatus, setSceneAttribution, showError,
    setSceneStopped: () => finish('stopped', 'Scene stopped', 'Restart to explore the scene again.') };
}
