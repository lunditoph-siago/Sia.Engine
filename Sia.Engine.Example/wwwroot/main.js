const canvas = document.getElementById('canvas');
const parameters = new URLSearchParams(location.search);
const pipeline = parameters.get('pipeline') ?? 'pbr';
const finest = parameters.get('lod') !== 'auto';
const inspection = document.getElementById('inspection');
const settingsToggle = document.getElementById('settings-toggle');
const distanceControl = document.getElementById('inspection-distance');
const tourControl = document.getElementById('inspection-tour');
const materialControl = document.getElementById('inspection-material');
const resetControl = document.getElementById('inspection-reset');
const atmosphereControl = document.getElementById('inspection-atmosphere');
const loading = document.getElementById('loading');
const loadingProgress = document.getElementById('loading-progress');
let atmosphereEnabled = parameters.get('atmosphere') === 'on';
let inspectionCommands = atmosphereEnabled ? 32 : 0;
let inspectionDistance = NaN;
let failed = false;
const loadingStages = [];
const startupErrors = [];

function closeSettings() {
  inspection.hidden = true;
  settingsToggle.setAttribute('aria-expanded', 'false');
}

settingsToggle.addEventListener('click', () => {
  inspection.hidden = !inspection.hidden;
  settingsToggle.setAttribute('aria-expanded', String(!inspection.hidden));
});
canvas.addEventListener('pointerdown', () => { closeSettings(); canvas.focus(); });
canvas.addEventListener('contextmenu', event => event.preventDefault());
window.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !inspection.hidden) {
    closeSettings();
    settingsToggle.focus();
    event.preventDefault();
    event.stopImmediatePropagation();
  } else if (event.target !== canvas) event.stopImmediatePropagation();
}, true);
document.getElementById('retry').addEventListener('click', () => location.reload());
document.getElementById('copy-error').addEventListener('click', async () => {
  const button = document.getElementById('copy-error');
  try {
    await navigator.clipboard.writeText(document.getElementById('error-output').textContent);
    button.textContent = 'Copied';
  } catch {
    document.getElementById('error-details').open = true;
    button.textContent = 'Select and copy the details below';
  }
});

function setLoadingState(stage, progress) {
  if (failed) return;
  if (loadingStages.at(-1)?.stage !== stage) {
    loadingStages.push({ stage, seconds: +(performance.now() / 1000).toFixed(2) });
    if (loadingStages.length > 16) loadingStages.shift();
  }
  loadingStages.at(-1).progress = Number.isFinite(progress) ? Math.round(progress * 100) : null;
  document.getElementById('loading-stage').textContent = stage;
  if (Number.isFinite(progress)) {
    loadingProgress.value = Math.max(0, Math.min(1, progress));
    document.getElementById('loading-percent').textContent = Math.round(loadingProgress.value * 100) + '%';
  } else {
    loadingProgress.removeAttribute('value');
    document.getElementById('loading-percent').textContent = '';
  }
}

function setSceneReady() {
  if (failed) return;
  setLoadingState('Scene running', 1);
  loading.hidden = true;
  loading.setAttribute('aria-busy', 'false');
  inspection.disabled = false;
  settingsToggle.disabled = false;
  document.getElementById('explore-hint').hidden = pipeline !== 'pbr';
  canvas.focus();
}

function hasCameraFocus() {
  return !failed && inspection.hidden && document.hasFocus() && document.activeElement === canvas;
}

distanceControl.addEventListener('input', () => { inspectionDistance = distanceControl.valueAsNumber / 1000; });
tourControl.addEventListener('click', () => { inspectionCommands |= 1; });
materialControl.addEventListener('click', () => { inspectionCommands |= 2; });
resetControl.addEventListener('click', () => { inspectionCommands |= 4; });
atmosphereControl.addEventListener('click', () => {
  atmosphereEnabled = !atmosphereEnabled;
  inspectionCommands ^= 32;
  atmosphereControl.textContent = atmosphereEnabled ? 'Disable atmosphere' : 'Enable atmosphere';
});

function takeInspectionCommands() {
  const commands = inspectionCommands
    | (hasCameraFocus() ? 128 : 0)
    | (Number.isFinite(inspectionDistance) ? 256 : 0);
  inspectionCommands = 0;
  return commands;
}

function takeInspectionDistance() {
  const distance = inspectionDistance;
  inspectionDistance = NaN;
  return distance;
}

