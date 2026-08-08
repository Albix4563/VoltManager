/**
 * Advanced Power Plan Parameter Editor + RAM Cleaner
 * Two new accordion panels in the Power Management view.
 *
 * Advanced Params: reads/writes hidden Windows power settings (processor min/max,
 *   turbo boost mode, PCI Express ASPM) via the Host bridge → powercfg.
 *
 * RAM Cleaner: shows a segmented memory bar (in-use / standby / free) and lets
 *   the user purge the Windows standby cache using NtSetSystemInformation.
 */
(function () {
    if (!Host.available) return;

    // ── State ──────────────────────────────────────────────────────────────────
    let advMounted       = false;
    let timeoutMounted   = false;
    let ramMounted       = false;
    let advWired         = false;
    let timeoutWired     = false;
    let ramWired         = false;
    let advParams        = null;   // current PlanParameterSet from backend
    let timeoutParams    = null;   // current PowerPlanTimeoutSet from backend
    let advSaveTimer     = null;
    let timeoutSaveTimer = null;
    let ramStatus        = null;   // current MemoryStatus from backend
    let ramAutoRefresh   = null;   // setInterval handle
    let ramLastClean     = null;   // timestamp of last purge
    let ramViewActive    = false;
    let advShowDc        = false;  // whether to show battery (DC) column
    let hasBattery       = null;
    let powerPlans       = [];
    let activePlanGuid   = null;
    let timeoutFollowActive = true;
    let advFollowActive = true;

    // ── i18n ──────────────────────────────────────────────────────────────────
    function t(key) {
        return window.I18n && I18n.t ? I18n.t(key) : key;
    }

    function esc(v) {
        const d = document.createElement('div');
        d.textContent = v == null ? '' : String(v);
        return d.innerHTML;
    }

    // ── CSS injection (once) ──────────────────────────────────────────────────
    function ensureAdvStyles() {
        if (document.getElementById('adv-feature-styles')) return;
        const style = document.createElement('style');
        style.id = 'adv-feature-styles';
        style.textContent = `
/* ── Advanced params ─────────────────────────────────────────────── */
.adv-panel{position:relative;overflow:hidden;}
.adv-panel:before{content:"";position:absolute;inset:-40% auto auto -12%;width:300px;height:300px;
  border-radius:999px;background:radial-gradient(circle,rgb(var(--vm-accent-rgb) / .12),transparent 66%);pointer-events:none;}
.adv-param-row{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.03);
  border-radius:16px;padding:16px 18px;transition:border-color .22s,background .22s;}
.adv-param-row:hover{border-color:rgb(var(--vm-accent-rgb) / .22);background:rgba(255,255,255,.055);}
.adv-slider-wrap{display:flex;align-items:center;gap:10px;margin-top:10px;}
.adv-slider{-webkit-appearance:none;appearance:none;width:100%;height:4px;border-radius:999px;
  background:linear-gradient(to right,var(--vm-accent) var(--pct,50%),rgba(255,255,255,.12) var(--pct,50%));
  outline:none;cursor:pointer;}
.adv-slider::-webkit-slider-thumb{-webkit-appearance:none;appearance:none;width:18px;height:18px;
  border-radius:999px;background:linear-gradient(135deg,#f4fbff,#9fb4c8);
  box-shadow:0 4px 12px rgba(0,0,0,.35),inset 0 1px 0 rgba(255,255,255,.8);cursor:pointer;
  transition:transform .15s ease,box-shadow .15s ease;}
.adv-slider::-webkit-slider-thumb:hover{transform:scale(1.2);box-shadow:0 0 0 6px rgb(var(--vm-accent-rgb) / .18),0 4px 12px rgba(0,0,0,.35);}
.adv-slider-val{min-width:42px;text-align:right;font-variant-numeric:tabular-nums;
  color:var(--vm-accent);font-weight:700;font-size:14px;}
.adv-select{background:rgba(18,33,49,.9);color:var(--vm-accent);font-weight:600;padding:8px 12px;
  border-radius:10px;border:1px solid rgb(var(--vm-accent-rgb) / .3);cursor:pointer;outline:none;
  font-size:14px;transition:border-color .2s,box-shadow .2s;}
.adv-select:focus{border-color:var(--vm-accent);box-shadow:0 0 0 3px rgb(var(--vm-accent-rgb) / .15);}
.adv-col-tag{display:inline-flex;align-items:center;gap:5px;padding:3px 9px;border-radius:999px;
  font-size:11px;font-weight:700;letter-spacing:.06em;}
.adv-col-ac{background:rgb(var(--vm-accent-rgb) / .1);color:var(--vm-accent);border:1px solid rgb(var(--vm-accent-rgb) / .25);}
.adv-col-dc{background:rgba(120,180,255,.1);color:#78b4ff;border:1px solid rgba(120,180,255,.25);}
.adv-status-bar{height:4px;border-radius:999px;margin-top:6px;transition:opacity .3s;}
.adv-status-ok{background:var(--vm-accent);opacity:1;}
.adv-status-err{background:#ff6b6b;opacity:1;}
.adv-plan-badge{display:inline-flex;align-items:center;gap:7px;padding:5px 12px;
  border-radius:999px;background:rgb(var(--vm-accent-rgb) / .08);border:1px solid rgb(var(--vm-accent-rgb) / .2);
  color:var(--vm-accent);font-size:12px;font-weight:700;}
.adv-toolbar,.power-timeout-toolbar{display:flex;align-items:flex-end;justify-content:space-between;gap:16px;
  padding:15px 16px;border:1px solid rgba(255,255,255,.09);border-radius:16px;
  background:linear-gradient(135deg,rgba(255,255,255,.045),rgba(255,255,255,.018));}
.adv-plan-field,.power-timeout-plan-field{display:flex;flex-direction:column;gap:7px;min-width:min(100%,300px);}
.adv-plan-select,.power-timeout-select{width:100%;min-height:42px;padding:9px 38px 9px 12px;border-radius:11px;
  border:1px solid rgb(var(--vm-accent-rgb) / .26);background:rgba(10,18,31,.94);color:var(--vm-text);
  font:inherit;font-size:13px;font-weight:650;outline:none;cursor:pointer;}
.adv-plan-select:focus,.power-timeout-select:focus{border-color:var(--vm-accent);box-shadow:0 0 0 3px rgb(var(--vm-accent-rgb) / .13);}
.adv-group{display:flex;flex-direction:column;gap:10px;margin-top:18px;}
.adv-group-head{display:flex;align-items:flex-start;gap:11px;padding:0 2px 2px;}
.adv-group-icon{width:34px;height:34px;display:flex;align-items:center;justify-content:center;border-radius:11px;
  background:rgb(var(--vm-accent-rgb) / .09);border:1px solid rgb(var(--vm-accent-rgb) / .18);color:var(--vm-accent);}
.adv-group-title{font-size:14px;font-weight:800;color:var(--vm-text);}
.adv-group-sub{margin-top:2px;font-size:12px;color:rgba(211,222,239,.62);line-height:1.4;}
.adv-param-row[data-supported="false"]{display:none;}
.power-timeout-panel{display:flex;flex-direction:column;gap:16px;}
.power-timeout-heading{display:flex;align-items:flex-start;gap:12px;}
.power-timeout-heading-icon{width:40px;height:40px;display:flex;align-items:center;justify-content:center;flex:0 0 40px;
  border-radius:13px;background:rgb(var(--vm-accent-rgb) / .1);border:1px solid rgb(var(--vm-accent-rgb) / .2);color:var(--vm-accent);}
.power-timeout-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;}
.power-timeout-card{padding:16px;border:1px solid rgba(255,255,255,.09);border-radius:16px;background:rgba(255,255,255,.025);}
.power-timeout-card-head{display:flex;align-items:flex-start;gap:10px;margin-bottom:14px;}
.power-timeout-card-head>.material-symbols-outlined{color:var(--vm-accent);font-size:21px;}
.power-timeout-side{display:grid;grid-template-columns:minmax(100px,.8fr) minmax(145px,1.2fr);align-items:center;gap:12px;padding-top:10px;}
.power-timeout-side+.power-timeout-side{margin-top:10px;border-top:1px solid rgba(255,255,255,.07);}
.power-timeout-side-label{display:flex;align-items:center;gap:7px;color:rgba(211,222,239,.72);font-size:12px;font-weight:700;}
.power-timeout-status{min-height:18px;font-size:12px;color:rgba(211,222,239,.68);}
.power-timeout-status.is-ok{color:var(--vm-accent);}.power-timeout-status.is-error{color:#ff8585;}
@media(max-width:760px){.power-timeout-grid{grid-template-columns:1fr}.adv-toolbar,.power-timeout-toolbar{align-items:stretch;flex-direction:column}
  .adv-plan-field,.power-timeout-plan-field{min-width:0;width:100%}.power-timeout-side{grid-template-columns:1fr;gap:7px}}
@keyframes advSlideIn{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:translateY(0)}}
.adv-panel>*{animation:advSlideIn .28s ease both;}

/* ── RAM Cleaner ─────────────────────────────────────────────────── */
.ram-panel{position:relative;overflow:hidden;}
.ram-panel:before{content:"";position:absolute;top:-30%;right:-10%;width:260px;height:260px;
  border-radius:999px;background:radial-gradient(circle,rgb(var(--vm-accent-rgb) / .1),transparent 65%);pointer-events:none;}
.ram-bar-outer{width:100%;height:20px;border-radius:999px;overflow:hidden;background:rgba(255,255,255,.06);
  border:1px solid rgba(255,255,255,.1);display:flex;}
.ram-bar-inuse{height:100%;background:linear-gradient(90deg,var(--vm-accent-hover),var(--vm-accent));transition:width .8s ease;}
.ram-bar-standby{height:100%;background:rgb(var(--vm-accent-rgb) / .28);transition:width .8s ease;}
.ram-bar-free{height:100%;flex:1;background:rgba(255,255,255,.06);}
.ram-legend{display:flex;align-items:center;gap:6px;font-size:12px;color:rgba(211,222,239,.72);}
.ram-legend-dot{width:10px;height:10px;border-radius:999px;flex-shrink:0;}
.ram-stat-card{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.03);
  border-radius:14px;padding:14px 16px;text-align:center;}
.ram-stat-val{font-size:22px;font-weight:800;color:var(--vm-accent);font-variant-numeric:tabular-nums;}
.ram-stat-label{font-size:11px;color:rgba(211,222,239,.62);margin-top:2px;text-transform:uppercase;letter-spacing:.06em;}
.ram-btn-clean{display:inline-flex;align-items:center;gap:8px;padding:12px 24px;border-radius:12px;
  font-size:14px;font-weight:700;cursor:pointer;border:0;transition:all .25s ease;}
.ram-btn-clean-active{background:linear-gradient(135deg,var(--vm-accent-hover),var(--vm-accent));color:var(--vm-on-accent);
  box-shadow:0 0 24px rgb(var(--vm-accent-rgb) / .3);}
.ram-btn-clean-active:hover{transform:translateY(-2px);box-shadow:0 0 32px rgb(var(--vm-accent-rgb) / .45);}
.ram-btn-clean-idle{background:rgba(255,255,255,.06);color:rgba(211,222,239,.78);
  border:1px solid rgba(255,255,255,.12);}
.ram-btn-clean-idle:hover{background:rgba(255,255,255,.1);}
.ram-btn-clean:disabled{opacity:.55;cursor:wait;transform:none!important;}
@keyframes ramPulse{0%,100%{box-shadow:0 0 0 0 rgb(var(--vm-accent-rgb) / .4)}50%{box-shadow:0 0 0 8px rgb(var(--vm-accent-rgb) / 0)}}
.ram-btn-cleaning{animation:ramPulse 1s ease infinite;}
        `.trim();
        document.head.appendChild(style);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PLAN CATALOG + DISPLAY/SLEEP TIMEOUTS
    // ══════════════════════════════════════════════════════════════════════════

    async function refreshPlanCatalog() {
        try {
            const plans = await Host.call('listPowerPlans');
            powerPlans = Array.isArray(plans) ? plans : [];
            const active = powerPlans.find(p => p && p.isActive);
            activePlanGuid = active ? active.guid : activePlanGuid;
            fillPlanSelect('power-timeout-plan-select', timeoutParams?.planGuid || activePlanGuid);
            fillPlanSelect('adv-plan-select', advParams?.planGuid || activePlanGuid);
            return powerPlans;
        } catch (err) {
            console.error('listPowerPlans failed', err);
            return powerPlans;
        }
    }

    function planLabel(plan) {
        if (!plan) return '';
        const name = plan.name || plan.planId || plan.guid || '';
        return name + (plan.isActive ? ' · ' + t('power_plan_active') : '');
    }

    function fillPlanSelect(id, selectedGuid) {
        const select = document.getElementById(id);
        if (!select || !powerPlans.length) return;
        const wanted = selectedGuid || activePlanGuid || powerPlans[0]?.guid;
        select.innerHTML = '';
        powerPlans.forEach(plan => {
            const option = document.createElement('option');
            option.value = plan.guid;
            option.textContent = planLabel(plan);
            option.selected = plan.guid === wanted;
            select.appendChild(option);
        });
        if (wanted && [...select.options].some(o => o.value === wanted)) select.value = wanted;
    }

    function formatTimeout(seconds) {
        const value = Math.max(0, Number(seconds) || 0);
        if (value === 0) return t('power_timeout_never');
        if (value % 3600 === 0) {
            const hours = value / 3600;
            return t(hours === 1 ? 'power_timeout_hour' : 'power_timeout_hours').replace('{n}', hours);
        }
        if (value % 60 === 0) {
            const minutes = value / 60;
            return t(minutes === 1 ? 'power_timeout_minute' : 'power_timeout_minutes').replace('{n}', minutes);
        }
        return t('power_timeout_seconds').replace('{n}', value);
    }

    function fillTimeoutSelect(id, currentValue) {
        const select = document.getElementById(id);
        if (!select) return;
        const base = [0, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 14400];
        const value = Math.max(0, Number(currentValue) || 0);
        const values = base.includes(value) ? base : [...base, value].sort((a, b) => a - b);
        select.innerHTML = values.map(v => `<option value="${v}">${esc(formatTimeout(v))}</option>`).join('');
        select.value = String(value);
    }

    function mountTimeoutUi() {
        if (timeoutMounted) return;
        ensureAdvStyles();
        const mount = document.getElementById('power-timeouts-mount');
        if (!mount) return;
        mount.innerHTML = `
<div class="power-timeout-panel" id="power-timeout-panel">
  <div class="power-timeout-heading">
    <span class="power-timeout-heading-icon"><span class="material-symbols-outlined">schedule</span></span>
    <div><h3 class="text-title-md text-on-surface font-semibold" id="power-timeout-title"></h3>
    <p class="text-label-sm text-on-surface-variant mt-1" id="power-timeout-sub"></p></div>
  </div>
  <div class="power-timeout-toolbar">
    <label class="power-timeout-plan-field"><span class="text-label-sm text-on-surface-variant" id="power-timeout-plan-label"></span>
      <select class="power-timeout-select" id="power-timeout-plan-select"></select>
    </label>
    <span class="text-label-sm text-on-surface-variant" id="power-timeout-plan-note"></span>
  </div>
  <div class="power-timeout-grid">
    ${buildTimeoutCard('display','desktop_windows')}
    ${buildTimeoutCard('sleep','bedtime')}
  </div>
  <div class="power-timeout-status" id="power-timeout-status" role="status" aria-live="polite"></div>
</div>`;
        timeoutMounted = true;
        refreshTimeoutLabels();
        checkBatteryPresence();
    }

    function buildTimeoutCard(key, icon) {
        return `<div class="power-timeout-card">
  <div class="power-timeout-card-head"><span class="material-symbols-outlined">${icon}</span><div>
    <p class="text-body-md text-on-surface font-semibold" id="power-timeout-${key}-title"></p>
    <p class="text-label-sm text-on-surface-variant mt-1" id="power-timeout-${key}-sub"></p>
  </div></div>
  <label class="power-timeout-side"><span class="power-timeout-side-label"><span class="material-symbols-outlined text-[17px]">power</span><span id="power-timeout-${key}-ac-label"></span></span>
    <select class="power-timeout-select" id="power-timeout-${key}-ac" data-timeout-key="${key}"></select></label>
  <label class="power-timeout-side timeout-dc-section"><span class="power-timeout-side-label"><span class="material-symbols-outlined text-[17px]">battery_5_bar</span><span id="power-timeout-${key}-dc-label"></span></span>
    <select class="power-timeout-select" id="power-timeout-${key}-dc" data-timeout-key="${key}"></select></label>
</div>`;
    }

    function refreshTimeoutLabels() {
        const map = {
            'power-timeout-title': 'power_timeout_title',
            'power-timeout-sub': 'power_timeout_sub',
            'power-timeout-plan-label': 'power_timeout_plan',
            'power-timeout-plan-note': 'power_timeout_plan_note',
            'power-timeout-display-title': 'power_timeout_display',
            'power-timeout-display-sub': 'power_timeout_display_sub',
            'power-timeout-sleep-title': 'power_timeout_sleep',
            'power-timeout-sleep-sub': 'power_timeout_sleep_sub',
        };
        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = t(key);
        });
        ['display','sleep'].forEach(key => {
            const ac = document.getElementById(`power-timeout-${key}-ac-label`);
            const dc = document.getElementById(`power-timeout-${key}-dc-label`);
            if (ac) ac.textContent = t('adv_ac');
            if (dc) dc.textContent = t('adv_dc');
        });
        if (timeoutParams) applyTimeoutParams(timeoutParams);
        fillPlanSelect('power-timeout-plan-select', timeoutParams?.planGuid || activePlanGuid);
    }

    function applyTimeoutParams(params) {
        timeoutParams = params;
        fillPlanSelect('power-timeout-plan-select', params.planGuid);
        fillTimeoutSelect('power-timeout-display-ac', params.displayTimeoutAc);
        fillTimeoutSelect('power-timeout-display-dc', params.displayTimeoutDc);
        fillTimeoutSelect('power-timeout-sleep-ac', params.sleepTimeoutAc);
        fillTimeoutSelect('power-timeout-sleep-dc', params.sleepTimeoutDc);
    }

    function showTimeoutStatus(message, isError) {
        const el = document.getElementById('power-timeout-status');
        if (!el) return;
        el.textContent = message || '';
        el.className = 'power-timeout-status ' + (isError ? 'is-error' : 'is-ok');
        clearTimeout(timeoutSaveTimer);
        timeoutSaveTimer = setTimeout(() => {
            el.textContent = '';
            el.className = 'power-timeout-status';
        }, 3200);
    }

    async function loadTimeoutParams(planGuid) {
        try {
            const guid = planGuid || document.getElementById('power-timeout-plan-select')?.value || activePlanGuid;
            const params = await Host.call('getPlanTimeouts', guid ? { planGuid: guid } : {});
            if (params?.error) throw new Error(params.error);
            applyTimeoutParams(params);
        } catch (err) {
            showTimeoutStatus(t('power_timeout_load_err'), true);
            console.error('getPlanTimeouts failed', err);
        }
    }

    async function saveTimeoutParam(key) {
        if (!timeoutParams) return;
        const ac = document.getElementById(`power-timeout-${key}-ac`);
        const dc = document.getElementById(`power-timeout-${key}-dc`);
        if (!ac || !dc) return;
        try {
            const result = await Host.call('setPlanParameter', {
                planGuid: timeoutParams.planGuid,
                settingKey: key === 'display' ? 'displayTimeout' : 'sleepTimeout',
                acValue: Number(ac.value),
                dcValue: Number(dc.value),
            });
            if (!result?.success) throw new Error('powercfg rejected setting');
            showTimeoutStatus(t('power_timeout_saved'), false);
        } catch (err) {
            showTimeoutStatus(t('power_timeout_save_err'), true);
            await loadTimeoutParams(timeoutParams.planGuid);
        }
    }

    function wireTimeoutUi() {
        if (timeoutWired) return;
        document.addEventListener('change', e => {
            const el = e.target;
            if (el?.id === 'power-timeout-plan-select') {
                timeoutFollowActive = el.value === activePlanGuid;
                timeoutParams = null;
                loadTimeoutParams(el.value);
                return;
            }
            const key = el?.dataset?.timeoutKey;
            if (key === 'display' || key === 'sleep') saveTimeoutParam(key);
        });
        timeoutWired = true;
    }

    async function activateTimeoutPanel() {
        mountTimeoutUi();
        wireTimeoutUi();
        await refreshPlanCatalog();
        if (!timeoutParams) await loadTimeoutParams(activePlanGuid);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ADVANCED PARAMS — mount / render / wire
    // ══════════════════════════════════════════════════════════════════════════

    function mountAdvancedUi() {
        if (advMounted) return;
        ensureAdvStyles();
        const mount = document.getElementById('advanced-params-mount');
        if (!mount) return;

        mount.innerHTML = buildAdvHtml();
        advMounted = true;
        refreshAdvLabels();
        checkBatteryPresence();
    }

    function buildAdvHtml() {
        const boostOptions = [0,1,2,3,4,5,6].map(v => ({v, k:`adv_boost_${v}`}));
        const pcieOptions = [{v:0,k:'adv_pcie_0'},{v:1,k:'adv_pcie_1'},{v:2,k:'adv_pcie_2'}];
        const diskOptions = [
            {v:0,k:'power_timeout_never'}, {v:60,k:'adv_disk_1m'}, {v:300,k:'adv_disk_5m'},
            {v:600,k:'adv_disk_10m'}, {v:1200,k:'adv_disk_20m'}, {v:1800,k:'adv_disk_30m'}, {v:3600,k:'adv_disk_60m'}
        ];
        const wakeOptions = [{v:0,k:'adv_wake_0'},{v:1,k:'adv_wake_1'},{v:2,k:'adv_wake_2'}];
        return `
<div class="adv-panel relative z-10" id="adv-panel">
  <p class="text-body-md text-on-surface-variant mb-md" id="adv-sub-text"></p>
  <div class="adv-toolbar mb-md">
    <label class="adv-plan-field"><span class="text-label-sm text-on-surface-variant" id="adv-plan-label"></span>
      <select class="adv-plan-select" id="adv-plan-select"></select>
    </label>
    <div class="flex items-center gap-sm" id="adv-toggle-dc-row">
      <div class="mini-toggle cursor-pointer" id="adv-toggle-dc" data-on="false"><div class="mini-toggle-knob"></div></div>
      <span class="text-body-sm text-on-surface" id="adv-show-dc-label"></span>
    </div>
  </div>
  <div class="adv-status-bar opacity-0" id="adv-status-bar"></div>
  <p class="text-label-sm text-on-surface-variant mt-1 mb-md hidden" id="adv-status-msg"></p>
  <div id="adv-loading" class="text-body-md text-on-surface-variant opacity-70 py-6 text-center">
    <span class="material-symbols-outlined text-secondary-container animate-spin inline-block">refresh</span>
  </div>
  <div class="hidden" id="adv-rows">
    ${buildAdvGroup('developer_board','adv_group_cpu','adv_group_cpu_sub', `
      ${buildParamRow('processorMin', 'slider', 0, 100, 5, 'adv-proc-min', 'adv-proc-min-sub', '%')}
      ${buildParamRow('processorMax', 'slider', 0, 100, 100, 'adv-proc-max', 'adv-proc-max-sub', '%')}
      ${buildParamRow('processorEpp', 'slider', 0, 100, 50, 'adv-epp', 'adv-epp-sub', '%')}
      ${buildParamRow('boostMode', 'select', 0, 6, 2, 'adv-boost', 'adv-boost-sub', '', boostOptions)}
      ${buildParamRow('coreParkingMin', 'slider', 0, 100, 10, 'adv-core-parking', 'adv-core-parking-sub', '%')}`)}
    ${buildAdvGroup('memory_alt','adv_group_devices','adv_group_devices_sub', `
      ${buildParamRow('pcieLinkState', 'select', 0, 2, 0, 'adv-pcie', 'adv-pcie-sub', '', pcieOptions)}
      ${buildParamRow('diskIdle', 'select', 0, 3600, 0, 'adv-disk', 'adv-disk-sub', '', diskOptions)}
      ${buildParamRow('wakeTimers', 'select', 0, 2, 2, 'adv-wake', 'adv-wake-sub', '', wakeOptions)}`)}
  </div>
</div>`;
    }

    function buildAdvGroup(icon, titleKey, subKey, content) {
        return `<section class="adv-group">
  <div class="adv-group-head"><span class="adv-group-icon material-symbols-outlined">${icon}</span><div>
    <p class="adv-group-title" data-adv-i18n="${titleKey}"></p><p class="adv-group-sub" data-adv-i18n="${subKey}"></p>
  </div></div>${content}</section>`;
    }

    /**
     * Builds a single parameter row HTML string.
     * @param {string} key   - settingKey sent to the backend
     * @param {string} type  - 'slider' | 'select'
     * @param {number} min
     * @param {number} max
     * @param {number} defVal
     * @param {string} labelKey - i18n key for the title
     * @param {string} subKey   - i18n key for the subtitle
     * @param {string} unit     - '%' or ''
     * @param {Array}  options  - for select only: [{v, k}]
     */
    function buildParamRow(key, type, min, max, defVal, labelKey, subKey, unit, options) {
        const controlAc = type === 'slider'
            ? `<input type="range" class="adv-slider" id="adv-${key}-ac"
                min="${min}" max="${max}" value="${defVal}"
                style="--pct:${Math.round((defVal-min)/(max-min)*100)}%">
               <span class="adv-slider-val" id="adv-${key}-ac-val">${defVal}${unit}</span>`
            : `<select class="adv-select" id="adv-${key}-ac">
                ${(options||[]).map(o=>`<option value="${o.v}" id="adv-opt-${key}-ac-${o.v}"></option>`).join('')}
               </select>`;
        const controlDc = type === 'slider'
            ? `<input type="range" class="adv-slider" id="adv-${key}-dc"
                min="${min}" max="${max}" value="${defVal}"
                style="--pct:${Math.round((defVal-min)/(max-min)*100)}%">
               <span class="adv-slider-val" id="adv-${key}-dc-val">${defVal}${unit}</span>`
            : `<select class="adv-select" id="adv-${key}-dc">
                ${(options||[]).map(o=>`<option value="${o.v}" id="adv-opt-${key}-dc-${o.v}"></option>`).join('')}
               </select>`;

        return `
<div class="adv-param-row" id="adv-row-${key}">
  <div class="flex items-start justify-between gap-md mb-sm">
    <div>
      <p class="text-body-md text-on-surface font-semibold" id="adv-lbl-${key}"></p>
      <p class="text-label-sm text-on-surface-variant mt-1" id="adv-sub-${key}"></p>
    </div>
  </div>
  <div class="space-y-sm">
    <div>
      <span class="adv-col-tag adv-col-ac" id="adv-ac-tag-${key}"></span>
      <div class="adv-slider-wrap">${controlAc}</div>
    </div>
    <div class="adv-dc-section hidden" id="adv-dc-section-${key}">
      <span class="adv-col-tag adv-col-dc" id="adv-dc-tag-${key}"></span>
      <div class="adv-slider-wrap">${controlDc}</div>
    </div>
  </div>
</div>`;
    }

    function refreshAdvLabels() {
        const map = {
            'adv-sub-text': 'adv_sub',
            'adv-plan-label': 'adv_plan_editing',
            'adv-show-dc-label': 'adv_show_dc',
            'adv-lbl-processorMin': 'adv_proc_min', 'adv-sub-processorMin': 'adv_proc_min_sub',
            'adv-lbl-processorMax': 'adv_proc_max', 'adv-sub-processorMax': 'adv_proc_max_sub',
            'adv-lbl-processorEpp': 'adv_epp', 'adv-sub-processorEpp': 'adv_epp_sub',
            'adv-lbl-boostMode': 'adv_boost', 'adv-sub-boostMode': 'adv_boost_sub',
            'adv-lbl-coreParkingMin': 'adv_core_parking', 'adv-sub-coreParkingMin': 'adv_core_parking_sub',
            'adv-lbl-pcieLinkState': 'adv_pcie', 'adv-sub-pcieLinkState': 'adv_pcie_sub',
            'adv-lbl-diskIdle': 'adv_disk', 'adv-sub-diskIdle': 'adv_disk_sub',
            'adv-lbl-wakeTimers': 'adv_wake', 'adv-sub-wakeTimers': 'adv_wake_sub',
        };
        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = t(key);
        });
        document.querySelectorAll('[data-adv-i18n]').forEach(el => {
            el.textContent = t(el.dataset.advI18n);
        });

        const keys = ['processorMin','processorMax','processorEpp','boostMode','coreParkingMin','pcieLinkState','diskIdle','wakeTimers'];
        keys.forEach(k => {
            const ac = document.getElementById(`adv-ac-tag-${k}`);
            const dc = document.getElementById(`adv-dc-tag-${k}`);
            if (ac) ac.textContent = t('adv_ac');
            if (dc) dc.textContent = t('adv_dc');
        });

        const optionGroups = {
            boostMode: [0,1,2,3,4,5,6].map(v => ({v,k:`adv_boost_${v}`})),
            pcieLinkState: [{v:0,k:'adv_pcie_0'},{v:1,k:'adv_pcie_1'},{v:2,k:'adv_pcie_2'}],
            diskIdle: [
                {v:0,k:'power_timeout_never'},{v:60,k:'adv_disk_1m'},{v:300,k:'adv_disk_5m'},
                {v:600,k:'adv_disk_10m'},{v:1200,k:'adv_disk_20m'},{v:1800,k:'adv_disk_30m'},{v:3600,k:'adv_disk_60m'}
            ],
            wakeTimers: [{v:0,k:'adv_wake_0'},{v:1,k:'adv_wake_1'},{v:2,k:'adv_wake_2'}],
        };
        ['ac','dc'].forEach(side => {
            Object.entries(optionGroups).forEach(([key, options]) => {
                options.forEach(o => {
                    const el = document.getElementById(`adv-opt-${key}-${side}-${o.v}`);
                    if (el) el.textContent = t(o.k);
                });
            });
        });
        fillPlanSelect('adv-plan-select', advParams?.planGuid || activePlanGuid);
        updateDcVisibility();
    }

    function updateDcVisibility() {
        const toggle = document.getElementById('adv-toggle-dc');
        if (toggle) toggle.dataset.on = advShowDc ? 'true' : 'false';
        document.querySelectorAll('.adv-dc-section').forEach(el => {
            el.classList.toggle('hidden', !advShowDc);
        });
    }

    function checkBatteryPresence() {
        const info = window.VoltSystemInfo;
        if (info && typeof info.hasBattery === 'boolean') {
            applyBatteryPresence(info.hasBattery);
        } else if (hasBattery == null && Host.available && !checkBatteryPresence._loading) {
            checkBatteryPresence._loading = true;
            Host.call('getSystemInfo').then(systemInfo => {
                if (systemInfo && typeof systemInfo.hasBattery === 'boolean') applyBatteryPresence(systemInfo.hasBattery);
            }).catch(() => {}).finally(() => { checkBatteryPresence._loading = false; });
        }
        if (!checkBatteryPresence._wired) {
            checkBatteryPresence._wired = true;
            document.addEventListener('systeminfoloaded', (e) => {
                if (e?.detail && typeof e.detail.hasBattery === 'boolean') {
                    applyBatteryPresence(e.detail.hasBattery);
                }
            });
            document.addEventListener('voltbatteryavailabilitychanged', (e) => {
                if (e?.detail && typeof e.detail.hasBattery === 'boolean') {
                    applyBatteryPresence(e.detail.hasBattery);
                }
            });
        }
    }

    function applyBatteryPresence(value) {
        if (typeof value === 'boolean') hasBattery = value;
        const hide = hasBattery === false;
        const toggleRow = document.getElementById('adv-toggle-dc-row');
        if (toggleRow) {
            toggleRow.classList.toggle('hidden', hide);
            toggleRow.style.display = hide ? 'none' : '';
            toggleRow.setAttribute('aria-hidden', hide ? 'true' : 'false');
        }
        document.querySelectorAll('.timeout-dc-section').forEach(el => {
            el.classList.toggle('hidden', hide);
            el.style.display = hide ? 'none' : '';
        });
        if (hide) {
            advShowDc = false;
            updateDcVisibility();
        }
    }

    /** Populate controls from the loaded PlanParameterSet */
    function applyAdvParams(params) {
        advParams = params;
        fillPlanSelect('adv-plan-select', params.planGuid);

        setSliderOrSelect('processorMin', 'ac', params.processorMinAc, 0, 100, '%');
        setSliderOrSelect('processorMin', 'dc', params.processorMinDc, 0, 100, '%');
        setSliderOrSelect('processorMax', 'ac', params.processorMaxAc, 0, 100, '%');
        setSliderOrSelect('processorMax', 'dc', params.processorMaxDc, 0, 100, '%');
        setSliderOrSelect('processorEpp', 'ac', params.processorEppAc, 0, 100, '%');
        setSliderOrSelect('processorEpp', 'dc', params.processorEppDc, 0, 100, '%');
        setSliderOrSelect('boostMode', 'ac', params.boostModeAc, 0, 6, '');
        setSliderOrSelect('boostMode', 'dc', params.boostModeDc, 0, 6, '');
        setSliderOrSelect('coreParkingMin', 'ac', params.coreParkingMinAc, 0, 100, '%');
        setSliderOrSelect('coreParkingMin', 'dc', params.coreParkingMinDc, 0, 100, '%');
        setSliderOrSelect('pcieLinkState', 'ac', params.pcieLinkStateAc, 0, 2, '');
        setSliderOrSelect('pcieLinkState', 'dc', params.pcieLinkStateDc, 0, 2, '');
        setSliderOrSelect('diskIdle', 'ac', params.diskIdleAc, 0, 3600, '');
        setSliderOrSelect('diskIdle', 'dc', params.diskIdleDc, 0, 3600, '');
        setSliderOrSelect('wakeTimers', 'ac', params.wakeTimersAc, 0, 2, '');
        setSliderOrSelect('wakeTimers', 'dc', params.wakeTimersDc, 0, 2, '');

        setAdvSupported('processorEpp', params.processorEppSupported);
        setAdvSupported('coreParkingMin', params.coreParkingSupported);
        setAdvSupported('diskIdle', params.diskIdleSupported);
        setAdvSupported('wakeTimers', params.wakeTimersSupported);

        document.getElementById('adv-loading')?.classList.add('hidden');
        document.getElementById('adv-rows')?.classList.remove('hidden');
    }

    function setAdvSupported(key, supported) {
        const row = document.getElementById(`adv-row-${key}`);
        if (row) row.dataset.supported = supported === false ? 'false' : 'true';
    }

    function setSliderOrSelect(key, side, value, min, max, unit) {
        const el = document.getElementById(`adv-${key}-${side}`);
        if (!el) return;
        if (el.tagName === 'INPUT') {
            el.value = value;
            const pct = max > min ? Math.round((value - min) / (max - min) * 100) : 0;
            el.style.setProperty('--pct', pct + '%');
            const valEl = document.getElementById(`adv-${key}-${side}-val`);
            if (valEl) valEl.textContent = value + unit;
        } else {
            const stringValue = String(value);
            if (![...el.options].some(o => o.value === stringValue)) {
                const option = document.createElement('option');
                option.value = stringValue;
                option.textContent = key === 'diskIdle' ? formatTimeout(value) : stringValue;
                el.appendChild(option);
            }
            el.value = stringValue;
        }
    }

    function showAdvStatus(msg, isError) {
        const bar = document.getElementById('adv-status-bar');
        const txt = document.getElementById('adv-status-msg');
        if (!bar || !txt) return;
        bar.className = 'adv-status-bar ' + (isError ? 'adv-status-err' : 'adv-status-ok');
        txt.textContent = msg;
        txt.classList.remove('hidden');
        clearTimeout(advSaveTimer);
        advSaveTimer = setTimeout(() => {
            bar.className = 'adv-status-bar opacity-0';
            txt.classList.add('hidden');
        }, 3000);
    }

    async function loadAdvParams(planGuid) {
        if (!advParams) {
            document.getElementById('adv-loading')?.classList.remove('hidden');
            document.getElementById('adv-rows')?.classList.add('hidden');
        }
        try {
            const guid = planGuid || document.getElementById('adv-plan-select')?.value || activePlanGuid;
            const p = await Host.call('getPlanParameters', guid ? { planGuid: guid } : {});
            if (p?.error) throw new Error(p.error);
            applyAdvParams(p);
        } catch (err) {
            showAdvStatus(t('adv_load_err'), true);
            document.getElementById('adv-loading')?.classList.add('hidden');
            document.getElementById('adv-rows')?.classList.remove('hidden');
            console.error('getPlanParameters failed', err);
        }
    }

    async function saveSingleParam(key, acVal, dcVal) {
        if (!advParams) return;
        try {
            const result = await Host.call('setPlanParameter', {
                planGuid: advParams.planGuid,
                settingKey: key,
                acValue: acVal,
                dcValue: dcVal,
            });
            if (!result?.success) throw new Error('powercfg rejected setting');
            showAdvStatus(t('adv_save_ok'), false);
        } catch (err) {
            showAdvStatus(t('adv_save_err'), true);
            await loadAdvParams(advParams.planGuid);
        }
    }

    function wireAdvancedUi() {
        if (advWired) return;

        // DC toggle
        document.addEventListener('click', e => {
            const dcToggle = e.target.closest('#adv-toggle-dc');
            if (dcToggle) {
                advShowDc = !advShowDc;
                updateDcVisibility();
            }
        });

        // Sliders — live update value display, debounced save
        let sliderTimer = null;
        document.addEventListener('input', e => {
            const el = e.target;
            if (!el || el.tagName !== 'INPUT' || el.type !== 'range') return;
            const match = el.id && el.id.match(/^adv-([\w]+)-(ac|dc)$/);
            if (!match) return;
            const [, key, side] = match;
            const min = +el.min, max = +el.max, val = +el.value;
            const pct = max > min ? Math.round((val - min) / (max - min) * 100) : 0;
            el.style.setProperty('--pct', pct + '%');
            const unit = ['processorMin','processorMax','processorEpp','coreParkingMin'].includes(key) ? '%' : '';
            const valEl = document.getElementById(`adv-${key}-${side}-val`);
            if (valEl) valEl.textContent = val + unit;
            // Debounced save
            clearTimeout(sliderTimer);
            sliderTimer = setTimeout(() => {
                const acEl = document.getElementById(`adv-${key}-ac`);
                const dcEl = document.getElementById(`adv-${key}-dc`);
                if (acEl && dcEl) saveSingleParam(key, +acEl.value, +dcEl.value);
            }, 600);
        });

        // Selects — immediate save
        document.addEventListener('change', e => {
            const el = e.target;
            if (!el || el.tagName !== 'SELECT') return;
            if (el.id === 'adv-plan-select') {
                advFollowActive = el.value === activePlanGuid;
                advParams = null;
                loadAdvParams(el.value);
                return;
            }
            const match = el.id && el.id.match(/^adv-([\w]+)-(ac|dc)$/);
            if (!match) return;
            const [, key] = match;
            const acEl = document.getElementById(`adv-${key}-ac`);
            const dcEl = document.getElementById(`adv-${key}-dc`);
            if (acEl && dcEl) saveSingleParam(key, +acEl.value, +dcEl.value);
        });

        advWired = true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RAM CLEANER — mount / render / wire
    // ══════════════════════════════════════════════════════════════════════════

    function mountRamUi() {
        if (ramMounted) return;
        ensureAdvStyles();
        const mount = document.getElementById('ram-cleaner-mount');
        if (!mount) return;
        mount.innerHTML = buildRamHtml();
        ramMounted = true;
        refreshRamLabels();
    }

    function buildRamHtml() {
        return `
<div class="ram-panel relative z-10" id="ram-panel">
  <div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg">
    <p class="text-body-md text-on-surface-variant max-w-2xl" id="ram-sub-text"></p>
  </div>

  <!-- Memory bar -->
  <div class="mb-md">
    <div class="ram-bar-outer" id="ram-bar-outer">
      <div class="ram-bar-inuse" id="ram-bar-inuse" style="width:0%"></div>
      <div class="ram-bar-standby" id="ram-bar-standby" style="width:0%"></div>
      <div class="ram-bar-free"></div>
    </div>
    <div class="flex items-center gap-lg mt-sm flex-wrap">
      <div class="ram-legend">
        <div class="ram-legend-dot" style="background:linear-gradient(90deg,var(--vm-accent-hover),var(--vm-accent))"></div>
        <span id="ram-legend-inuse"></span>
      </div>
      <div class="ram-legend">
        <div class="ram-legend-dot" style="background:rgb(var(--vm-accent-rgb) / .36)"></div>
        <span id="ram-legend-standby"></span>
      </div>
      <div class="ram-legend">
        <div class="ram-legend-dot" style="background:rgba(255,255,255,.15)"></div>
        <span id="ram-legend-free"></span>
      </div>
    </div>
  </div>

  <!-- Stat cards -->
  <div class="grid grid-cols-3 gap-sm mb-lg" id="ram-stats-grid">
    <div class="ram-stat-card">
      <div class="ram-stat-val" id="ram-val-inuse">—</div>
      <div class="ram-stat-label" id="ram-lbl-inuse"></div>
    </div>
    <div class="ram-stat-card" style="border-color:rgb(var(--vm-accent-rgb) / .18);background:rgb(var(--vm-accent-rgb) / .04);">
      <div class="ram-stat-val" id="ram-val-standby" style="color:var(--vm-accent);"></div>
      <div class="ram-stat-label" id="ram-lbl-standby"></div>
    </div>
    <div class="ram-stat-card">
      <div class="ram-stat-val" id="ram-val-free" style="color:rgba(211,222,239,.7);"></div>
      <div class="ram-stat-label" id="ram-lbl-free"></div>
    </div>
  </div>

  <!-- Auto Cleaner Section -->
  <div class="glass-panel p-md rounded-xl mb-lg flex flex-col sm:flex-row sm:items-center justify-between gap-md hover:border-white/20 transition-all duration-300">
    <div class="flex items-center gap-md flex-1">
      <div class="mini-toggle cursor-pointer" id="ram-toggle-auto" data-on="false">
        <div class="mini-toggle-knob"></div>
      </div>
      <div class="flex-1 flex flex-wrap items-center gap-x-xs gap-y-2 text-body-md text-on-surface">
        <span id="ram-auto-title-lbl" style="font-weight:600;margin-right:10px;">Auto Cleaner</span>
        <span id="ram-auto-threshold-lbl">Threshold (GB):</span>
        <input class="w-16 h-8 bg-surface-container-lowest border-b border-white/20 rounded text-center text-body-md text-secondary-container input-glow transition-all" id="ram-auto-threshold" min="0.5" max="128.0" step="0.5" type="number" value="2.0"/>
        <span id="ram-auto-interval-lbl" style="margin-left:10px;">Interval (min):</span>
        <input class="w-16 h-8 bg-surface-container-lowest border-b border-white/20 rounded text-center text-body-md text-secondary-container input-glow transition-all" id="ram-auto-interval" min="5" max="1440" type="number" value="60"/>
      </div>
    </div>
    <div class="text-right">
      <p class="text-label-sm text-on-surface-variant font-semibold" id="ram-auto-status">Auto: inactive</p>
    </div>
  </div>

  <!-- Total + action -->
  <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-md">
    <div>
      <p class="text-label-sm text-on-surface-variant">
        <span id="ram-total-label"></span>
        <span class="text-on-surface font-semibold ml-1" id="ram-total-val">—</span>
      </p>
      <p class="text-label-sm text-on-surface-variant mt-1">
        <span id="ram-last-clean-label"></span>
        <span class="text-secondary-container ml-1" id="ram-last-clean-val"></span>
      </p>
    </div>
    <button class="ram-btn-clean ram-btn-clean-idle" id="ram-btn-clean" type="button">
      <span class="material-symbols-outlined text-[18px]">cleaning_services</span>
      <span id="ram-btn-label"></span>
    </button>
  </div>

  <!-- Feedback message -->
  <p class="text-label-md text-on-surface-variant mt-md hidden" id="ram-status-msg"></p>
</div>`;
    }

    function refreshRamLabels() {
        const map = {
            'ram-sub-text':      'ram_sub',
            'ram-legend-inuse':  'ram_inuse',
            'ram-legend-standby':'ram_standby',
            'ram-legend-free':   'ram_free',
            'ram-lbl-inuse':     'ram_inuse',
            'ram-lbl-standby':   'ram_standby',
            'ram-lbl-free':      'ram_free',
            'ram-total-label':   'ram_total',
            'ram-last-clean-label': 'ram_last_clean',
            'ram-btn-label':     'ram_btn_clean',
            'ram-auto-title-lbl': 'ram_auto_title',
            'ram-auto-threshold-lbl': 'ram_auto_threshold',
            'ram-auto-interval-lbl': 'ram_auto_interval',
        };
        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = t(key);
        });
        const lastClean = document.getElementById('ram-last-clean-val');
        if (lastClean) lastClean.textContent = ramLastClean ? fmtTime(ramLastClean) : t('ram_never');
        if (ramAutoSettings) {
            applyRamAutoCleanSettings(ramAutoSettings);
        }
    }

    function fmtTime(date) {
        return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    function applyRamStatus(status) {
        ramStatus = status;
        if (!status) return;

        const pctInUse   = Math.max(0, Math.min(100, status.inUsePct));
        const pctStandby = Math.max(0, Math.min(100 - pctInUse, status.standbyPct));

        const barInUse   = document.getElementById('ram-bar-inuse');
        const barStandby = document.getElementById('ram-bar-standby');
        if (barInUse)   barInUse.style.width   = pctInUse + '%';
        if (barStandby) barStandby.style.width  = pctStandby + '%';

        const setVal = (id, gb) => {
            const el = document.getElementById(id);
            if (el) el.textContent = gb.toFixed(1) + ' GB';
        };
        setVal('ram-val-inuse',   status.inUseGb);
        setVal('ram-val-standby', status.standbyGb);
        setVal('ram-val-free',    status.freeGb);

        const total = document.getElementById('ram-total-val');
        if (total) total.textContent = status.totalGb.toFixed(1) + ' GB';

        // Update the clean button style based on standby size
        const btn = document.getElementById('ram-btn-clean');
        if (btn && !btn.disabled) {
            const bigStandby = status.standbyGb > 0.5;
            btn.className = 'ram-btn-clean ' + (bigStandby ? 'ram-btn-clean-active' : 'ram-btn-clean-idle');
        }
    }

    let ramAutoSettings = null;

    function applyRamAutoCleanSettings(settings) {
        ramAutoSettings = settings;
        if (!settings) return;

        const toggle = document.getElementById('ram-toggle-auto');
        if (toggle) toggle.dataset.on = settings.enabled ? 'true' : 'false';

        const thresh = document.getElementById('ram-auto-threshold');
        if (thresh) thresh.value = settings.thresholdGb;

        const interval = document.getElementById('ram-auto-interval');
        if (interval) interval.value = settings.intervalMinutes;

        const statusEl = document.getElementById('ram-auto-status');
        if (statusEl) {
            statusEl.textContent = settings.enabled ? t('ram_auto_enabled') : t('ram_auto_disabled');
            statusEl.style.color = settings.enabled ? 'var(--vm-accent)' : 'rgba(211,222,239,.62)';
        }
    }

    async function loadRamAutoCleanSettings() {
        try {
            const settings = await Host.call('getStandbyAutoCleanSettings');
            applyRamAutoCleanSettings(settings);
            if (settings && settings.lastPurgedUtc) {
                ramLastClean = new Date(settings.lastPurgedUtc);
                const lastEl = document.getElementById('ram-last-clean-val');
                if (lastEl) lastEl.textContent = fmtTime(ramLastClean);
            }
        } catch (err) {
            console.error('getStandbyAutoCleanSettings failed', err);
        }
    }

    function showRamStatus(msg, isError) {
        const el = document.getElementById('ram-status-msg');
        if (!el) return;
        el.textContent = msg;
        el.classList.remove('hidden');
        el.style.color = isError ? '#ff8a80' : 'var(--vm-accent)';
        setTimeout(() => el.classList.add('hidden'), 3500);
    }

    async function loadRamStatus() {
        try {
            const s = await Host.call('getMemoryStatus');
            applyRamStatus(s);
        } catch (err) {
            console.error('getMemoryStatus failed', err);
        }
    }

    function wireRamUi() {
        if (ramWired) return;

        document.addEventListener('click', async e => {
            const btn = e.target.closest('#ram-btn-clean');
            if (!btn) return;
            btn.disabled = true;
            btn.className = 'ram-btn-clean ram-btn-clean-idle ram-btn-cleaning';
            const labelEl = document.getElementById('ram-btn-label');
            if (labelEl) labelEl.textContent = t('ram_cleaning');
            try {
                const res = await Host.call('purgeStandbyList');
                if (res.memory) applyRamStatus(res.memory);
                ramLastClean = new Date();
                const lastEl = document.getElementById('ram-last-clean-val');
                if (lastEl) lastEl.textContent = fmtTime(ramLastClean);
                showRamStatus(t('ram_cleaned'), false);
            } catch (err) {
                showRamStatus(t('ram_err'), true);
            } finally {
                btn.disabled = false;
                btn.className = 'ram-btn-clean ' +
                    ((ramStatus && ramStatus.standbyGb > 0.5) ? 'ram-btn-clean-active' : 'ram-btn-clean-idle');
                if (labelEl) labelEl.textContent = t('ram_btn_clean');
            }
        });

        // Auto Cleaner toggle
        document.addEventListener('click', async e => {
            const toggle = e.target.closest('#ram-toggle-auto');
            if (!toggle || !ramAutoSettings) return;
            const enable = toggle.dataset.on !== 'true';
            toggle.dataset.on = enable ? 'true' : 'false';
            ramAutoSettings.enabled = enable;

            const statusEl = document.getElementById('ram-auto-status');
            if (statusEl) {
                statusEl.textContent = enable ? t('ram_auto_enabled') : t('ram_auto_disabled');
                statusEl.style.color = enable ? 'var(--vm-accent)' : 'rgba(211,222,239,.62)';
            }

            try {
                const res = await Host.call('setStandbyAutoCleanSettings', ramAutoSettings);
                if (res && res.settings) applyRamAutoCleanSettings(res.settings);
            } catch (err) {
                console.error('setStandbyAutoCleanSettings failed', err);
                toggle.dataset.on = (!enable) ? 'true' : 'false';
                ramAutoSettings.enabled = !enable;
                if (statusEl) {
                    statusEl.textContent = (!enable) ? t('ram_auto_enabled') : t('ram_auto_disabled');
                    statusEl.style.color = (!enable) ? 'var(--vm-accent)' : 'rgba(211,222,239,.62)';
                }
            }
        });

        // Auto Cleaner inputs (threshold and interval)
        let autoCleanSaveTimer = null;
        const handleInputChange = () => {
            if (!ramAutoSettings) return;
            const thresholdEl = document.getElementById('ram-auto-threshold');
            const intervalEl = document.getElementById('ram-auto-interval');
            if (!thresholdEl || !intervalEl) return;

            const threshold = parseFloat(thresholdEl.value);
            const interval = parseInt(intervalEl.value, 10);

            if (isNaN(threshold) || isNaN(interval)) return;

            ramAutoSettings.thresholdGb = threshold;
            ramAutoSettings.intervalMinutes = interval;

            clearTimeout(autoCleanSaveTimer);
            autoCleanSaveTimer = setTimeout(async () => {
                try {
                    const res = await Host.call('setStandbyAutoCleanSettings', ramAutoSettings);
                    if (res && res.settings) applyRamAutoCleanSettings(res.settings);
                } catch (err) {
                    console.error('setStandbyAutoCleanSettings failed', err);
                }
            }, 500);
        };

        document.addEventListener('input', e => {
            const el = e.target;
            if (el && (el.id === 'ram-auto-threshold' || el.id === 'ram-auto-interval')) {
                handleInputChange();
            }
        });

        ramWired = true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // View lifecycle — mount + auto-refresh when accordion opens
    // ══════════════════════════════════════════════════════════════════════════

    function isAccordionOpen(mountId) {
        const mount = document.getElementById(mountId);
        return mount && mount.closest('.vm-acc-item[data-open="true"]') !== null;
    }

    async function activateAdvPanel() {
        mountAdvancedUi();
        wireAdvancedUi();
        await refreshPlanCatalog();
        const selected = advParams?.planGuid || document.getElementById('adv-plan-select')?.value || activePlanGuid;
        if (!advParams || (selected && advParams.planGuid !== selected)) await loadAdvParams(selected);
    }

    function activateRamPanel() {
        mountRamUi();
        wireRamUi();
        loadRamStatus();
        loadRamAutoCleanSettings();
        // Auto-refresh every 5 s while panel is open
        clearInterval(ramAutoRefresh);
        ramAutoRefresh = setInterval(loadRamStatus, 5000);
    }

    function stopRamRefresh() {
        clearInterval(ramAutoRefresh);
        ramAutoRefresh = null;
    }

    // Sub-nav (pm-seg) switching — the active panel is driven by .pm-active,
    // not by accordion clicks, so mount on segment selection.
    document.addEventListener('click', e => {
        const seg = e.target.closest('#view-power .pm-seg');
        if (!seg) return;
        const key = seg.dataset.pm;
        // Wait one tick for app.js activatePowerPanel to flip .pm-active.
        setTimeout(() => {
            if (key === 'ram') {
                activateRamPanel();
                return;
            }
            stopRamRefresh();           // leaving ram → stop its polling
            if (key === 'advanced') activateAdvPanel();
        }, 20);
    });

    // Listen to accordion toggle clicks
    document.addEventListener('click', e => {
        const header = e.target.closest('#view-power .vm-acc-header');
        if (!header) return;
        const item = header.closest('.vm-acc-item');
        if (!item) return;

        // Wait one tick for data-open to be updated by power.js accordion handler
        setTimeout(() => {
            const isOpen = item.dataset.open === 'true';
            const hasAdvMount   = !!item.querySelector('#advanced-params-mount');
            const hasRamMount   = !!item.querySelector('#ram-cleaner-mount');

            if (hasAdvMount && isOpen) {
                activateAdvPanel();
            }
            if (hasRamMount) {
                if (isOpen) {
                    activateRamPanel();
                } else {
                    stopRamRefresh();
                }
            }
        }, 20);
    });

    // Also refresh when the legacy power view becomes active.
    document.addEventListener('viewchange', e => {
        if (!e.detail || e.detail.view !== 'power') {
            clearInterval(ramAutoRefresh);
            ramAutoRefresh = null;
            return;
        }
        if (isAccordionOpen('advanced-params-mount')) activateAdvPanel();
        if (isAccordionOpen('ram-cleaner-mount')) {
            mountRamUi();
            wireRamUi();
            loadRamStatus();
            loadRamAutoCleanSettings();
            clearInterval(ramAutoRefresh);
            ramAutoRefresh = setInterval(loadRamStatus, 5000);
        }
    });

    // Reorganized Power Plans view: Alimentazione owns the timeout editor, while
    // Advanced parameters are loaded only when that subview is actually opened.
    document.addEventListener('voltuiviewchanged', e => {
        if (e?.detail?.view !== 'power-plans') return;
        activateTimeoutPanel();
        const advancedPanel = document.querySelector('[data-vm-panel-group="power-plans"][data-vm-panel="advanced"]');
        if (advancedPanel?.classList.contains('active')) activateAdvPanel();
    });

    document.addEventListener('voltuisubviewchanged', e => {
        if (e?.detail?.group !== 'power-plans') return;
        if (e.detail.view === 'source') activateTimeoutPanel();
        if (e.detail.view === 'advanced') activateAdvPanel();
    });

    // Refresh labels on language change.
    document.addEventListener('langchanged', () => {
        if (timeoutMounted) refreshTimeoutLabels();
        if (advMounted) refreshAdvLabels();
        if (ramMounted) refreshRamLabels();
        refreshPlanCatalog();
    });

    // Keep "follow active plan" behavior until the user explicitly chooses a
    // different plan from either selector.
    Host.on('activePlanChanged', async () => {
        await refreshPlanCatalog();
        if (timeoutFollowActive) {
            timeoutParams = null;
            if (timeoutMounted) await loadTimeoutParams(activePlanGuid);
        }
        if (advFollowActive) {
            advParams = null;
            const advancedPanel = document.querySelector('[data-vm-panel-group="power-plans"][data-vm-panel="advanced"]');
            if (isAccordionOpen('advanced-params-mount') || advancedPanel?.classList.contains('active')) {
                await loadAdvParams(activePlanGuid);
            }
        }
    });

    Host.on('standbyAutoCleaned', (freshMemory) => {
        applyRamStatus(freshMemory);
        ramLastClean = new Date();
        const lastEl = document.getElementById('ram-last-clean-val');
        if (lastEl) lastEl.textContent = fmtTime(ramLastClean);
        showRamStatus(t('ram_cleaned'), false);
    });
})();
