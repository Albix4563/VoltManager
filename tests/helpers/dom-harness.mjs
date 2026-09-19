export class FakeClassList {
  constructor(initial = []) { this.values = new Set(initial); }
  add(...names) { names.forEach(name => this.values.add(name)); }
  remove(...names) { names.forEach(name => this.values.delete(name)); }
  contains(name) { return this.values.has(name); }
  toggle(name, force) {
    const enabled = force === undefined ? !this.values.has(name) : !!force;
    if (enabled) this.values.add(name);
    else this.values.delete(name);
    return enabled;
  }
}

export class FakeNode {
  constructor({ id = '', dataset = {}, classes = [], tagName = 'DIV' } = {}) {
    this.id = id;
    this.dataset = { ...dataset };
    this.tagName = tagName;
    this.classList = new FakeClassList(classes);
    this.style = {};
    this.children = [];
    this.parentElement = null;
    this.hidden = false;
    this.textContent = '';
    this.value = '';
    this.tabIndex = 0;
    this.disabled = false;
    this.isConnected = true;
    this.attributes = new Map();
    this.events = new Map();
    this.queries = new Map();
    this.focused = false;
  }

  addEventListener(name, handler) {
    if (!this.events.has(name)) this.events.set(name, []);
    this.events.get(name).push(handler);
  }
  removeEventListener(name, handler) {
    const handlers = this.events.get(name) || [];
    const index = handlers.indexOf(handler);
    if (index >= 0) handlers.splice(index, 1);
  }
  dispatchEvent(event) {
    event.target ||= this;
    for (const handler of this.events.get(event.type) || []) handler(event);
    return true;
  }
  listenerCount(name) { return (this.events.get(name) || []).length; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  removeAttribute(name) { this.attributes.delete(name); }
  appendChild(child) {
    child.parentElement = this;
    this.children.push(child);
    return child;
  }
  replaceChildren(...children) {
    this.children = [];
    children.forEach(child => this.appendChild(child));
  }
  registerQuery(selector, value) {
    this.queries.set(selector, Array.isArray(value) ? value : [value]);
    return this;
  }
  querySelector(selector) { return (this.queries.get(selector) || [])[0] || null; }
  querySelectorAll(selector) { return this.queries.get(selector) || []; }
  matches() { return false; }
  closest() { return null; }
  focus() { this.focused = true; }
  scrollIntoView(options) { this.scrollOptions = options; }
}

export function createDocumentHarness({ readyState = 'loading' } = {}) {
  const ids = new Map();
  const selectors = new Map();
  const events = new Map();
  const documentElement = new FakeNode({ tagName: 'HTML' });
  const body = new FakeNode({ tagName: 'BODY' });

  const document = {
    readyState,
    hidden: false,
    documentElement,
    body,
    activeElement: null,
    registerId(node) {
      if (node?.id) ids.set(node.id, node);
      return node;
    },
    registerSelector(selector, value) {
      selectors.set(selector, Array.isArray(value) ? value : [value]);
      return value;
    },
    getElementById(id) { return ids.get(id) || null; },
    querySelector(selector) { return (selectors.get(selector) || [])[0] || null; },
    querySelectorAll(selector) { return selectors.get(selector) || []; },
    createElement(tagName) { return new FakeNode({ tagName: String(tagName).toUpperCase() }); },
    createDocumentFragment() { return new FakeNode({ tagName: '#FRAGMENT' }); },
    addEventListener(name, handler) {
      if (!events.has(name)) events.set(name, []);
      events.get(name).push(handler);
    },
    removeEventListener(name, handler) {
      const handlers = events.get(name) || [];
      const index = handlers.indexOf(handler);
      if (index >= 0) handlers.splice(index, 1);
    },
    dispatchEvent(event) {
      for (const handler of events.get(event.type) || []) handler(event);
      return true;
    },
    listenerCount(name) { return (events.get(name) || []).length; },
    contains(node) { return !!node?.isConnected; },
  };

  const originalAppend = body.appendChild.bind(body);
  body.appendChild = child => {
    originalAppend(child);
    document.registerId(child);
    return child;
  };

  return document;
}

export function event(type, extra = {}) {
  return {
    type,
    defaultPrevented: false,
    preventDefault() { this.defaultPrevented = true; },
    stopImmediatePropagation() {},
    ...extra,
  };
}
