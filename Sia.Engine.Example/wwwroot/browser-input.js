export function createBrowserInput(changed, activate, focused, resetView) {
  const state = { right: 0, forward: 0, up: 0, yaw: 0, pitch: 0, speed: 4 };
  const releases = [];
  const keys = new Set();
  const movementKeys = new Set(['KeyW', 'KeyA', 'KeyS', 'KeyD', 'KeyQ', 'KeyE',
    'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'ShiftLeft', 'ShiftRight']);
  window.addEventListener('keydown', event => {
    if (!focused() || event.ctrlKey || event.altKey || event.metaKey || event.isComposing) return;
    if (event.code === 'KeyR') {
      event.preventDefault();
      if (!event.repeat) resetView();
    } else if (movementKeys.has(event.code)) {
      event.preventDefault();
      if (keys.has(event.code)) return;
      keys.add(event.code);
      changed();
    }
  });
  window.addEventListener('keyup', event => {
    if (keys.delete(event.code)) changed();
  });

  function stick(id, horizontal, vertical) {
    const pad = document.getElementById(id);
    let pointer = null, bounds;
    function move(event) {
      if (event.pointerId !== pointer) return;
      const radius = bounds.width * .32;
      let x = (event.clientX - bounds.left - bounds.width / 2) / radius;
      let y = (bounds.top + bounds.height / 2 - event.clientY) / radius;
      const length = Math.hypot(x, y);
      const magnitude = Math.max(0, Math.min(1, (length - .08) / .92));
      x = length ? x / length * magnitude : 0;
      y = length ? y / length * magnitude : 0;
      if (state[horizontal] === x && state[vertical] === y) return;
      state[horizontal] = x; state[vertical] = y;
      pad.style.setProperty('--stick-x', `${x * radius}px`);
      pad.style.setProperty('--stick-y', `${-y * radius}px`);
      changed();
    }
    function release(event) {
      if (pointer === null || (event && event.pointerId !== pointer)) return;
      const previous = pointer;
      pointer = null;
      state[horizontal] = 0; state[vertical] = 0;
      pad.style.setProperty('--stick-x', '0px');
      pad.style.setProperty('--stick-y', '0px');
      if (pad.hasPointerCapture(previous)) pad.releasePointerCapture(previous);
      changed();
    }
    pad.addEventListener('pointerdown', event => {
      if (pointer !== null || event.button !== 0 || !activate()) return;
      event.preventDefault();
      pointer = event.pointerId;
      bounds = pad.getBoundingClientRect();
      pad.setPointerCapture(pointer);
      move(event);
    });
    pad.addEventListener('pointermove', move);
    for (const type of ['pointerup', 'pointercancel', 'lostpointercapture']) pad.addEventListener(type, release);
    releases.push(() => release());
  }

  let up = false, down = false;
  function elevation(id, direction) {
    const button = document.getElementById(id);
    let pointer = null;
    function update(held) {
      if (direction > 0) up = held; else down = held;
      state.up = Number(up) - Number(down);
      button.classList.toggle('held', held);
      changed();
    }
    function release(event) {
      if (pointer === null || (event && event.pointerId !== pointer)) return;
      const previous = pointer;
      pointer = null;
      if (button.hasPointerCapture(previous)) button.releasePointerCapture(previous);
      update(false);
    }
    button.addEventListener('pointerdown', event => {
      if (pointer !== null || event.button !== 0 || !activate()) return;
      event.preventDefault();
      pointer = event.pointerId;
      button.setPointerCapture(pointer);
      update(true);
    });
    for (const type of ['pointerup', 'pointercancel', 'lostpointercapture']) button.addEventListener(type, release);
    releases.push(() => release());
  }

  stick('move-pad', 'right', 'forward');
  const look = document.getElementById('look-zone');
  const canvas = document.getElementById('canvas');
  let lookPointer = null, lookSurface, previousX = 0, previousY = 0;
  function moveLook(event) {
    if (event.pointerId !== lookPointer) return;
    state.yaw += event.clientX - previousX;
    state.pitch += previousY - event.clientY;
    previousX = event.clientX; previousY = event.clientY;
    changed();
  }
  function releaseLook(event) {
    if (lookPointer === null || (event && event.pointerId !== lookPointer)) return;
    const previous = lookPointer;
    lookPointer = null;
    if (lookSurface.hasPointerCapture(previous)) lookSurface.releasePointerCapture(previous);
  }
  for (const surface of [look, canvas]) {
    surface.addEventListener('pointerdown', event => {
      if (surface === look && event.pointerType === 'mouse' && event.button === 0) {
        if (activate()) event.preventDefault();
        return;
      }
      const drag = event.pointerType === 'mouse' ? event.button === 2 : surface === look && event.button === 0;
      if (lookPointer !== null || !drag || !activate()) return;
      event.preventDefault();
      lookPointer = event.pointerId;
      lookSurface = surface;
      previousX = event.clientX; previousY = event.clientY;
      surface.setPointerCapture(lookPointer);
    });
    surface.addEventListener('pointermove', moveLook);
    surface.addEventListener('contextmenu', event => event.preventDefault());
    for (const type of ['pointerup', 'pointercancel', 'lostpointercapture']) surface.addEventListener(type, releaseLook);
  }
  releases.push(() => releaseLook());
  elevation('move-up', 1);
  elevation('move-down', -1);
  document.getElementById('camera-speed').addEventListener('input', event => {
    state.speed = event.target.valueAsNumber;
    changed();
  });
  const reset = () => {
    keys.clear();
    state.yaw = 0; state.pitch = 0;
    for (const release of releases) release();
    changed();
  };
  canvas.addEventListener('blur', reset);
  window.addEventListener('blur', reset);
  window.addEventListener('resize', reset);
  document.addEventListener('visibilitychange', () => { if (document.hidden) reset(); });
  function take() {
    const axis = (positive, negative) => Number(keys.has(positive)) - Number(keys.has(negative));
    const clamp = value => Math.max(-1, Math.min(1, value));
    const snapshot = {
      ...state,
      right: clamp(state.right + axis('KeyD', 'KeyA')),
      forward: clamp(state.forward + axis('KeyW', 'KeyS')),
      up: clamp(state.up + axis('KeyE', 'KeyQ')),
      turnRight: axis('ArrowRight', 'ArrowLeft'),
      turnUp: axis('ArrowUp', 'ArrowDown'),
      speed: state.speed * (keys.has('ShiftLeft') || keys.has('ShiftRight') ? 4 : 1)
    };
    state.yaw = 0; state.pitch = 0;
    return snapshot;
  }
  return { reset, take };
}
