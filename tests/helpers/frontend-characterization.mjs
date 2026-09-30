import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { FakeNode, createDocumentHarness, event } from './dom-harness.mjs';

export const frontendSource = name => readFileSync(
  new URL(`../../src/VoltManager/wwwroot/js/${name}`, import.meta.url),
  'utf8',
);

const camelData = name => name.replace(/-([a-z])/g, (_, ch) => ch.toUpperCase());

export function richNode(document, options = {}) {
  const node = new FakeNode(options);
  node._innerHTML = '';
  node.className = '';
  node.style = {
    setProperty(name, value) { this[name] = String(value); },
    getPropertyValue(name) { return this[name] || ''; },
  };
  node.contains = other => {
    if (other === node) return true;
    return node.children.some(child => child.contains ? child.contains(other) : child === other);
  };
  node.insertBefore = (child, reference) => {
    child.parentElement = node;
    const index = node.children.indexOf(reference);
    if (index < 0) node.children.push(child);
    else node.children.splice(index, 0, child);
    document.registerId(child);
    return child;
  };
  node.remove = () => {
    node.isConnected = false;
    if (!node.parentElement) return;
    const index = node.parentElement.children.indexOf(node);
    if (index >= 0) node.parentElement.children.splice(index, 1);
  };
  node.click = () => node.dispatchEvent(event('click', { currentTarget: node }));
  node.getBoundingClientRect = () => ({ top: 0, left: 0, width: 120, height: 40, right: 120, bottom: 40 });
  node.setPointerCapture = () => {};
  node.releasePointerCapture = () => {};
  node.closest = selector => {
    if (selector === `#${node.id}`) return node;
    if (selector === 'button' && node.tagName === 'BUTTON') return node;
    if (selector.startsWith('[data-') && selector.endsWith(']')) {
      const key = camelData(selector.slice(6, -1));
      return Object.hasOwn(node.dataset, key) ? node : null;
    }
    return null;
  };
  Object.defineProperty(node, 'options', {
    get() { return node.children.filter(child => child.tagName === 'OPTION'); },
  });
  Object.defineProperty(node, 'firstElementChild', {
    get() { return node.children[0] || null; },
  });
  Object.defineProperty(node, 'innerHTML', {
    get() { return node._innerHTML; },
    set(html) {
      node._innerHTML = String(html ?? '');
      const tags = [...node._innerHTML.matchAll(/<([a-z0-9-]+)\b([^>]*)>/gi)];
      for (const [, tag, attrs] of tags) {
        const id = /\bid="([^"]+)"/.exec(attrs)?.[1];
        if (!id) continue;
        let child = document.getElementById(id);
        if (!child) {
          child = richNode(document, { id, tagName: tag.toUpperCase() });
          document.registerId(child);
        }
        for (const match of attrs.matchAll(/\bdata-([a-z0-9-]+)="([^"]*)"/gi)) {
          child.dataset[camelData(match[1])] = match[2];
        }
        const classAttr = /\bclass="([^"]*)"/.exec(attrs)?.[1];
        if (classAttr) classAttr.split(/\s+/).filter(Boolean).forEach(name => child.classList.add(name));
        if (!child.parentElement) node.appendChild(child);
      }
    },
  });
  return node;
}

export function createHost(responses = {}) {
  const calls = [];
  const handlers = new Map();
  const failures = [];
  const Host = {
    available: true,
    call(method, payload) {
      calls.push({ method, payload });
      const value = responses[method];
      try {
        return Promise.resolve(typeof value === 'function' ? value(payload, calls) : value ?? {});
      } catch (error) {
        return Promise.reject(error);
      }
    },
    on(name, handler) {
      if (!handlers.has(name)) handlers.set(name, []);
      handlers.get(name).push(handler);
    },
    fail(error, callback) {
      failures.push(error);
      callback?.(error?.message || String(error));
    },
  };
  return { Host, calls, handlers, failures };
}

