import { createBrowserInput } from './browser-input.js';

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
let updateBrowserInput;
let inputPending = false, inputDirty = false;
const controller = document.getElementById('touch-controller');
const input = createBrowserInput(sendBrowserInput, () => {
  if (!loading.hidden || failed) return false;
  closeSettings();
  canvas.focus();
  return true;
}, () => pipeline === 'pbr' && hasCameraFocus(), () => { inspectionCommands |= 4; sendBrowserInput(); });

async function sendBrowserInput() {
  inputDirty = true;
  if (!updateBrowserInput || inputPending) return;
  inputPending = true;
  try {
    while (inputDirty && updateBrowserInput) {
      inputDirty = false;
      const state = input.take();
      await updateBrowserInput(getCanvasWidth(), getCanvasHeight(), takeInspectionCommands(), takeInspectionDistance(),
        state.right, state.forward, state.up, state.yaw, state.pitch, state.turnRight, state.turnUp, state.speed);
    }
  } catch (error) { showError(String(error)); }
  finally { inputPending = false; }
}

function closeSettings() {
  inspection.hidden = true;
  settingsToggle.setAttribute('aria-expanded', 'false');
  controller.hidden = pipeline !== 'pbr' || !loading.hidden || failed;
  sendBrowserInput();
}

settingsToggle.addEventListener('click', () => {
  inspection.hidden = !inspection.hidden;
  settingsToggle.setAttribute('aria-expanded', String(!inspection.hidden));
  if (!inspection.hidden) input.reset();
  controller.hidden = pipeline !== 'pbr' || !inspection.hidden;
  sendBrowserInput();
});
canvas.addEventListener('pointerdown', () => { closeSettings(); canvas.focus(); });
document.getElementById('retry').addEventListener('click', () => location.reload());

function setLoadingState(stage, progress) {
  if (failed) return;
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
  loading.hidden = true;
  loading.setAttribute('aria-busy', 'false');
  inspection.disabled = false;
  settingsToggle.disabled = false;
  controller.hidden = pipeline !== 'pbr';
  canvas.focus();
}

function hasCameraFocus() {
  return !failed && inspection.hidden && document.hasFocus() && document.activeElement === canvas;
}

