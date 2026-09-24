(function () {
  'use strict';

  const $ = id => document.getElementById(id);
  const loginView = $('login-view');
  const appView = $('app-view');
  const loginForm = $('login-form');
  const pinInput = $('pin-input');
  const loginButton = $('login-button');
  const loginError = $('login-error');
  const actionsGrid = $('actions-grid');
  const actionsEmpty = $('actions-empty');
  const feedback = $('action-feedback');
  const connectionPill = $('connection-pill');
  const connectionLabel = $('connection-label');

  let csrfToken = '';
  let eventSource = null;
  let currentState = null;

  function setConnection(online) {
    connectionPill.dataset.state = online ? 'online' : 'offline';
    connectionLabel.textContent = online ? 'Connected' : 'Offline';
  }

  function setFeedback(message, isError = false) {
    feedback.textContent = message || '';
    feedback.dataset.error = isError ? 'true' : 'false';
  }

  function showLogin(message = '') {
    if (eventSource) {
      eventSource.close();
      eventSource = null;
    }
    csrfToken = '';
    currentState = null;
    appView.classList.add('hidden');
    loginView.classList.remove('hidden');
    loginError.textContent = message;
    setConnection(false);
    pinInput.value = '';
    pinInput.focus();
  }

  function showApp() {
    loginView.classList.add('hidden');
    appView.classList.remove('hidden');
    loginError.textContent = '';
    setConnection(true);
  }

  async function api(path, options = {}) {
    const request = { ...options, credentials: 'same-origin', headers: { ...(options.headers || {}) } };
    if (request.body && !request.headers['Content-Type']) request.headers['Content-Type'] = 'application/json';
    if (request.method === 'POST' && csrfToken) request.headers['X-CSRF-Token'] = csrfToken;
    const response = await fetch(path, request);
    if (response.status === 401) {
      showLogin('Session expired. Enter the PIN again.');
      throw new Error('unauthorized');
    }
    if (!response.ok) {
      let code = 'request_failed';
      try { code = (await response.json()).error || code; } catch { }
      const error = new Error(code);
      error.status = response.status;
      throw error;
    }
    return response.status === 204 ? null : response.json();
  }

  function planLabel(plan) {
    if (plan === 'powerSaver') return 'Power saver';
    if (plan === 'balanced') return 'Balanced';
    if (plan === 'performance') return 'Performance';
    return 'Unknown';
  }

  function makeButton(label, icon, onClick, className = 'action-button') {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = className;
    const symbol = document.createElement('span');
    symbol.className = 'material-symbols-outlined';
    symbol.setAttribute('aria-hidden', 'true');
    symbol.textContent = icon;
    const text = document.createElement('span');
    text.textContent = label;
    button.append(symbol, text);
    button.addEventListener('click', onClick);
    return button;
  }

  function makeCard(title, icon, className = '') {
    const card = document.createElement('article');
    card.className = `action-card ${className}`.trim();
    const head = document.createElement('div');
    head.className = 'action-card__head';
    const symbol = document.createElement('span');
    symbol.className = 'material-symbols-outlined';
    symbol.setAttribute('aria-hidden', 'true');
    symbol.textContent = icon;
    const heading = document.createElement('h3');
    heading.textContent = title;
    head.append(symbol, heading);
    card.append(head);
    return card;
  }

  async function runAction(path, body, successMessage) {
    setFeedback('Sending command…');
    try {
      await api(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined });
      setFeedback(successMessage);
      await loadState();
    } catch (error) {
      if (error.message !== 'unauthorized') setFeedback('The command could not be completed.', true);
    }
  }

  function renderPlanCard(state) {
    const card = makeCard('Power plan', 'electric_bolt', 'action-card--plan');
    const options = document.createElement('div');
    options.className = 'plan-options';
    for (const [plan, label] of [['powerSaver', 'Saver'], ['balanced', 'Balanced'], ['performance', 'Performance']]) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'plan-button';
      button.textContent = label;
      button.dataset.active = state.plan === plan ? 'true' : 'false';
      button.setAttribute('aria-pressed', state.plan === plan ? 'true' : 'false');
      button.addEventListener('click', () => runAction('/api/actions/power-plan', { plan }, `${label} plan requested.`));
      options.append(button);
    }
    card.append(options);
    return card;
  }

  function renderState(state) {
    currentState = state;
    $('device-name').textContent = state.device || 'PC';
    $('device-version').textContent = state.version || '—';
    $('active-plan').textContent = planLabel(state.plan);
    actionsGrid.replaceChildren();

    const permissions = state.permissions || {};
    if (permissions.planChange) actionsGrid.append(renderPlanCard(state));
    if (permissions.shutdown) {
      const card = makeCard('Shut down', 'power_settings_new');
      card.append(makeButton('Shut down computer', 'power_settings_new', () => {
        if (window.confirm('Shut down this computer now?')) runAction('/api/actions/shutdown', null, 'Shutdown requested.');
      }, 'action-button danger'));
      actionsGrid.append(card);
    }
    if (permissions.restart) {
      const card = makeCard('Restart', 'restart_alt');
      card.append(makeButton('Restart computer', 'restart_alt', () => {
        if (window.confirm('Restart this computer now?')) runAction('/api/actions/restart', null, 'Restart requested.');
      }, 'action-button danger'));
      actionsGrid.append(card);
    }
    actionsEmpty.classList.toggle('hidden', actionsGrid.children.length > 0);
  }

  async function loadState() {
    try {
      const state = await api('/api/state');
      renderState(state);
      setConnection(true);
      return state;
    } catch (error) {
      if (error.message !== 'unauthorized') {
        setConnection(false);
        setFeedback('Connection lost. Check the local network.', true);
      }
      return null;
    }
  }

  function connectEvents() {
    if (eventSource) eventSource.close();
    eventSource = new EventSource('/api/events');
    eventSource.addEventListener('ready', () => setConnection(true));
    eventSource.addEventListener('state', () => { loadState(); });
    eventSource.onerror = () => {
      setConnection(false);
      window.setTimeout(() => { if (csrfToken) loadState(); }, 800);
    };
  }

  loginForm.addEventListener('submit', async event => {
    event.preventDefault();
    const pin = pinInput.value.trim();
    if (!/^[0-9]{4}$/.test(pin)) {
      loginError.textContent = 'Enter exactly 4 digits.';
      return;
    }
    loginButton.disabled = true;
    loginError.textContent = '';
    try {
      const response = await fetch('/api/auth/login', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin })
      });
      if (response.status === 401) {
        loginError.textContent = 'Incorrect PIN.';
        return;
      }
      if (response.status === 429) {
        loginError.textContent = 'Too many attempts. Try again in a minute.';
        return;
      }
      if (!response.ok) {
        loginError.textContent = 'Could not connect to VoltManager.';
        return;
      }
      const data = await response.json();
      csrfToken = data.csrfToken || '';
      if (!csrfToken) throw new Error('missing_csrf');
      showApp();
      await loadState();
      if (currentState) connectEvents();
    } catch {
      loginError.textContent = 'Could not connect to VoltManager.';
      setConnection(false);
    } finally {
      loginButton.disabled = false;
    }
  });

  pinInput.addEventListener('input', event => {
    event.target.value = event.target.value.replace(/[^0-9]/g, '').slice(0, 4);
  });

  $('logout-button').addEventListener('click', async () => {
    try { await api('/api/auth/logout', { method: 'POST' }); }
    catch (error) { if (error.message !== 'unauthorized') setFeedback('Could not disconnect cleanly.', true); }
    showLogin();
  });

  window.VoltLanRemoteApp = Object.freeze({ renderState, loadState, showLogin });
  showLogin();
})();