function compareLodAtCamera(pose) {
  const url = new URL(location.href);
  url.searchParams.set('lod', finest ? 'auto' : 'finest');
  url.searchParams.set('debug', materialControl.textContent === 'Show shaded' ? 'triangles' : 'shaded');
  url.searchParams.set('atmosphere', atmosphereEnabled ? 'on' : 'off');
  url.searchParams.set('camera', pose);
  location.href = url.href;
}

function setInspectionStatus(status, distance, touring, triangles, atmosphere) {
  atmosphereEnabled = atmosphere !== ((inspectionCommands & 32) !== 0);
  document.getElementById('inspection-status').textContent = pipeline === 'pbr'
    ? (finest ? 'Geometry · Full detail' : 'Geometry · Automatic LOD') : '135 bunnies · Automatic LOD';
  distanceControl.value = Math.round(distance * 1000);
  tourControl.textContent = touring ? 'Pause tour' : 'Resume tour';
  materialControl.textContent = triangles ? 'Show shaded' : 'Show triangles';
  materialControl.setAttribute('aria-pressed', String(triangles));
  atmosphereControl.textContent = atmosphereEnabled ? 'Disable atmosphere' : 'Enable atmosphere';
  atmosphereControl.setAttribute('aria-pressed', String(atmosphereEnabled));
}

function getCanvasWidth() { return Math.max(1, Math.round(window.innerWidth)); }
function getCanvasHeight() { return Math.max(1, Math.round(window.innerHeight)); }
function getBrowserFeatureLevel() { return parameters.get('feature-level') ?? 'core'; }
function setSceneAttribution(attribution) {
  const credit = document.getElementById('pbr-credit');
  credit.textContent = attribution;
  credit.hidden = false;
  document.getElementById('credits').hidden = false;
}

function showError(message) {
  const output = document.getElementById('error-output');
  if (!failed) {
    const moduleFailure = /importing a module script failed|failed to fetch dynamically imported module|error loading dynamically imported module|failed to load module script/i.test(message);
    document.getElementById('loading-title').textContent = moduleFailure ? 'Module loading interrupted'
      : loading.hidden ? 'Scene interrupted' : 'Unable to open scene';
    document.getElementById('loading-stage').textContent = `Stopped during: ${loadingStages.at(-1)?.stage ?? 'Starting engine'}`;
    const path = value => { const url = new URL(value, location.href); return url.origin + url.pathname; };
    const resources = performance.getEntriesByType('resource')
      .filter(entry => /\.(js|wasm)(?:\?|$)/i.test(entry.name)).slice(-16)
      .map(entry => ({ url: path(entry.name), status: entry.responseStatus ?? null,
        durationMs: Math.round(entry.duration), transferredBytes: entry.transferSize }));
    output.textContent = JSON.stringify({ page: path(location.href), browser: navigator.userAgent,
      online: navigator.onLine, pipeline, lod: finest ? 'finest' : 'auto',
      seconds: +(performance.now() / 1000).toFixed(2), stages: loadingStages,
      modules: ['./main.js', './_framework/dotnet.js', './_framework/dotnet.runtime.js', './_framework/dotnet.native.js']
        .map(name => path(import.meta.resolve(name))), recentModuleRequests: resources }, null, 2)
      + '\n\n' + startupErrors.join('\n\n') + '\n\n';
  }
  failed = true;
  closeSettings();
  settingsToggle.disabled = true;
  document.getElementById('explore-hint').hidden = true;
  loading.hidden = false;
  loading.setAttribute('aria-busy', 'false');
  document.getElementById('loading-percent').textContent = '';
  loadingProgress.hidden = true;
  document.getElementById('loading-note').hidden = true;
  document.getElementById('retry').hidden = false;
  document.getElementById('copy-error').hidden = false;
  document.getElementById('error-details').hidden = false;
  output.textContent = (output.textContent + message + '\n\n').slice(0, 16000);
}

function formatErrorValue(value) {
  if (value instanceof Error) return value.stack ?? `${value.name}: ${value.message}`;
  if (typeof value === 'string') return value;
  try { return JSON.stringify(value, null, 2) ?? String(value); }
  catch { return String(value); }
}

function recordError(message) {
  if (failed || loading.hidden) { showError(message); return; }
  startupErrors.push(message.slice(0, 1000));
  if (startupErrors.length > 8) startupErrors.shift();
}

