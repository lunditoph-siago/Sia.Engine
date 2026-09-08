import { dotnet } from './_framework/dotnet.js';

const canvas = document.getElementById('canvas');
canvas.addEventListener('pointerdown', () => canvas.focus());
window.addEventListener('keydown', event => {
  if (event.target !== canvas) event.stopImmediatePropagation();
}, true);

const inspection = document.getElementById('inspection');
const distanceControl = document.getElementById('inspection-distance');
const tourControl = document.getElementById('inspection-tour');
const materialControl = document.getElementById('inspection-material');
const resetControl = document.getElementById('inspection-reset');
let atmosphereEnabled = new URLSearchParams(location.search).get('atmosphere') === 'on';
let inspectionCommands = atmosphereEnabled ? 32 : 0;
let inspectionDistance = NaN;
distanceControl.addEventListener('input', () => { inspectionDistance = distanceControl.valueAsNumber / 1000; });
tourControl.addEventListener('click', () => { inspectionCommands |= 1; });
materialControl.addEventListener('click', () => { inspectionCommands |= 2; });
resetControl.addEventListener('click', () => { inspectionCommands |= 4; });
document.getElementById('inspection-atmosphere').addEventListener('click', () => {
  atmosphereEnabled = !atmosphereEnabled;
  inspectionCommands ^= 32;
});

function takeInspectionCommands() {
  const commands = inspectionCommands;
  inspectionCommands = 0;
  return commands;
}

function takeInspectionDistance() {
  const distance = inspectionDistance;
  inspectionDistance = NaN;
  return distance;
}

function setInspectionStatus(status, distance, touring, triangles, atmosphere) {
  atmosphereEnabled = atmosphere !== ((inspectionCommands & 32) !== 0);
  document.getElementById('inspection-status').textContent = status;
  distanceControl.value = Math.round(distance * 1000);
  tourControl.textContent = touring ? 'Pause tour' : 'Resume tour';
  materialControl.textContent = triangles ? 'Show shaded' : 'Show triangles';
  inspection.disabled = false;
}

function getCanvasWidth() {
  return Math.max(1, Math.round(window.innerWidth));
}

function setSceneAttribution(attribution) {
  const credit = document.getElementById('pbr-credit');
  credit.textContent = attribution;
  credit.hidden = false;
}

function getCanvasHeight() {
  return Math.max(1, Math.round(window.innerHeight));
}

function getBrowserFeatureLevel() {
  return new URLSearchParams(location.search).get('feature-level') ?? 'compatibility';
}

function showError(message) {
  let overlay = document.getElementById('error-overlay');
  if (!overlay) {
    overlay = document.createElement('pre');
    overlay.id = 'error-overlay';
    overlay.style.cssText = 'position:fixed;right:10px;bottom:10px;max-width:600px;max-height:50vh;overflow:auto;margin:0;padding:10px;background:#222;color:#f88;border-radius:6px;font:12px monospace;white-space:pre-wrap;z-index:99999;';
    overlay.setAttribute('role', 'alert');
    overlay.setAttribute('aria-live', 'assertive');
    document.body.appendChild(overlay);
  }
  overlay.textContent = (overlay.textContent + message + '\n\n').slice(-16000);
  overlay.scrollTop = overlay.scrollHeight;
}

function formatErrorValue(value) {
  if (value instanceof Error) {
    return value.stack ?? `${value.name}: ${value.message}`;
  }
  if (typeof value === 'string') {
    return value;
  }

  try {
    return JSON.stringify(value, null, 2) ?? String(value);
  } catch {
    return String(value);
  }
}

const originalConsoleError = console.error.bind(console);
console.error = (...values) => {
  originalConsoleError(...values);
  showError('[console.error] ' + values.map(formatErrorValue).join(' '));
};

window.addEventListener('error', e => showError('[error] ' + (e.error?.stack ?? e.message)));
window.addEventListener('unhandledrejection', e => showError('[unhandledrejection] ' + (e.reason?.stack ?? e.reason)));

try {
  const parameters = new URLSearchParams(window.location.search);
  const pipeline = parameters.get('pipeline') ?? 'pbr';
  const args = ['--pipeline', pipeline];
  if (parameters.has('debug')) args.push('--debug', parameters.get('debug'));
  if (parameters.has('distance')) args.push('--distance', parameters.get('distance'));
  if (parameters.has('lod')) args.push('--lod', parameters.get('lod'));
  if (pipeline === 'pbr') args.push('--scene', new URL(parameters.get('scene') ?? 'Assets/Bistro.siapbr', location.href).href);
  if (pipeline === 'bunny' || pipeline === 'pbr') {
    inspection.hidden = false;
    if (pipeline === 'bunny') document.getElementById('bunny-credit').hidden = false;
    document.getElementById('inspection-atmosphere').hidden = pipeline !== 'pbr';
  }
  if (pipeline === 'pbr') {
    const lodControl = document.getElementById('inspection-lod');
    const finest = parameters.get('lod') === 'finest';
    lodControl.hidden = false;
    lodControl.textContent = finest ? 'Compare auto LOD' : 'Compare finest';
    lodControl.addEventListener('click', () => {
      const url = new URL(location.href);
      url.searchParams.set('lod', finest ? 'auto' : 'finest');
      url.searchParams.set('distance', distanceControl.valueAsNumber / 1000);
      url.searchParams.set('debug', materialControl.textContent === 'Show shaded' ? 'triangles' : 'shaded');
      url.searchParams.set('atmosphere', atmosphereEnabled ? 'on' : 'off');
      location.href = url.href;
    });
  }
  for (const link of document.querySelectorAll('nav a')) {
    if (new URL(link.href).searchParams.get('pipeline') === pipeline) link.setAttribute('aria-current', 'page');
  }
  const { runMain, Module, setModuleImports } = await dotnet
    .withApplicationArguments(...args)
    .create();
  Module.canvas = canvas;
  Module.print = console.log;
  Module.printErr = line => console.error('[stderr]', line);
  setModuleImports('main.js', { getCanvasWidth, getCanvasHeight, getBrowserFeatureLevel, setInspectionStatus, setSceneAttribution, takeInspectionCommands, takeInspectionDistance });
  canvas.focus();
  await runMain();
} catch (error) {
  console.error('[startup]', error);
}