for (const type of ['resize', 'focus', 'blur']) window.addEventListener(type, sendBrowserInput);
for (const type of ['focus', 'blur']) canvas.addEventListener(type, sendBrowserInput);
distanceControl.addEventListener('input', () => { inspectionDistance = distanceControl.valueAsNumber / 1000; sendBrowserInput(); });
tourControl.addEventListener('click', () => { inspectionCommands |= 1; sendBrowserInput(); });
materialControl.addEventListener('click', () => { inspectionCommands |= 2; sendBrowserInput(); });
resetControl.addEventListener('click', () => { inspectionCommands |= 4; sendBrowserInput(); });
document.getElementById('scene-stop').addEventListener('click', () => { input.reset(); inspectionCommands |= 512; sendBrowserInput(); });
atmosphereControl.addEventListener('click', () => {
  inspectionCommands ^= 32;
  sendBrowserInput();
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

function setInspectionStatus(distance, flags) {
  const touring = (flags & 1) !== 0, triangles = (flags & 2) !== 0, atmosphere = (flags & 4) !== 0;
  atmosphereEnabled = atmosphere;
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
  if (failed) return;
  controller.hidden = true;
  input.reset();
  document.getElementById('loading-title').textContent = loading.hidden ? 'Scene interrupted' : 'Unable to open scene';
  document.getElementById('loading-stage').textContent = message;
  failed = true;
  closeSettings();
  settingsToggle.disabled = true;
  loading.hidden = false;
  loading.setAttribute('aria-busy', 'false');
  document.getElementById('loading-percent').textContent = '';
  loadingProgress.hidden = true;
  document.getElementById('loading-note').hidden = true;
  document.getElementById('retry').hidden = false;
}

document.getElementById('loading-title').textContent = pipeline === 'pbr' ? 'Bistro' : pipeline === 'bunny' ? 'Stanford Bunny' : 'Unlit';
if (pipeline !== 'pbr') document.getElementById('loading-note').textContent = 'Preparing the scene for your device.';
settingsToggle.hidden = pipeline === 'unlit';
if (pipeline === 'bunny') {
  document.getElementById('bunny-credit').hidden = false;
  document.getElementById('credits').hidden = false;
}
if (pipeline === 'pbr') {
  document.getElementById('inspection-distance-row').hidden = true;
  document.getElementById('camera-speed-row').hidden = false;
  tourControl.hidden = true;
  resetControl.textContent = 'Reset view';
  atmosphereControl.hidden = false;
  document.getElementById('inspection-help').textContent = 'Touch: left stick to move, drag the right half to look, hold ↑ / ↓ to change height. Keyboard: WASD to move, Q / E down / up, arrows or right mouse drag to look, Shift to move faster, R to reset.';
  const lodControl = document.getElementById('inspection-lod');
  lodControl.hidden = false;
  lodControl.textContent = finest ? 'Compare automatic LOD' : 'Use full detail';
  lodControl.addEventListener('click', () => {
    lodControl.disabled = true;
    inspectionCommands |= 64;
    sendBrowserInput();
  });
}
for (const link of document.querySelectorAll('nav a')) {
  if (new URL(link.href).searchParams.get('pipeline') === pipeline) link.setAttribute('aria-current', 'page');
}

try {
  const isolationKey = 'sia.engine.isolation-reload';
  if ('serviceWorker' in navigator) {
    try {
      const workerUrl = import.meta.resolve('./asset-cache.js');
      await navigator.serviceWorker.register(workerUrl, { updateViaCache: 'none' });
      if (navigator.serviceWorker.controller?.scriptURL !== workerUrl) {
        await new Promise((resolve, reject) => {
          const changed = () => {
            if (navigator.serviceWorker.controller?.scriptURL !== workerUrl) return;
            clearTimeout(timeout);
            navigator.serviceWorker.removeEventListener('controllerchange', changed);
            resolve();
          };
          const timeout = setTimeout(() => {
            navigator.serviceWorker.removeEventListener('controllerchange', changed);
            reject(new Error('The scene service worker did not take control.'));
          }, 10000);
          navigator.serviceWorker.addEventListener('controllerchange', changed);
          changed();
        });
      }
    } catch (error) {
      if (!crossOriginIsolated) throw error;
      console.warn('Scene cache unavailable:', error);
    }
  }
  if (!crossOriginIsolated) {
    if (!navigator.serviceWorker?.controller || sessionStorage.getItem(isolationKey) === '1') {
      sessionStorage.removeItem(isolationKey);
      throw new Error('The browser could not enable scene processing. Try reloading this page.');
    }
    sessionStorage.setItem(isolationKey, '1');
    location.reload();
    await new Promise(() => {});
  }
  sessionStorage.removeItem(isolationKey);
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
      const hash = finest ? canvas.dataset.bistroFinestSha256 : canvas.dataset.bistroSha256;
      scene.searchParams.set('sha256', hash);
    }
    args.push('--scene', scene.href);
  }
  setLoadingState('Starting engine runtime', NaN);
  // Preload runtime/timer/HTTP workers plus the four bounded CPU decoder workers.
  // Synchronous thread startup cannot wait for a new browser worker to load.
  // Worker-generated traces in this runtime cannot be reused by native UI calls.
  const { runMain, Module, setModuleImports, getAssemblyExports, getConfig } = await dotnet
    .withRuntimeOptions(['--no-jiterpreter-traces-enabled'])
    .withConfig({ pthreadPoolInitialSize: 16 }).withApplicationArguments(...args).create();
  Module.canvas = canvas;
  Module.print = console.log;
  Module.printErr = line => console.error('[stderr]', line);
  setModuleImports('main.js', { getBrowserFeatureLevel, setSceneAttribution, setLoadingState, setSceneReady, showError, setInspectionStatus, compareLodAtCamera });
  const exports = await getAssemblyExports(getConfig().mainAssemblyName);
  updateBrowserInput = exports.Sia.Engine.Example.Program.UpdateBrowserInput;
  await sendBrowserInput();
  const exitCode = await runMain();
  updateBrowserInput = undefined;
  input.reset();
  controller.hidden = true;
  inspection.disabled = true;
  settingsToggle.disabled = true;
  if (exitCode !== 0) showError(`The engine stopped with exit code ${exitCode}.`);
} catch (error) {
  console.error('[startup]', error);
  showError(error instanceof Error ? error.message : String(error));
}