const originalConsoleError = console.error.bind(console);
console.error = (...values) => {
  originalConsoleError(...values);
  recordError(values.map(formatErrorValue).join(' '));
};
window.addEventListener('error', event => recordError(event.error?.stack ?? event.message ?? 'Script loading failed'));
window.addEventListener('unhandledrejection', event => recordError(formatErrorValue(event.reason)));

document.getElementById('loading-title').textContent = pipeline === 'pbr' ? 'Bistro' : pipeline === 'bunny' ? 'Stanford Bunny' : 'Unlit';
if (pipeline !== 'pbr') document.getElementById('loading-note').textContent = 'Preparing the scene for your device.';
settingsToggle.hidden = pipeline === 'unlit';
if (pipeline === 'bunny') {
  document.getElementById('bunny-credit').hidden = false;
  document.getElementById('credits').hidden = false;
}
if (pipeline === 'pbr') {
  document.getElementById('inspection-distance-row').hidden = true;
  tourControl.hidden = true;
  resetControl.textContent = 'Reset view';
  atmosphereControl.hidden = false;
  document.getElementById('inspection-help').textContent = 'WASD: move · Q/E: down/up · Right-drag or arrows: look · Shift: fast · C: slow · R: reset · M: triangles · B: atmosphere';
  const lodControl = document.getElementById('inspection-lod');
  lodControl.hidden = false;
  lodControl.textContent = finest ? 'Compare automatic LOD' : 'Use full detail';
  lodControl.addEventListener('click', () => {
    lodControl.disabled = true;
    inspectionCommands |= 64;
  });
}
for (const link of document.querySelectorAll('nav a')) {
  if (new URL(link.href).searchParams.get('pipeline') === pipeline) link.setAttribute('aria-current', 'page');
}

try {
  setLoadingState('Loading engine modules', NaN);
  const { dotnet } = await import('./_framework/dotnet.js');
  const args = ['--pipeline', pipeline];
  if (parameters.has('debug')) args.push('--debug', parameters.get('debug'));
  if (parameters.has('distance')) args.push('--distance', parameters.get('distance'));
  if (parameters.has('lod')) args.push('--lod', parameters.get('lod'));
  if (parameters.has('camera')) args.push('--camera', parameters.get('camera'));
  if (pipeline === 'pbr') {
    const scene = new URL(parameters.get('scene') ?? (finest ? 'Assets/BistroFinest.siapbr' : 'Assets/Bistro.siapbr'), location.href);
    if (!parameters.has('scene')) {
      setLoadingState('Checking scene version', NaN);
      const response = await fetch(new URL('Assets/Bistro.assets.json', location.href), { cache: 'no-store' });
      if (!response.ok) throw new Error(`Unable to load scene hashes (${response.status}).`);
      const hashes = await response.json();
      const hash = hashes[finest ? 'BistroFinest.siapbr' : 'Bistro.siapbr'];
      if (typeof hash !== 'string' || !/^[a-f0-9]{64}$/i.test(hash)) throw new Error('Invalid scene hash.');
      scene.searchParams.set('sha256', hash);
      if ('serviceWorker' in navigator) {
        try {
          setLoadingState('Opening scene cache', NaN);
          await navigator.serviceWorker.register(import.meta.resolve('./asset-cache.js'), { updateViaCache: 'none' });
          await navigator.serviceWorker.ready;
          if (!navigator.serviceWorker.controller) {
            await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true }));
          }
        } catch (error) { console.warn('Scene cache unavailable:', error); }
      }
    }
    args.push('--scene', scene.href);
  }
  setLoadingState('Starting engine runtime', NaN);
  const { runMain, Module, setModuleImports } = await dotnet.withApplicationArguments(...args).create();
  Module.canvas = canvas;
  Module.print = console.log;
  Module.printErr = line => console.error('[stderr]', line);
  setModuleImports('main.js', { getCanvasWidth, getCanvasHeight, getBrowserFeatureLevel, setInspectionStatus, setSceneAttribution, compareLodAtCamera, setLoadingState, setSceneReady, takeInspectionCommands, takeInspectionDistance });
  const exitCode = await runMain();
  if (exitCode !== 0) showError(`The engine stopped with exit code ${exitCode}.`);
} catch (error) {
  originalConsoleError('[startup]', error);
  showError(formatErrorValue(error));
}
