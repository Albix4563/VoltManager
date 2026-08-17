/* VoltManager Advanced Cooling / Fan Center
 * Draft-first UI. All hardware writes go through host-side capabilities,
 * FanSafetyPolicy, conflict checks and the supervised fan-control watchdog.
 */
(function () {
    'use strict';

    const state = {
        mounted: false,
        active: false,
        loading: false,
        actionBusy: false,
        topology: null,
        systemInfo: null,
        controlState: null,
        safetyPolicy: null,
        profiles: [],
        selectedProfileId: '',
        selectedProfile: null,
        selectedFanId: null,
        drafts: Object.create(null),
        presets: Object.create(null),
        previews: Object.create(null),
        dirtyFans: new Set(),
        groups: [],
        modal: null,
        curveDrag: null,
        previewTimer: null,
        lastRefreshAt: 0,
        toast: null,
        toastTimer: null,
    };

    const strings = {
        it: {
            profile: 'Setup', noProfile: 'Nessun setup selezionato', saveSetup: 'Salva setup', applyProfile: 'Applica setup',
            rename: 'Rinomina', duplicate: 'Duplica', remove: 'Elimina', import: 'Importa', export: 'Esporta', groups: 'Gruppi',
            compatibility: 'Compatibilità', refresh: 'Rileva hardware', readOnly: 'Sola lettura', controlAvailable: 'Controllo disponibile',
            unavailable: 'Telemetria non disponibile', externalBlocked: 'Controllo disabilitato per convivenza', possibleSoftware: 'Utility hardware rilevata',
            possibleSoftwareBody: '{software} è in esecuzione{process}{service}. Per evitare conflitti VoltManager non forza il controller: usa {software} per modificare le ventole interessate.',
            config: 'Configurazione', configSub: 'Le modifiche restano in anteprima finché non scegli Applica.', fansDetected: '{count} rilevate',
            groupCpu: 'CPU / Pompa', groupGpu: 'GPU', groupCase: 'Case / System', groupOther: 'Non classificate',
            rpm: 'RPM', pwm: 'PWM', temperature: 'Temperatura', rpmUnit: 'RPM', pwmUnavailable: 'N/D', tempUnavailable: 'N/D',
            mode: 'Modalità', automatic: 'Automatico', manual: 'Manuale', curve: 'Curva', sensor: 'Sensore di riferimento', noSensor: 'Nessun sensore associabile',
            manualOutput: 'Output manuale', fanCurve: 'Curva ventola', curveHint: 'Trascina i punti. Il raffreddamento non può diminuire all’aumentare della temperatura.', addPoint: 'Aggiungi punto',
            presetSilent: 'Silent', presetBalanced: 'Balanced', presetPerformance: 'Performance', presetCustom: 'Custom',
            safety: 'VoltManager mantiene i limiti verificati dal backend, non deduce Fan Stop da 0% e aumenta automaticamente il raffreddamento alle temperature elevate.',
            apply: 'Applica modifiche', restore: 'Ripristina default', pending: 'Modifiche non applicate', noWrites: 'Nessuna modifica in attesa.', applied: 'Configurazione applicata', applyFailed: 'Applicazione non riuscita',
            preview: 'Anteprima', effectiveOutput: 'Output effettivo', safetyOverride: 'Safety override attivo', controlSession: 'Sessione controllata', hardwareDefault: 'Controllo hardware/default',
            hardwareVisual: 'Visualizzazione hardware', hardwareVisualSub: 'Modello 3D locale e telemetria del componente associato.', model3d: 'Modello 3D locale',
            sourceSensor: 'Sensore principale', roleConfidence: 'Identificazione', controller: 'Controller', sensorName: 'Canale sensore', telemetryLink: 'Temperatura → comportamento ventola',
            capabilities: 'Capability rilevate', capRpm: 'RPM leggibili', capControlRead: 'PWM leggibile', capControlWrite: 'Controllo scrivibile', capCurve: 'Curva software', capFanStop: 'Fan Stop', capRestore: 'Ripristino default',
            supported: 'Sì', unsupported: 'No', unknown: 'Sconosciuto', minLimit: 'Min backend', maxLimit: 'Max backend', coreSensors: 'Sensori termici', none: 'Nessuno',
            noFans: 'Nessuna ventola rilevata', noFansBody: 'VoltManager non ha trovato sensori fan. Firmware o controller potrebbero non esporli al sistema operativo.',
            noSensors: 'Sensori hardware non disponibili', noSensorsBody: 'Il provider hardware o il driver di accesso non è disponibile. Nessun controllo viene tentato.',
            bridgeUnavailable: 'Fan Center non disponibile in anteprima browser', bridgeUnavailableBody: 'Apri questa schermata dentro VoltManager per accedere all’hardware.',
            loading: 'Analisi del sistema di raffreddamento', loadingBody: 'Rilevamento ventole, sensori, capability e possibili conflitti.',
            saveTitle: 'Salva nuovo setup', saveBody: 'Salva nomi, curve, sensori, gruppi e modalità correnti.', profileName: 'Nome setup', save: 'Salva', cancel: 'Annulla',
            renameProfileTitle: 'Rinomina setup', duplicateProfileTitle: 'Duplica setup', renameFanTitle: 'Rinomina ventola', fanName: 'Nome ventola',
            deleteTitle: 'Eliminare questo setup?', deleteBody: 'Il file del profilo verrà rimosso. L’hardware non viene modificato.', confirmDelete: 'Elimina setup',
            compatibilityTitle: 'Compatibilità del setup', compatibilityBody: 'Dry-run: verifica ventole e sensori prima di qualsiasi scrittura.', matched: 'Compatibile', needsMapping: 'Mapping manuale richiesto', missing: 'Dispositivo mancante', incompatible: 'Incompatibile',
            storedOnly: 'Il setup può essere archiviato, ma richiede mapping o capability compatibili prima di essere applicato.', mappingChooseFan: 'Seleziona ventola locale', mappingChooseSensor: 'Seleziona sensore locale', applySetup: 'Applica setup',
            groupTitle: 'Gruppi ventole', groupName: 'Nome gruppo', addGroup: 'Crea gruppo', deleteGroup: 'Rimuovi gruppo', applyToGroup: 'Applica configurazione corrente al gruppo',
            profileSaved: 'Setup salvato.', profileRenamed: 'Setup rinominato.', profileDuplicated: 'Setup duplicato.', profileDeleted: 'Setup eliminato.', profileImported: 'Setup importato e validato.', profileExported: 'Setup esportato: {file}', profileApplied: 'Setup applicato.', fanRenamed: 'Nome ventola aggiornato.', groupApplied: 'Configurazione applicata al gruppo.',
            operationFailed: 'Operazione non riuscita: {error}', confidenceConfirmed: 'Confermata', confidenceHigh: 'Alta', confidenceMedium: 'Media', confidenceLow: 'Bassa', confidenceUserAssigned: 'Utente',
            roleCpuFan: 'CPU Fan', roleCpuOptional: 'CPU Optional', roleGpuFan: 'GPU Fan', roleCaseFan: 'Case / System Fan', rolePump: 'Pump / AIO', roleExternal: 'Controller esterno', roleUnknown: 'Ventola non classificata',
            monitoringBackend: 'Monitoring', current: 'corrente', automaticDescription: 'Rilascia il canale e restituisce la gestione al backend/hardware.', deviceDisconnected: 'La ventola selezionata è stata disconnessa o non è più rilevabile.', fanCount: 'Ventole GPU', safetyThresholds: 'Rampa sicurezza: {start} °C · forte: {strong} °C · massimo: {emergency} °C',
        },
        en: {
            profile: 'Setup', noProfile: 'No setup selected', saveSetup: 'Save setup', applyProfile: 'Apply setup', rename: 'Rename', duplicate: 'Duplicate', remove: 'Delete', import: 'Import', export: 'Export', groups: 'Groups',
            compatibility: 'Compatibility', refresh: 'Detect hardware', readOnly: 'Read only', controlAvailable: 'Control available', unavailable: 'Telemetry unavailable', externalBlocked: 'Control disabled for coexistence', possibleSoftware: 'Hardware utility detected',
            possibleSoftwareBody: '{software} is running{process}{service}. To avoid conflicts VoltManager will not force the controller; use {software} to change the affected fans.',
            config: 'Configuration', configSub: 'Changes remain a preview until you choose Apply.', fansDetected: '{count} detected', groupCpu: 'CPU / Pump', groupGpu: 'GPU', groupCase: 'Case / System', groupOther: 'Unclassified',
            rpm: 'RPM', pwm: 'PWM', temperature: 'Temperature', rpmUnit: 'RPM', pwmUnavailable: 'N/A', tempUnavailable: 'N/A', mode: 'Mode', automatic: 'Automatic', manual: 'Manual', curve: 'Curve', sensor: 'Reference sensor', noSensor: 'No compatible sensor',
            manualOutput: 'Manual output', fanCurve: 'Fan curve', curveHint: 'Drag points. Cooling cannot decrease as temperature rises.', addPoint: 'Add point', presetSilent: 'Silent', presetBalanced: 'Balanced', presetPerformance: 'Performance', presetCustom: 'Custom',
            safety: 'VoltManager preserves verified backend limits, never infers Fan Stop from 0%, and automatically raises cooling at high temperatures.', apply: 'Apply changes', restore: 'Restore default', pending: 'Unapplied changes', noWrites: 'No pending changes.', applied: 'Configuration applied', applyFailed: 'Apply failed',
            preview: 'Preview', effectiveOutput: 'Effective output', safetyOverride: 'Safety override active', controlSession: 'Supervised session', hardwareDefault: 'Hardware/default control', hardwareVisual: 'Hardware visual', hardwareVisualSub: 'Local 3D model and telemetry for the associated component.', model3d: 'Local 3D model',
            sourceSensor: 'Primary sensor', roleConfidence: 'Identification', controller: 'Controller', sensorName: 'Sensor channel', telemetryLink: 'Temperature → fan behavior', capabilities: 'Detected capabilities', capRpm: 'RPM readable', capControlRead: 'PWM readable', capControlWrite: 'Writable control', capCurve: 'Software curve', capFanStop: 'Fan Stop', capRestore: 'Restore default', supported: 'Yes', unsupported: 'No', unknown: 'Unknown', minLimit: 'Backend min', maxLimit: 'Backend max', coreSensors: 'Thermal sensors', none: 'None',
            noFans: 'No fans detected', noFansBody: 'VoltManager found no fan sensors. Firmware or the controller may not expose them to the operating system.', noSensors: 'Hardware sensors unavailable', noSensorsBody: 'The hardware provider or access driver is unavailable. No control is attempted.', bridgeUnavailable: 'Fan Center unavailable in browser preview', bridgeUnavailableBody: 'Open this screen inside VoltManager to access hardware.', loading: 'Analyzing cooling system', loadingBody: 'Detecting fans, sensors, capabilities and possible conflicts.',
            saveTitle: 'Save new setup', saveBody: 'Save current names, curves, sensors, groups and modes.', profileName: 'Setup name', save: 'Save', cancel: 'Cancel', renameProfileTitle: 'Rename setup', duplicateProfileTitle: 'Duplicate setup', renameFanTitle: 'Rename fan', fanName: 'Fan name', deleteTitle: 'Delete this setup?', deleteBody: 'The profile file will be removed. Hardware is not changed.', confirmDelete: 'Delete setup',
            compatibilityTitle: 'Setup compatibility', compatibilityBody: 'Dry-run: verify fans and sensors before any write.', matched: 'Compatible', needsMapping: 'Manual mapping required', missing: 'Device missing', incompatible: 'Incompatible', storedOnly: 'The setup can be stored, but needs compatible mappings/capabilities before control.', mappingChooseFan: 'Choose local fan', mappingChooseSensor: 'Choose local sensor', applySetup: 'Apply setup',
            groupTitle: 'Fan groups', groupName: 'Group name', addGroup: 'Create group', deleteGroup: 'Remove group', applyToGroup: 'Apply current configuration to group', profileSaved: 'Setup saved.', profileRenamed: 'Setup renamed.', profileDuplicated: 'Setup duplicated.', profileDeleted: 'Setup deleted.', profileImported: 'Setup imported and validated.', profileExported: 'Setup exported: {file}', profileApplied: 'Setup applied.', fanRenamed: 'Fan name updated.', groupApplied: 'Configuration applied to group.',
            operationFailed: 'Operation failed: {error}', confidenceConfirmed: 'Confirmed', confidenceHigh: 'High', confidenceMedium: 'Medium', confidenceLow: 'Low', confidenceUserAssigned: 'User', roleCpuFan: 'CPU Fan', roleCpuOptional: 'CPU Optional', roleGpuFan: 'GPU Fan', roleCaseFan: 'Case / System Fan', rolePump: 'Pump / AIO', roleExternal: 'External controller', roleUnknown: 'Unclassified fan', monitoringBackend: 'Monitoring', current: 'current', automaticDescription: 'Release the channel and return control to the backend/hardware.', deviceDisconnected: 'The selected fan was disconnected or is no longer detectable.', fanCount: 'GPU fans', safetyThresholds: 'Safety ramp: {start} °C · strong: {strong} °C · maximum: {emergency} °C',
        }
    };

    function lang() {
        const raw = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
        return raw === 'it' ? 'it' : 'en';
    }
    function t(key, params) {
        let value = (strings[lang()] && strings[lang()][key]) || strings.en[key] || key;
        Object.entries(params || {}).forEach(([name, replacement]) => value = value.replaceAll('{' + name + '}', String(replacement)));
        return value;
    }
    function esc(value) {
        return String(value == null ? '' : value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
    }
    function normalized(value) { return String(value || '').toLowerCase(); }
    function finite(value) { return typeof value === 'number' && Number.isFinite(value); }
    function fmtRpm(value) { return finite(value) ? Math.round(value).toLocaleString(lang()==='it'?'it-IT':'en-US') : 'N/D'; }
    function fmtTemp(value) { return finite(value) ? (Math.round(value*10)/10) + ' °C' : t('tempUnavailable'); }
    function fmtPct(value) { return finite(value) ? (Math.round(value*10)/10) + '%' : t('pwmUnavailable'); }
    function clone(value) { return value == null ? value : JSON.parse(JSON.stringify(value)); }
    function roleKey(role) {
        return ({cpufan:'cpuFan',cpuoptional:'cpuOptional',gpufan:'gpuFan',casefan:'caseFan',pump:'pump',externalcontrollerfan:'external'})[normalized(role)] || 'unknown';
    }
    function roleLabel(role) { return t({cpuFan:'roleCpuFan',cpuOptional:'roleCpuOptional',gpuFan:'roleGpuFan',caseFan:'roleCaseFan',pump:'rolePump',external:'roleExternal',unknown:'roleUnknown'}[roleKey(role)]); }
    function roleIcon(role) { return {cpuFan:'memory',cpuOptional:'memory',gpuFan:'developer_board',caseFan:'mode_fan',pump:'water_drop',external:'hub',unknown:'mode_fan'}[roleKey(role)]; }
    function confidenceLabel(value) { return t({confirmed:'confidenceConfirmed',high:'confidenceHigh',medium:'confidenceMedium',low:'confidenceLow',userassigned:'confidenceUserAssigned'}[normalized(value)] || 'confidenceLow'); }
    function devices() { return state.topology && state.topology.devices || []; }
    function selectedFan() { return devices().find(f => f.id === state.selectedFanId) || devices()[0] || null; }
    function selectedProfileSummary() { return state.profiles.find(p => p.id === state.selectedProfileId) || null; }
    function activeSession(fanId) { return state.controlState && (state.controlState.sessions || []).find(s => s.fanId === fanId) || null; }
    function syncDraftsFromRuntime() {(state.controlState&&state.controlState.sessions||[]).forEach(session=>{if(session.configuration&&!state.dirtyFans.has(session.fanId))state.drafts[session.fanId]=clone(session.configuration);});}
    function isControllable(fan) { return !!(fan && normalized(fan.controlState)==='controlavailable' && fan.capabilities && fan.capabilities.controlWritable); }
    function canRestore(fan) { return !!(fan && fan.controlIdentifier && fan.capabilities && fan.capabilities.canRestoreDefault); }
    function canRestoreSafely(fan) { return canRestore(fan) && normalized(fan.controlState) !== 'externalcontrollerdetected'; }

    function defaultDraft(fan) {
        const caps = fan.capabilities || {};
        const min = finite(caps.minimumControl) ? caps.minimumControl : 30;
        const max = finite(caps.maximumControl) ? caps.maximumControl : 100;
        const sensorId = (fan.availableTemperatureSensors || [])[0]?.id || null;
        const current = finite(fan.telemetry && fan.telemetry.controlPercent) ? fan.telemetry.controlPercent : Math.min(max, Math.max(min, 50));
        return { mode:'automatic', sensorId, fixedControlPercent:current, curve:[] };
    }
    function ensureDraft(fan) {
        if (!fan) return null;
        if (!state.drafts[fan.id]) state.drafts[fan.id] = defaultDraft(fan);
        const draft = state.drafts[fan.id];
        if (!draft.sensorId && (fan.availableTemperatureSensors || []).length) draft.sensorId = fan.availableTemperatureSensors[0].id;
        if ((!draft.curve || draft.curve.length < 2) && state.presets[fan.id] && state.presets[fan.id].balanced) draft.curve = clone(state.presets[fan.id].balanced);
        return draft;
    }
    async function ensurePresets(fan) {
        if (!fan || !isControllable(fan) || state.presets[fan.id] || !window.Host || !Host.available) return;
        try {
            state.presets[fan.id] = await Host.call('getFanPresets', { fanId: fan.id }) || {};
            ensureDraft(fan);
            if (state.active) render();
        } catch (_) { state.presets[fan.id] = {}; }
    }
    function markDirty(fanId) {
        state.dirtyFans.add(fanId); state.previews[fanId] = null; schedulePreview(fanId); render();
    }
    function schedulePreview(fanId) {
        if (state.previewTimer) clearTimeout(state.previewTimer);
        state.previewTimer = setTimeout(() => previewFan(fanId, true), 180);
    }
    async function previewFan(fanId, silent) {
        const fan = devices().find(f => f.id===fanId), draft = fan && ensureDraft(fan);
        if (!fan || !draft || !window.Host || !Host.available) return null;
        try {
            const result = await Host.call('previewFanConfiguration', { fanId, configuration:draft });
            state.previews[fanId] = result; if (state.active) render(); return result;
        } catch (error) { if (!silent) showError(error); return null; }
    }

    function groupFor(fan) {
        const key=roleKey(fan.role); if(key==='cpuFan'||key==='cpuOptional'||key==='pump')return'cpu'; if(key==='gpuFan')return'gpu'; if(key==='caseFan')return'case'; return'other';
    }
    function renderToolbar() {
        const selected=selectedProfileSummary(), anyControl=devices().some(isControllable), sensors=state.topology&&state.topology.sensorsAvailable;
        const stateName=!sensors?'offline':anyControl?'control':'readonly', stateText=!sensors?t('unavailable'):anyControl?t('controlAvailable'):t('readOnly');
        const options=[`<option value="">${esc(t('noProfile'))}</option>`].concat(state.profiles.map(p=>`<option value="${esc(p.id)}"${p.id===state.selectedProfileId?' selected':''}>${esc(p.name)} · ${p.fanCount}</option>`)).join('');
        return `<div class="vm-fan-toolbar">
            <div class="vm-fan-toolbar__profile">
                <span class="vm-fan-toolbar__label">${esc(t('profile'))}</span>
                <select class="vm-fan-select vm-fan-toolbar__select" id="vm-fan-profile-select" aria-label="${esc(t('profile'))}">${options}</select>
            </div>
            <div class="vm-fan-toolbar__actions vm-fan-toolbar__actions--primary">
                ${tool('save-profile','save',t('saveSetup'),true)}${tool('apply-profile','play_arrow',t('applyProfile'),!!selected)}${tool('compatibility','rule',t('compatibility'),!!selected)}${tool('groups','hub',t('groups'),true)}${tool('rename-profile','edit',t('rename'),!!selected)}${tool('duplicate-profile','content_copy',t('duplicate'),!!selected)}${tool('delete-profile','delete',t('remove'),!!selected,'vm-fan-button--danger')}
            </div>
            <div class="vm-fan-toolbar__actions vm-fan-toolbar__actions--utility">
                <span class="vm-fan-global-state" data-state="${stateName}"><span class="vm-fan-state-dot"></span>${esc(stateText)}</span>
                ${tool('import-profile','file_open',t('import'),true)}${tool('export-profile','ios_share',t('export'),!!selected)}${tool('refresh','refresh',t('refresh'),true,'vm-fan-button--accent')}
            </div>
        </div>`;
    }
    function tool(action,icon,label,enabled,cls){return `<button type="button" class="vm-fan-button ${cls||''}" data-fan-action="${action}" ${enabled?'':'disabled'} title="${esc(label)}"><span class="material-symbols-outlined">${icon}</span><span>${esc(label)}</span></button>`;}

    function renderNotices() {
        const notices=[];
        if(state.controlState&&state.controlState.lastError)notices.push(`<div class="vm-fan-notice vm-fan-notice--info"><span class="material-symbols-outlined">shield</span><div><strong>${esc(t('controlSession'))}</strong><p>${esc(state.controlState.lastError)}</p></div></div>`);
        (state.topology&&state.topology.externalSoftware||[]).forEach(item=>{
            const process=item.processName?` · ${item.processName}`:'';
            const service=item.serviceName?` · ${t('service')}: ${item.serviceName}`:'';
            notices.push(`<div class="vm-fan-notice ${item.blocksControl?'':'vm-fan-notice--info'}"><span class="material-symbols-outlined">${item.blocksControl?'warning':'info'}</span><div><strong>${esc(item.blocksControl?t('externalBlocked'):t('possibleSoftware'))}</strong><p>${esc(t('possibleSoftwareBody',{software:item.softwareName,process,service}))}</p></div></div>`);
        });
        return notices.join('');
    }

    function renderFanList() {
        const groups={cpu:[],gpu:[],case:[],other:[]}; devices().forEach(f=>groups[groupFor(f)].push(f));
        return [['cpu','groupCpu'],['gpu','groupGpu'],['case','groupCase'],['other','groupOther']].map(([key,label])=>groups[key].length?`<div class="vm-fan-group-label">${esc(t(label))}</div>${groups[key].map(renderFanRow).join('')}`:'').join('');
    }
    function renderFanRow(fan) {
        const selected=fan.id===state.selectedFanId, session=activeSession(fan.id), stateKey=normalized(fan.controlState);
        return `<button type="button" class="vm-fan-row" data-fan-id="${esc(fan.id)}" aria-selected="${selected?'true':'false'}" data-control-state="${esc(stateKey)}"><span class="vm-fan-row__icon"><span class="material-symbols-outlined">${roleIcon(fan.role)}</span></span><span><span class="vm-fan-row__name">${esc(fan.displayName||fan.sensorName||roleLabel(fan.role))}</span><span class="vm-fan-row__meta">${esc(roleLabel(fan.role))} · ${esc(fan.headerName||fan.sensorName||t('unknown'))}${session?' · '+esc(t('controlSession')):''}</span></span><span class="vm-fan-row__rpm">${esc(fmtRpm(fan.telemetry&&fan.telemetry.rpm))}<small>${esc(t('rpmUnit'))}</small></span></button>`;
    }

    function renderConfigPanel(fan) {
        const caps=fan.capabilities||{}, controllable=isControllable(fan), draft=ensureDraft(fan), preview=state.previews[fan.id], session=activeSession(fan.id), dirty=state.dirtyFans.has(fan.id), temps=fan.availableTemperatureSensors||[];
        const mode=normalized(draft.mode);
        const min=finite(caps.minimumControl)?caps.minimumControl:0, max=finite(caps.maximumControl)?caps.maximumControl:100;
        const stateLabel=controllable?t('controlAvailable'):normalized(fan.controlState)==='externalcontrollerdetected'?t('externalBlocked'):normalized(fan.controlState)==='sensorunavailable'?t('unavailable'):t('readOnly');
        return `<section class="vm-fan-panel vm-fan-config-panel"><div class="vm-fan-panel__header"><div><span class="vm-fan-eyebrow">HUD / CONTROL</span><h3>${esc(t('config'))}</h3><p>${esc(t('configSub'))}</p></div><span class="vm-fan-count">${esc(t('fansDetected',{count:devices().length}))}</span></div><div class="vm-fan-panel__body"><div class="vm-fan-list">${renderFanList()}</div><div class="vm-fan-selection">
            <div class="vm-fan-selected-title"><div><h4>${esc(fan.displayName)}</h4><p>${esc(fan.hardwareName)} · ${esc(fan.headerName||fan.sensorName)}</p></div><div class="vm-fan-inline"><span class="vm-fan-badge ${controllable?'vm-fan-badge--control':'vm-fan-badge--readonly'}">${esc(stateLabel)}</span>${tool('rename-fan','edit',t('rename'),true)}</div></div>
            ${fan.safetyReason?`<div class="vm-fan-safety-reason"><span class="material-symbols-outlined">shield_lock</span><span>${esc(fan.safetyReason)}</span></div>`:''}
            <div class="vm-fan-kpis">${kpi(t('rpm'),fmtRpm(fan.telemetry&&fan.telemetry.rpm),t('rpmUnit'))}${kpi(t('pwm'),fmtPct(fan.telemetry&&fan.telemetry.controlPercent),'')}${kpi(t('temperature'),fmtTemp(referenceTemp(fan,draft)),'')}${kpi(t('minLimit'),finite(caps.minimumControl)?fmtPct(caps.minimumControl):'N/D','')}${kpi(t('maxLimit'),finite(caps.maximumControl)?fmtPct(caps.maximumControl):'N/D','')}</div>
            <div class="vm-fan-section"><div class="vm-fan-section__top"><h5>${esc(t('mode'))}</h5><span>${esc(caps.backend||t('monitoringBackend'))}</span></div><div class="vm-fan-modes">${modeButton('automatic','automatic',canRestoreSafely(fan),mode)}${modeButton('manual','manual',controllable&&caps.fixedControlSupported,mode)}${modeButton('curve','curve',controllable&&caps.softwareCurveSupported,mode)}</div>
                <div class="vm-fan-field"><label for="vm-fan-sensor-select">${esc(t('sensor'))}</label><select id="vm-fan-sensor-select" class="vm-fan-select" ${mode==='automatic'||!controllable?'disabled':''}>${temps.length?temps.map(s=>`<option value="${esc(s.id)}"${s.id===draft.sensorId?' selected':''}>${esc(s.name)} · ${esc(fmtTemp(s.value))}</option>`).join(''):`<option value="">${esc(t('noSensor'))}</option>`}</select></div>
                ${mode==='manual'?renderManual(fan,draft,min,max):mode==='curve'?renderCurveControls(fan,draft,min,max):`<div class="vm-fan-mode-description"><span class="material-symbols-outlined">settings_backup_restore</span><span>${esc(t('automaticDescription'))}</span></div>`}
            </div>
            <div class="vm-fan-section">${mode==='curve'?renderCurve(fan,draft,min,max):''}<div class="vm-fan-safety-strip"><span class="material-symbols-outlined">health_and_safety</span><span>${esc(t('safety'))}${state.safetyPolicy?`<small>${esc(t('safetyThresholds',{start:state.safetyPolicy.rampStartTemperature,strong:state.safetyPolicy.strongRampTemperature,emergency:state.safetyPolicy.emergencyTemperature}))}</small>`:''}</span></div></div>
            ${renderPreview(fan,preview,session)}
            <div class="vm-fan-applybar"><div class="vm-fan-applybar__status" data-dirty="${dirty?'true':'false'}">${esc(dirty?t('pending'):session?t('controlSession'):t('noWrites'))}</div><div class="vm-fan-applybar__actions"><button class="vm-fan-button" type="button" data-fan-action="restore" ${canRestoreSafely(fan)?'':'disabled'}><span class="material-symbols-outlined">settings_backup_restore</span>${esc(t('restore'))}</button><button class="vm-fan-button vm-fan-button--accent" type="button" data-fan-action="apply" ${dirty&&(controllable||(mode==='automatic'&&canRestoreSafely(fan)))?'':'disabled'}><span class="material-symbols-outlined">done_all</span>${esc(t('apply'))}</button></div></div>
        </div></div></section>`;
    }
    function kpi(label,value,unit){return `<div class="vm-fan-kpi"><span>${esc(label)}</span><strong>${esc(value)}${unit?` <small>${esc(unit)}</small>`:''}</strong></div>`;}
    function modeButton(mode,key,enabled,current){return `<button type="button" class="vm-fan-mode" data-fan-mode="${mode}" data-active="${current===mode?'true':'false'}" ${enabled?'':'disabled'}>${esc(t(key))}</button>`;}
    function renderManual(fan,draft,min,max){return `<div class="vm-fan-manual"><div class="vm-fan-section__top"><h5>${esc(t('manualOutput'))}</h5><strong>${esc(fmtPct(draft.fixedControlPercent))}</strong></div><input id="vm-fan-manual-range" class="vm-fan-range" type="range" min="${min}" max="${max}" step="1" value="${Math.min(max,Math.max(min,Number(draft.fixedControlPercent)||min))}"><div class="vm-fan-range-labels"><span>${esc(fmtPct(min))}</span><span>${esc(fmtPct(max))}</span></div></div>`;}
    function renderCurveControls(fan,draft,min,max){
        const presets=state.presets[fan.id]||{}, active=Object.keys(presets).find(name=>sameCurve(draft.curve,presets[name]))||'custom';
        return `<div class="vm-fan-presetbar">${[['silent','presetSilent'],['balanced','presetBalanced'],['performance','presetPerformance']].map(([name,key])=>`<button class="vm-fan-preset" data-active="${active===name?'true':'false'}" data-fan-preset="${name}" type="button" ${presets[name]?'':'disabled'}>${esc(t(key))}</button>`).join('')}<span class="vm-fan-preset" data-active="${active==='custom'?'true':'false'}">${esc(t('presetCustom'))}</span><button class="vm-fan-preset" data-fan-action="add-curve-point" type="button" ${(draft.curve||[]).length>=32?'disabled':''}><span class="material-symbols-outlined">add</span>${esc(t('addPoint'))}</button></div>`;
    }
    function sameCurve(a,b){if(!Array.isArray(a)||!Array.isArray(b)||a.length!==b.length)return false;return a.every((p,i)=>Math.abs(p.temperature-b[i].temperature)<.01&&Math.abs(p.controlPercent-b[i].controlPercent)<.01);}
    function referenceTemp(fan,draft){return (fan.availableTemperatureSensors||[]).find(s=>s.id===draft.sensorId)?.value ?? fan.telemetry?.referenceTemperature;}
    function renderPreview(fan,preview,session){
        if(!preview&&!session)return'';
        const error=preview&&preview.valid===false, effective=preview&&preview.effectiveControlPercent;
        return `<div class="vm-fan-preview" data-error="${error?'true':'false'}"><div><span class="vm-fan-eyebrow">${esc(t('preview'))}</span><strong>${error?esc((preview.errors||[]).join(' · ')):esc(finite(effective)?`${t('effectiveOutput')}: ${fmtPct(effective)}`:session?`${t('controlSession')}: ${session.mode}`:t('hardwareDefault'))}</strong></div>${preview&&preview.safetyOverrideActive?`<span class="vm-fan-badge vm-fan-badge--warning">${esc(t('safetyOverride'))}</span>`:''}</div>`;
    }

    function renderCurve(fan,draft,min,max){
        const curve=(draft.curve||[]).slice().sort((a,b)=>a.temperature-b.temperature), temp=referenceTemp(fan,draft), width=320,height=190,l=30,r=306,top=14,bottom=162;
        const x=v=>l+Math.max(0,Math.min(1,(v-20)/80))*(r-l), y=v=>bottom-(Math.max(0,Math.min(100,v))/100)*(bottom-top);
        const points=curve.map((p,i)=>`${x(p.temperature).toFixed(1)},${y(p.controlPercent).toFixed(1)}`).join(' ');
        return `<div class="vm-fan-curve-editor"><div class="vm-fan-section__top"><div><h5>${esc(t('fanCurve'))}</h5><p>${esc(t('curveHint'))}</p></div><span>${esc(`${fmtPct(min)} – ${fmtPct(max)}`)}</span></div><svg class="vm-fan-curve-svg" viewBox="0 0 ${width} ${height}" data-fan-curve-svg="${esc(fan.id)}" role="img">
            <line class="vm-fan-curve__axis" x1="${l}" y1="${top}" x2="${l}" y2="${bottom}"></line><line class="vm-fan-curve__axis" x1="${l}" y1="${bottom}" x2="${r}" y2="${bottom}"></line>
            ${[25,50,75,100].map(v=>`<line class="vm-fan-gridline" x1="${l}" y1="${y(v)}" x2="${r}" y2="${y(v)}"></line><text class="vm-fan-curve__label" x="2" y="${y(v)+3}">${v}%</text>`).join('')}
            ${[20,40,60,80,100].map(v=>`<text class="vm-fan-curve__label" x="${x(v)-8}" y="181">${v}°</text>`).join('')}
            ${finite(temp)?`<line class="vm-fan-curve__current" x1="${x(temp)}" y1="${top}" x2="${x(temp)}" y2="${bottom}"></line>`:''}
            ${points?`<polyline class="vm-fan-curve-line" points="${points}"></polyline>`:''}
            ${finite(temp)&&finite(fan.telemetry&&fan.telemetry.controlPercent)?`<circle class="vm-fan-current-marker" cx="${x(temp)}" cy="${y(fan.telemetry.controlPercent)}" r="5"></circle>`:''}
            ${curve.map((p,i)=>`<circle class="vm-fan-curve-point" data-curve-point="${i}" cx="${x(p.temperature)}" cy="${y(p.controlPercent)}" r="7"></circle><text class="vm-fan-point-label" x="${x(p.temperature)+8}" y="${y(p.controlPercent)-8}">${Math.round(p.temperature)}° · ${Math.round(p.controlPercent)}%</text>`).join('')}
        </svg></div>`;
    }

    function renderVisualPanel(fan){
        const temps=(fan.availableTemperatureSensors||[]).slice(0,8), sameGpu=roleKey(fan.role)==='gpuFan'?devices().filter(x=>roleKey(x.role)==='gpuFan'&&x.hardwareName===fan.hardwareName):[], type=roleKey(fan.role)==='gpuFan'?'gpu':roleKey(fan.role)==='cpuFan'||roleKey(fan.role)==='cpuOptional'?'cpu':roleKey(fan.role)==='pump'?'pump':'fan';
        const componentName=type==='cpu'&&(state.systemInfo&&state.systemInfo.cpuName)?state.systemInfo.cpuName:type==='gpu'&&fan.hardwareName?fan.hardwareName:fan.hardwareName;
        return `<section class="vm-fan-panel vm-fan-visual-panel"><div class="vm-fan-panel__header"><div><span class="vm-fan-eyebrow">LIVE HARDWARE</span><h3>${esc(t('hardwareVisual'))}</h3><p>${esc(t('hardwareVisualSub'))}</p></div><span class="vm-fan-badge vm-fan-badge--accent">${esc(roleLabel(fan.role))}</span></div><div class="vm-fan-panel__body">
            <div class="vm-fan-stage"><div class="vm-fan-model-wrap"><canvas id="vm-fan-webgl" class="vm-fan-webgl" data-model-type="${type}"></canvas><div class="vm-fan-model-fallback"><span class="material-symbols-outlined">${roleIcon(fan.role)}</span></div><div class="vm-fan-visual-label">${esc(t('model3d'))}</div></div><div class="vm-fan-visual-data"><span class="vm-fan-eyebrow">${esc(roleLabel(fan.role))}</span><h4>${esc(componentName||fan.displayName)}</h4><p>${esc(fan.displayName)}</p><div class="vm-fan-live-primary"><strong>${esc(fmtRpm(fan.telemetry&&fan.telemetry.rpm))}</strong><span>${esc(t('rpmUnit'))}</span></div><div class="vm-fan-sensor-list">${infoRow(t('sourceSensor'),temps[0]?`${temps[0].name} · ${fmtTemp(temps[0].value)}`:t('none'))}${type==='gpu'?infoRow(t('fanCount'),String(Math.max(1,sameGpu.length))):''}${infoRow(t('roleConfidence'),confidenceLabel(fan.roleConfidence))}${infoRow(t('controller'),fan.controllerId||t('unknown'))}${infoRow(t('sensorName'),fan.sensorName||t('unknown'))}</div></div></div>
            <div class="vm-fan-linkage" style="--fan-linkage:${finite(fan.telemetry&&fan.telemetry.referenceTemperature)?'100%':'0%'}"><div class="vm-fan-linkage__top"><strong>${esc(t('telemetryLink'))}</strong><span>${esc(activeSession(fan.id)?t('controlSession'):t('hardwareDefault'))}</span></div><div class="vm-fan-linkage__track"></div></div>
            <div class="vm-fan-visual-bottom"><div class="vm-fan-section__top"><h5>${esc(t('coreSensors'))}</h5><span>${temps.length}</span></div><div class="vm-fan-sensor-list">${temps.length?temps.map(s=>`<div class="vm-fan-sensor-row"><span>${esc(s.name)} · ${esc(s.hardware)}</span><strong>${esc(fmtTemp(s.value))}</strong></div>`).join(''):`<div class="vm-fan-sensor-row"><span>${esc(t('noSensor'))}</span><strong>—</strong></div>`}</div>${renderCapabilities(fan.capabilities||{})}</div>
        </div></section>`;
    }
    function infoRow(label,value){return `<div class="vm-fan-sensor-row"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;}
    function renderCapabilities(c){const items=[['capRpm',!!c.rpmReadable],['capControlRead',!!c.controlReadable],['capControlWrite',!!c.controlWritable],['capCurve',!!c.softwareCurveSupported],['capFanStop',!!c.fanStopSupported],['capRestore',!!c.canRestoreDefault]];return `<div class="vm-fan-section vm-fan-capabilities"><div class="vm-fan-section__top"><h5>${esc(t('capabilities'))}</h5><span>${esc(c.backend||t('monitoringBackend'))}</span></div><div class="vm-fan-kpis">${items.map(([k,v])=>`<div class="vm-fan-kpi"><span>${esc(t(k))}</span><strong>${esc(v?t('supported'):t('unsupported'))}</strong></div>`).join('')}</div></div>`;}

    function renderEmpty(){if(!window.Host||!Host.available)return empty('web_asset_off',t('bridgeUnavailable'),t('bridgeUnavailableBody'));if(state.loading)return `<div class="vm-fan-loading"><div><span class="material-symbols-outlined">progress_activity</span><strong>${esc(t('loading'))}</strong><p>${esc(t('loadingBody'))}</p></div></div>`;if(!state.topology||!state.topology.sensorsAvailable)return empty('sensors_off',t('noSensors'),t('noSensorsBody'));return empty('mode_fan_off',t('noFans'),t('noFansBody'));}
    function empty(icon,title,body){return `<div class="vm-fan-empty"><div><span class="material-symbols-outlined">${icon}</span><strong>${esc(title)}</strong><p>${esc(body)}</p></div></div>`;}

    function renderCompatibilityModal(modal){
        const report=modal.report||{items:[]}, profile=modal.profile||state.selectedProfile, mappings=modal.mappings||{};
        const rows=(report.items||[]).map(item=>{
            const map=mappings[item.profileFanId]||{}, localId=map.localFanId||item.matchedFanId||'', localFan=devices().find(f=>f.id===localId), sensors=localFan&&localFan.availableTemperatureSensors||[], sensorId=map.localSensorId||item.matchedSensorId||'';
            const fanOptions=[`<option value="">${esc(t('mappingChooseFan'))}</option>`].concat(devices().map(f=>`<option value="${esc(f.id)}"${f.id===localId?' selected':''}>${esc(f.displayName)} · ${esc(roleLabel(f.role))}</option>`)).join('');
            const sensorOptions=[`<option value="">${esc(t('mappingChooseSensor'))}</option>`].concat(sensors.map(s=>`<option value="${esc(s.id)}"${s.id===sensorId?' selected':''}>${esc(s.name)} · ${esc(fmtTemp(s.value))}</option>`)).join('');
            return `<div class="vm-fan-map-card"><div class="vm-fan-map-card__head"><div><strong>${esc(item.displayName)}</strong><p>${esc(item.reason||'')}</p></div><span class="vm-fan-badge">${esc(t(normalized(item.status)==='matched'?'matched':normalized(item.status)==='needsmapping'?'needsMapping':normalized(item.status)==='missing'?'missing':'incompatible'))}</span></div><div class="vm-fan-map-card__fields"><select class="vm-fan-select" data-map-fan="${esc(item.profileFanId)}">${fanOptions}</select>${profileFanNeedsSensor(profile,item.profileFanId)?`<select class="vm-fan-select" data-map-sensor="${esc(item.profileFanId)}">${sensorOptions}</select>`:''}</div></div>`;
        }).join('');
        return modalShell(t('compatibilityTitle'),t('compatibilityBody'),rows||`<p>${esc(t('none'))}</p>`,`<button class="vm-fan-button" data-fan-action="modal-close" type="button">${esc(t('cancel'))}</button><button class="vm-fan-button vm-fan-button--accent" data-fan-action="apply-profile-modal" type="button">${esc(t('applySetup'))}</button>`,'vm-fan-modal__dialog--wide');
    }
    function profileFanNeedsSensor(profile,profileFanId){return !!(profile&&profile.fans||[]).find(f=>f.profileFanId===profileFanId&&normalized(f.configuration&&f.configuration.mode)==='curve');}
    function renderGroupsModal(){
        const rows=(state.groups||[]).map((g,idx)=>`<div class="vm-fan-group-card" data-group-index="${idx}"><div class="vm-fan-group-card__head"><input class="vm-fan-input" value="${esc(g.name||'Fan group')}" data-group-name="${idx}" maxlength="120"><div class="vm-fan-inline"><button class="vm-fan-button" data-group-apply="${idx}" type="button">${esc(t('applyToGroup'))}</button><button class="vm-fan-button vm-fan-button--danger" data-group-delete="${idx}" type="button">${esc(t('deleteGroup'))}</button></div></div><div class="vm-fan-group-members">${devices().map(f=>`<label><input type="checkbox" data-group-member="${idx}" value="${esc(f.id)}" ${(g.fanProfileIds||[]).includes(f.id)?'checked':''}><span>${esc(f.displayName)}</span></label>`).join('')}</div></div>`).join('');
        return modalShell(t('groupTitle'),'',''+(rows||`<p class="vm-fan-muted">${esc(t('none'))}</p>`),`<button class="vm-fan-button" data-fan-action="group-add" type="button"><span class="material-symbols-outlined">add</span>${esc(t('addGroup'))}</button><button class="vm-fan-button vm-fan-button--accent" data-fan-action="modal-close" type="button">OK</button>`,'vm-fan-modal__dialog--wide');
    }
    function renderModal(){
        const m=state.modal;if(!m)return `<div class="vm-fan-modal" id="vm-fan-modal" data-open="false"></div>`;
        if(m.type==='compatibility')return renderCompatibilityModal(m);
        if(m.type==='groups')return renderGroupsModal();
        if(m.type==='delete-profile')return modalShell(t('deleteTitle'),t('deleteBody'),' ',`<button class="vm-fan-button" data-fan-action="modal-close">${esc(t('cancel'))}</button><button class="vm-fan-button vm-fan-button--danger" data-fan-action="modal-submit">${esc(t('confirmDelete'))}</button>`);
        const defs={'save-profile':[t('saveTitle'),t('saveBody'),t('profileName'),m.value||''],'rename-profile':[t('renameProfileTitle'),'',t('profileName'),m.value||''],'duplicate-profile':[t('duplicateProfileTitle'),'',t('profileName'),m.value||''],'rename-fan':[t('renameFanTitle'),'',t('fanName'),m.value||'']},d=defs[m.type];if(!d)return'';
        const input=`<label class="vm-fan-eyebrow" for="vm-fan-modal-input">${esc(d[2])}</label><input id="vm-fan-modal-input" class="vm-fan-input" maxlength="${m.type==='rename-fan'?60:80}" value="${esc(d[3])}" autocomplete="off">`;
        return modalShell(d[0],d[1],input,`<button class="vm-fan-button" data-fan-action="modal-close">${esc(t('cancel'))}</button><button class="vm-fan-button vm-fan-button--accent" data-fan-action="modal-submit">${esc(t('save'))}</button>`);
    }
    function modalShell(title,subtitle,body,footer,cls){return `<div class="vm-fan-modal" id="vm-fan-modal" data-open="true" role="dialog" aria-modal="true"><div class="vm-fan-modal__dialog ${cls||''}"><div class="vm-fan-modal__header"><div><h3>${esc(title)}</h3>${subtitle?`<p>${esc(subtitle)}</p>`:''}</div><button class="vm-fan-modal__close" data-fan-action="modal-close"><span class="material-symbols-outlined">close</span></button></div><div class="vm-fan-modal__body">${body}</div><div class="vm-fan-modal__footer">${footer}</div></div></div>`;}
    function renderToast(){if(!state.toast)return `<div class="vm-fan-toast" data-open="false"></div>`;return `<div class="vm-fan-toast" data-open="true" data-error="${state.toast.error?'true':'false'}"><span class="material-symbols-outlined">${state.toast.error?'error':'check_circle'}</span><span>${esc(state.toast.message)}</span></div>`;}

    function render(){
        const root=document.getElementById('vm-fan-center');if(!root)return;
        if(devices().length&&!devices().some(f=>f.id===state.selectedFanId))state.selectedFanId=devices()[0].id;
        const fan=selectedFan(); if(fan)ensureDraft(fan);
        root.innerHTML=`${renderToolbar()}${renderNotices()}${fan?`<div class="vm-fan-layout">${renderConfigPanel(fan)}${renderVisualPanel(fan)}</div>`:renderEmpty()}${renderModal()}${renderToast()}`;
        requestAnimationFrame(()=>{document.getElementById('vm-fan-modal-input')?.focus(); mountVisualizer(fan);});
    }
    function mountVisualizer(fan){const canvas=document.getElementById('vm-fan-webgl');if(!canvas||!fan||!window.FanHardwareVisualizer)return;const type=canvas.dataset.modelType||'fan', fanCount=type==='gpu'?devices().filter(x=>roleKey(x.role)==='gpuFan'&&x.hardwareName===fan.hardwareName).length:1;window.FanHardwareVisualizer.mount(canvas,{type,fanCount,rpm:fan.telemetry&&fan.telemetry.rpm});}

    async function loadAll(){
        if(!window.Host||!Host.available){render();return;}if(state.loading)return;state.loading=true;render();
        try{const [topology,profiles,controlState,systemInfo,safetyPolicy]=await Promise.all([Host.call('getFanTopology'),Host.call('listFanProfiles'),Host.call('getFanControlState'),Host.call('getSystemInfo'),Host.call('getFanSafetyPolicy')]);state.topology=topology;state.profiles=profiles||[];state.controlState=controlState;state.systemInfo=systemInfo;state.safetyPolicy=safetyPolicy;syncDraftsFromRuntime();if(!state.selectedFanId&&devices().length)state.selectedFanId=devices()[0].id;if(state.selectedFanId)await ensurePresets(selectedFan());state.lastRefreshAt=Date.now();}
        catch(error){showError(error);}finally{state.loading=false;render();}
    }
    async function refreshTopology(silent){if(!window.Host||!Host.available||state.actionBusy)return;try{const previousFanId=state.selectedFanId;const [topology,controlState]=await Promise.all([Host.call('getFanTopology'),Host.call('getFanControlState')]);state.topology=topology;state.controlState=controlState;if(previousFanId&&!devices().some(f=>f.id===previousFanId)){state.selectedFanId=devices()[0]?.id||null;state.toast={message:t('deviceDisconnected'),error:true};}if(state.selectedFanId)ensurePresets(selectedFan());state.lastRefreshAt=Date.now();render();}catch(error){if(!silent)showError(error);}}
    async function reloadProfiles(){state.profiles=await Host.call('listFanProfiles');if(state.selectedProfileId&&!state.profiles.some(p=>p.id===state.selectedProfileId)){state.selectedProfileId='';state.selectedProfile=null;}}
    async function selectProfile(id){state.selectedProfileId=id||'';state.selectedProfile=null;if(!id){state.groups=[];render();return;}try{const profile=await Host.call('getFanProfile',{profileId:id});state.selectedProfile=profile;state.groups=clone(profile.groups||[]);(profile.fans||[]).forEach(pf=>{const local=devices().find(f=>f.id===pf.profileFanId);if(local&&pf.configuration){state.drafts[local.id]=clone(pf.configuration);state.dirtyFans.add(local.id);}});const preferred=profile.uiPreferences&&profile.uiPreferences.selectedFanId;if(preferred&&devices().some(f=>f.id===preferred))state.selectedFanId=preferred;render();}catch(error){showError(error);}}
    function openModal(type,value,extra){state.modal=Object.assign({type,value:value||''},extra||{});render();}
    function closeModal(){state.modal=null;render();}
    function toast(message,error){state.toast={message,error:!!error};if(state.toastTimer)clearTimeout(state.toastTimer);render();state.toastTimer=setTimeout(()=>{state.toast=null;render();},3600);}
    function showError(error){toast(t('operationFailed',{error:error&&error.message?error.message:String(error||'Error')}),true);}
    async function runAction(fn){if(state.actionBusy)return;state.actionBusy=true;try{await fn();}catch(error){showError(error);}finally{state.actionBusy=false;}}

    function profileConfigurations(){const result={};devices().forEach(f=>{if(state.drafts[f.id])result[f.id]=clone(state.drafts[f.id]);});return result;}
    function profileSavePayload(name,profileId){return {profileId:profileId||null,name,configurations:profileConfigurations(),groups:state.groups,uiPreferences:{selectedFanId:state.selectedFanId||''}};}
    async function saveSelectedProfile(){const p=selectedProfileSummary();if(!p){openModal('save-profile','');return;}await runAction(async()=>{const result=await Host.call('saveFanProfile',profileSavePayload(p.name,p.id));state.selectedProfileId=result.id;await reloadProfiles();state.selectedProfile=await Host.call('getFanProfile',{profileId:result.id});state.groups=clone(state.selectedProfile.groups||[]);toast(t('profileSaved'));});}
    async function submitModal(){
        const m=state.modal;if(!m)return;
        if(m.type==='delete-profile'){await runAction(async()=>{await Host.call('deleteFanProfile',{profileId:state.selectedProfileId});state.selectedProfileId='';state.selectedProfile=null;state.groups=[];state.modal=null;await reloadProfiles();toast(t('profileDeleted'));});return;}
        const input=document.getElementById('vm-fan-modal-input'),value=(input&&input.value||'').trim();if(!value){input?.focus();return;}
        if(m.type==='save-profile')await runAction(async()=>{const result=await Host.call('saveFanProfile',profileSavePayload(value,null));state.selectedProfileId=result.id;state.modal=null;await reloadProfiles();state.selectedProfile=await Host.call('getFanProfile',{profileId:result.id});state.groups=clone(state.selectedProfile.groups||[]);toast(t('profileSaved'));});
        else if(m.type==='rename-profile')await runAction(async()=>{await Host.call('renameFanProfile',{profileId:state.selectedProfileId,name:value});state.modal=null;await reloadProfiles();toast(t('profileRenamed'));});
        else if(m.type==='duplicate-profile')await runAction(async()=>{const r=await Host.call('duplicateFanProfile',{profileId:state.selectedProfileId,name:value});state.selectedProfileId=r.id;state.modal=null;await reloadProfiles();state.selectedProfile=await Host.call('getFanProfile',{profileId:r.id});toast(t('profileDuplicated'));});
        else if(m.type==='rename-fan')await runAction(async()=>{state.topology=await Host.call('renameFan',{fanId:state.selectedFanId,alias:value});state.modal=null;toast(t('fanRenamed'));});
    }

    async function applyCurrent(){const fan=selectedFan(),draft=fan&&ensureDraft(fan);if(!fan||!draft)return;await runAction(async()=>{const preview=await Host.call('previewFanConfiguration',{fanId:fan.id,configuration:draft});state.previews[fan.id]=preview;if(!preview.valid){render();toast(t('applyFailed')+': '+(preview.errors||[]).join(' · '),true);return;}const result=await Host.call('applyFanConfiguration',{topologyRevision:state.topology.revision,fanId:fan.id,configuration:draft});if(!result.success){toast(t('applyFailed')+': '+result.message,true);return;}state.dirtyFans.delete(fan.id);toast(t('applied'));await refreshTopology(true);});}
    async function restoreCurrent(){const fan=selectedFan();if(!fan)return;await runAction(async()=>{const result=await Host.call('restoreFanDefault',{fanId:fan.id});if(!result.success){toast(t('applyFailed')+': '+result.message,true);return;}state.drafts[fan.id]=defaultDraft(fan);state.dirtyFans.delete(fan.id);state.previews[fan.id]=null;toast(t('hardwareDefault'));await refreshTopology(true);});}
    async function openCompatibility(apply){const p=selectedProfileSummary();if(!p)return;await runAction(async()=>{const [report,profile]=await Promise.all([Host.call('analyzeFanProfileCompatibility',{profileId:p.id}),Host.call('getFanProfile',{profileId:p.id})]);const mappings={};(report.items||[]).forEach(i=>mappings[i.profileFanId]={localFanId:i.matchedFanId||'',localSensorId:i.matchedSensorId||''});openModal('compatibility','',{report,profile,mappings,applyImmediately:!!apply});});}
    async function applyProfileModal(){const m=state.modal;if(!m||m.type!=='compatibility')return;const mappings=Object.entries(m.mappings||{}).map(([profileFanId,v])=>({profileFanId,localFanId:v.localFanId||'',localSensorId:v.localSensorId||null}));await runAction(async()=>{const r=await Host.call('applyFanProfile',{profileId:state.selectedProfileId,mappings});if(!r.success){toast(t('applyFailed')+': '+r.message,true);return;}adoptMappedProfile(m.profile,mappings);state.modal=null;toast(t('profileApplied'));await refreshTopology(true);});}
    function adoptMappedProfile(profile,mappings){if(!profile)return;const byProfile=Object.create(null);(mappings||[]).forEach(m=>{if(m.profileFanId&&m.localFanId)byProfile[m.profileFanId]=m;});(profile.fans||[]).forEach(pf=>{const map=byProfile[pf.profileFanId]||{localFanId:pf.profileFanId,localSensorId:null};if(!map.localFanId||!pf.configuration||!devices().some(f=>f.id===map.localFanId))return;const cfg=clone(pf.configuration);if(normalized(cfg.mode)==='curve'&&map.localSensorId)cfg.sensorId=map.localSensorId;state.drafts[map.localFanId]=cfg;state.dirtyFans.delete(map.localFanId);});state.groups=(profile.groups||[]).map(g=>({id:g.id,name:g.name,fanProfileIds:(g.fanProfileIds||[]).map(id=>(byProfile[id]&&byProfile[id].localFanId)||id).filter(id=>devices().some(f=>f.id===id))}));const preferred=profile.uiPreferences&&profile.uiPreferences.selectedFanId;const mappedPreferred=(byProfile[preferred]&&byProfile[preferred].localFanId)||preferred;if(mappedPreferred&&devices().some(f=>f.id===mappedPreferred))state.selectedFanId=mappedPreferred;}

    function applyPreset(name){const fan=selectedFan(),draft=fan&&ensureDraft(fan),preset=fan&&state.presets[fan.id]&&state.presets[fan.id][name];if(!fan||!draft||!preset)return;draft.mode='curve';draft.curve=clone(preset);markDirty(fan.id);}
    function addCurvePoint(){const fan=selectedFan(),draft=fan&&ensureDraft(fan);if(!fan||!draft)return;let curve=draft.curve||[];if(curve.length<2){curve=[{temperature:40,controlPercent:fan.capabilities.minimumControl},{temperature:90,controlPercent:fan.capabilities.maximumControl}];}else{let best=0,gap=-1;for(let i=1;i<curve.length;i++){const g=curve[i].temperature-curve[i-1].temperature;if(g>gap){gap=g;best=i;}}const a=curve[best-1],b=curve[best];curve.splice(best,0,{temperature:Math.round((a.temperature+b.temperature)/2),controlPercent:Math.round((a.controlPercent+b.controlPercent)/2)});}draft.curve=curve;draft.mode='curve';markDirty(fan.id);}

    async function handleAction(action){
        if(!window.Host||!Host.available)return;const p=selectedProfileSummary(),fan=selectedFan();
        switch(action){case'refresh':await refreshTopology(false);break;case'save-profile':await saveSelectedProfile();break;case'apply-profile':if(p)await openCompatibility(true);break;case'compatibility':if(p)await openCompatibility(false);break;case'groups':openModal('groups');break;case'rename-profile':if(p)openModal('rename-profile',p.name);break;case'duplicate-profile':if(p)openModal('duplicate-profile',p.name+' copy');break;case'delete-profile':if(p)openModal('delete-profile');break;case'rename-fan':if(fan)openModal('rename-fan',fan.userName||fan.displayName);break;case'modal-close':closeModal();break;case'modal-submit':await submitModal();break;case'apply':await applyCurrent();break;case'restore':await restoreCurrent();break;case'add-curve-point':addCurvePoint();break;case'apply-profile-modal':await applyProfileModal();break;case'group-add':state.groups.push({id:'group-'+Date.now().toString(36),name:'Fan group '+(state.groups.length+1),fanProfileIds:[]});render();break;
        case'import-profile':await runAction(async()=>{const r=await Host.call('importFanProfile');if(!r||r.canceled)return;await reloadProfiles();state.selectedProfileId=r.profile.id;state.selectedProfile=r.profile;state.groups=clone(r.profile.groups||[]);const mappings={};(r.compatibility.items||[]).forEach(i=>mappings[i.profileFanId]={localFanId:i.matchedFanId||'',localSensorId:i.matchedSensorId||''});toast(t('profileImported'));openModal('compatibility','',{report:r.compatibility,profile:r.profile,mappings});});break;
        case'export-profile':if(p)await runAction(async()=>{const r=await Host.call('exportFanProfile',{profileId:p.id});if(r&&!r.canceled)toast(t('profileExported',{file:r.fileName||'JSON'}));});break;}
    }

    function curvePointFromEvent(svg,event,index){const fan=selectedFan(),draft=fan&&ensureDraft(fan);if(!fan||!draft||!svg.isConnected)return;const rect=svg.getBoundingClientRect(),px=(event.clientX-rect.left)/rect.width*320,py=(event.clientY-rect.top)/rect.height*190,l=30,r=306,top=14,bottom=162;let temp=20+(Math.max(l,Math.min(r,px))-l)/(r-l)*80,control=(bottom-Math.max(top,Math.min(bottom,py)))/(bottom-top)*100;const curve=draft.curve||[],caps=fan.capabilities||{},min=finite(caps.minimumControl)?caps.minimumControl:0,max=finite(caps.maximumControl)?caps.maximumControl:100;control=Math.max(min,Math.min(max,control));const prev=curve[index-1],next=curve[index+1];if(prev){temp=Math.max(prev.temperature+1,temp);control=Math.max(prev.controlPercent,control);}if(next){temp=Math.min(next.temperature-1,temp);control=Math.min(next.controlPercent,control);}curve[index].temperature=Math.round(temp);curve[index].controlPercent=Math.round(control);state.dirtyFans.add(fan.id);state.previews[fan.id]=null;paintCurveDom(svg,curve);}
    function paintCurveDom(svg,curve){const l=30,r=306,top=14,bottom=162,x=v=>l+Math.max(0,Math.min(1,(v-20)/80))*(r-l),y=v=>bottom-(Math.max(0,Math.min(100,v))/100)*(bottom-top);const line=svg.querySelector('.vm-fan-curve-line');if(line)line.setAttribute('points',curve.map(p=>`${x(p.temperature).toFixed(1)},${y(p.controlPercent).toFixed(1)}`).join(' '));svg.querySelectorAll('[data-curve-point]').forEach((circle,i)=>{const p=curve[i];if(!p)return;circle.setAttribute('cx',x(p.temperature));circle.setAttribute('cy',y(p.controlPercent));});svg.querySelectorAll('.vm-fan-point-label').forEach((label,i)=>{const p=curve[i];if(!p)return;label.setAttribute('x',x(p.temperature)+8);label.setAttribute('y',y(p.controlPercent)-8);label.textContent=`${Math.round(p.temperature)}° · ${Math.round(p.controlPercent)}%`;});}

    async function applyGroup(index){const g=state.groups[index],fan=selectedFan(),draft=fan&&ensureDraft(fan);if(!g||!fan||!draft||!(g.fanProfileIds||[]).length)return;await runAction(async()=>{const r=await Host.call('applyFanGroupConfiguration',{topologyRevision:state.topology.revision,fanIds:g.fanProfileIds,configuration:draft});if(!r.success){toast(t('applyFailed')+': '+r.message,true);return;}toast(t('groupApplied'));await refreshTopology(true);});}

    function wireRoot(root){
        root.addEventListener('click',event=>{const fb=event.target.closest('[data-fan-id]');if(fb){state.selectedFanId=fb.dataset.fanId;ensurePresets(selectedFan());render();return;}const mode=event.target.closest('[data-fan-mode]');if(mode&&!mode.disabled){const fan=selectedFan(),draft=fan&&ensureDraft(fan);if(draft){draft.mode=mode.dataset.fanMode;markDirty(fan.id);}return;}const preset=event.target.closest('[data-fan-preset]');if(preset&&!preset.disabled){applyPreset(preset.dataset.fanPreset);return;}const gd=event.target.closest('[data-group-delete]');if(gd){state.groups.splice(Number(gd.dataset.groupDelete),1);render();return;}const ga=event.target.closest('[data-group-apply]');if(ga){applyGroup(Number(ga.dataset.groupApply));return;}const action=event.target.closest('[data-fan-action]');if(action&&!action.disabled)handleAction(action.dataset.fanAction);});
        root.addEventListener('input',event=>{const fan=selectedFan(),draft=fan&&ensureDraft(fan);if(event.target.id==='vm-fan-manual-range'&&draft){draft.fixedControlPercent=Number(event.target.value);state.dirtyFans.add(fan.id);state.previews[fan.id]=null;schedulePreview(fan.id);render();return;}if(event.target.matches('[data-group-name]')){const g=state.groups[Number(event.target.dataset.groupName)];if(g)g.name=event.target.value;}});
        root.addEventListener('change',event=>{if(event.target.id==='vm-fan-profile-select'){selectProfile(event.target.value);return;}const fan=selectedFan(),draft=fan&&ensureDraft(fan);if(event.target.id==='vm-fan-sensor-select'&&draft){draft.sensorId=event.target.value||null;markDirty(fan.id);return;}if(event.target.matches('[data-map-fan]')){const key=event.target.dataset.mapFan,m=state.modal;m.mappings[key]=m.mappings[key]||{};m.mappings[key].localFanId=event.target.value;m.mappings[key].localSensorId='';render();return;}if(event.target.matches('[data-map-sensor]')){const key=event.target.dataset.mapSensor,m=state.modal;m.mappings[key]=m.mappings[key]||{};m.mappings[key].localSensorId=event.target.value;return;}if(event.target.matches('[data-group-member]')){const idx=Number(event.target.dataset.groupMember),g=state.groups[idx];if(!g)return;g.fanProfileIds=g.fanProfileIds||[];if(event.target.checked&&!g.fanProfileIds.includes(event.target.value))g.fanProfileIds.push(event.target.value);if(!event.target.checked)g.fanProfileIds=g.fanProfileIds.filter(id=>id!==event.target.value);}});
        root.addEventListener('pointerdown',event=>{const point=event.target.closest('[data-curve-point]'),svg=event.target.closest('[data-fan-curve-svg]');if(!point||!svg)return;event.preventDefault();state.curveDrag={index:Number(point.dataset.curvePoint),svg};try{svg.setPointerCapture(event.pointerId);}catch(_){}});
        root.addEventListener('pointermove',event=>{if(!state.curveDrag)return;curvePointFromEvent(state.curveDrag.svg,event,state.curveDrag.index);});
        root.addEventListener('pointerup',event=>{if(!state.curveDrag)return;const fan=selectedFan();state.curveDrag=null;if(fan)schedulePreview(fan.id);render();});
    }

    function mount(){const root=document.getElementById('vm-fan-center');if(!root||state.mounted)return;state.mounted=true;wireRoot(root);render();}
    function setActive(active){state.active=!!active;if(!state.active)return;mount();if(!state.topology)loadAll();else if(Date.now()-state.lastRefreshAt>2500)refreshTopology(true);}
    document.addEventListener('voltuiready',()=>{mount();const view=document.getElementById('view-cooling');setActive(!!view&&!view.classList.contains('hidden'));});
    document.addEventListener('voltuiviewchanged',event=>setActive(event.detail&&event.detail.view==='cooling'));
    document.addEventListener('langchanged',()=>{if(state.mounted)render();});
    document.addEventListener('keydown',event=>{if(event.key==='Escape'&&state.modal)closeModal();});
    if(window.Host&&Host.on){Host.on('metrics',()=>{if(!state.active||state.loading||state.actionBusy||Date.now()-state.lastRefreshAt<2100)return;refreshTopology(true);});Host.on('fanControlChanged',runtime=>{state.controlState=runtime;syncDraftsFromRuntime();if(state.active)render();});}
    if(document.readyState==='complete')setTimeout(()=>{mount();const view=document.getElementById('view-cooling');if(view&&!view.classList.contains('hidden'))setActive(true);},0);
})();