export function createFrontendHarness({
  script,
  search = '',
  responses = {},
  ids = {},
  selectors = {},
  beforeRun,
  preload = [],
  windowValues = {},
} = {}) {
  const source = frontendSource(script);
  const document = createDocumentHarness({ readyState: 'complete' });
  document.head = richNode(document, { tagName: 'HEAD' });
  const nodes = new Map();
  const install = (id, node = richNode(document, { id })) => {
    nodes.set(id, node);
    document.registerId(node);
    return node;
  };
  for (const id of new Set([...source.matchAll(/getElementById\(['"]([^'"]+)['"]\)/g)].map(match => match[1]))) {
    install(id);
  }
  for (const [id, node] of Object.entries(ids)) install(id, node);
  for (const [selector, value] of Object.entries(selectors)) document.registerSelector(selector, value);
  const body = document.body;
  body.style = { setProperty(name, value) { this[name] = String(value); } };
  body.contains = node => node === body || body.children.includes(node);
  body.insertBefore = (child, reference) => {
    child.parentElement = body;
    const index = body.children.indexOf(reference);
    if (index < 0) body.children.push(child); else body.children.splice(index, 0, child);
    document.registerId(child);
    return child;
  };

  const host = createHost(responses);
  const timers = new Map();
  const intervals = new Map();
  let timerId = 0;
  const I18n = {
    t: key => key,
    feature: (_area, key) => key,
    format: (template, values) => String(template).replace(/\{(\w+)\}/g, (_, key) => values?.[key] ?? ''),
    getLang: () => 'it',
    getLocale: () => 'it-IT',
    setLang() {},
    apply() {},
  };
  const lifecycleEntries = new Map();
  const VoltViewLifecycle = {
    register(name, config) { lifecycleEntries.set(name, config); },
    route: () => ({ view: 'home', subviews: {} }),
    transition() {},
  };
  const window = {
    Host: host.Host,
    I18n,
    VoltViewLifecycle,
    matchMedia: () => ({ matches: true, addEventListener() {}, removeEventListener() {} }),
    addEventListener() {},
    removeEventListener() {},
    ...windowValues,
  };
  window.window = window;
  window.document = document;
  const context = vm.createContext({
    window,
    document,
    Host: host.Host,
    I18n,
    VoltViewLifecycle,
    location: { search },
    URLSearchParams,
    CustomEvent: class CustomEvent {
      constructor(type, init = {}) { this.type = type; this.detail = init.detail; this.target = null; }
    },
    Event: class Event { constructor(type) { this.type = type; } },
    console,
    Intl,
    Date,
    Math,
    Promise,
    setTimeout(handler, delay = 0) {
      const id = ++timerId;
      timers.set(id, { handler, delay });
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
    setInterval(handler, delay = 0) {
      const id = ++timerId;
      intervals.set(id, { handler, delay });
      return id;
    },
    clearInterval(id) { intervals.delete(id); },
    requestAnimationFrame(handler) { return ++timerId && timers.set(timerId, { handler, delay: 0 }), timerId; },
    cancelAnimationFrame(id) { timers.delete(id); },
    getComputedStyle: () => ({ animationDuration: '0s', animationDelay: '0s' }),
    localStorage: { getItem: () => null, setItem() {} },
  });
  Object.assign(context, windowValues);
  beforeRun?.({ document, nodes, window, context, host, lifecycleEntries, richNode: opts => richNode(document, opts) });
  for (const file of preload) {
    vm.runInContext(frontendSource(file), context, { filename: file });
    for (const key of ['VoltManagerBatteryHistory']) {
      if (context[key] && !window[key]) window[key] = context[key];
    }
  }
  vm.runInContext(source, context, { filename: script });
  return {
    source,
    document,
    nodes,
    window,
    context,
    I18n,
    host,
    lifecycleEntries,
    timers,
    intervals,
    runTimers(kind = 'timeout') {
      const map = kind === 'interval' ? intervals : timers;
      const pending = [...map.entries()];
      if (kind === 'timeout') map.clear();
      for (const [, item] of pending) item.handler();
    },
  };
}

export const flush = () => new Promise(resolve => setImmediate(resolve));
export { event };
