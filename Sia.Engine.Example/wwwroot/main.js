import { readSceneOptions } from './scene-options.js';
import { createSceneUI } from './scene-ui.js';
import { defaultScene } from './example-content.js';

let scene;
let updateBrowserInput;
let commands = 0;
let distance = NaN;
let inputPending = false;
let inputDirty = false;
const ui = createSceneUI({ changed: sendBrowserInput,
  command(value) { commands |= value; sendBrowserInput(); },
  distance(value) { distance = value; sendBrowserInput(); } });

function showError(message) {
  updateBrowserInput = undefined;
  ui.showError(message);
}

async function sendBrowserInput() {
  inputDirty = true;
  if (!updateBrowserInput || inputPending) return;
  inputPending = true;
  try {
    while (inputDirty && updateBrowserInput) {
      inputDirty = false;
      const flags = commands | (Number.isFinite(distance) ? 256 : 0);
      const selectedDistance = distance;
      commands = 0;
      distance = NaN;
      const width = Math.max(1, Math.round(window.innerWidth));
      const height = Math.max(1, Math.round(window.innerHeight));
      await updateBrowserInput(width, height, flags, selectedDistance);
    }
  } catch (error) { showError(String(error)); }
  finally { inputPending = false; }
}

async function enableIsolation() {
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
}

try {
  scene = readSceneOptions(location.href);
  ui.configure(scene);
  await enableIsolation();
  const args = [...scene.args];
  if (scene.quality) {
    const path = scene.sceneUrl ?? await defaultScene(location.href);
    args.push('--scene', new URL(path, location.href).href);
  }
  ui.setLoadingState('Loading engine modules', NaN);
  const { dotnet } = await import('./_framework/dotnet.js');
  ui.setLoadingState('Starting engine runtime', NaN);
  const { runMain, Module, setModuleImports, getAssemblyExports, getConfig } = await dotnet
    .withRuntimeOptions(['--no-jiterpreter-traces-enabled'])
    .withConfig({ pthreadPoolInitialSize: 16 }).withApplicationArguments(...args).create();
  Module.canvas = ui.canvas;
  Module.print = console.log;
  Module.printErr = line => console.error('[stderr]', line);
  setModuleImports('main.js', {
    setSceneAttribution: ui.setSceneAttribution, setLoadingState: ui.setLoadingState,
    setSceneReady: ui.setSceneReady, showError, setInspectionStatus: ui.setInspectionStatus });
  const exports = await getAssemblyExports(getConfig().mainAssemblyName);
  updateBrowserInput = exports.Sia.Engine.Example.Program.UpdateBrowserInput;
  await sendBrowserInput();
  const exitCode = await runMain();
  updateBrowserInput = undefined;
  if (exitCode !== 0) showError(`The engine stopped with exit code ${exitCode}.`);
  else ui.setSceneStopped();
} catch (error) {
  console.error('[startup]', error);
  showError(error instanceof Error ? error.message : String(error));
}
