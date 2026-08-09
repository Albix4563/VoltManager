/* VoltManager Advanced Cooling / Fan Center
 * Hardware writes are intentionally absent in this phase. The UI consumes a capability
 * model from the host and never turns an RPM sensor into an assumed writable controller.
 */
(function () {
    'use strict';

    const state = {
        mounted: false,
        active: false,
        loading: false,
        actionBusy: false,
        topology: null,
        profiles: [],
        selectedFanId: null,
        selectedProfileId: '',
        lastRefreshAt: 0,
        modal: null,
        toast: null,
        toastTimer: null,
    };

    const strings = {
        it: {
            profile: 'Setup', noProfile: 'Nessun setup selezionato', saveSetup: 'Salva setup',
            rename: 'Rinomina', duplicate: 'Duplica', remove: 'Elimina', import: 'Importa', export: 'Esporta',
            compatibility: 'Compatibilità', refresh: 'Rileva hardware', readOnly: 'Sola lettura', controlAvailable: 'Controllo disponibile',
            unavailable: 'Telemetria non disponibile', possibleSoftware: 'Possibile software di controllo rilevato',
            possibleSoftwareBody: '{software} è in esecuzione ({process}). Il processo è solo un indizio: Windows non espone un ownership universale degli header. VoltManager non tenta di prendere forzatamente il controllo.',
            monitorOnlyBanner: 'Questa versione del Fan Center usa solo la telemetria già raccolta da VoltManager. Nessuna scrittura PWM viene effettuata finché un backend non dichiara capability di controllo verificabili.',
            config: 'Configurazione', configSub: 'Seleziona una ventola e verifica capability, sensori e stato.', fansDetected: '{count} rilevate',
            groupCpu: 'CPU / Pompa', groupGpu: 'GPU', groupCase: 'Case / System', groupOther: 'Non classificate',
            rpm: 'RPM', pwm: 'PWM', temperature: 'Temperatura', rpmUnit: 'RPM', pwmUnavailable: 'N/D', tempUnavailable: 'N/D',
            mode: 'Modalità', automatic: 'Automatico', manual: 'Manuale', curve: 'Curva', sensor: 'Sensore di riferimento', noSensor: 'Nessun sensore associabile',
            fanCurve: 'Curva ventola', curveUnavailable: 'Editor curva non disponibile', curveUnavailableBody: 'Il controller espone gli RPM ma non un controllo scrivibile verificato. La curva non viene inventata né applicata.',
            safety: 'VoltManager mantiene i limiti hardware e non abilita Fan Stop, PWM minimo o override che il backend non dichiara esplicitamente.',
            apply: 'Applica modifiche', restore: 'Ripristina default', noWrites: 'Nessuna modifica hardware in attesa.',
            hardwareVisual: 'Visualizzazione hardware', hardwareVisualSub: 'Rappresentazione locale e telemetria del componente associato.',
            sourceSensor: 'Sensore principale', roleConfidence: 'Identificazione', controller: 'Controller', sensorName: 'Canale sensore',
            telemetryLink: 'Temperatura → comportamento ventola', telemetryOnly: 'Telemetria attiva · nessuna curva software',
            capabilities: 'Capability rilevate', capRpm: 'RPM leggibili', capControlRead: 'PWM leggibile', capControlWrite: 'Controllo scrivibile',
            capCurve: 'Curva software', capFanStop: 'Fan Stop', capRestore: 'Ripristino default', supported: 'Sì', unsupported: 'No', unknown: 'Sconosciuto',
            noFans: 'Nessuna ventola rilevata', noFansBody: 'VoltManager non ha trovato sensori di tipo fan. Il firmware o il controller potrebbe non esporli al sistema operativo.',
            noSensors: 'Sensori hardware non disponibili', noSensorsBody: 'Il provider hardware non è disponibile o il driver di accesso è bloccato. Il Fan Center resta in sola lettura senza dati.',
            bridgeUnavailable: 'Fan Center non disponibile in anteprima browser', bridgeUnavailableBody: 'Apri questa schermata dentro VoltManager per accedere alla telemetria hardware.',
            loading: 'Analisi del sistema di raffreddamento', loadingBody: 'Sto costruendo la topologia usando i sensori già disponibili.',
            saveTitle: 'Salva nuovo setup', saveBody: 'Salva associazioni e nomi correnti. Nessun parametro hardware viene applicato.', profileName: 'Nome setup', save: 'Salva', cancel: 'Annulla',
            renameProfileTitle: 'Rinomina setup', duplicateProfileTitle: 'Duplica setup', renameFanTitle: 'Rinomina ventola', fanName: 'Nome ventola',
            deleteTitle: 'Eliminare questo setup?', deleteBody: 'Il file del profilo verrà rimosso. Questa azione non modifica l’hardware.', confirmDelete: 'Elimina setup',
            compatibilityTitle: 'Compatibilità del setup', compatibilityBody: 'Dry-run: nessuna configurazione viene applicata.', matched: 'Compatibile', needsMapping: 'Mapping manuale richiesto', missing: 'Dispositivo mancante', incompatible: 'Incompatibile',
            storedOnly: 'Il profilo può essere archiviato, ma non applicato al controllo hardware con le capability attuali.',
            profileSaved: 'Setup salvato.', profileRenamed: 'Setup rinominato.', profileDuplicated: 'Setup duplicato.', profileDeleted: 'Setup eliminato.', profileImported: 'Setup importato e validato.', profileExported: 'Setup esportato: {file}', fanRenamed: 'Nome ventola aggiornato.',
            operationFailed: 'Operazione non riuscita: {error}', confidenceConfirmed: 'Confermata', confidenceHigh: 'Alta', confidenceMedium: 'Media', confidenceLow: 'Bassa', confidenceUserAssigned: 'Utente',
            roleCpuFan: 'CPU Fan', roleCpuOptional: 'CPU Optional', roleGpuFan: 'GPU Fan', roleCaseFan: 'Case / System Fan', rolePump: 'Pump / AIO', roleExternal: 'Controller esterno', roleUnknown: 'Ventola non classificata',
            monitoringBackend: 'Monitoring', profileActions: 'Azioni setup', current: 'corrente', coreSensors: 'Sensori termici', none: 'Nessuno',
        },
        en: {
            profile: 'Setup', noProfile: 'No setup selected', saveSetup: 'Save setup', rename: 'Rename', duplicate: 'Duplicate', remove: 'Delete', import: 'Import', export: 'Export', compatibility: 'Compatibility', refresh: 'Detect hardware',
            readOnly: 'Read only', controlAvailable: 'Control available', unavailable: 'Telemetry unavailable', possibleSoftware: 'Possible control software detected',
            possibleSoftwareBody: '{software} is running ({process}). The process is evidence only: Windows exposes no universal ownership API for fan headers. VoltManager will not force control.',
            monitorOnlyBanner: 'This Fan Center build uses only telemetry already collected by VoltManager. No PWM write is performed until a backend declares verified control capabilities.',
            config: 'Configuration', configSub: 'Select a fan and inspect capabilities, sensors, and state.', fansDetected: '{count} detected',
            groupCpu: 'CPU / Pump', groupGpu: 'GPU', groupCase: 'Case / System', groupOther: 'Unclassified', rpm: 'RPM', pwm: 'PWM', temperature: 'Temperature', rpmUnit: 'RPM', pwmUnavailable: 'N/A', tempUnavailable: 'N/A',
            mode: 'Mode', automatic: 'Automatic', manual: 'Manual', curve: 'Curve', sensor: 'Reference sensor', noSensor: 'No compatible sensor', fanCurve: 'Fan curve', curveUnavailable: 'Curve editor unavailable', curveUnavailableBody: 'The controller exposes RPM telemetry but no verified writable control. VoltManager does not invent or apply a curve.',
            safety: 'VoltManager preserves hardware limits and never enables Fan Stop, minimum PWM, or overrides unless the backend explicitly declares them.', apply: 'Apply changes', restore: 'Restore default', noWrites: 'No pending hardware changes.',
            hardwareVisual: 'Hardware visual', hardwareVisualSub: 'Local representation and telemetry for the associated component.', sourceSensor: 'Primary sensor', roleConfidence: 'Identification', controller: 'Controller', sensorName: 'Sensor channel', telemetryLink: 'Temperature → fan behavior', telemetryOnly: 'Telemetry active · no software curve',
            capabilities: 'Detected capabilities', capRpm: 'RPM readable', capControlRead: 'PWM readable', capControlWrite: 'Writable control', capCurve: 'Software curve', capFanStop: 'Fan Stop', capRestore: 'Restore default', supported: 'Yes', unsupported: 'No', unknown: 'Unknown',
            noFans: 'No fans detected', noFansBody: 'VoltManager found no fan-type sensors. Firmware or the controller may not expose them to the operating system.', noSensors: 'Hardware sensors unavailable', noSensorsBody: 'The hardware provider is unavailable or its access driver is blocked. Fan Center remains read-only without telemetry.', bridgeUnavailable: 'Fan Center unavailable in browser preview', bridgeUnavailableBody: 'Open this screen inside VoltManager to access hardware telemetry.', loading: 'Analyzing cooling topology', loadingBody: 'Building the topology from sensors already available to VoltManager.',
            saveTitle: 'Save new setup', saveBody: 'Save current mappings and names. No hardware parameter is applied.', profileName: 'Setup name', save: 'Save', cancel: 'Cancel', renameProfileTitle: 'Rename setup', duplicateProfileTitle: 'Duplicate setup', renameFanTitle: 'Rename fan', fanName: 'Fan name', deleteTitle: 'Delete this setup?', deleteBody: 'The profile file will be removed. Hardware is not changed.', confirmDelete: 'Delete setup',
            compatibilityTitle: 'Setup compatibility', compatibilityBody: 'Dry-run only: no configuration is applied.', matched: 'Compatible', needsMapping: 'Manual mapping required', missing: 'Device missing', incompatible: 'Incompatible', storedOnly: 'The profile can be stored, but cannot control hardware with the current capabilities.',
            profileSaved: 'Setup saved.', profileRenamed: 'Setup renamed.', profileDuplicated: 'Setup duplicated.', profileDeleted: 'Setup deleted.', profileImported: 'Setup imported and validated.', profileExported: 'Setup exported: {file}', fanRenamed: 'Fan name updated.', operationFailed: 'Operation failed: {error}',
            confidenceConfirmed: 'Confirmed', confidenceHigh: 'High', confidenceMedium: 'Medium', confidenceLow: 'Low', confidenceUserAssigned: 'User', roleCpuFan: 'CPU Fan', roleCpuOptional: 'CPU Optional', roleGpuFan: 'GPU Fan', roleCaseFan: 'Case / System Fan', rolePump: 'Pump / AIO', roleExternal: 'External controller', roleUnknown: 'Unclassified fan', monitoringBackend: 'Monitoring', profileActions: 'Setup actions', current: 'current', coreSensors: 'Thermal sensors', none: 'None',
        },
        es: {
            profile: 'Configuración', noProfile: 'Ninguna configuración seleccionada', saveSetup: 'Guardar', rename: 'Renombrar', duplicate: 'Duplicar', remove: 'Eliminar', import: 'Importar', export: 'Exportar', compatibility: 'Compatibilidad', refresh: 'Detectar hardware', readOnly: 'Solo lectura', controlAvailable: 'Control disponible', unavailable: 'Telemetría no disponible',
            possibleSoftware: 'Posible software de control detectado', possibleSoftwareBody: '{software} está en ejecución ({process}). El proceso es solo una evidencia: Windows no ofrece una API universal de propiedad de los headers. VoltManager no forzará el control.', monitorOnlyBanner: 'Esta versión del Fan Center usa solo la telemetría ya recopilada por VoltManager. No se escribe PWM hasta disponer de un backend con capacidades verificadas.',
            config: 'Configuración', configSub: 'Selecciona un ventilador y revisa capacidades, sensores y estado.', fansDetected: '{count} detectados', groupCpu: 'CPU / Bomba', groupGpu: 'GPU', groupCase: 'Caja / Sistema', groupOther: 'Sin clasificar', rpm: 'RPM', pwm: 'PWM', temperature: 'Temperatura', rpmUnit: 'RPM', pwmUnavailable: 'N/D', tempUnavailable: 'N/D', mode: 'Modo', automatic: 'Automático', manual: 'Manual', curve: 'Curva', sensor: 'Sensor de referencia', noSensor: 'Sin sensor compatible', fanCurve: 'Curva del ventilador', curveUnavailable: 'Editor de curva no disponible', curveUnavailableBody: 'El controlador expone RPM, pero no un control escribible verificado. VoltManager no inventa ni aplica una curva.', safety: 'VoltManager conserva los límites de hardware y no habilita Fan Stop, PWM mínimo ni overrides si el backend no los declara.', apply: 'Aplicar cambios', restore: 'Restaurar default', noWrites: 'No hay cambios de hardware pendientes.',
            hardwareVisual: 'Vista de hardware', hardwareVisualSub: 'Representación local y telemetría del componente asociado.', sourceSensor: 'Sensor principal', roleConfidence: 'Identificación', controller: 'Controlador', sensorName: 'Canal del sensor', telemetryLink: 'Temperatura → comportamiento', telemetryOnly: 'Telemetría activa · sin curva software', capabilities: 'Capacidades detectadas', capRpm: 'RPM legibles', capControlRead: 'PWM legible', capControlWrite: 'Control escribible', capCurve: 'Curva software', capFanStop: 'Fan Stop', capRestore: 'Restaurar default', supported: 'Sí', unsupported: 'No', unknown: 'Desconocido',
            noFans: 'No se detectaron ventiladores', noFansBody: 'VoltManager no encontró sensores de tipo ventilador. El firmware o controlador puede no exponerlos al sistema operativo.', noSensors: 'Sensores de hardware no disponibles', noSensorsBody: 'El proveedor de hardware no está disponible o su driver está bloqueado.', bridgeUnavailable: 'Fan Center no disponible en la vista previa', bridgeUnavailableBody: 'Abre esta pantalla dentro de VoltManager para usar la telemetría.', loading: 'Analizando refrigeración', loadingBody: 'Construyendo la topología con los sensores ya disponibles.',
            saveTitle: 'Guardar nueva configuración', saveBody: 'Guarda asociaciones y nombres actuales. No se aplica ningún parámetro de hardware.', profileName: 'Nombre', save: 'Guardar', cancel: 'Cancelar', renameProfileTitle: 'Renombrar configuración', duplicateProfileTitle: 'Duplicar configuración', renameFanTitle: 'Renombrar ventilador', fanName: 'Nombre del ventilador', deleteTitle: '¿Eliminar esta configuración?', deleteBody: 'Se eliminará el archivo del perfil. El hardware no cambia.', confirmDelete: 'Eliminar', compatibilityTitle: 'Compatibilidad', compatibilityBody: 'Dry-run: no se aplica ninguna configuración.', matched: 'Compatible', needsMapping: 'Requiere mapping manual', missing: 'Dispositivo ausente', incompatible: 'Incompatible', storedOnly: 'El perfil se puede guardar, pero no controlar el hardware con las capacidades actuales.', profileSaved: 'Configuración guardada.', profileRenamed: 'Configuración renombrada.', profileDuplicated: 'Configuración duplicada.', profileDeleted: 'Configuración eliminada.', profileImported: 'Configuración importada y validada.', profileExported: 'Configuración exportada: {file}', fanRenamed: 'Nombre actualizado.', operationFailed: 'Error: {error}', confidenceConfirmed: 'Confirmada', confidenceHigh: 'Alta', confidenceMedium: 'Media', confidenceLow: 'Baja', confidenceUserAssigned: 'Usuario', roleCpuFan: 'CPU Fan', roleCpuOptional: 'CPU Optional', roleGpuFan: 'GPU Fan', roleCaseFan: 'Case / System Fan', rolePump: 'Pump / AIO', roleExternal: 'Controlador externo', roleUnknown: 'Ventilador sin clasificar', monitoringBackend: 'Monitorización', profileActions: 'Acciones', current: 'actual', coreSensors: 'Sensores térmicos', none: 'Ninguno',
        },
        zh: {
            profile: '配置', noProfile: '未选择配置', saveSetup: '保存配置', rename: '重命名', duplicate: '复制', remove: '删除', import: '导入', export: '导出', compatibility: '兼容性', refresh: '检测硬件', readOnly: '只读', controlAvailable: '可控制', unavailable: '遥测不可用', possibleSoftware: '检测到可能的控制软件', possibleSoftwareBody: '{software} 正在运行（{process}）。进程仅作为线索；Windows 不提供通用的风扇接口所有权 API。VoltManager 不会强制接管。', monitorOnlyBanner: '当前 Fan Center 仅使用 VoltManager 已收集的遥测。只有后端明确声明经过验证的控制能力后才允许写入 PWM。',
            config: '配置', configSub: '选择风扇并查看能力、传感器和状态。', fansDetected: '检测到 {count} 个', groupCpu: 'CPU / 水泵', groupGpu: 'GPU', groupCase: '机箱 / 系统', groupOther: '未分类', rpm: 'RPM', pwm: 'PWM', temperature: '温度', rpmUnit: 'RPM', pwmUnavailable: '不可用', tempUnavailable: '不可用', mode: '模式', automatic: '自动', manual: '手动', curve: '曲线', sensor: '参考传感器', noSensor: '无兼容传感器', fanCurve: '风扇曲线', curveUnavailable: '曲线编辑器不可用', curveUnavailableBody: '控制器提供 RPM 遥测，但没有经过验证的可写控制。VoltManager 不会虚构或应用曲线。', safety: 'VoltManager 保留硬件限制，只有后端明确声明时才启用 Fan Stop、最小 PWM 或覆盖。', apply: '应用更改', restore: '恢复默认', noWrites: '没有待处理的硬件更改。',
            hardwareVisual: '硬件视图', hardwareVisualSub: '关联组件的本地可视化和遥测。', sourceSensor: '主要传感器', roleConfidence: '识别可信度', controller: '控制器', sensorName: '传感器通道', telemetryLink: '温度 → 风扇行为', telemetryOnly: '遥测已启用 · 无软件曲线', capabilities: '检测到的能力', capRpm: '可读取 RPM', capControlRead: '可读取 PWM', capControlWrite: '可写控制', capCurve: '软件曲线', capFanStop: 'Fan Stop', capRestore: '恢复默认', supported: '是', unsupported: '否', unknown: '未知',
            noFans: '未检测到风扇', noFansBody: 'VoltManager 没有发现风扇类型传感器。固件或控制器可能未向操作系统公开。', noSensors: '硬件传感器不可用', noSensorsBody: '硬件提供程序不可用或访问驱动被阻止。', bridgeUnavailable: '浏览器预览中无法使用 Fan Center', bridgeUnavailableBody: '请在 VoltManager 中打开此页面以访问硬件遥测。', loading: '正在分析散热拓扑', loadingBody: '正在使用已有传感器构建拓扑。',
            saveTitle: '保存新配置', saveBody: '保存当前映射和名称，不会应用硬件参数。', profileName: '配置名称', save: '保存', cancel: '取消', renameProfileTitle: '重命名配置', duplicateProfileTitle: '复制配置', renameFanTitle: '重命名风扇', fanName: '风扇名称', deleteTitle: '删除此配置？', deleteBody: '配置文件将被删除，硬件不会改变。', confirmDelete: '删除', compatibilityTitle: '配置兼容性', compatibilityBody: '仅进行 dry-run，不应用任何配置。', matched: '兼容', needsMapping: '需要手动映射', missing: '设备缺失', incompatible: '不兼容', storedOnly: '配置可以保存，但当前能力不足以控制硬件。', profileSaved: '配置已保存。', profileRenamed: '配置已重命名。', profileDuplicated: '配置已复制。', profileDeleted: '配置已删除。', profileImported: '配置已导入并验证。', profileExported: '配置已导出：{file}', fanRenamed: '风扇名称已更新。', operationFailed: '操作失败：{error}', confidenceConfirmed: '已确认', confidenceHigh: '高', confidenceMedium: '中', confidenceLow: '低', confidenceUserAssigned: '用户', roleCpuFan: 'CPU 风扇', roleCpuOptional: 'CPU 可选风扇', roleGpuFan: 'GPU 风扇', roleCaseFan: '机箱 / 系统风扇', rolePump: '水泵 / AIO', roleExternal: '外部控制器', roleUnknown: '未分类风扇', monitoringBackend: '监控', profileActions: '配置操作', current: '当前', coreSensors: '温度传感器', none: '无',
        }
    };

    function lang() {
        const raw = window.I18n && I18n.getLang ? I18n.getLang() : 'it';
        return strings[raw] ? raw : (raw && raw.startsWith('zh') ? 'zh' : 'en');
    }

    function t(key, params) {
        let value = (strings[lang()] && strings[lang()][key]) || strings.en[key] || key;
        Object.entries(params || {}).forEach(([name, replacement]) => {
            value = value.replaceAll('{' + name + '}', String(replacement));
        });
        return value;
    }

    function esc(value) {
        return String(value == null ? '' : value)
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }

    function normalized(value) { return String(value || '').toLowerCase(); }
    function finite(value) { return typeof value === 'number' && Number.isFinite(value); }
    function fmtRpm(value) { return finite(value) ? Math.round(value).toLocaleString(lang() === 'it' ? 'it-IT' : 'en-US') : 'N/D'; }
    function fmtTemp(value) { return finite(value) ? Math.round(value * 10) / 10 + ' °C' : t('tempUnavailable'); }
    function fmtPct(value) { return finite(value) ? Math.round(value) + '%' : t('pwmUnavailable'); }

    function roleKey(role) {
        switch (normalized(role)) {
            case 'cpufan': return 'cpuFan';
            case 'cpuoptional': return 'cpuOptional';
            case 'gpufan': return 'gpuFan';
            case 'casefan': return 'caseFan';
            case 'pump': return 'pump';
            case 'externalcontrollerfan': return 'external';
            default: return 'unknown';
        }
    }

    function roleLabel(role) {
        return t({ cpuFan: 'roleCpuFan', cpuOptional: 'roleCpuOptional', gpuFan: 'roleGpuFan', caseFan: 'roleCaseFan', pump: 'rolePump', external: 'roleExternal', unknown: 'roleUnknown' }[roleKey(role)]);
    }

    function roleIcon(role) {
        return { cpuFan: 'memory', cpuOptional: 'memory', gpuFan: 'developer_board', caseFan: 'mode_fan', pump: 'water_drop', external: 'hub', unknown: 'mode_fan' }[roleKey(role)];
    }

    function confidenceLabel(value) {
        return t({ confirmed: 'confidenceConfirmed', high: 'confidenceHigh', medium: 'confidenceMedium', low: 'confidenceLow', userassigned: 'confidenceUserAssigned' }[normalized(value)] || 'confidenceLow');
    }

    function controlWritable(fan) { return !!(fan && fan.capabilities && fan.capabilities.controlWritable); }

    function profileSelected() {
        return state.profiles.find(profile => profile.id === state.selectedProfileId) || null;
    }

    function selectedFan() {
        const devices = state.topology && state.topology.devices || [];
        return devices.find(fan => fan.id === state.selectedFanId) || devices[0] || null;
    }

    function groupFor(fan) {
        const key = roleKey(fan.role);
        if (key === 'cpuFan' || key === 'cpuOptional' || key === 'pump') return 'cpu';
        if (key === 'gpuFan') return 'gpu';
        if (key === 'caseFan') return 'case';
        return 'other';
    }

    function fanGroups(devices) {
        const groups = { cpu: [], gpu: [], case: [], other: [] };
        (devices || []).forEach(fan => groups[groupFor(fan)].push(fan));
        return groups;
    }

    function renderToolbar() {
        const selected = profileSelected();
        const anyControl = !!(state.topology && state.topology.anyControlAvailable);
        const sensors = state.topology && state.topology.sensorsAvailable;
        const stateName = !sensors ? 'offline' : (anyControl ? 'control' : 'readonly');
        const stateText = !sensors ? t('unavailable') : (anyControl ? t('controlAvailable') : t('readOnly'));
        const options = [`<option value="">${esc(t('noProfile'))}</option>`]
            .concat(state.profiles.map(profile => `<option value="${esc(profile.id)}"${profile.id === state.selectedProfileId ? ' selected' : ''}>${esc(profile.name)} · ${profile.fanCount}</option>`))
            .join('');

        return `<div class="vm-fan-toolbar">
            <div class="vm-fan-toolbar__profile">
                <span class="vm-fan-toolbar__label">${esc(t('profile'))}</span>
                <select class="vm-fan-select" id="vm-fan-profile-select" aria-label="${esc(t('profile'))}">${options}</select>
                <span class="vm-fan-global-state" data-state="${stateName}"><span class="vm-fan-state-dot"></span>${esc(stateText)}</span>
            </div>
            <div class="vm-fan-toolbar__actions" aria-label="${esc(t('profileActions'))}">
                ${toolButton('save-profile', 'save', t('saveSetup'), true)}
                ${toolButton('compatibility', 'rule', t('compatibility'), !!selected)}
                ${toolButton('rename-profile', 'edit', t('rename'), !!selected)}
                ${toolButton('duplicate-profile', 'content_copy', t('duplicate'), !!selected)}
                ${toolButton('delete-profile', 'delete', t('remove'), !!selected, 'vm-fan-button--danger')}
                ${toolButton('import-profile', 'file_open', t('import'), true)}
                ${toolButton('export-profile', 'ios_share', t('export'), !!selected)}
                ${toolButton('refresh', 'refresh', t('refresh'), true, 'vm-fan-button--accent')}
            </div>
        </div>`;
    }

    function toolButton(action, icon, label, enabled, extraClass) {
        return `<button type="button" class="vm-fan-button ${extraClass || ''}" data-fan-action="${action}" ${enabled ? '' : 'disabled'} title="${esc(label)}" aria-label="${esc(label)}">
            <span class="material-symbols-outlined">${icon}</span><span>${esc(label)}</span>
        </button>`;
    }

    function renderNotices() {
        const notices = [];
        const software = state.topology && state.topology.externalSoftware || [];
        software.slice(0, 2).forEach(item => {
            notices.push(`<div class="vm-fan-notice">
                <span class="material-symbols-outlined">warning</span>
                <div><strong>${esc(t('possibleSoftware'))}</strong><p>${esc(t('possibleSoftwareBody', { software: item.softwareName, process: item.processName }))}</p></div>
            </div>`);
        });
        if (!(state.topology && state.topology.anyControlAvailable)) {
            notices.push(`<div class="vm-fan-notice vm-fan-notice--info">
                <span class="material-symbols-outlined">shield_lock</span>
                <div><strong>${esc(t('readOnly'))}</strong><p>${esc(t('monitorOnlyBanner'))}</p></div>
            </div>`);
        }
        return notices.join('');
    }

    function renderFanList(devices) {
        const groups = fanGroups(devices);
        const defs = [ ['cpu', 'groupCpu'], ['gpu', 'groupGpu'], ['case', 'groupCase'], ['other', 'groupOther'] ];
        return defs.map(([key, label]) => {
            if (!groups[key].length) return '';
            return `<div class="vm-fan-group-label">${esc(t(label))}</div>` + groups[key].map(renderFanRow).join('');
        }).join('');
    }

    function renderFanRow(fan) {
        const selected = fan.id === state.selectedFanId;
        return `<button type="button" class="vm-fan-row" data-fan-id="${esc(fan.id)}" aria-selected="${selected ? 'true' : 'false'}">
            <span class="vm-fan-row__icon"><span class="material-symbols-outlined">${roleIcon(fan.role)}</span></span>
            <span><span class="vm-fan-row__name">${esc(fan.displayName || fan.sensorName || roleLabel(fan.role))}</span><span class="vm-fan-row__meta">${esc(roleLabel(fan.role))} · ${esc(fan.headerName || fan.sensorName || t('unknown'))}</span></span>
            <span class="vm-fan-row__rpm">${esc(fmtRpm(fan.telemetry && fan.telemetry.rpm))}<small>${esc(t('rpmUnit'))}</small></span>
        </button>`;
    }

    function renderConfigPanel(fan, devices) {
        const caps = fan.capabilities || {};
        const writable = controlWritable(fan);
        const temps = fan.availableTemperatureSensors || [];
        return `<section class="vm-fan-panel vm-fan-config-panel">
            <div class="vm-fan-panel__header">
                <div><span class="vm-fan-eyebrow">HUD / CONTROL</span><h3>${esc(t('config'))}</h3><p>${esc(t('configSub'))}</p></div>
                <span class="vm-fan-count">${esc(t('fansDetected', { count: devices.length }))}</span>
            </div>
            <div class="vm-fan-panel__body">
                <div class="vm-fan-list">${renderFanList(devices)}</div>
                <div class="vm-fan-selection">
                    <div class="vm-fan-selected-title">
                        <div><h4>${esc(fan.displayName)}</h4><p>${esc(fan.hardwareName)} · ${esc(fan.headerName || fan.sensorName)}</p></div>
                        <div style="display:flex;gap:6px;align-items:center;flex-wrap:wrap;justify-content:flex-end">
                            <span class="vm-fan-badge ${writable ? 'vm-fan-badge--control' : 'vm-fan-badge--readonly'}">${esc(writable ? t('controlAvailable') : t('readOnly'))}</span>
                            <button class="vm-fan-button" type="button" data-fan-action="rename-fan" title="${esc(t('rename'))}" aria-label="${esc(t('rename'))}"><span class="material-symbols-outlined">edit</span></button>
                        </div>
                    </div>
                    <div class="vm-fan-kpis">
                        ${kpi(t('rpm'), fmtRpm(fan.telemetry && fan.telemetry.rpm), t('rpmUnit'))}
                        ${kpi(t('pwm'), fmtPct(fan.telemetry && fan.telemetry.controlPercent), '')}
                        ${kpi(t('temperature'), fmtTemp(fan.telemetry && fan.telemetry.referenceTemperature), '')}
                    </div>

                    <div class="vm-fan-section">
                        <div class="vm-fan-section__top"><h5>${esc(t('mode'))}</h5><span>${esc(caps.backend || t('monitoringBackend'))}</span></div>
                        <div class="vm-fan-modes">
                            ${modeButton('automatic', 'automatic', writable)}
                            ${modeButton('manual', 'manual', writable && caps.fixedControlSupported)}
                            ${modeButton('curve', 'curve', writable && caps.softwareCurveSupported)}
                        </div>
                        <div class="vm-fan-field"><label for="vm-fan-sensor-select">${esc(t('sensor'))}</label>
                            <select id="vm-fan-sensor-select" class="vm-fan-select" ${writable ? '' : 'disabled'}>
                                ${temps.length ? temps.map((sensor, i) => `<option value="${esc(sensor.id)}"${i === 0 ? ' selected' : ''}>${esc(sensor.name)} · ${esc(fmtTemp(sensor.value))}</option>`).join('') : `<option>${esc(t('noSensor'))}</option>`}
                            </select>
                        </div>
                    </div>

                    <div class="vm-fan-section">
                        <div class="vm-fan-section__top"><h5>${esc(t('fanCurve'))}</h5><span>${esc(t('temperature'))} → ${esc(t('pwm'))}</span></div>
                        ${renderCurve(fan)}
                        <div class="vm-fan-safety-strip"><span class="material-symbols-outlined">health_and_safety</span><span>${esc(t('safety'))}</span></div>
                    </div>

                    <div class="vm-fan-applybar">
                        <div class="vm-fan-applybar__status">${esc(t('noWrites'))}</div>
                        <div class="vm-fan-applybar__actions">
                            <button class="vm-fan-button" type="button" disabled><span class="material-symbols-outlined">settings_backup_restore</span>${esc(t('restore'))}</button>
                            <button class="vm-fan-button vm-fan-button--accent" type="button" disabled><span class="material-symbols-outlined">done_all</span>${esc(t('apply'))}</button>
                        </div>
                    </div>
                </div>
            </div>
        </section>`;
    }

    function kpi(label, value, unit) {
        return `<div class="vm-fan-kpi"><span>${esc(label)}</span><strong>${esc(value)}${unit ? ` <small>${esc(unit)}</small>` : ''}</strong></div>`;
    }

    function modeButton(mode, labelKey, enabled) {
        return `<button type="button" class="vm-fan-mode" data-mode="${mode}" data-active="${mode === 'automatic' ? 'true' : 'false'}" ${enabled ? '' : 'disabled'}>${esc(t(labelKey))}</button>`;
    }

    function renderCurve(fan) {
        const temp = fan.telemetry && fan.telemetry.referenceTemperature;
        const x = finite(temp) ? Math.max(26, Math.min(292, 26 + ((temp - 20) / 80) * 266)) : null;
        return `<div class="vm-fan-curve" aria-label="${esc(t('fanCurve'))}">
            <svg viewBox="0 0 320 188" role="img" aria-hidden="true">
                <line class="vm-fan-curve__axis" x1="26" y1="12" x2="26" y2="160"></line>
                <line class="vm-fan-curve__axis" x1="26" y1="160" x2="300" y2="160"></line>
                <text class="vm-fan-curve__label" x="5" y="18">100%</text><text class="vm-fan-curve__label" x="10" y="88">50%</text><text class="vm-fan-curve__label" x="14" y="160">0%</text>
                <text class="vm-fan-curve__label" x="22" y="180">20°</text><text class="vm-fan-curve__label" x="151" y="180">60°</text><text class="vm-fan-curve__label" x="280" y="180">100°</text>
                ${x == null ? '' : `<line class="vm-fan-curve__current" x1="${x.toFixed(1)}" y1="12" x2="${x.toFixed(1)}" y2="160"></line>`}
            </svg>
            <div class="vm-fan-curve__empty"><div><span class="material-symbols-outlined">query_stats</span><strong>${esc(t('curveUnavailable'))}</strong><p>${esc(t('curveUnavailableBody'))}</p></div></div>
        </div>`;
    }

    function renderVisualPanel(fan, devices) {
        const temps = (fan.availableTemperatureSensors || []).slice(0, 6);
        const sameGpu = roleKey(fan.role) === 'gpuFan'
            ? devices.filter(item => roleKey(item.role) === 'gpuFan' && item.hardwareName === fan.hardwareName)
            : [];
        const rotorCount = roleKey(fan.role) === 'gpuFan' ? Math.max(1, Math.min(3, sameGpu.length || 1)) : 1;
        const rpm = fan.telemetry && fan.telemetry.rpm;
        const duration = finite(rpm) && rpm > 0 ? Math.max(.18, Math.min(2.4, 1200 / rpm)) : 1.2;
        const stopped = !(finite(rpm) && rpm > 0);
        const refTemp = fan.telemetry && fan.telemetry.referenceTemperature;
        const modelType = ({ cpuFan: 'cpu', cpuOptional: 'cpu', gpuFan: 'gpu', caseFan: 'case', pump: 'pump', external: 'case', unknown: 'unknown' })[roleKey(fan.role)];
        const sensorPrimary = temps[0];
        return `<section class="vm-fan-panel vm-fan-visual-panel">
            <div class="vm-fan-panel__header">
                <div><span class="vm-fan-eyebrow">LIVE HARDWARE</span><h3>${esc(t('hardwareVisual'))}</h3><p>${esc(t('hardwareVisualSub'))}</p></div>
                <span class="vm-fan-badge vm-fan-badge--accent">${esc(roleLabel(fan.role))}</span>
            </div>
            <div class="vm-fan-panel__body">
                <div class="vm-fan-stage">
                    <div class="vm-fan-model-wrap">
                        <div class="vm-fan-hardware-model vm-fan-hardware-model--${modelType}" style="--fan-spin-duration:${duration.toFixed(2)}s">
                            ${Array.from({ length: rotorCount }, () => rotorHtml(stopped)).join('')}
                        </div>
                        <div class="vm-fan-visual-label">${esc(fan.hardwareName)}<br>${esc(fan.headerName || fan.sensorName)}</div>
                    </div>
                    <div class="vm-fan-visual-data">
                        <span class="vm-fan-eyebrow">${esc(roleLabel(fan.role))}</span>
                        <h4>${esc(fan.displayName)}</h4>
                        <p>${esc(fan.hardwareName)}</p>
                        <div class="vm-fan-live-primary"><strong>${esc(fmtRpm(rpm))}</strong><span>${esc(t('rpmUnit'))}</span></div>
                        <div class="vm-fan-live-caption">${esc(sensorPrimary ? `${sensorPrimary.name} · ${fmtTemp(sensorPrimary.value)}` : t('noSensor'))}</div>
                        <div class="vm-fan-sensor-list">
                            ${infoRow(t('sourceSensor'), sensorPrimary ? sensorPrimary.name : t('none'))}
                            ${infoRow(t('roleConfidence'), confidenceLabel(fan.roleConfidence))}
                            ${infoRow(t('controller'), fan.controllerId || t('unknown'))}
                            ${infoRow(t('sensorName'), fan.sensorName || t('unknown'))}
                        </div>
                    </div>
                </div>
                <div class="vm-fan-linkage" style="--fan-linkage:${finite(refTemp) ? '100%' : '0%'}">
                    <div class="vm-fan-linkage__top"><strong>${esc(t('telemetryLink'))}</strong><span>${esc(t('telemetryOnly'))}</span></div>
                    <div class="vm-fan-linkage__track"></div>
                </div>
                <div style="padding:0 22px 22px">
                    <div class="vm-fan-section__top"><h5>${esc(t('coreSensors'))}</h5><span>${temps.length}</span></div>
                    <div class="vm-fan-sensor-list" style="margin-top:0">
                        ${temps.length ? temps.map(sensor => `<div class="vm-fan-sensor-row"><span>${esc(sensor.name)} · ${esc(sensor.hardware)}</span><strong>${esc(fmtTemp(sensor.value))}</strong></div>`).join('') : `<div class="vm-fan-sensor-row"><span>${esc(t('noSensor'))}</span><strong>—</strong></div>`}
                    </div>
                    ${renderCapabilities(fan.capabilities || {})}
                </div>
            </div>
        </section>`;
    }

    function rotorHtml(stopped) {
        return `<div class="vm-fan-rotor" data-stopped="${stopped ? 'true' : 'false'}"><div class="vm-fan-rotor__blades"></div><div class="vm-fan-rotor__hub"></div></div>`;
    }

    function infoRow(label, value) {
        return `<div class="vm-fan-sensor-row"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`;
    }

    function renderCapabilities(caps) {
        const items = [
            ['capRpm', !!caps.rpmReadable], ['capControlRead', !!caps.controlReadable], ['capControlWrite', !!caps.controlWritable],
            ['capCurve', !!caps.softwareCurveSupported], ['capFanStop', !!caps.fanStopSupported], ['capRestore', !!caps.canRestoreDefault],
        ];
        return `<div class="vm-fan-section"><div class="vm-fan-section__top"><h5>${esc(t('capabilities'))}</h5><span>${esc(caps.backend || t('monitoringBackend'))}</span></div>
            <div class="vm-fan-kpis">${items.map(([key, yes]) => `<div class="vm-fan-kpi"><span>${esc(t(key))}</span><strong>${esc(yes ? t('supported') : t('unsupported'))}</strong></div>`).join('')}</div>
        </div>`;
    }

    function renderEmpty() {
        if (!window.Host || !Host.available) {
            return emptyBlock('web_asset_off', t('bridgeUnavailable'), t('bridgeUnavailableBody'));
        }
        if (state.loading) return `<div class="vm-fan-loading"><div><span class="material-symbols-outlined">progress_activity</span><strong>${esc(t('loading'))}</strong><p>${esc(t('loadingBody'))}</p></div></div>`;
        if (!state.topology || !state.topology.sensorsAvailable) return emptyBlock('sensors_off', t('noSensors'), t('noSensorsBody'));
        return emptyBlock('mode_fan_off', t('noFans'), t('noFansBody'));
    }

    function emptyBlock(icon, title, body) {
        return `<div class="vm-fan-empty"><div><span class="material-symbols-outlined">${icon}</span><strong>${esc(title)}</strong><p>${esc(body)}</p></div></div>`;
    }

    function renderModal() {
        const modal = state.modal;
        if (!modal) return `<div class="vm-fan-modal" id="vm-fan-modal" data-open="false"></div>`;

        if (modal.type === 'compatibility') {
            const report = modal.report;
            const rows = (report.items || []).map(item => {
                const status = normalized(item.status);
                const labelKey = status === 'matched' ? 'matched' : status === 'needsmapping' ? 'needsMapping' : status === 'missing' ? 'missing' : 'incompatible';
                const local = item.matchedFanId ? ((state.topology.devices || []).find(f => f.id === item.matchedFanId) || {}).displayName : null;
                return `<div class="vm-fan-map-row"><div><div class="vm-fan-map-row__name">${esc(item.displayName)}</div><div class="vm-fan-map-row__reason">${esc(item.reason || '')}</div></div><span class="material-symbols-outlined">arrow_forward</span><div><div class="vm-fan-map-row__name">${esc(local || t(labelKey))}</div><div class="vm-fan-map-row__reason">${esc(t(labelKey))}${item.candidateFanIds && item.candidateFanIds.length ? ` · ${item.candidateFanIds.length}` : ''}</div></div></div>`;
            }).join('');
            return modalShell(t('compatibilityTitle'), t('compatibilityBody'), `<div class="vm-fan-notice vm-fan-notice--info" style="margin-bottom:12px"><span class="material-symbols-outlined">info</span><div><strong>${esc(report.canApplyControl ? t('controlAvailable') : t('readOnly'))}</strong><p>${esc(report.canApplyControl ? t('matched') : t('storedOnly'))}</p></div></div>${rows || `<p style="color:var(--vm-muted);font-size:11px">${esc(t('none'))}</p>`}`, `<button class="vm-fan-button vm-fan-button--accent" data-fan-action="modal-close" type="button">OK</button>`);
        }

        if (modal.type === 'delete-profile') {
            return modalShell(t('deleteTitle'), t('deleteBody'), '', `<button class="vm-fan-button" data-fan-action="modal-close" type="button">${esc(t('cancel'))}</button><button class="vm-fan-button vm-fan-button--danger" data-fan-action="modal-submit" type="button">${esc(t('confirmDelete'))}</button>`);
        }

        const definitions = {
            'save-profile': [t('saveTitle'), t('saveBody'), t('profileName'), modal.value || ''],
            'rename-profile': [t('renameProfileTitle'), '', t('profileName'), modal.value || ''],
            'duplicate-profile': [t('duplicateProfileTitle'), '', t('profileName'), modal.value || ''],
            'rename-fan': [t('renameFanTitle'), '', t('fanName'), modal.value || ''],
        };
        const def = definitions[modal.type];
        if (!def) return '';
        const input = `<label class="vm-fan-eyebrow" for="vm-fan-modal-input">${esc(def[2])}</label><input id="vm-fan-modal-input" class="vm-fan-input" maxlength="${modal.type === 'rename-fan' ? '60' : '80'}" value="${esc(def[3])}" autocomplete="off" style="margin-top:8px">`;
        return modalShell(def[0], def[1], input, `<button class="vm-fan-button" data-fan-action="modal-close" type="button">${esc(t('cancel'))}</button><button class="vm-fan-button vm-fan-button--accent" data-fan-action="modal-submit" type="button">${esc(t('save'))}</button>`);
    }

    function modalShell(title, subtitle, body, footer) {
        return `<div class="vm-fan-modal" id="vm-fan-modal" data-open="true" role="dialog" aria-modal="true" aria-label="${esc(title)}">
            <div class="vm-fan-modal__dialog">
                <div class="vm-fan-modal__header"><div><h3>${esc(title)}</h3>${subtitle ? `<p>${esc(subtitle)}</p>` : ''}</div><button class="vm-fan-modal__close" data-fan-action="modal-close" type="button" aria-label="${esc(t('cancel'))}"><span class="material-symbols-outlined">close</span></button></div>
                <div class="vm-fan-modal__body">${body}</div><div class="vm-fan-modal__footer">${footer}</div>
            </div></div>`;
    }

    function renderToast() {
        if (!state.toast) return `<div class="vm-fan-toast" data-open="false"></div>`;
        return `<div class="vm-fan-toast" data-open="true" data-error="${state.toast.error ? 'true' : 'false'}"><span class="material-symbols-outlined">${state.toast.error ? 'error' : 'check_circle'}</span><span>${esc(state.toast.message)}</span></div>`;
    }

    function render() {
        const root = document.getElementById('vm-fan-center');
        if (!root) return;
        const devices = state.topology && state.topology.devices || [];
        if (devices.length && !devices.some(f => f.id === state.selectedFanId)) state.selectedFanId = devices[0].id;
        const fan = selectedFan();
        root.innerHTML = `${renderToolbar()}${renderNotices()}${fan ? `<div class="vm-fan-layout">${renderConfigPanel(fan, devices)}${renderVisualPanel(fan, devices)}</div>` : renderEmpty()}${renderModal()}${renderToast()}`;
        requestAnimationFrame(() => document.getElementById('vm-fan-modal-input')?.focus());
    }

    async function loadAll() {
        if (!window.Host || !Host.available) { render(); return; }
        if (state.loading) return;
        state.loading = true;
        render();
        try {
            const [topology, profiles] = await Promise.all([Host.call('getFanTopology'), Host.call('listFanProfiles')]);
            state.topology = topology;
            state.profiles = profiles || [];
            if (!state.selectedFanId && topology && topology.devices && topology.devices.length) state.selectedFanId = topology.devices[0].id;
            if (state.selectedProfileId && !state.profiles.some(p => p.id === state.selectedProfileId)) state.selectedProfileId = '';
            state.lastRefreshAt = Date.now();
        } catch (error) {
            showError(error);
        } finally {
            state.loading = false;
            render();
        }
    }

    async function refreshTopology(silent) {
        if (!window.Host || !Host.available || state.actionBusy) return;
        try {
            const topology = await Host.call('getFanTopology');
            state.topology = topology;
            if (state.selectedFanId && !(topology.devices || []).some(f => f.id === state.selectedFanId)) state.selectedFanId = topology.devices && topology.devices[0] && topology.devices[0].id;
            state.lastRefreshAt = Date.now();
            render();
        } catch (error) {
            if (!silent) showError(error);
        }
    }

    async function reloadProfiles() {
        state.profiles = await Host.call('listFanProfiles');
        if (state.selectedProfileId && !state.profiles.some(p => p.id === state.selectedProfileId)) state.selectedProfileId = '';
    }

    function openModal(type, value, extra) {
        state.modal = Object.assign({ type, value: value || '' }, extra || {});
        render();
    }

    function closeModal() { state.modal = null; render(); }

    function toast(message, error) {
        state.toast = { message, error: !!error };
        if (state.toastTimer) clearTimeout(state.toastTimer);
        render();
        state.toastTimer = setTimeout(() => { state.toast = null; render(); }, 3600);
    }

    function showError(error) {
        const message = error && error.message ? error.message : String(error || 'Error');
        toast(t('operationFailed', { error: message }), true);
    }

    async function runAction(callback) {
        if (state.actionBusy) return;
        state.actionBusy = true;
        try { await callback(); }
        catch (error) { showError(error); }
        finally { state.actionBusy = false; }
    }

    async function submitModal() {
        const modal = state.modal;
        if (!modal) return;
        if (modal.type === 'delete-profile') {
            await runAction(async () => {
                await Host.call('deleteFanProfile', { profileId: state.selectedProfileId });
                state.selectedProfileId = '';
                state.modal = null;
                await reloadProfiles();
                toast(t('profileDeleted'));
            });
            return;
        }

        const input = document.getElementById('vm-fan-modal-input');
        const value = (input && input.value || '').trim();
        if (!value) { input?.focus(); return; }

        if (modal.type === 'save-profile') {
            await runAction(async () => {
                const result = await Host.call('saveCurrentFanProfile', { name: value });
                state.selectedProfileId = result.id;
                state.modal = null;
                await reloadProfiles();
                toast(t('profileSaved'));
            });
        } else if (modal.type === 'rename-profile') {
            await runAction(async () => {
                await Host.call('renameFanProfile', { profileId: state.selectedProfileId, name: value });
                state.modal = null;
                await reloadProfiles();
                toast(t('profileRenamed'));
            });
        } else if (modal.type === 'duplicate-profile') {
            await runAction(async () => {
                const result = await Host.call('duplicateFanProfile', { profileId: state.selectedProfileId, name: value });
                state.selectedProfileId = result.id;
                state.modal = null;
                await reloadProfiles();
                toast(t('profileDuplicated'));
            });
        } else if (modal.type === 'rename-fan') {
            await runAction(async () => {
                state.topology = await Host.call('renameFan', { fanId: state.selectedFanId, alias: value });
                state.modal = null;
                toast(t('fanRenamed'));
            });
        }
    }

    async function handleAction(action) {
        if (!window.Host || !Host.available) return;
        const profile = profileSelected();
        const fan = selectedFan();
        switch (action) {
            case 'refresh': await refreshTopology(false); break;
            case 'save-profile': openModal('save-profile', ''); break;
            case 'rename-profile': if (profile) openModal('rename-profile', profile.name); break;
            case 'duplicate-profile': if (profile) openModal('duplicate-profile', profile.name + ' copy'); break;
            case 'delete-profile': if (profile) openModal('delete-profile'); break;
            case 'rename-fan': if (fan) openModal('rename-fan', fan.userName || fan.displayName); break;
            case 'modal-close': closeModal(); break;
            case 'modal-submit': await submitModal(); break;
            case 'compatibility':
                if (profile) await runAction(async () => openModal('compatibility', '', { report: await Host.call('analyzeFanProfileCompatibility', { profileId: profile.id }) }));
                break;
            case 'import-profile':
                await runAction(async () => {
                    const result = await Host.call('importFanProfile');
                    if (!result || result.canceled) return;
                    await reloadProfiles();
                    state.selectedProfileId = result.profile.id;
                    toast(t('profileImported'));
                    openModal('compatibility', '', { report: result.compatibility });
                });
                break;
            case 'export-profile':
                if (profile) await runAction(async () => {
                    const result = await Host.call('exportFanProfile', { profileId: profile.id });
                    if (result && !result.canceled) toast(t('profileExported', { file: result.fileName || 'JSON' }));
                });
                break;
        }
    }

    function wireRoot(root) {
        root.addEventListener('click', event => {
            const fanButton = event.target.closest('[data-fan-id]');
            if (fanButton) {
                state.selectedFanId = fanButton.dataset.fanId;
                render();
                return;
            }
            const action = event.target.closest('[data-fan-action]');
            if (action && !action.disabled) handleAction(action.dataset.fanAction);
        });
        root.addEventListener('change', event => {
            if (event.target && event.target.id === 'vm-fan-profile-select') {
                state.selectedProfileId = event.target.value || '';
                render();
            }
        });
    }

    function mount() {
        const root = document.getElementById('vm-fan-center');
        if (!root || state.mounted) return;
        state.mounted = true;
        wireRoot(root);
        render();
    }

    function setActive(active) {
        state.active = !!active;
        if (!state.active) return;
        mount();
        if (!state.topology) loadAll();
        else if (Date.now() - state.lastRefreshAt > 2500) refreshTopology(true);
    }

    document.addEventListener('voltuiready', () => {
        mount();
        const view = document.getElementById('view-cooling');
        setActive(!!view && !view.classList.contains('hidden'));
    });
    document.addEventListener('voltuiviewchanged', event => setActive(event.detail && event.detail.view === 'cooling'));
    document.addEventListener('langchanged', () => { if (state.mounted) render(); });
    document.addEventListener('keydown', event => { if (event.key === 'Escape' && state.modal) closeModal(); });

    if (window.Host && Host.on) {
        Host.on('metrics', () => {
            if (!state.active || state.loading || state.actionBusy) return;
            if (Date.now() - state.lastRefreshAt < 2600) return;
            refreshTopology(true);
        });
    }

    if (document.readyState === 'complete') {
        setTimeout(() => {
            mount();
            const view = document.getElementById('view-cooling');
            if (view && !view.classList.contains('hidden')) setActive(true);
        }, 0);
    }
})();
