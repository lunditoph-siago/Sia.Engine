import { dotnet } from './_framework/dotnet.js';

const svgNamespace = 'http://www.w3.org/2000/svg';
const svg = document.getElementById('presentation');
const events = [];
const nodes = new Map();

const roles = [
  'group', 'button', 'link', 'checkbox', 'radio', 'slider', 'textbox',
  'heading', 'list', 'listitem', 'tab', 'tablist', 'tabpanel', 'tree',
  'treeitem', 'region', 'status', 'alert', 'dialog', 'separator'
];
const cursors = ['default', 'pointer', 'text', 'ew-resize', 'ns-resize'];

function queue(kind, key) {
  events.push(`${kind}\n${key}`);
}

function eventNode(event) {
  return event.target.closest('[data-key]');
}

svg.addEventListener('pointerover', event => {
  const node = eventNode(event);
  if (node && !node.contains(event.relatedTarget)) queue('enter', node.dataset.key);
});
svg.addEventListener('pointerout', event => {
  const node = eventNode(event);
  if (node && !node.contains(event.relatedTarget)) queue('leave', node.dataset.key);
});
svg.addEventListener('pointerdown', event => {
  const node = eventNode(event);
  if (!node) return;
  node.setPointerCapture(event.pointerId);
  node.focus();
  queue('down', node.dataset.key);
});
svg.addEventListener('pointerup', event => {
  const node = eventNode(event);
  if (node) queue('up', node.dataset.key);
});
svg.addEventListener('click', event => {
  const node = eventNode(event);
  if (node) queue('activate', node.dataset.key);
});
svg.addEventListener('focusin', event => {
  const node = eventNode(event);
  if (node) queue('focus', node.dataset.key);
});
svg.addEventListener('focusout', event => {
  const node = eventNode(event);
  if (node) queue('blur', node.dataset.key);
});
svg.addEventListener('keydown', event => {
  const node = eventNode(event);
  if (node && (event.key === 'Enter' || event.key === ' ')) {
    event.preventDefault();
    queue('activate', node.dataset.key);
  }
});

function rgba(color, opacity) {
  const channel = value => Math.round(Math.max(0, Math.min(1, value)) * 255);
  const alpha = Math.max(0, Math.min(1, color.A * opacity));
  return `rgba(${channel(color.R)},${channel(color.G)},${channel(color.B)},${alpha})`;
}

function ariaState(element, name, value) {
  if (value === 0) {
    element.removeAttribute(name);
  } else {
    element.setAttribute(name, value === 3 ? 'mixed' : value === 2 ? 'true' : 'false');
  }
}

function wrapLines(value, width, fontSize, wrap) {
  const source = value.split('\n');
  if (!wrap || width <= 0 || fontSize <= 0) return source;
  const limit = Math.max(1, Math.floor(width / (fontSize * 0.55)));
  const result = [];
  for (const line of source) {
    const words = line.split(' ');
    let current = '';
    for (const word of words) {
      const next = current ? `${current} ${word}` : word;
      if (current && next.length > limit) {
        result.push(current);
        current = word;
      } else {
        current = next;
      }
    }
    result.push(current);
  }
  return result;
}

function ensureNode(key) {
  if (nodes.has(key)) return nodes.get(key);
  const group = document.createElementNS(svgNamespace, 'g');
  const rect = document.createElementNS(svgNamespace, 'rect');
  const text = document.createElementNS(svgNamespace, 'text');
  group.dataset.key = key;
  group.append(rect, text);
  svg.append(group);
  const value = { group, rect, text };
  nodes.set(key, value);
  return value;
}

function updateNode(node) {
  const value = ensureNode(node.Key);
  const { group, rect, text } = value;
  group.setAttribute('visibility', node.Visible ? 'visible' : 'hidden');
  group.setAttribute('pointer-events', node.HitTestVisible ? 'all' : 'none');
  group.setAttribute('cursor', cursors[node.Cursor] ?? 'default');
  group.setAttribute('role', roles[node.Role] ?? 'group');
  group.setAttribute('tabindex', node.Focusable ? '0' : '-1');
  group.setAttribute('aria-label', node.Name);
  if (node.Description) group.setAttribute('aria-description', node.Description);
  else group.removeAttribute('aria-description');
  if (node.HeadingLevel > 0) group.setAttribute('aria-level', `${node.HeadingLevel}`);
  else group.removeAttribute('aria-level');
  ariaState(group, 'aria-disabled', node.Disabled);
  ariaState(group, 'aria-readonly', node.ReadOnly);
  ariaState(group, 'aria-selected', node.Selected);
  ariaState(group, 'aria-checked', node.Checked);
  ariaState(group, 'aria-expanded', node.Expanded);

  rect.setAttribute('x', `${node.X}`);
  rect.setAttribute('y', `${node.Y}`);
  rect.setAttribute('width', `${Math.max(0, node.Width)}`);
  rect.setAttribute('height', `${Math.max(0, node.Height)}`);
  rect.setAttribute('rx', `${Math.max(0, node.CornerRadius)}`);
  rect.setAttribute('fill', rgba(node.Background, node.Opacity));
  rect.setAttribute('stroke', rgba(node.Border, node.Opacity));
  rect.setAttribute('stroke-width', `${Math.max(0, node.BorderWidth)}`);

  text.replaceChildren();
  if (!node.Text) return;
  const x = node.X + node.BorderWidth + node.PaddingLeft;
  const y = node.Y + node.BorderWidth + node.PaddingTop + node.FontSize;
  text.setAttribute('x', `${x}`);
  text.setAttribute('y', `${y}`);
  text.setAttribute('fill', rgba(node.Foreground, node.Opacity));
  text.setAttribute('font-family', node.Font === 1 ? 'Noto Sans, sans-serif' : 'sans-serif');
  text.setAttribute('font-size', `${node.FontSize}`);
  text.setAttribute('font-weight', `${node.FontWeight || 400}`);
  text.setAttribute('font-style', node.Italic ? 'italic' : 'normal');
  const available = node.Width - node.PaddingLeft * 2 - node.BorderWidth * 2;
  const lines = wrapLines(node.Text, available, node.FontSize, node.Wrap);
  lines.forEach((line, index) => {
    const span = document.createElementNS(svgNamespace, 'tspan');
    span.setAttribute('x', `${x}`);
    span.setAttribute('dy', index === 0 ? '0' : '1.25em');
    span.textContent = line;
    text.append(span);
  });
}

function renderFrame(json) {
  const frame = JSON.parse(json);
  svg.setAttribute('width', `${frame.Width}`);
  svg.setAttribute('height', `${frame.Height}`);
  svg.setAttribute('viewBox', `0 0 ${frame.Width} ${frame.Height}`);
  const active = new Set();
  for (const node of frame.Nodes) {
    active.add(node.Key);
    updateNode(node);
  }
  for (const [key, value] of nodes) {
    if (active.has(key)) continue;
    value.group.remove();
    nodes.delete(key);
  }
}

function dequeueEvent() {
  return events.shift() ?? null;
}

function getViewportWidth() {
  return Math.max(1, Math.floor(document.documentElement.clientWidth));
}

function getViewportHeight() {
  return Math.max(1, Math.floor(document.documentElement.clientHeight));
}

const { runMain, setModuleImports } = await dotnet.create();
setModuleImports('main.js', {
  renderFrame,
  dequeueEvent,
  getViewportWidth,
  getViewportHeight,
});
await runMain();
