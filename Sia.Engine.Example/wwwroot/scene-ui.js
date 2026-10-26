export function createSceneUI(actions) {
  const element = id => document.getElementById(id);
  const canvas = element('canvas');
  const inspection = element('inspection');
  const settings = element('settings-toggle');
  const loading = element('loading');
  const progress = element('loading-progress');
  const credits = element('credits');
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
  }

  function focusCamera() {
    if (phase !== 'ready') return false;
    setSettingsOpen(false);
    canvas.focus();
    return true;
  }

  function configure(selected) {
    scene = selected;
    element('loading-title').textContent = scene.title;
    element('scene-title').textContent = scene.title;
    const quality = element('quality-select');
    quality.hidden = !scene.quality;
    quality.value = scene.quality ?? 'high';
    settings.hidden = !scene.quality;
    element('bunny-credit').hidden = scene.pipeline !== 'bunny';
    const inspectionCamera = scene.pipeline === 'bunny' && !scene.camera;
    element('inspection-distance-row').hidden = !inspectionCamera;
    tour.hidden = !inspectionCamera;
    element('inspection-reset').hidden = !inspectionCamera;
    element('inspection-help').textContent = scene.camera
      ? 'This scene uses the selected camera. Use the shading control to inspect triangles.'
      : 'Drag the distance slider to move closer or farther. Use the controls above to pause the tour, reset the view or change shading.';
    element('loading-note').textContent = 'Preparing the scene for your device.';
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
    if (phase === 'failed' || phase === 'stopped') return;
    phase = next;
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
    if (phase === 'failed' || phase === 'stopped') return;
    const credit = element('scene-credit');
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
  element('quality-select').addEventListener('change', event => actions.quality(event.target.value));
  tour.addEventListener('click', () => actions.command(1));
  material.addEventListener('click', () => actions.command(2));
  element('inspection-reset').addEventListener('click', () => actions.command(4));
  element('scene-stop').addEventListener('click', () => actions.command(512));
  window.addEventListener('resize', actions.changed);

  return { canvas, configure, setLoadingState, setSceneReady,
    setInspectionStatus, setSceneAttribution, showError,
    setSceneStopped: () => finish('stopped', 'Scene stopped', 'Restart to explore the scene again.') };
}
