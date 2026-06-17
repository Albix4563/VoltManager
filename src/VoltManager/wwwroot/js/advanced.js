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
    let advMounted     = false;
    let ramMounted     = false;
    let advWired       = false;
    let ramWired       = false;
    let advParams      = null;   // current PlanParameterSet from backend
    let advSaveTimer   = null;
    let ramStatus      = null;   // current MemoryStatus from backend
    let ramAutoRefresh = null;   // setInterval handle
    let ramLastClean   = null;   // timestamp of last purge
    let ramViewActive  = false;
    let advShowDc      = false;  // whether to show battery (DC) column

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
  border-radius:999px;background:radial-gradient(circle,rgba(0,241,254,.12),transparent 66%);pointer-events:none;}
.adv-param-row{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.03);
  border-radius:16px;padding:16px 18px;transition:border-color .22s,background .22s;}
.adv-param-row:hover{border-color:rgba(0,241,254,.22);background:rgba(255,255,255,.055);}
.adv-slider-wrap{display:flex;align-items:center;gap:10px;margin-top:10px;}
.adv-slider{-webkit-appearance:none;appearance:none;width:100%;height:4px;border-radius:999px;
  background:linear-gradient(to right,#00f1fe var(--pct,50%),rgba(255,255,255,.12) var(--pct,50%));
  outline:none;cursor:pointer;}
.adv-slider::-webkit-slider-thumb{-webkit-appearance:none;appearance:none;width:18px;height:18px;
  border-radius:999px;background:linear-gradient(135deg,#f4fbff,#9fb4c8);
  box-shadow:0 4px 12px rgba(0,0,0,.35),inset 0 1px 0 rgba(255,255,255,.8);cursor:pointer;
  transition:transform .15s ease,box-shadow .15s ease;}
.adv-slider::-webkit-slider-thumb:hover{transform:scale(1.2);box-shadow:0 0 0 6px rgba(0,241,254,.18),0 4px 12px rgba(0,0,0,.35);}
.adv-slider-val{min-width:42px;text-align:right;font-variant-numeric:tabular-nums;
  color:#00f1fe;font-weight:700;font-size:14px;}
.adv-select{background:rgba(18,33,49,.9);color:#00f1fe;font-weight:600;padding:8px 12px;
  border-radius:10px;border:1px solid rgba(0,241,254,.3);cursor:pointer;outline:none;
  font-size:14px;transition:border-color .2s,box-shadow .2s;}
.adv-select:focus{border-color:#00f1fe;box-shadow:0 0 0 3px rgba(0,241,254,.15);}
.adv-col-tag{display:inline-flex;align-items:center;gap:5px;padding:3px 9px;border-radius:999px;
  font-size:11px;font-weight:700;letter-spacing:.06em;}
.adv-col-ac{background:rgba(0,241,254,.1);color:#00f1fe;border:1px solid rgba(0,241,254,.25);}
.adv-col-dc{background:rgba(120,180,255,.1);color:#78b4ff;border:1px solid rgba(120,180,255,.25);}
.adv-status-bar{height:4px;border-radius:999px;margin-top:6px;transition:opacity .3s;}
.adv-status-ok{background:#00f1fe;opacity:1;}
.adv-status-err{background:#ff6b6b;opacity:1;}
.adv-plan-badge{display:inline-flex;align-items:center;gap:7px;padding:5px 12px;
  border-radius:999px;background:rgba(0,241,254,.08);border:1px solid rgba(0,241,254,.2);
  color:#00f1fe;font-size:12px;font-weight:700;}
@keyframes advSlideIn{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:translateY(0)}}
.adv-panel>*{animation:advSlideIn .28s ease both;}

/* ── RAM Cleaner ─────────────────────────────────────────────────── */
.ram-panel{position:relative;overflow:hidden;}
.ram-panel:before{content:"";position:absolute;top:-30%;right:-10%;width:260px;height:260px;
  border-radius:999px;background:radial-gradient(circle,rgba(0,241,254,.1),transparent 65%);pointer-events:none;}
.ram-bar-outer{width:100%;height:20px;border-radius:999px;overflow:hidden;background:rgba(255,255,255,.06);
  border:1px solid rgba(255,255,255,.1);display:flex;}
.ram-bar-inuse{height:100%;background:linear-gradient(90deg,#006a70,#00f1fe);transition:width .8s ease;}
.ram-bar-standby{height:100%;background:rgba(0,241,254,.28);transition:width .8s ease;}
.ram-bar-free{height:100%;flex:1;background:rgba(255,255,255,.06);}
.ram-legend{display:flex;align-items:center;gap:6px;font-size:12px;color:rgba(211,222,239,.72);}
.ram-legend-dot{width:10px;height:10px;border-radius:999px;flex-shrink:0;}
.ram-stat-card{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.03);
  border-radius:14px;padding:14px 16px;text-align:center;}
.ram-stat-val{font-size:22px;font-weight:800;color:#00f1fe;font-variant-numeric:tabular-nums;}
.ram-stat-label{font-size:11px;color:rgba(211,222,239,.62);margin-top:2px;text-transform:uppercase;letter-spacing:.06em;}
.ram-btn-clean{display:inline-flex;align-items:center;gap:8px;padding:12px 24px;border-radius:12px;
  font-size:14px;font-weight:700;cursor:pointer;border:0;transition:all .25s ease;}
.ram-btn-clean-active{background:linear-gradient(135deg,#00b4bc,#00f1fe);color:#022;
  box-shadow:0 0 24px rgba(0,241,254,.3);}
.ram-btn-clean-active:hover{transform:translateY(-2px);box-shadow:0 0 32px rgba(0,241,254,.45);}
.ram-btn-clean-idle{background:rgba(255,255,255,.06);color:rgba(211,222,239,.78);
  border:1px solid rgba(255,255,255,.12);}
.ram-btn-clean-idle:hover{background:rgba(255,255,255,.1);}
.ram-btn-clean:disabled{opacity:.55;cursor:wait;transform:none!important;}
@keyframes ramPulse{0%,100%{box-shadow:0 0 0 0 rgba(0,241,254,.4)}50%{box-shadow:0 0 0 8px rgba(0,241,254,0)}}
.ram-btn-cleaning{animation:ramPulse 1s ease infinite;}
        `.trim();
        document.head.appendChild(style);
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
    }

    function buildAdvHtml() {
        return `
<div class="adv-panel relative z-10" id="adv-panel">
  <div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg">
    <p class="text-body-md text-on-surface-variant max-w-2xl" id="adv-sub-text"></p>
    <div class="flex items-center gap-sm flex-shrink-0">
      <span class="adv-plan-badge" id="adv-plan-badge">
        <span class="material-symbols-outlined text-[15px]">bolt</span>
        <span id="adv-plan-name">&hellip;</span>
      </span>
    </div>
  </div>

  <!-- DC toggle -->
  <div class="flex items-center gap-sm mb-lg">
    <div class="mini-toggle cursor-pointer" id="adv-toggle-dc" data-on="false">
      <div class="mini-toggle-knob"></div>
    </div>
    <span class="text-body-md text-on-surface" id="adv-show-dc-label"></span>
  </div>

  <!-- Status bar (feedback after save) -->
  <div class="adv-status-bar opacity-0" id="adv-status-bar"></div>
  <p class="text-label-sm text-on-surface-variant mt-1 mb-lg hidden" id="adv-status-msg"></p>

  <!-- Loading state -->
  <div id="adv-loading" class="text-body-md text-on-surface-variant opacity-70 py-6 text-center">
    <span class="material-symbols-outlined text-secondary-container animate-spin inline-block">refresh</span>
  </div>

  <!-- Parameter rows (hidden until loaded) -->
  <div class="space-y-sm hidden" id="adv-rows">
    ${buildParamRow('processorMin', 'slider', 0, 100, 5, 'adv-proc-min', 'adv-proc-min-sub', '%')}
    ${buildParamRow('processorMax', 'slider', 0, 100, 100, 'adv-proc-max', 'adv-proc-max-sub', '%')}
    ${buildParamRow('boostMode',    'select', 0, 4, 2, 'adv-boost', 'adv-boost-sub', '',
      [{v:0,k:'adv_boost_0'},{v:1,k:'adv_boost_1'},{v:2,k:'adv_boost_2'},{v:4,k:'adv_boost_4'}])}
    ${buildParamRow('pcieLinkState','select', 0, 2, 0, 'adv-pcie', 'adv-pcie-sub', '',
      [{v:0,k:'adv_pcie_0'},{v:1,k:'adv_pcie_1'},{v:2,k:'adv_pcie_2'}])}
  </div>
</div>`;
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
            'adv-show-dc-label': 'adv_show_dc',
            'adv-lbl-processorMin': 'adv_proc_min', 'adv-sub-processorMin': 'adv_proc_min_sub',
            'adv-lbl-processorMax': 'adv_proc_max', 'adv-sub-processorMax': 'adv_proc_max_sub',
            'adv-lbl-boostMode':    'adv_boost',    'adv-sub-boostMode':    'adv_boost_sub',
            'adv-lbl-pcieLinkState':'adv_pcie',     'adv-sub-pcieLinkState':'adv_pcie_sub',
        };
        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = t(key);
        });
        // AC/DC column tags
        ['processorMin','processorMax','boostMode','pcieLinkState'].forEach(k => {
            const ac = document.getElementById(`adv-ac-tag-${k}`);
            const dc = document.getElementById(`adv-dc-tag-${k}`);
            if (ac) ac.textContent = t('adv_ac');
            if (dc) dc.textContent = t('adv_dc');
        });
        // Select option labels
        const boostOpts = [{v:0,k:'adv_boost_0'},{v:1,k:'adv_boost_1'},{v:2,k:'adv_boost_2'},{v:4,k:'adv_boost_4'}];
        const pcieOpts  = [{v:0,k:'adv_pcie_0'},{v:1,k:'adv_pcie_1'},{v:2,k:'adv_pcie_2'}];
        ['ac','dc'].forEach(side => {
            boostOpts.forEach(o => {
                const el = document.getElementById(`adv-opt-boostMode-${side}-${o.v}`);
                if (el) el.textContent = t(o.k);
            });
            pcieOpts.forEach(o => {
                const el = document.getElementById(`adv-opt-pcieLinkState-${side}-${o.v}`);
                if (el) el.textContent = t(o.k);
            });
        });
        // Update DC toggle
        updateDcVisibility();
    }

    function updateDcVisibility() {
        const toggle = document.getElementById('adv-toggle-dc');
        if (toggle) toggle.dataset.on = advShowDc ? 'true' : 'false';
        document.querySelectorAll('.adv-dc-section').forEach(el => {
            el.classList.toggle('hidden', !advShowDc);
        });
    }

    /** Populate controls from the loaded PlanParameterSet */
    function applyAdvParams(params) {
        advParams = params;
        const planName = document.getElementById('adv-plan-name');
        if (planName) planName.textContent = params.planName || params.planGuid || '—';

        setSliderOrSelect('processorMin', 'ac', params.processorMinAc, 0, 100, '%');
        setSliderOrSelect('processorMin', 'dc', params.processorMinDc, 0, 100, '%');
        setSliderOrSelect('processorMax', 'ac', params.processorMaxAc, 0, 100, '%');
        setSliderOrSelect('processorMax', 'dc', params.processorMaxDc, 0, 100, '%');
        setSliderOrSelect('boostMode',    'ac', params.boostModeAc,    0, 4, '');
        setSliderOrSelect('boostMode',    'dc', params.boostModeDc,    0, 4, '');
        setSliderOrSelect('pcieLinkState','ac', params.pcieLinkStateAc, 0, 2, '');
        setSliderOrSelect('pcieLinkState','dc', params.pcieLinkStateDc, 0, 2, '');

        document.getElementById('adv-loading')?.classList.add('hidden');
        document.getElementById('adv-rows')?.classList.remove('hidden');
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
            el.value = String(value);
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

    async function loadAdvParams() {
        if (!advParams) {
            document.getElementById('adv-loading')?.classList.remove('hidden');
            document.getElementById('adv-rows')?.classList.add('hidden');
        }
        try {
            const p = await Host.call('getPlanParameters');
            applyAdvParams(p);
        } catch (err) {
            showAdvStatus(t('adv_save_err') + ' ' + err.message, true);
            document.getElementById('adv-loading')?.classList.add('hidden');
            document.getElementById('adv-rows')?.classList.remove('hidden');
        }
    }

    async function saveSingleParam(key, acVal, dcVal) {
        if (!advParams) return;
        try {
            await Host.call('setPlanParameter', {
                planGuid: advParams.planGuid,
                settingKey: key,
                acValue: acVal,
                dcValue: dcVal,
            });
            showAdvStatus(t('adv_save_ok'), false);
        } catch (err) {
            showAdvStatus(t('adv_save_err'), true);
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
            const unit = (key === 'processorMin' || key === 'processorMax') ? '%' : '';
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
        <div class="ram-legend-dot" style="background:linear-gradient(90deg,#006a70,#00f1fe)"></div>
        <span id="ram-legend-inuse"></span>
      </div>
      <div class="ram-legend">
        <div class="ram-legend-dot" style="background:rgba(0,241,254,.36)"></div>
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
    <div class="ram-stat-card" style="border-color:rgba(0,241,254,.18);background:rgba(0,241,254,.04);">
      <div class="ram-stat-val" id="ram-val-standby" style="color:#00f1fe;"></div>
      <div class="ram-stat-label" id="ram-lbl-standby"></div>
    </div>
    <div class="ram-stat-card">
      <div class="ram-stat-val" id="ram-val-free" style="color:rgba(211,222,239,.7);"></div>
      <div class="ram-stat-label" id="ram-lbl-free"></div>
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
        };
        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = t(key);
        });
        const lastClean = document.getElementById('ram-last-clean-val');
        if (lastClean) lastClean.textContent = ramLastClean ? fmtTime(ramLastClean) : t('ram_never');
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

    function showRamStatus(msg, isError) {
        const el = document.getElementById('ram-status-msg');
        if (!el) return;
        el.textContent = msg;
        el.classList.remove('hidden');
        el.style.color = isError ? '#ff8a80' : '#00f1fe';
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

        ramWired = true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // View lifecycle — mount + auto-refresh when accordion opens
    // ══════════════════════════════════════════════════════════════════════════

    function isAccordionOpen(mountId) {
        const mount = document.getElementById(mountId);
        return mount && mount.closest('.vm-acc-item[data-open="true"]') !== null;
    }

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
                mountAdvancedUi();
                wireAdvancedUi();
                loadAdvParams();
            }
            if (hasRamMount) {
                if (isOpen) {
                    mountRamUi();
                    wireRamUi();
                    loadRamStatus();
                    // Auto-refresh every 5 s while panel is open
                    clearInterval(ramAutoRefresh);
                    ramAutoRefresh = setInterval(loadRamStatus, 5000);
                } else {
                    clearInterval(ramAutoRefresh);
                    ramAutoRefresh = null;
                }
            }
        }, 20);
    });

    // Also refresh when the power view becomes active (in case panels were already open)
    document.addEventListener('viewchange', e => {
        if (!e.detail || e.detail.view !== 'power') {
            clearInterval(ramAutoRefresh);
            ramAutoRefresh = null;
            return;
        }
        if (isAccordionOpen('advanced-params-mount')) {
            mountAdvancedUi();
            wireAdvancedUi();
            loadAdvParams();
        }
        if (isAccordionOpen('ram-cleaner-mount')) {
            mountRamUi();
            wireRamUi();
            loadRamStatus();
            clearInterval(ramAutoRefresh);
            ramAutoRefresh = setInterval(loadRamStatus, 5000);
        }
    });

    // Refresh labels on language change
    document.addEventListener('langchanged', () => {
        if (advMounted) refreshAdvLabels();
        if (ramMounted) refreshRamLabels();
    });

    // Listen for active plan changes so the editor always shows the right plan
    Host.on('activePlanChanged', () => {
        if (isAccordionOpen('advanced-params-mount')) {
            advParams = null; // force reload
            loadAdvParams();
        }
    });
})();
