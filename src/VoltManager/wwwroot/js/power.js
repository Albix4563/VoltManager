/**
 * Gestione Energetica: automation rules editor, debounced save.
 * Heavy app detection: Windows GPU preferences + generic game/heavy workload heuristics.
 * Keep-awake mode: runtime Windows power request to prevent automatic sleep.
 */
(function () {
    if (!Host.available) return;

    let settings = null;
    let saveTimer = null;
    let appProfileWired = false;
    let appProfileStatus = null;
    let heavyAppWired = false;
    let heavyAppStatus = null;
    let keepAwakeWired = false;
    let keepAwakeState = null;
    let thermalWired = false;
    let thermalState = null;
    let idleWired = false;
    let idleState = null;

    const ruleIds = ['saver', 'balanced', 'performance'];
    const planIds = ['powerSaver', 'balanced', 'performance'];

    const text = {
        it: {
            appProfileTitle: 'Piani energetici per app',
            appProfileSub: 'Scegli un file .exe e VoltManager applichera il piano energetico selezionato mentre quell app e aperta.',
            appProfileToggle: 'Attiva profili per app',
            appProfileToggleSub: 'Le regole funzionano solo quando l automazione background e attiva.',
            appProfileAdd: 'Aggiungi app',
            appProfileEmpty: 'Nessuna app configurata.',
            appProfileStatusIdle: 'In ascolto',
            appProfileStatusDisabled: 'Disattivato',
            appProfileStatusActive: 'Profilo app attivo',
            appProfileDetected: 'Attive',
            appProfileMissing: 'File non trovato',
            appProfileRemove: 'Rimuovi',
            heavyTitle: 'Rilevamento giochi e app pesanti',
            heavySub: 'Quando VoltManager rileva un gioco o un carico pesante applica automaticamente il piano scelto, senza creare liste infinite di applicazioni.',
            heavyToggle: 'Attiva rilevamento automatico',
            heavyToggleSub: 'Usa le Preferenze grafiche di Windows e euristiche locali generiche.',
            heavyTarget: 'Piano da usare',
            heavyTargetSub: 'Predefinito: Prestazioni elevate.',
            heavyWindows: 'Preferenze grafiche Windows',
            heavyWindowsSub: 'Rileva app marcate come “Prestazioni elevate” in Windows.',
            heavyGamePaths: 'Percorsi giochi installati',
            heavyGamePathsSub: 'Rileva Steam, Epic, GOG, Xbox, Riot, Battle.net e simili senza database dei giochi.',
            heavyResources: 'Carichi pesanti generici',
            heavyResourcesSub: 'Rileva processi utente con memoria elevata quando non esiste una preferenza Windows.',
            keepTitle: 'Tieni il PC attivo',
            keepSub: 'Blocca la sospensione automatica senza modificare permanentemente i timeout dei piani energetici.',
            keepToggle: 'Impedisci autosospensione',
            keepToggleSub: 'Utile per download notturni, rendering, training AI e task lunghi.',
            keepBatteryGuard: 'Spegni a batteria',
            keepBatteryGuardSub: 'Disattiva automaticamente se scolleghi l’alimentatore (protegge la batteria).',
            keepMaxDuration: 'Durata massima',
            keepMaxDurationSub: '0 = illimitato. Dopo il limite il blocco sospensione si spegne da solo.',
            keepMaxMinutesUnit: 'min',
            keepStatusActive: 'Attivo: il PC non andrà in sospensione automatica.',
            keepStatusActiveTimed: 'Attivo. Tempo residuo: {time}.',
            keepStatusIdle: 'Disattivo: valgono le normali regole del piano energetico.',
            keepStatusBattery: 'Disattivato automaticamente: PC a batteria.',
            keepStatusTimeout: 'Disattivato automaticamente: durata massima raggiunta.',
            keepBadgeActive: 'No sospensione',
            keepBadgeIdle: 'Sospensione normale',
            keepNote: 'Lo schermo continua a seguire le impostazioni di Windows; viene bloccata solo la sospensione del sistema.',
            statusIdle: 'In ascolto',
            statusDisabled: 'Disattivato',
            statusActive: 'Modalità app pesante attiva',
            detected: 'Rilevate',
            noneDetected: 'Nessuna app pesante rilevata.',
            refresh: 'Aggiorna stato',
            reason_windowsGpuPreference: 'Preferenza GPU Windows',
            reason_gameInstallPath: 'Percorso gioco',
            reason_gameBinaryLayout: 'Layout motore gioco',
            reason_launcherChild: 'Avviato dal launcher',
            reason_foregroundActive: 'In primo piano',
            reason_gpuActive: 'GPU 3D in uso',
            reason_userRule: 'Regola manuale',
            reason_resourceHeuristic: 'Carico risorse',
            level_confirmed: 'Confermato',
            level_probable: 'Probabile',
            kindGame: 'Gioco',
            kindHeavyApp: 'App pesante',
            heavyScore: 'Punteggio',
            heavyAlwaysTitle: 'Tratta sempre come gioco',
            heavyAlwaysSub: 'Attivano il piano senza analisi.',
            heavyNeverTitle: 'Non è mai un gioco',
            heavyNeverSub: 'Esclusi dalla rilevazione. Vince su ogni altra regola.',
            heavyRulesAdd: 'Aggiungi',
            heavyRulesEmpty: 'Nessun percorso.',
            heavyRulesRemove: 'Rimuovi',
            planConflictTitle: 'Piano energetico ripristinato',
            planConflictExternal: 'Cambio piano esterno rilevato',
            planConflictKnown: 'Processo rilevato',
            planConflictProbable: 'Processo probabile',
            planConflictExpected: 'Piano corretto',
            plan_powerSaver: 'Risparmio energia',
            plan_balanced: 'Bilanciato',
            plan_performance: 'Prestazioni elevate',
            thermalSub: 'Se CPU o GPU restano calde oltre la soglia, applica un piano più fresco e lo ripristina quando la temperatura scende.',
            thermalToggle: 'Attiva protezione termica',
            thermalToggleSub: 'Richiede sensori leggibili. Off di default.',
            thermalThreshold: 'Soglia di intervento',
            thermalCool: 'Soglia di ripristino',
            thermalHold: 'Durata minima a caldo',
            thermalHoldUnit: 's',
            thermalTarget: 'Piano da applicare',
            thermalWatchGpu: 'Monitora anche GPU',
            thermalWatchGpuSub: 'Usa la temperatura GPU se disponibile.',
            thermalBadgeActive: 'Raffreddamento attivo',
            thermalBadgeIdle: 'In ascolto',
            thermalBadgeOff: 'Disattivato',
            thermalStatusActive: 'Temperatura elevata: piano fresco in uso.',
            thermalStatusWarming: 'In riscaldamento… ({held}s / {need}s)',
            thermalStatusIdle: 'Temperature nella norma.',
            thermalStatusNoSensors: 'Sensori non disponibili su questo PC.',
            thermalPeak: 'Picco',
            idleSub: 'Se non usi tastiera o mouse per un po’, passa a un piano parco e ripristina al primo input.',
            idleToggle: 'Attiva risparmio a inattività',
            idleToggleSub: 'Utile in batteria. Off di default.',
            idleMinutes: 'Minuti di inattività',
            idleMinutesUnit: 'min',
            idleTarget: 'Piano da applicare',
            idleBatteryOnly: 'Solo a batteria',
            idleBatteryOnlySub: 'Su rete elettrica non cambia il piano per inattività.',
            idleBadgeActive: 'Piano inattività',
            idleBadgeIdle: 'In ascolto',
            idleBadgeOff: 'Disattivato',
            idleStatusActive: 'Utente inattivo: piano parco attivo.',
            idleStatusWaiting: 'Inattività: {idle} / {need} min',
            idleStatusSkip: 'In attesa (solo batteria o AC).',
            idleStatusNoInput: 'Input non leggibile.'
        },
        es: {
            appProfileTitle: 'Planes de energía por aplicación',
            appProfileSub: 'Elige un archivo .exe y VoltManager aplicará el plan de energía seleccionado mientras esa app esté abierta.',
            appProfileToggle: 'Activar perfiles por aplicación',
            appProfileToggleSub: 'Las reglas solo funcionan cuando la automatización en segundo plano está activa.',
            appProfileAdd: 'Añadir aplicación',
            appProfileEmpty: 'Ninguna aplicación configurada.',
            appProfileStatusIdle: 'En escucha',
            appProfileStatusDisabled: 'Desactivado',
            appProfileStatusActive: 'Perfil de aplicación activo',
            appProfileDetected: 'Activas',
            appProfileMissing: 'Archivo no encontrado',
            appProfileRemove: 'Eliminar',
            heavyTitle: 'Detección de juegos y apps pesadas',
            heavySub: 'Cuando VoltManager detecta un juego o carga pesada, aplica automáticamente el plan elegido sin crear listas infinitas de aplicaciones.',
            heavyToggle: 'Activar detección automática',
            heavyToggleSub: 'Usa las Preferencias gráficas de Windows y heurísticas locales genéricas.',
            heavyTarget: 'Plan a usar',
            heavyTargetSub: 'Predeterminado: Alto rendimiento.',
            heavyWindows: 'Preferencias gráficas de Windows',
            heavyWindowsSub: 'Detecta apps marcadas como "Alto rendimiento" en Windows.',
            heavyGamePaths: 'Rutas de juegos instalados',
            heavyGamePathsSub: 'Detecta Steam, Epic, GOG, Xbox, Riot, Battle.net y similares sin base de datos de juegos.',
            heavyResources: 'Cargas pesadas genéricas',
            heavyResourcesSub: 'Detecta procesos de usuario con memoria elevada cuando no existe una preferencia de Windows.',
            keepTitle: 'Mantener el PC activo',
            keepSub: 'Bloquea la suspensión automática sin modificar permanentemente los tiempos de espera de los planes de energía.',
            keepToggle: 'Impedir suspensión automática',
            keepToggleSub: 'Útil para descargas nocturnas, renderizado, entrenamiento de IA y tareas largas.',
            keepBatteryGuard: 'Apagar con batería',
            keepBatteryGuardSub: 'Se desactiva al desconectar el cargador (protege la batería).',
            keepMaxDuration: 'Duración máxima',
            keepMaxDurationSub: '0 = ilimitado. Al llegar al límite se desactiva solo.',
            keepMaxMinutesUnit: 'min',
            keepStatusActive: 'Activo: el PC no entrará en suspensión automática.',
            keepStatusActiveTimed: 'Activo. Tiempo restante: {time}.',
            keepStatusIdle: 'Inactivo: se aplican las reglas normales del plan de energía.',
            keepStatusBattery: 'Desactivado automáticamente: PC con batería.',
            keepStatusTimeout: 'Desactivado automáticamente: duración máxima alcanzada.',
            keepBadgeActive: 'Sin suspensión',
            keepBadgeIdle: 'Suspensión normal',
            keepNote: 'La pantalla sigue las configuraciones de Windows; solo se bloquea la suspensión del sistema.',
            statusIdle: 'En escucha',
            statusDisabled: 'Desactivado',
            statusActive: 'Modo de app pesada activo',
            detected: 'Detectadas',
            noneDetected: 'Ninguna app pesada detectada.',
            refresh: 'Actualizar estado',
            reason_windowsGpuPreference: 'Preferencia GPU de Windows',
            reason_gameInstallPath: 'Ruta de juego',
            reason_gameBinaryLayout: 'Diseño del motor del juego',
            reason_launcherChild: 'Iniciado por el launcher',
            reason_foregroundActive: 'En primer plano',
            reason_gpuActive: 'GPU 3D en uso',
            reason_userRule: 'Regla manual',
            reason_resourceHeuristic: 'Carga de recursos',
            level_confirmed: 'Confirmado',
            level_probable: 'Probable',
            kindGame: 'Juego',
            kindHeavyApp: 'App pesada',
            heavyScore: 'Puntuación',
            heavyAlwaysTitle: 'Tratar siempre como juego',
            heavyAlwaysSub: 'Aplican el plan sin análisis.',
            heavyNeverTitle: 'Nunca es un juego',
            heavyNeverSub: 'Excluidos de la detección. Gana sobre cualquier otra regla.',
            heavyRulesAdd: 'Añadir',
            heavyRulesEmpty: 'Ninguna ruta.',
            heavyRulesRemove: 'Quitar',
            planConflictTitle: 'Plan de energía restaurado',
            planConflictExternal: 'Cambio de plan externo detectado',
            planConflictKnown: 'Proceso detectado',
            planConflictProbable: 'Proceso probable',
            planConflictExpected: 'Plan correcto',
            plan_powerSaver: 'Ahorro de energía',
            plan_balanced: 'Equilibrado',
            plan_performance: 'Alto rendimiento',
            thermalSub: 'Si CPU o GPU se mantienen calientes por encima del umbral, aplica un plan más fresco y lo restaura al enfriar.',
            thermalToggle: 'Activar protección térmica',
            thermalToggleSub: 'Requiere sensores legibles. Desactivado por defecto.',
            thermalThreshold: 'Umbral de activación',
            thermalCool: 'Umbral de restauración',
            thermalHold: 'Tiempo mínimo en caliente',
            thermalHoldUnit: 's',
            thermalTarget: 'Plan a aplicar',
            thermalWatchGpu: 'Vigilar también GPU',
            thermalWatchGpuSub: 'Usa la temperatura de GPU si está disponible.',
            thermalBadgeActive: 'Enfriamiento activo',
            thermalBadgeIdle: 'En escucha',
            thermalBadgeOff: 'Desactivado',
            thermalStatusActive: 'Temperatura alta: plan fresco en uso.',
            thermalStatusWarming: 'Calentando… ({held}s / {need}s)',
            thermalStatusIdle: 'Temperaturas normales.',
            thermalStatusNoSensors: 'Sensores no disponibles en este PC.',
            thermalPeak: 'Pico',
            idleSub: 'Si no usas teclado o ratón un rato, aplica un plan frugal y lo restaura al primer input.',
            idleToggle: 'Activar ahorro en inactividad',
            idleToggleSub: 'Útil con batería. Desactivado por defecto.',
            idleMinutes: 'Minutos de inactividad',
            idleMinutesUnit: 'min',
            idleTarget: 'Plan a aplicar',
            idleBatteryOnly: 'Solo con batería',
            idleBatteryOnlySub: 'En CA no cambia el plan por inactividad.',
            idleBadgeActive: 'Plan inactivo',
            idleBadgeIdle: 'En escucha',
            idleBadgeOff: 'Desactivado',
            idleStatusActive: 'Usuario inactivo: plan frugal activo.',
            idleStatusWaiting: 'Inactividad: {idle} / {need} min',
            idleStatusSkip: 'En espera (solo batería o CA).',
            idleStatusNoInput: 'Entrada no legible.'
        },
        en: {
            appProfileTitle: 'Per-app power plans',
            appProfileSub: 'Choose an .exe file and VoltManager will apply the selected power plan while that app is open.',
            appProfileToggle: 'Enable app profiles',
            appProfileToggleSub: 'Rules run only while background automation is enabled.',
            appProfileAdd: 'Add app',
            appProfileEmpty: 'No app configured.',
            appProfileStatusIdle: 'Listening',
            appProfileStatusDisabled: 'Disabled',
            appProfileStatusActive: 'App profile active',
            appProfileDetected: 'Active',
            appProfileMissing: 'File not found',
            appProfileRemove: 'Remove',
            heavyTitle: 'Game and heavy app detection',
            heavySub: 'When VoltManager detects a game or heavy workload, it applies the selected plan automatically without maintaining a huge app list.',
            heavyToggle: 'Enable automatic detection',
            heavyToggleSub: 'Uses Windows Graphics preferences and local generic heuristics.',
            heavyTarget: 'Power plan to use',
            heavyTargetSub: 'Default: High performance.',
            heavyWindows: 'Windows Graphics preferences',
            heavyWindowsSub: 'Detects apps marked as “High performance” in Windows.',
            heavyGamePaths: 'Installed game locations',
            heavyGamePathsSub: 'Detects Steam, Epic, GOG, Xbox, Riot, Battle.net, and similar paths without a game database.',
            heavyResources: 'Generic heavy workloads',
            heavyResourcesSub: 'Detects user processes with high memory usage when no Windows preference exists.',
            keepTitle: 'Keep PC awake',
            keepSub: 'Prevents automatic system sleep without permanently changing power-plan timeout values.',
            keepToggle: 'Prevent automatic sleep',
            keepToggleSub: 'Useful for overnight downloads, rendering, AI training, and long-running jobs.',
            keepBatteryGuard: 'Turn off on battery',
            keepBatteryGuardSub: 'Automatically disables when you unplug AC power (protects the battery).',
            keepMaxDuration: 'Maximum duration',
            keepMaxDurationSub: '0 = unlimited. After the limit, keep-awake turns itself off.',
            keepMaxMinutesUnit: 'min',
            keepStatusActive: 'Active: the PC will not automatically go to sleep.',
            keepStatusActiveTimed: 'Active. Time remaining: {time}.',
            keepStatusIdle: 'Off: the current power plan controls sleep normally.',
            keepStatusBattery: 'Auto-disabled: PC is on battery.',
            keepStatusTimeout: 'Auto-disabled: maximum duration reached.',
            keepBadgeActive: 'Sleep blocked',
            keepBadgeIdle: 'Normal sleep',
            keepNote: 'The display still follows Windows settings; only system sleep is blocked.',
            statusIdle: 'Listening',
            statusDisabled: 'Disabled',
            statusActive: 'Heavy app mode active',
            detected: 'Detected',
            noneDetected: 'No heavy app detected.',
            refresh: 'Refresh status',
            reason_windowsGpuPreference: 'Windows GPU preference',
            reason_gameInstallPath: 'Game path',
            reason_gameBinaryLayout: 'Game engine layout',
            reason_launcherChild: 'Started by launcher',
            reason_foregroundActive: 'In the foreground',
            reason_gpuActive: '3D GPU in use',
            reason_userRule: 'Manual rule',
            reason_resourceHeuristic: 'Resource load',
            level_confirmed: 'Confirmed',
            level_probable: 'Probable',
            kindGame: 'Game',
            kindHeavyApp: 'Heavy app',
            heavyScore: 'Score',
            heavyAlwaysTitle: 'Always treat as a game',
            heavyAlwaysSub: 'Switch the plan without any analysis.',
            heavyNeverTitle: 'Never a game',
            heavyNeverSub: 'Excluded from detection. Wins over every other rule.',
            heavyRulesAdd: 'Add',
            heavyRulesEmpty: 'No paths yet.',
            heavyRulesRemove: 'Remove',
            planConflictTitle: 'Power plan restored',
            planConflictExternal: 'External power-plan change detected',
            planConflictKnown: 'Detected process',
            planConflictProbable: 'Likely process',
            planConflictExpected: 'Correct plan',
            plan_powerSaver: 'Power saver',
            plan_balanced: 'Balanced',
            plan_performance: 'High performance',
            thermalSub: 'When CPU or GPU stay hot above the threshold, apply a cooler power plan and restore it once temperatures drop.',
            thermalToggle: 'Enable thermal guard',
            thermalToggleSub: 'Needs readable sensors. Off by default.',
            thermalThreshold: 'Trip threshold',
            thermalCool: 'Restore threshold',
            thermalHold: 'Minimum hot duration',
            thermalHoldUnit: 's',
            thermalTarget: 'Plan to apply',
            thermalWatchGpu: 'Also watch GPU',
            thermalWatchGpuSub: 'Use GPU temperature when available.',
            thermalBadgeActive: 'Cooling active',
            thermalBadgeIdle: 'Listening',
            thermalBadgeOff: 'Disabled',
            thermalStatusActive: 'High temperature: cooler plan in use.',
            thermalStatusWarming: 'Warming… ({held}s / {need}s)',
            thermalStatusIdle: 'Temperatures normal.',
            thermalStatusNoSensors: 'Sensors not available on this PC.',
            thermalPeak: 'Peak',
            idleSub: 'When keyboard and mouse stay idle, switch to a frugal plan and restore on the next input.',
            idleToggle: 'Enable idle power guard',
            idleToggleSub: 'Most useful on battery. Off by default.',
            idleMinutes: 'Idle minutes',
            idleMinutesUnit: 'min',
            idleTarget: 'Plan to apply',
            idleBatteryOnly: 'Battery only',
            idleBatteryOnlySub: 'On AC power, idle does not change the plan.',
            idleBadgeActive: 'Idle plan on',
            idleBadgeIdle: 'Listening',
            idleBadgeOff: 'Disabled',
            idleStatusActive: 'User idle: frugal plan active.',
            idleStatusWaiting: 'Idle: {idle} / {need} min',
            idleStatusSkip: 'Waiting (battery-only or on AC).',
            idleStatusNoInput: 'Input not readable.'
        },
        zh: {
            appProfileTitle: '按应用电源计划',
            appProfileSub: '选择一个 .exe 文件，VoltManager 会在该应用打开时应用所选电源计划。',
            appProfileToggle: '启用应用配置',
            appProfileToggleSub: '规则仅在后台自动化启用时运行。',
            appProfileAdd: '添加应用',
            appProfileEmpty: '未配置应用。',
            appProfileStatusIdle: '监听中',
            appProfileStatusDisabled: '已禁用',
            appProfileStatusActive: '应用配置已激活',
            appProfileDetected: '活动中',
            appProfileMissing: '文件未找到',
            appProfileRemove: '移除',
            heavyTitle: '游戏和重负载应用检测',
            heavySub: '当 VoltManager 检测到游戏或重负载时，会自动应用所选计划，无需维护庞大的应用列表。',
            heavyToggle: '启用自动检测',
            heavyToggleSub: '使用 Windows 图形偏好和本地通用启发式规则。',
            heavyTarget: '要使用的电源计划',
            heavyTargetSub: '默认：高性能。',
            heavyWindows: 'Windows 图形偏好',
            heavyWindowsSub: '检测 Windows 中标记为“高性能”的应用。',
            heavyGamePaths: '已安装游戏位置',
            heavyGamePathsSub: '无需游戏数据库即可检测 Steam、Epic、GOG、Xbox、Riot、Battle.net 及类似路径。',
            heavyResources: '通用重负载',
            heavyResourcesSub: '在没有 Windows 偏好时，检测内存占用较高的用户进程。',
            keepTitle: '保持电脑唤醒',
            keepSub: '防止系统自动睡眠，而不永久更改电源计划超时值。',
            keepToggle: '阻止自动睡眠',
            keepToggleSub: '适用于夜间下载、渲染、AI 训练和长时间任务。',
            keepBatteryGuard: '使用电池时关闭',
            keepBatteryGuardSub: '拔掉电源后自动关闭（保护电池）。',
            keepMaxDuration: '最长持续时间',
            keepMaxDurationSub: '0 = 不限。到达限制后会自动关闭。',
            keepMaxMinutesUnit: '分钟',
            keepStatusActive: '已启用：电脑不会自动进入睡眠。',
            keepStatusActiveTimed: '已启用。剩余时间：{time}。',
            keepStatusIdle: '关闭：当前电源计划正常控制睡眠。',
            keepStatusBattery: '已自动关闭：正在使用电池。',
            keepStatusTimeout: '已自动关闭：已达最长时间。',
            keepBadgeActive: '睡眠已阻止',
            keepBadgeIdle: '正常睡眠',
            keepNote: '显示器仍遵循 Windows 设置；仅阻止系统睡眠。',
            statusIdle: '监听中',
            statusDisabled: '已禁用',
            statusActive: '重负载应用模式已激活',
            detected: '已检测到',
            noneDetected: '未检测到重负载应用。',
            refresh: '刷新状态',
            reason_windowsGpuPreference: 'Windows GPU 偏好',
            reason_gameInstallPath: '游戏路径',
            reason_gameBinaryLayout: '游戏引擎布局',
            reason_launcherChild: '由启动器启动',
            reason_foregroundActive: '前台运行',
            reason_gpuActive: '正在使用 3D GPU',
            reason_userRule: '手动规则',
            reason_resourceHeuristic: '资源负载',
            level_confirmed: '已确认',
            level_probable: '可能',
            kindGame: '游戏',
            kindHeavyApp: '高负载应用',
            heavyScore: '评分',
            heavyAlwaysTitle: '始终视为游戏',
            heavyAlwaysSub: '无需分析即可切换电源计划。',
            heavyNeverTitle: '从不视为游戏',
            heavyNeverSub: '排除在检测之外，优先于其他所有规则。',
            heavyRulesAdd: '添加',
            heavyRulesEmpty: '暂无路径。',
            heavyRulesRemove: '移除',
            planConflictTitle: '电源计划已恢复',
            planConflictExternal: '检测到外部电源计划更改',
            planConflictKnown: '检测到的进程',
            planConflictProbable: '可能的进程',
            planConflictExpected: '正确计划',
            plan_powerSaver: '节能',
            plan_balanced: '平衡',
            plan_performance: '高性能',
            thermalSub: '当 CPU 或 GPU 持续超过温度阈值时，应用更凉爽的电源计划，降温后恢复。',
            thermalToggle: '启用温度保护',
            thermalToggleSub: '需要可读传感器。默认关闭。',
            thermalThreshold: '触发阈值',
            thermalCool: '恢复阈值',
            thermalHold: '最短过热持续时间',
            thermalHoldUnit: '秒',
            thermalTarget: '要应用的计划',
            thermalWatchGpu: '同时监控 GPU',
            thermalWatchGpuSub: '可用时使用 GPU 温度。',
            thermalBadgeActive: '冷却中',
            thermalBadgeIdle: '监听中',
            thermalBadgeOff: '已禁用',
            thermalStatusActive: '温度过高：正在使用节能计划。',
            thermalStatusWarming: '升温中…（{held}s / {need}s）',
            thermalStatusIdle: '温度正常。',
            thermalStatusNoSensors: '此电脑上无可用传感器。',
            thermalPeak: '峰值',
            idleSub: '键盘鼠标空闲一段时间后切换到更省电的计划，有输入时恢复。',
            idleToggle: '启用空闲节能',
            idleToggleSub: '使用电池时最有用。默认关闭。',
            idleMinutes: '空闲分钟数',
            idleMinutesUnit: '分钟',
            idleTarget: '要应用的计划',
            idleBatteryOnly: '仅电池',
            idleBatteryOnlySub: '接电源时不因空闲改计划。',
            idleBadgeActive: '空闲计划中',
            idleBadgeIdle: '监听中',
            idleBadgeOff: '已禁用',
            idleStatusActive: '用户空闲：已启用省电计划。',
            idleStatusWaiting: '空闲：{idle} / {need} 分钟',
            idleStatusSkip: '等待中（仅电池或已接电源）。',
            idleStatusNoInput: '无法读取输入。'
        }
    };

    function lang() {
        return window.I18n && I18n.getLang ? I18n.getLang() : 'it';
    }

    function tt(key) {
        const l = lang();
        return (text[l] && text[l][key]) || (text.en && text.en[key]) || key;
    }

    function esc(value) {
        const div = document.createElement('div');
        div.textContent = value == null ? '' : String(value);
        return div.innerHTML;
    }

    function ruleById(id) {
        return settings.rules.find(r => r.id === id);
    }

    function setToggle(el, on) {
        if (el) el.dataset.on = on ? 'true' : 'false';
    }

    function normalizeHeavyAppDetection() {
        if (!settings.heavyAppDetection) {
            settings.heavyAppDetection = {
                enabled: true,
                targetPlan: 'performance',
                useWindowsGpuPreferences: true,
                useGameInstallHeuristics: true,
                useResourceHeuristics: true,
                minWorkingSetMb: 1536
            };
        }

        const cfg = settings.heavyAppDetection;
        if (!planIds.includes(cfg.targetPlan)) cfg.targetPlan = 'performance';
        if (!Number.isFinite(Number(cfg.minWorkingSetMb))) cfg.minWorkingSetMb = 1536;
        cfg.minWorkingSetMb = Math.max(256, Math.min(8192, Number(cfg.minWorkingSetMb)));
        if (!cfg.useWindowsGpuPreferences && !cfg.useGameInstallHeuristics && !cfg.useResourceHeuristics) {
            cfg.useWindowsGpuPreferences = true;
        }
        cfg.alwaysGamePaths = normalizeUserPathList(cfg.alwaysGamePaths);
        cfg.neverGamePaths = normalizeUserPathList(cfg.neverGamePaths);
        return cfg;
    }

    // Same rules as SettingsService.NormalizeUserPathList: no blanks, case-insensitive
    // dedupe, hard cap so a runaway list cannot slow every classification down.
    function normalizeUserPathList(list) {
        if (!Array.isArray(list)) return [];
        const seen = new Set();
        return list.reduce((out, entry) => {
            const path = String(entry == null ? '' : entry).trim().replace(/^"+|"+$/g, '');
            const key = path.toLowerCase();
            if (path && !seen.has(key) && out.length < 200) {
                seen.add(key);
                out.push(path);
            }
            return out;
        }, []);
    }

    function normalizeAppPowerProfiles() {
        if (!settings.appPowerProfiles) {
            settings.appPowerProfiles = {
                enabled: true,
                rules: []
            };
        }

        const cfg = settings.appPowerProfiles;
        cfg.enabled = cfg.enabled !== false;
        if (!Array.isArray(cfg.rules)) cfg.rules = [];
        const seen = new Set();
        cfg.rules = cfg.rules.filter(rule => {
            if (!rule || !rule.path) return false;
            rule.path = String(rule.path).trim().replace(/^"+|"+$/g, '');
            const key = rule.path.toLowerCase();
            if (!key || seen.has(key)) return false;
            seen.add(key);
            if (!rule.id) rule.id = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now() + Math.random());
            if (!rule.name) rule.name = appNameFromPath(rule.path);
            if (!planIds.includes(rule.targetPlan)) rule.targetPlan = 'performance';
            rule.enabled = rule.enabled !== false;
            return true;
        });
        return cfg;
    }

    function samePath(a, b) {
        return String(a || '').toLowerCase() === String(b || '').toLowerCase();
    }

    function appNameFromPath(path) {
        const file = String(path || '').split(/[\\/]/).pop() || 'App';
        return file.replace(/\.[^.]+$/, '') || 'App';
    }

    function planPriority(plan) {
        return { performance: 3, balanced: 2, powerSaver: 1 }[plan] || 0;
    }

    function normalizeKeepAwake() {
        if (!settings.keepAwake) settings.keepAwake = { enabled: false, lastChangedUtc: null };
        settings.keepAwake.enabled = !!settings.keepAwake.enabled;
        if (typeof settings.keepAwake.autoDisableOnBattery !== 'boolean')
            settings.keepAwake.autoDisableOnBattery = true;
        let maxM = Number(settings.keepAwake.maxMinutes);
        if (!Number.isFinite(maxM) || maxM < 0) maxM = 0;
        if (maxM > 24 * 60) maxM = 24 * 60;
        settings.keepAwake.maxMinutes = Math.round(maxM);
        return settings.keepAwake;
    }

    function normalizeThermalGuard() {
        if (!settings.thermalGuard) {
            settings.thermalGuard = {
                enabled: false,
                thresholdCelsius: 90,
                coolThresholdCelsius: 82,
                holdSeconds: 20,
                targetPlan: 'powerSaver',
                watchGpu: true,
            };
        }
        const t = settings.thermalGuard;
        t.enabled = !!t.enabled;
        t.watchGpu = t.watchGpu !== false;
        let thr = Number(t.thresholdCelsius);
        if (!Number.isFinite(thr)) thr = 90;
        t.thresholdCelsius = Math.max(60, Math.min(105, thr));
        let cool = Number(t.coolThresholdCelsius);
        if (!Number.isFinite(cool)) cool = t.thresholdCelsius - 8;
        t.coolThresholdCelsius = Math.max(45, Math.min(t.thresholdCelsius - 1, cool));
        let hold = Number(t.holdSeconds);
        if (!Number.isFinite(hold)) hold = 20;
        t.holdSeconds = Math.max(5, Math.min(300, Math.round(hold)));
        if (!planIds.includes(t.targetPlan)) t.targetPlan = 'powerSaver';
        return t;
    }

    async function pushThermalSettings() {
        const cfg = normalizeThermalGuard();
        if (!Host.available) {
            scheduleSave();
            return;
        }
        try {
            thermalState = await Host.call('setThermalGuardSettings', cfg);
            renderThermalState(thermalState);
            // Keep settings.json in sync for backup/export without re-applying host state.
            await saveSettingsNow().catch(() => {});
        } catch (err) {
            console.error('setThermalGuardSettings failed', err);
            scheduleSave();
        }
    }

    function normalizeIdlePowerGuard() {
        if (!settings.idlePowerGuard) {
            settings.idlePowerGuard = {
                enabled: false,
                idleMinutes: 10,
                targetPlan: 'powerSaver',
                onlyOnBattery: true,
            };
        }
        const t = settings.idlePowerGuard;
        t.enabled = !!t.enabled;
        t.onlyOnBattery = t.onlyOnBattery !== false;
        let m = Number(t.idleMinutes);
        if (!Number.isFinite(m)) m = 10;
        t.idleMinutes = Math.max(1, Math.min(120, Math.round(m)));
        if (!planIds.includes(t.targetPlan)) t.targetPlan = 'powerSaver';
        return t;
    }

    async function pushIdleSettings() {
        const cfg = normalizeIdlePowerGuard();
        if (!Host.available) {
            scheduleSave();
            return;
        }
        try {
            idleState = await Host.call('setIdlePowerGuardSettings', cfg);
            renderIdleState(idleState);
            await saveSettingsNow().catch(() => {});
        } catch (err) {
            console.error('setIdlePowerGuardSettings failed', err);
            scheduleSave();
        }
    }

    function formatKeepRemaining(seconds) {
        const s = Math.max(0, Math.floor(Number(seconds) || 0));
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        if (h > 0) return h + 'h ' + m + 'm';
        if (m > 0) return m + 'm';
        return s + 's';
    }

    async function pushKeepAwakeSafety() {
        const cfg = normalizeKeepAwake();
        if (!Host.available) {
            await save();
            return;
        }
        try {
            const state = await Host.call('setKeepAwakeSafety', {
                autoDisableOnBattery: !!cfg.autoDisableOnBattery,
                maxMinutes: cfg.maxMinutes | 0,
            });
            keepAwakeState = state;
            renderKeepAwakeState(state);
            await save();
        } catch (err) {
            console.error('setKeepAwakeSafety failed', err);
            await save();
        }
    }

    function normalizeCpuAutomation() {
        if (!settings.cpuAutomation) settings.cpuAutomation = { sampleIntervalSeconds: 1 };
        const n = Number(settings.cpuAutomation.sampleIntervalSeconds);
        settings.cpuAutomation.sampleIntervalSeconds = Number.isFinite(n)
            ? Math.max(1, Math.min(60, Math.round(n)))
            : 1;
        return settings.cpuAutomation;
    }

    function ensurePowerStyles() {
        if (document.getElementById('power-feature-styles')) return;

        const style = document.createElement('style');
        style.id = 'power-feature-styles';
        style.textContent = `
@keyframes heavyAppGlow{0%{box-shadow:0 0 0 0 rgb(var(--vm-accent-rgb) / .26)}70%{box-shadow:0 0 0 13px rgb(var(--vm-accent-rgb) / 0)}100%{box-shadow:0 0 0 0 rgb(var(--vm-accent-rgb) / 0)}}
.app-profile-panel,.heavy-app-panel,.keep-awake-panel{position:relative;overflow:hidden;border:1px solid rgb(var(--vm-accent-rgb) / .13);background:linear-gradient(135deg,rgba(18,33,49,.82),rgba(10,17,40,.68));}
.app-profile-panel:before,.heavy-app-panel:before,.keep-awake-panel:before{content:"";position:absolute;inset:-40% auto auto -12%;width:320px;height:320px;border-radius:999px;background:radial-gradient(circle,rgb(var(--vm-accent-rgb) / .14),transparent 66%);pointer-events:none;}
.heavy-app-grid{display:grid;grid-template-columns:minmax(0,1.15fr) minmax(260px,.85fr);gap:18px;position:relative;z-index:1;}
.heavy-app-option{border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.035);border-radius:16px;padding:14px;display:flex;align-items:center;justify-content:space-between;gap:14px;transition:border-color .22s ease,background .22s ease,transform .22s ease;}
.heavy-app-option:hover{border-color:rgb(var(--vm-accent-rgb) / .24);background:rgba(255,255,255,.055);transform:translateY(-1px);}
.heavy-app-badge,.keep-awake-badge{display:inline-flex;align-items:center;gap:7px;padding:5px 10px;border-radius:999px;border:1px solid rgba(255,255,255,.1);background:rgba(255,255,255,.05);color:rgba(211,222,239,.74);font-size:12px;line-height:1;}
.heavy-app-badge[data-active="true"],.keep-awake-badge[data-active="true"]{border-color:rgb(var(--vm-accent-rgb) / .32);background:rgb(var(--vm-accent-rgb) / .1);color:var(--vm-accent);animation:heavyAppGlow .9s ease-out;}
.heavy-app-list{display:grid;gap:8px;max-height:268px;overflow:auto;padding-right:2px;}
.heavy-app-row{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:12px;padding:10px 12px;}
.heavy-app-path{display:block;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:rgba(211,222,239,.58);font-size:11px;margin-top:3px;}
.heavy-app-meta{display:block;color:rgba(211,222,239,.74);font-size:11px;margin-top:4px;}
.heavy-app-chip{display:inline-flex;align-items:center;padding:3px 9px;border-radius:999px;border:1px solid rgba(255,255,255,.1);background:rgba(255,255,255,.05);color:rgba(211,222,239,.72);font-size:11px;line-height:1;white-space:nowrap;flex-shrink:0;}
.heavy-app-chip[data-level="confirmed"]{border-color:rgb(var(--vm-accent-rgb) / .32);background:rgb(var(--vm-accent-rgb) / .1);color:var(--vm-accent);}
.heavy-rules-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px;margin-top:18px;position:relative;z-index:1;}
.heavy-rules-card{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:16px;padding:14px;}
.heavy-rules-list{display:grid;gap:6px;max-height:148px;overflow:auto;margin-top:12px;padding-right:2px;}
.heavy-rule-row{display:flex;align-items:center;gap:10px;border:1px solid rgba(255,255,255,.07);background:rgba(255,255,255,.03);border-radius:10px;padding:6px 6px 6px 10px;}
.heavy-rule-row .app-profile-icon-btn{width:30px;height:30px;border-radius:8px;flex-shrink:0;}
.app-profile-list{display:grid;gap:10px;position:relative;z-index:1;}
.app-profile-row{display:grid;grid-template-columns:minmax(0,1fr) 170px 42px 42px;gap:10px;align-items:center;border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:14px;padding:12px;}
.app-profile-path{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:rgba(211,222,239,.58);font-size:11px;margin-top:3px;}
.app-profile-icon-btn{width:38px;height:38px;border-radius:10px;display:flex;align-items:center;justify-content:center;border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.04);color:rgba(211,222,239,.72);transition:border-color .2s ease,color .2s ease,background .2s ease;}
.app-profile-icon-btn:hover{border-color:rgb(var(--vm-accent-rgb) / .26);color:var(--vm-accent);background:rgb(var(--vm-accent-rgb) / .08);}
.app-profile-missing{color:#ffb4ab;}
.keep-awake-grid{display:grid;grid-template-columns:minmax(0,1fr) minmax(240px,.38fr);gap:18px;position:relative;z-index:1;align-items:stretch;}
.keep-awake-status{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.035);border-radius:16px;padding:16px;display:flex;flex-direction:column;justify-content:space-between;gap:12px;}
.vm-acc-item{overflow:hidden;}
.vm-acc-header{display:flex;align-items:center;gap:12px;width:100%;padding:18px 24px;background:transparent;border:0;cursor:pointer;text-align:left;color:inherit;font:inherit;transition:background .2s ease;}
.vm-acc-header:hover{background:rgba(255,255,255,.04);}
.vm-acc-title{flex:1;min-width:0;}
.vm-acc-chevron{margin-left:auto;color:rgba(198,198,206,.8);transition:transform .3s cubic-bezier(.4,0,.2,1);flex-shrink:0;}
.vm-acc-item[data-open="true"] .vm-acc-chevron{transform:rotate(180deg);color:var(--vm-accent);}
.vm-acc-body{display:grid;grid-template-rows:0fr;transition:grid-template-rows .32s cubic-bezier(.4,0,.2,1);}
.vm-acc-item[data-open="true"] .vm-acc-body{grid-template-rows:1fr;}
.vm-acc-body-inner{overflow:hidden;min-height:0;padding:0 24px;transition:padding .32s cubic-bezier(.4,0,.2,1);}
.vm-acc-item[data-open="true"] .vm-acc-body-inner{padding:0 24px 24px;}
.heavy-app-panel-inner,.keep-awake-panel-inner{position:relative;}
@media (max-width:960px){.heavy-app-grid,.keep-awake-grid,.heavy-rules-grid{grid-template-columns:1fr}.app-profile-row{grid-template-columns:1fr 1fr 38px 38px}}
        `.trim();
        document.head.appendChild(style);
    }

    function optionHtml(id, titleKey, subKey, icon, on) {
        return '<div class="heavy-app-option" id="pref-' + id + '">' +
            '<div class="flex items-center gap-md">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center">' +
            '<span class="material-symbols-outlined text-secondary-container">' + icon + '</span>' +
            '</div><div><p class="text-body-md text-on-surface" id="' + id + '-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="' + id + '-sub"></p></div></div>' +
            '<div class="mini-toggle cursor-pointer" data-on="' + (on ? 'true' : 'false') + '" id="toggle-' + id + '">' +
            '<div class="mini-toggle-knob"></div></div></div>';
    }

    // Hide battery-only power prefs on desktops (same pattern as dashboard/advanced).
    function checkBatteryPresence() {
        const info = window.VoltSystemInfo;
        if (info) {
            applyBatteryPresence(info.hasBattery);
        } else {
            document.addEventListener('systeminfoloaded', (e) => {
                applyBatteryPresence(e.detail.hasBattery);
            });
        }
    }

    function applyBatteryPresence(hasBattery) {
        const keepAwakeBattery = document.getElementById('pref-keep-awake-battery');
        if (keepAwakeBattery) keepAwakeBattery.classList.toggle('hidden', hasBattery === false);
        const idleBattery = document.getElementById('pref-idle-battery');
        if (idleBattery) idleBattery.classList.toggle('hidden', hasBattery === false);
    }

    function heavyRulesCardHtml(list, icon) {
        return '<div class="heavy-rules-card">' +
            '<div class="flex items-start justify-between gap-sm">' +
            '<div class="flex items-center gap-sm min-w-0">' +
            '<span class="material-symbols-outlined text-secondary-container text-[20px]">' + icon + '</span>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="heavy-rules-' + list + '-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="heavy-rules-' + list + '-sub"></p></div></div>' +
            '<button class="btn-ghost rounded-lg py-1 px-3 text-label-md flex items-center gap-xs whitespace-nowrap heavy-rules-add" data-list="' + list + '" type="button">' +
            '<span class="material-symbols-outlined text-[18px]">add</span><span id="heavy-rules-' + list + '-add"></span></button></div>' +
            '<div class="heavy-rules-list" id="heavy-rules-' + list + '-list"></div></div>';
    }

    function mountAppPowerProfileUi() {
        if (document.getElementById('app-power-profile-panel')) return;
        ensurePowerStyles();

        const mount = document.getElementById('app-power-profile-mount');
        if (!mount) return;

        mount.innerHTML =
            '<div class="app-profile-panel app-profile-panel-inner rounded-xl p-lg" id="app-power-profile-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<div><p class="text-body-md text-on-surface-variant max-w-2xl" id="app-profile-sub"></p>' +
            '<div class="mt-sm flex items-center gap-sm"><span class="heavy-app-badge" id="app-profile-state-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">radio_button_checked</span>' +
            '<span id="app-profile-state-label"></span></span>' +
            '<span class="text-label-md text-on-surface-variant"><span id="app-profile-count">0</span> <span id="app-profile-detected-label"></span></span></div></div>' +
            '<button class="btn-primary rounded-lg py-2 px-4 text-label-md flex items-center gap-xs whitespace-nowrap" id="btn-app-profile-add" type="button">' +
            '<span class="material-symbols-outlined text-[18px]">add</span><span id="app-profile-add-label"></span></button></div>' +
            '<div class="space-y-sm mb-md relative z-10">' +
            optionHtml('app-profile-main', 'appProfileToggle', 'appProfileToggleSub', 'app_shortcut', true) +
            '</div>' +
            '<div class="app-profile-list" id="app-profile-list"></div></div>';
        refreshPowerLabels();
    }

    function mountKeepAwakeUi() {
        if (document.getElementById('keep-awake-panel')) return;
        ensurePowerStyles();

        const mount = document.getElementById('keep-awake-mount');
        if (!mount) return;

        mount.innerHTML =
            '<div class="keep-awake-panel-inner" id="keep-awake-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="keep-awake-sub"></p>' +
            '<span class="keep-awake-badge" id="keep-awake-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">power_settings_new</span>' +
            '<span id="keep-awake-badge-label"></span></span></div>' +
            '<div class="keep-awake-grid"><div class="space-y-sm">' +
            optionHtml('keep-awake-toggle', 'keepToggle', 'keepToggleSub', 'lock_clock', false) +
            optionHtml('keep-awake-battery', 'keepBatteryGuard', 'keepBatteryGuardSub', 'battery_alert', true) +
            '<div class="heavy-app-option" id="pref-keep-awake-max">' +
            '<div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">timer</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="keep-awake-max-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="keep-awake-max-sub"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="0" max="1440" step="15" id="keep-awake-max-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant" id="keep-awake-max-unit"></span></div></div>' +
            '</div><aside class="keep-awake-status">' +
            '<p class="text-body-md text-on-surface" id="keep-awake-status"></p>' +
            '<p class="text-label-sm text-on-surface-variant opacity-80" id="keep-awake-note"></p>' +
            '</aside></div></div>';
        refreshPowerLabels();
        checkBatteryPresence();
    }

    function mountThermalGuardUi() {
        if (document.getElementById('thermal-guard-panel')) return;
        ensurePowerStyles();
        const mount = document.getElementById('thermal-guard-mount');
        if (!mount) return;

        mount.innerHTML =
            '<div class="keep-awake-panel-inner" id="thermal-guard-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="thermal-sub"></p>' +
            '<span class="keep-awake-badge" id="thermal-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">device_thermostat</span>' +
            '<span id="thermal-badge-label"></span></span></div>' +
            '<div class="keep-awake-grid"><div class="space-y-sm">' +
            optionHtml('thermal-main', 'thermalToggle', 'thermalToggleSub', 'device_thermostat', false) +
            optionHtml('thermal-gpu', 'thermalWatchGpu', 'thermalWatchGpuSub', 'memory', true) +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">thermostat</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-thr-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="thermal-peak-line"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="60" max="105" step="1" id="thermal-threshold-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant">°C</span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">ac_unit</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-cool-title"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="45" max="104" step="1" id="thermal-cool-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant">°C</span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">timer</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-hold-title"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="5" max="300" step="5" id="thermal-hold-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant" id="thermal-hold-unit"></span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">bolt</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="thermal-target-title"></p></div></div>' +
            '<select id="thermal-target-plan" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container">' +
            '<option value="powerSaver" id="thermal-plan-powerSaver"></option>' +
            '<option value="balanced" id="thermal-plan-balanced"></option>' +
            '<option value="performance" id="thermal-plan-performance"></option></select></div>' +
            '</div><aside class="keep-awake-status">' +
            '<p class="text-body-md text-on-surface" id="thermal-status"></p>' +
            '</aside></div></div>';
        refreshPowerLabels();
    }

    function mountIdlePowerGuardUi() {
        if (document.getElementById('idle-power-guard-panel')) return;
        ensurePowerStyles();
        const mount = document.getElementById('idle-power-guard-mount');
        if (!mount) return;

        mount.innerHTML =
            '<div class="keep-awake-panel-inner" id="idle-power-guard-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="idle-sub"></p>' +
            '<span class="keep-awake-badge" id="idle-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">hourglass_empty</span>' +
            '<span id="idle-badge-label"></span></span></div>' +
            '<div class="keep-awake-grid"><div class="space-y-sm">' +
            optionHtml('idle-main', 'idleToggle', 'idleToggleSub', 'hourglass_empty', false) +
            optionHtml('idle-battery', 'idleBatteryOnly', 'idleBatteryOnlySub', 'battery_android', true) +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">timer</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="idle-min-title"></p></div></div>' +
            '<div class="flex items-center gap-xs shrink-0">' +
            '<input type="number" min="1" max="120" step="1" id="idle-minutes-input" ' +
            'class="w-20 bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-2 text-body-md text-center focus:outline-none focus:border-secondary-container" />' +
            '<span class="text-label-sm text-on-surface-variant" id="idle-min-unit"></span></div></div>' +
            '<div class="heavy-app-option"><div class="flex items-center gap-md min-w-0">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center shrink-0">' +
            '<span class="material-symbols-outlined text-secondary-container">bolt</span></div>' +
            '<div class="min-w-0"><p class="text-body-md text-on-surface" id="idle-target-title"></p></div></div>' +
            '<select id="idle-target-plan" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container">' +
            '<option value="powerSaver" id="idle-plan-powerSaver"></option>' +
            '<option value="balanced" id="idle-plan-balanced"></option>' +
            '<option value="performance" id="idle-plan-performance"></option></select></div>' +
            '</div><aside class="keep-awake-status">' +
            '<p class="text-body-md text-on-surface" id="idle-status"></p>' +
            '</aside></div></div>';
        refreshPowerLabels();
        checkBatteryPresence();
    }

    function mountHeavyAppUi() {
        if (document.getElementById('heavy-app-detection-panel')) return;
        ensurePowerStyles();

        const mount = document.getElementById('heavy-app-mount');
        if (!mount) return;

        mount.innerHTML =
            '<div class="heavy-app-panel-inner" id="heavy-app-detection-panel">' +
            '<div class="flex flex-col sm:flex-row sm:items-start justify-between gap-md mb-lg relative z-10">' +
            '<p class="text-body-md text-on-surface-variant max-w-2xl" id="heavy-app-sub"></p>' +
            '<button class="btn-ghost rounded-lg py-2 px-4 text-label-md flex items-center gap-xs whitespace-nowrap" id="btn-heavy-app-refresh" type="button">' +
            '<span class="material-symbols-outlined text-[18px]">refresh</span><span id="heavy-app-refresh-label"></span></button></div>' +
            '<div class="heavy-app-grid"><div class="space-y-sm">' +
            optionHtml('heavy-main', 'heavyToggle', 'heavyToggleSub', 'bolt', true) +
            '<div class="heavy-app-option"><div class="flex items-center gap-md">' +
            '<div class="w-11 h-11 rounded-xl bg-surface-container-lowest border border-white/5 flex items-center justify-center">' +
            '<span class="material-symbols-outlined text-secondary-container">speed</span></div>' +
            '<div><p class="text-body-md text-on-surface" id="heavy-app-target-title"></p>' +
            '<p class="text-label-sm text-on-surface-variant" id="heavy-app-target-sub"></p></div></div>' +
            '<select id="heavy-app-target-plan" class="bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container">' +
            '<option value="performance" id="heavy-plan-performance"></option>' +
            '<option value="balanced" id="heavy-plan-balanced"></option>' +
            '<option value="powerSaver" id="heavy-plan-powerSaver"></option></select></div>' +
            optionHtml('heavy-windows', 'heavyWindows', 'heavyWindowsSub', 'display_settings', true) +
            optionHtml('heavy-gamepaths', 'heavyGamePaths', 'heavyGamePathsSub', 'folder_special', true) +
            optionHtml('heavy-resources', 'heavyResources', 'heavyResourcesSub', 'memory', true) +
            '</div><aside class="glass-card rounded-xl p-md border border-white/10 bg-surface-container-low/30">' +
            '<div class="flex items-center justify-between gap-md mb-md">' +
            '<span class="heavy-app-badge" id="heavy-app-state-badge" data-active="false">' +
            '<span class="material-symbols-outlined text-[16px]">radio_button_checked</span>' +
            '<span id="heavy-app-state-label"></span></span>' +
            '<span class="text-label-md text-on-surface-variant"><span id="heavy-app-count">0</span> ' +
            '<span id="heavy-app-detected-label"></span></span></div>' +
            '<div class="heavy-app-list" id="heavy-app-list"></div></aside></div>' +
            '<div class="heavy-rules-grid">' +
            heavyRulesCardHtml('always', 'sports_esports') +
            heavyRulesCardHtml('never', 'block') +
            '</div></div>';
        refreshPowerLabels();
    }

    function refreshPowerLabels() {
        const map = {
            'app-profile-sub': 'appProfileSub',
            'app-profile-main-title': 'appProfileToggle',
            'app-profile-main-sub': 'appProfileToggleSub',
            'app-profile-add-label': 'appProfileAdd',
            'app-profile-detected-label': 'appProfileDetected',
            'heavy-app-title': 'heavyTitle',
            'heavy-app-sub': 'heavySub',
            'heavy-main-title': 'heavyToggle',
            'heavy-main-sub': 'heavyToggleSub',
            'heavy-app-target-title': 'heavyTarget',
            'heavy-app-target-sub': 'heavyTargetSub',
            'heavy-windows-title': 'heavyWindows',
            'heavy-windows-sub': 'heavyWindowsSub',
            'heavy-gamepaths-title': 'heavyGamePaths',
            'heavy-gamepaths-sub': 'heavyGamePathsSub',
            'heavy-resources-title': 'heavyResources',
            'heavy-resources-sub': 'heavyResourcesSub',
            'heavy-app-refresh-label': 'refresh',
            'heavy-app-detected-label': 'detected',
            'heavy-plan-powerSaver': 'plan_powerSaver',
            'heavy-plan-balanced': 'plan_balanced',
            'heavy-plan-performance': 'plan_performance',
            'keep-awake-title': 'keepTitle',
            'keep-awake-sub': 'keepSub',
            'keep-awake-toggle-title': 'keepToggle',
            'keep-awake-toggle-sub': 'keepToggleSub',
            'keep-awake-battery-title': 'keepBatteryGuard',
            'keep-awake-battery-sub': 'keepBatteryGuardSub',
            'keep-awake-max-title': 'keepMaxDuration',
            'keep-awake-max-sub': 'keepMaxDurationSub',
            'keep-awake-max-unit': 'keepMaxMinutesUnit',
            'keep-awake-note': 'keepNote',
            'thermal-sub': 'thermalSub',
            'thermal-main-title': 'thermalToggle',
            'thermal-main-sub': 'thermalToggleSub',
            'thermal-gpu-title': 'thermalWatchGpu',
            'thermal-gpu-sub': 'thermalWatchGpuSub',
            'thermal-thr-title': 'thermalThreshold',
            'thermal-cool-title': 'thermalCool',
            'thermal-hold-title': 'thermalHold',
            'thermal-hold-unit': 'thermalHoldUnit',
            'thermal-target-title': 'thermalTarget',
            'thermal-plan-powerSaver': 'plan_powerSaver',
            'thermal-plan-balanced': 'plan_balanced',
            'thermal-plan-performance': 'plan_performance',
            'idle-sub': 'idleSub',
            'idle-main-title': 'idleToggle',
            'idle-main-sub': 'idleToggleSub',
            'idle-battery-title': 'idleBatteryOnly',
            'idle-battery-sub': 'idleBatteryOnlySub',
            'idle-min-title': 'idleMinutes',
            'idle-min-unit': 'idleMinutesUnit',
            'idle-target-title': 'idleTarget',
            'idle-plan-powerSaver': 'plan_powerSaver',
            'idle-plan-balanced': 'plan_balanced',
            'idle-plan-performance': 'plan_performance',
            'heavy-rules-always-title': 'heavyAlwaysTitle',
            'heavy-rules-always-sub': 'heavyAlwaysSub',
            'heavy-rules-always-add': 'heavyRulesAdd',
            'heavy-rules-never-title': 'heavyNeverTitle',
            'heavy-rules-never-sub': 'heavyNeverSub',
            'heavy-rules-never-add': 'heavyRulesAdd'
        };

        Object.entries(map).forEach(([id, key]) => {
            const el = document.getElementById(id);
            if (el) el.textContent = tt(key);
        });
        renderAppPowerProfiles();
        renderAppPowerProfileStatus(appProfileStatus);
        renderHeavyAppStatus(heavyAppStatus);
        renderHeavyAppRules();
        renderKeepAwakeState(keepAwakeState);
        renderThermalState(thermalState);
        renderIdleState(idleState);
    }

    function syncAppPowerProfileUi() {
        setToggle(document.getElementById('toggle-app-profile-main'), normalizeAppPowerProfiles().enabled);
        renderAppPowerProfiles();
        renderAppPowerProfileStatus(appProfileStatus);
    }

    function syncHeavyAppUi() {
        const cfg = normalizeHeavyAppDetection();
        setToggle(document.getElementById('toggle-heavy-main'), cfg.enabled);
        setToggle(document.getElementById('toggle-heavy-windows'), cfg.useWindowsGpuPreferences);
        setToggle(document.getElementById('toggle-heavy-gamepaths'), cfg.useGameInstallHeuristics);
        setToggle(document.getElementById('toggle-heavy-resources'), cfg.useResourceHeuristics);
        const select = document.getElementById('heavy-app-target-plan');
        if (select) select.value = cfg.targetPlan;
        renderHeavyAppRules();
    }

    function syncKeepAwakeUi() {
        const cfg = normalizeKeepAwake();
        setToggle(document.getElementById('toggle-keep-awake-toggle'), cfg.enabled);
        setToggle(document.getElementById('toggle-keep-awake-battery'), cfg.autoDisableOnBattery !== false);
        const maxInput = document.getElementById('keep-awake-max-input');
        if (maxInput && document.activeElement !== maxInput)
            maxInput.value = String(cfg.maxMinutes | 0);
        renderKeepAwakeState(keepAwakeState);
    }

    function syncThermalUi() {
        const cfg = normalizeThermalGuard();
        setToggle(document.getElementById('toggle-thermal-main'), cfg.enabled);
        setToggle(document.getElementById('toggle-thermal-gpu'), cfg.watchGpu !== false);
        const thr = document.getElementById('thermal-threshold-input');
        const cool = document.getElementById('thermal-cool-input');
        const hold = document.getElementById('thermal-hold-input');
        const plan = document.getElementById('thermal-target-plan');
        if (thr && document.activeElement !== thr) thr.value = String(Math.round(cfg.thresholdCelsius));
        if (cool && document.activeElement !== cool) cool.value = String(Math.round(cfg.coolThresholdCelsius));
        if (hold && document.activeElement !== hold) hold.value = String(cfg.holdSeconds | 0);
        if (plan) plan.value = cfg.targetPlan;
        renderThermalState(thermalState);
    }

    function renderThermalState(state) {
        const cfg = settings ? normalizeThermalGuard() : { enabled: false };
        const badge = document.getElementById('thermal-badge');
        const badgeLabel = document.getElementById('thermal-badge-label');
        const status = document.getElementById('thermal-status');
        const peakLine = document.getElementById('thermal-peak-line');
        const enabled = !!(state ? state.enabled : cfg.enabled);
        const active = !!(state && state.active);

        setToggle(document.getElementById('toggle-thermal-main'), enabled);
        if (badge) badge.dataset.active = active ? 'true' : 'false';
        if (badgeLabel) {
            badgeLabel.textContent = !enabled ? tt('thermalBadgeOff')
                : (active ? tt('thermalBadgeActive') : tt('thermalBadgeIdle'));
        }
        if (status) {
            if (!enabled) status.textContent = tt('thermalBadgeOff');
            else if (state && state.message === 'no_sensors') status.textContent = tt('thermalStatusNoSensors');
            else if (active) status.textContent = tt('thermalStatusActive');
            else if (state && state.message === 'warming') {
                status.textContent = tt('thermalStatusWarming')
                    .replace('{held}', String(Math.round(state.hotHoldSeconds || 0)))
                    .replace('{need}', String(state.holdSeconds || cfg.holdSeconds || 20));
            } else status.textContent = tt('thermalStatusIdle');
        }
        if (peakLine) {
            const peak = state && state.peakTemp != null ? Number(state.peakTemp).toFixed(0) + ' °C' : '--';
            peakLine.textContent = tt('thermalPeak') + ': ' + peak;
        }
    }

    function syncIdleUi() {
        const cfg = normalizeIdlePowerGuard();
        setToggle(document.getElementById('toggle-idle-main'), cfg.enabled);
        setToggle(document.getElementById('toggle-idle-battery'), cfg.onlyOnBattery !== false);
        const minIn = document.getElementById('idle-minutes-input');
        const plan = document.getElementById('idle-target-plan');
        if (minIn && document.activeElement !== minIn) minIn.value = String(cfg.idleMinutes | 0);
        if (plan) plan.value = cfg.targetPlan;
        renderIdleState(idleState);
    }

    function renderIdleState(state) {
        const cfg = settings ? normalizeIdlePowerGuard() : { enabled: false, idleMinutes: 10 };
        const badge = document.getElementById('idle-badge');
        const badgeLabel = document.getElementById('idle-badge-label');
        const status = document.getElementById('idle-status');
        const enabled = !!(state ? state.enabled : cfg.enabled);
        const active = !!(state && state.active);

        setToggle(document.getElementById('toggle-idle-main'), enabled);
        if (badge) badge.dataset.active = active ? 'true' : 'false';
        if (badgeLabel) {
            badgeLabel.textContent = !enabled ? tt('idleBadgeOff')
                : (active ? tt('idleBadgeActive') : tt('idleBadgeIdle'));
        }
        if (status) {
            if (!enabled) status.textContent = tt('idleBadgeOff');
            else if (state && state.message === 'no_input') status.textContent = tt('idleStatusNoInput');
            else if (active) status.textContent = tt('idleStatusActive');
            else if (state && (state.message === 'battery_skip')) status.textContent = tt('idleStatusSkip');
            else if (state && state.message === 'waiting') {
                const idleMin = ((state.idleSeconds || 0) / 60).toFixed(1);
                const need = state.idleMinutes || cfg.idleMinutes || 10;
                status.textContent = tt('idleStatusWaiting')
                    .replace('{idle}', idleMin)
                    .replace('{need}', String(need));
            } else status.textContent = tt('idleBadgeIdle');
        }
    }

    function renderKeepAwakeState(state) {
        const cfg = settings ? normalizeKeepAwake() : { enabled: false, autoDisableOnBattery: true, maxMinutes: 0 };
        const active = !!(state ? state.enabled : cfg.enabled);
        const badge = document.getElementById('keep-awake-badge');
        const badgeLabel = document.getElementById('keep-awake-badge-label');
        const status = document.getElementById('keep-awake-status');

        setToggle(document.getElementById('toggle-keep-awake-toggle'), active);
        if (state && typeof state.autoDisableOnBattery === 'boolean')
            setToggle(document.getElementById('toggle-keep-awake-battery'), state.autoDisableOnBattery);
        else
            setToggle(document.getElementById('toggle-keep-awake-battery'), cfg.autoDisableOnBattery !== false);

        if (badge) badge.dataset.active = active ? 'true' : 'false';
        if (badgeLabel) badgeLabel.textContent = active ? tt('keepBadgeActive') : tt('keepBadgeIdle');
        if (status) {
            let text = active ? tt('keepStatusActive') : tt('keepStatusIdle');
            if (active && state && state.remainingSeconds != null && state.remainingSeconds >= 0 && (state.maxMinutes | 0) > 0) {
                text = tt('keepStatusActiveTimed').replace('{time}', formatKeepRemaining(state.remainingSeconds));
            } else if (!active && state && state.lastAutoDisableReason === 'battery') {
                text = tt('keepStatusBattery');
            } else if (!active && state && state.lastAutoDisableReason === 'timeout') {
                text = tt('keepStatusTimeout');
            } else if (!active && state && state.message === 'auto_off_battery') {
                text = tt('keepStatusBattery');
            } else if (!active && state && state.message === 'auto_off_timeout') {
                text = tt('keepStatusTimeout');
            }
            status.textContent = text;
        }
    }

    function renderAppPowerProfileStatus(status) {
        const cfg = settings ? normalizeAppPowerProfiles() : { enabled: true };
        const badge = document.getElementById('app-profile-state-badge');
        const label = document.getElementById('app-profile-state-label');
        const count = document.getElementById('app-profile-count');
        if (!badge || !label || !count) return;

        const active = !!(status && status.active && cfg.enabled);
        badge.dataset.active = active ? 'true' : 'false';
        label.textContent = !cfg.enabled ? tt('appProfileStatusDisabled') : (active ? tt('appProfileStatusActive') : tt('appProfileStatusIdle'));
        count.textContent = status && typeof status.detectedCount === 'number' ? String(status.detectedCount) : '0';
    }

    function renderAppPowerProfiles() {
        if (!settings) return;
        const list = document.getElementById('app-profile-list');
        if (!list) return;

        const cfg = normalizeAppPowerProfiles();
        setToggle(document.getElementById('toggle-app-profile-main'), cfg.enabled);

        if (!cfg.rules.length) {
            list.innerHTML = '<p class="text-label-md text-on-surface-variant opacity-70 py-3">' + esc(tt('appProfileEmpty')) + '</p>';
            return;
        }

        const activeIds = new Set((appProfileStatus && Array.isArray(appProfileStatus.activeProfiles)
            ? appProfileStatus.activeProfiles
            : []).map(p => p.ruleId));

        list.innerHTML = cfg.rules
            .slice()
            .sort((a, b) => Number(activeIds.has(b.id)) - Number(activeIds.has(a.id)) || planPriority(b.targetPlan) - planPriority(a.targetPlan) || a.name.localeCompare(b.name))
            .map(rule => {
                const missing = rule.fileExists === false;
                const active = activeIds.has(rule.id);
                return '<div class="app-profile-row" data-rule-id="' + esc(rule.id) + '">' +
                    '<div class="min-w-0"><div class="flex items-center gap-xs">' +
                    '<span class="material-symbols-outlined text-secondary-container text-[18px]">' + (active ? 'bolt' : 'app_shortcut') + '</span>' +
                    '<span class="text-body-md text-on-surface truncate">' + esc(rule.name || appNameFromPath(rule.path)) + '</span>' +
                    (missing ? '<span class="text-label-sm app-profile-missing">' + esc(tt('appProfileMissing')) + '</span>' : '') +
                    '</div><span class="app-profile-path" title="' + esc(rule.path) + '">' + esc(rule.path) + '</span></div>' +
                    '<select class="app-profile-plan bg-surface-container-low/50 text-secondary-container font-medium border border-white/10 rounded-lg py-2 px-3 text-body-md focus:outline-none focus:border-secondary-container" data-rule-id="' + esc(rule.id) + '">' +
                    '<option value="performance"' + (rule.targetPlan === 'performance' ? ' selected' : '') + '>' + esc(tt('plan_performance')) + '</option>' +
                    '<option value="balanced"' + (rule.targetPlan === 'balanced' ? ' selected' : '') + '>' + esc(tt('plan_balanced')) + '</option>' +
                    '<option value="powerSaver"' + (rule.targetPlan === 'powerSaver' ? ' selected' : '') + '>' + esc(tt('plan_powerSaver')) + '</option></select>' +
                    '<button class="app-profile-icon-btn app-profile-toggle-rule" data-rule-id="' + esc(rule.id) + '" type="button" title="' + esc(rule.enabled ? 'On' : 'Off') + '">' +
                    '<span class="material-symbols-outlined text-[20px]">' + (rule.enabled ? 'toggle_on' : 'toggle_off') + '</span></button>' +
                    '<button class="app-profile-icon-btn app-profile-remove-rule" data-rule-id="' + esc(rule.id) + '" type="button" title="' + esc(tt('appProfileRemove')) + '">' +
                    '<span class="material-symbols-outlined text-[20px]">delete</span></button></div>';
            }).join('');
    }

    function renderHeavyAppStatus(status) {
        const cfg = settings ? normalizeHeavyAppDetection() : null;
        const badge = document.getElementById('heavy-app-state-badge');
        const label = document.getElementById('heavy-app-state-label');
        const count = document.getElementById('heavy-app-count');
        const list = document.getElementById('heavy-app-list');
        if (!badge || !label || !count || !list) return;

        const active = !!(status && status.active && (!cfg || cfg.enabled));
        badge.dataset.active = active ? 'true' : 'false';
        label.textContent = cfg && !cfg.enabled ? tt('statusDisabled') : (active ? tt('statusActive') : tt('statusIdle'));
        count.textContent = status && typeof status.detectedCount === 'number' ? String(status.detectedCount) : '0';

        const apps = status && Array.isArray(status.activeProcesses) ? status.activeProcesses : [];
        if (!apps.length) {
            list.innerHTML = '<p class="text-label-md text-on-surface-variant opacity-70 py-3">' + esc(tt('noneDetected')) + '</p>';
            return;
        }

        list.innerHTML = apps.map(app => {
            const isGame = app.kind === 'game';
            const level = String(app.confidenceLevel || '');
            const chip = !isGame ? tt('kindHeavyApp')
                : (level === 'confirmed' || level === 'probable') ? tt('level_' + level) : tt('kindGame');
            const reason = tt('reason_' + app.reason);
            const mb = Number.isFinite(Number(app.workingSetMb)) ? ' · ' + Number(app.workingSetMb) + ' MB' : '';
            // Evidence codes stay raw: they are diagnostics, not copy, and they must match
            // what the detection log reports.
            const codes = Array.isArray(app.evidence) ? app.evidence.map(e => e.code).join(', ') : '';
            const hint = tt('heavyScore') + ' ' + (Number(app.confidenceScore) || 0) + '/100' + (codes ? ' · ' + codes : '');
            return '<div class="heavy-app-row" title="' + esc(hint) + '">' +
                '<div class="flex items-center justify-between gap-sm">' +
                '<span class="flex items-center gap-xs min-w-0">' +
                '<span class="material-symbols-outlined text-secondary-container text-[16px]">' + (isGame ? 'sports_esports' : 'memory') + '</span>' +
                '<span class="text-body-md text-on-surface truncate">' + esc(app.name || 'App') + '</span></span>' +
                '<span class="heavy-app-chip" data-level="' + esc(isGame ? level : 'heavyApp') + '">' + esc(chip) + '</span></div>' +
                '<span class="heavy-app-path" title="' + esc(app.path || '') + '">' + esc(app.path || '') + '</span>' +
                '<span class="heavy-app-meta">' + esc(reason + mb) + '</span></div>';
        }).join('');
    }

    function renderHeavyAppRules() {
        if (!settings) return;
        const cfg = normalizeHeavyAppDetection();

        [['always', cfg.alwaysGamePaths], ['never', cfg.neverGamePaths]].forEach(([name, paths]) => {
            const list = document.getElementById('heavy-rules-' + name + '-list');
            if (!list) return;

            if (!paths.length) {
                list.innerHTML = '<p class="text-label-sm text-on-surface-variant opacity-70 py-2">' + esc(tt('heavyRulesEmpty')) + '</p>';
                return;
            }

            list.innerHTML = paths.map(path =>
                '<div class="heavy-rule-row"><div class="min-w-0 flex-1">' +
                '<span class="text-label-md text-on-surface truncate block">' + esc(appNameFromPath(path)) + '</span>' +
                '<span class="heavy-app-path" title="' + esc(path) + '">' + esc(path) + '</span></div>' +
                '<button class="app-profile-icon-btn heavy-rules-remove" data-list="' + name + '" data-path="' + esc(path) + '" type="button" title="' + esc(tt('heavyRulesRemove')) + '">' +
                '<span class="material-symbols-outlined text-[18px]">delete</span></button></div>').join('');
        });
    }

    function renderPlanConflictToast(data) {
        if (!data || data.shouldNotifyUser === false) return;

        const previous = document.getElementById('power-plan-conflict-toast');
        if (previous) previous.remove();

        const suspects = Array.isArray(data.suspects) ? data.suspects : [];
        const suspect = suspects[0];
        const confidence = String((suspect && suspect.confidence) || '').toLowerCase();
        const processLine = suspect
            ? (confidence === 'known' ? tt('planConflictKnown') : tt('planConflictProbable')) + ': ' + (suspect.name || 'App')
            : tt('planConflictExternal');
        const expected = tt('plan_' + data.expectedPlan) || data.expectedPlan || '';

        const toast = document.createElement('div');
        toast.id = 'power-plan-conflict-toast';
        toast.style.cssText = 'position:fixed;right:22px;bottom:22px;z-index:9999;max-width:390px;border:1px solid rgb(var(--vm-accent-rgb) / .32);background:linear-gradient(135deg,rgba(18,33,49,.96),rgba(10,17,40,.96));color:#d3deef;border-radius:16px;padding:14px 16px;box-shadow:0 18px 45px rgba(0,0,0,.38),0 0 0 1px rgb(var(--vm-accent-rgb) / .08);display:flex;gap:12px;align-items:flex-start;';
        toast.innerHTML =
            '<span class="material-symbols-outlined text-secondary-container" style="font-size:24px;line-height:1;">admin_panel_settings</span>' +
            '<div style="min-width:0;flex:1;display:grid;gap:4px;">' +
            '<strong style="color:var(--vm-accent-dim);font-size:14px;">' + esc(tt('planConflictTitle')) + '</strong>' +
            '<span style="font-size:13px;line-height:1.35;color:rgba(211,222,239,.86);">' + esc(processLine) + '</span>' +
            '<span style="font-size:12px;line-height:1.35;color:rgba(211,222,239,.66);">' + esc(tt('planConflictExpected')) + ': ' + esc(expected) + '</span>' +
            '</div>' +
            '<button type="button" aria-label="close" style="background:none;border:0;color:#94a3b8;cursor:pointer;font-size:18px;line-height:1;padding:0;">x</button>';
        toast.querySelector('button')?.addEventListener('click', () => toast.remove());
        document.body.appendChild(toast);
        setTimeout(() => { if (toast.parentElement) toast.remove(); }, 12000);
    }

    function updateHeavySetting(update) {
        const cfg = normalizeHeavyAppDetection();
        update(cfg);
        syncHeavyAppUi();
        scheduleSave();
    }

    function updateAppPowerProfiles(update) {
        const cfg = normalizeAppPowerProfiles();
        update(cfg);
        syncAppPowerProfileUi();
        scheduleSave();
    }

    function wireAppPowerProfileUi() {
        if (appProfileWired) return;

        document.addEventListener('click', async (e) => {
            const main = e.target.closest('#pref-app-profile-main');
            if (main && settings) {
                updateAppPowerProfiles(cfg => { cfg.enabled = !cfg.enabled; });
                return;
            }

            const add = e.target.closest('#btn-app-profile-add');
            if (add && settings) {
                add.disabled = true;
                try {
                    const res = await Host.call('pickAppPowerProfileExecutable');
                    if (!res || !res.path) return;
                    const cfg = normalizeAppPowerProfiles();
                    const path = String(res.path).trim();
                    if (cfg.rules.some(r => r.path.toLowerCase() === path.toLowerCase())) return;
                    const id = (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now() + Math.random());
                    cfg.rules.push({
                        id,
                        enabled: true,
                        name: appNameFromPath(path),
                        path,
                        targetPlan: 'performance'
                    });
                    syncAppPowerProfileUi();
                    scheduleSave();
                } catch (err) {
                    const label = document.getElementById('app-profile-state-label');
                    Host.fail(err, (msg) => {
                        if (label) label.textContent = msg;
                    });
                } finally {
                    add.disabled = false;
                }
                return;
            }

            const toggle = e.target.closest('.app-profile-toggle-rule');
            if (toggle && settings) {
                const id = toggle.dataset.ruleId;
                updateAppPowerProfiles(cfg => {
                    const rule = cfg.rules.find(r => r.id === id);
                    if (rule) rule.enabled = !rule.enabled;
                });
                return;
            }

            const remove = e.target.closest('.app-profile-remove-rule');
            if (remove && settings) {
                const id = remove.dataset.ruleId;
                updateAppPowerProfiles(cfg => {
                    cfg.rules = cfg.rules.filter(r => r.id !== id);
                });
            }
        });

        document.addEventListener('change', (e) => {
            if (!settings || !e.target?.classList?.contains('app-profile-plan')) return;
            const id = e.target.dataset.ruleId;
            const value = planIds.includes(e.target.value) ? e.target.value : 'performance';
            updateAppPowerProfiles(cfg => {
                const rule = cfg.rules.find(r => r.id === id);
                if (rule) rule.targetPlan = value;
            });
        });

        Host.on('appPowerProfileActivityChanged', (status) => {
            appProfileStatus = status;
            renderAppPowerProfileStatus(status);
            renderAppPowerProfiles();
        });
        appProfileWired = true;
    }

    function wireHeavyAppUi() {
        if (heavyAppWired) return;

        document.addEventListener('click', async (e) => {
            const pref = e.target.closest('#pref-heavy-main,#pref-heavy-windows,#pref-heavy-gamepaths,#pref-heavy-resources');
            if (pref && settings) {
                updateHeavySetting(cfg => {
                    if (pref.id === 'pref-heavy-main') cfg.enabled = !cfg.enabled;
                    if (pref.id === 'pref-heavy-windows') cfg.useWindowsGpuPreferences = !cfg.useWindowsGpuPreferences;
                    if (pref.id === 'pref-heavy-gamepaths') cfg.useGameInstallHeuristics = !cfg.useGameInstallHeuristics;
                    if (pref.id === 'pref-heavy-resources') cfg.useResourceHeuristics = !cfg.useResourceHeuristics;
                });
                return;
            }

            const addRule = e.target.closest('.heavy-rules-add');
            if (addRule && settings) {
                addRule.disabled = true;
                try {
                    const res = await Host.call('pickAppPowerProfileExecutable');
                    if (!res || !res.path) return;
                    const path = String(res.path).trim();
                    updateHeavySetting(cfg => {
                        // A path can only sit in one list: adding it here removes it from the other.
                        cfg.alwaysGamePaths = cfg.alwaysGamePaths.filter(p => !samePath(p, path));
                        cfg.neverGamePaths = cfg.neverGamePaths.filter(p => !samePath(p, path));
                        (addRule.dataset.list === 'never' ? cfg.neverGamePaths : cfg.alwaysGamePaths).push(path);
                    });
                } catch (err) {
                    const label = document.getElementById('heavy-app-state-label');
                    Host.fail(err, (msg) => {
                        if (label) label.textContent = msg;
                    });
                } finally {
                    addRule.disabled = false;
                }
                return;
            }

            const removeRule = e.target.closest('.heavy-rules-remove');
            if (removeRule && settings) {
                const path = removeRule.dataset.path;
                const which = removeRule.dataset.list;
                updateHeavySetting(cfg => {
                    if (which === 'never') cfg.neverGamePaths = cfg.neverGamePaths.filter(p => !samePath(p, path));
                    else cfg.alwaysGamePaths = cfg.alwaysGamePaths.filter(p => !samePath(p, path));
                });
                return;
            }

            const refresh = e.target.closest('#btn-heavy-app-refresh');
            if (refresh) {
                refresh.disabled = true;
                try {
                    heavyAppStatus = await Host.call('refreshHeavyAppDetection');
                    renderHeavyAppStatus(heavyAppStatus);
                } catch (err) {
                    const label = document.getElementById('heavy-app-state-label');
                    Host.fail(err, (msg) => {
                        if (label) label.textContent = msg;
                    });
                } finally {
                    refresh.disabled = false;
                }
            }
        });

        document.addEventListener('change', (e) => {
            if (!settings || e.target?.id !== 'heavy-app-target-plan') return;
            const value = planIds.includes(e.target.value) ? e.target.value : 'performance';
            updateHeavySetting(cfg => { cfg.targetPlan = value; });
        });

        Host.on('heavyAppActivityChanged', (status) => {
            heavyAppStatus = status;
            renderHeavyAppStatus(status);
        });
        Host.on('powerPlanConflictDetected', renderPlanConflictToast);
        heavyAppWired = true;
    }

    function wireKeepAwakeUi() {
        if (keepAwakeWired) return;

        document.addEventListener('click', async (e) => {
            if (!settings) return;

            const batteryPref = e.target.closest('#pref-keep-awake-battery');
            if (batteryPref) {
                const cfg = normalizeKeepAwake();
                cfg.autoDisableOnBattery = !cfg.autoDisableOnBattery;
                setToggle(document.getElementById('toggle-keep-awake-battery'), cfg.autoDisableOnBattery);
                await pushKeepAwakeSafety();
                return;
            }

            const pref = e.target.closest('#pref-keep-awake-toggle');
            if (!pref) return;

            const cfg = normalizeKeepAwake();
            const next = !cfg.enabled;
            cfg.enabled = next;
            cfg.lastChangedUtc = new Date().toISOString();
            keepAwakeState = { enabled: next, applied: next };
            syncKeepAwakeUi();
            if (Host.available) {
                try {
                    keepAwakeState = await Host.call('setKeepAwake', { enabled: next });
                    if (settings) {
                        normalizeKeepAwake().enabled = !!(keepAwakeState && keepAwakeState.enabled);
                        if (keepAwakeState && typeof keepAwakeState.autoDisableOnBattery === 'boolean')
                            normalizeKeepAwake().autoDisableOnBattery = keepAwakeState.autoDisableOnBattery;
                    }
                    renderKeepAwakeState(keepAwakeState);
                } catch (err) {
                    cfg.enabled = !next;
                    keepAwakeState = { enabled: !next, applied: !next };
                    const status = document.getElementById('keep-awake-status');
                    Host.fail(err, (msg) => {
                        if (status) status.textContent = msg;
                    });
                    syncKeepAwakeUi();
                }
            } else {
                scheduleSave();
            }
        });

        document.addEventListener('change', (e) => {
            if (!settings || e.target.id !== 'keep-awake-max-input') return;
            let v = parseInt(e.target.value, 10);
            if (!Number.isFinite(v) || v < 0) v = 0;
            if (v > 1440) v = 1440;
            normalizeKeepAwake().maxMinutes = v;
            e.target.value = String(v);
            pushKeepAwakeSafety();
        });

        Host.on('keepAwakeChanged', (state) => {
            keepAwakeState = state;
            if (settings) {
                const cfg = normalizeKeepAwake();
                cfg.enabled = !!state.enabled;
                if (typeof state.autoDisableOnBattery === 'boolean')
                    cfg.autoDisableOnBattery = state.autoDisableOnBattery;
                if (typeof state.maxMinutes === 'number')
                    cfg.maxMinutes = state.maxMinutes;
            }
            renderKeepAwakeState(state);
            syncKeepAwakeUi();
        });
        keepAwakeWired = true;
    }

    function wireThermalGuardUi() {
        if (thermalWired) return;

        document.addEventListener('click', async (e) => {
            if (!settings) return;
            if (e.target.closest('#pref-thermal-main')) {
                const cfg = normalizeThermalGuard();
                cfg.enabled = !cfg.enabled;
                setToggle(document.getElementById('toggle-thermal-main'), cfg.enabled);
                if (Host.available) {
                    try {
                        thermalState = await Host.call('setThermalGuardEnabled', { enabled: cfg.enabled });
                        if (thermalState && typeof thermalState.enabled === 'boolean')
                            cfg.enabled = thermalState.enabled;
                        renderThermalState(thermalState);
                    } catch (err) {
                        cfg.enabled = !cfg.enabled;
                        setToggle(document.getElementById('toggle-thermal-main'), cfg.enabled);
                        const status = document.getElementById('thermal-status');
                        Host.fail(err, (msg) => {
                            if (status) status.textContent = msg;
                        });
                    }
                } else scheduleSave();
                return;
            }
            if (e.target.closest('#pref-thermal-gpu')) {
                const cfg = normalizeThermalGuard();
                cfg.watchGpu = !cfg.watchGpu;
                setToggle(document.getElementById('toggle-thermal-gpu'), cfg.watchGpu);
                await pushThermalSettings();
            }
        });

        document.addEventListener('change', (e) => {
            if (!settings) return;
            const id = e.target && e.target.id;
            if (!id || !id.startsWith('thermal-')) return;
            const cfg = normalizeThermalGuard();
            if (id === 'thermal-threshold-input') {
                cfg.thresholdCelsius = clamp(e.target.value, 60, 105, 90);
                e.target.value = String(cfg.thresholdCelsius);
                if (cfg.coolThresholdCelsius >= cfg.thresholdCelsius)
                    cfg.coolThresholdCelsius = cfg.thresholdCelsius - 8;
            } else if (id === 'thermal-cool-input') {
                cfg.coolThresholdCelsius = clamp(e.target.value, 45, cfg.thresholdCelsius - 1, cfg.thresholdCelsius - 8);
                e.target.value = String(cfg.coolThresholdCelsius);
            } else if (id === 'thermal-hold-input') {
                cfg.holdSeconds = Math.round(clamp(e.target.value, 5, 300, 20));
                e.target.value = String(cfg.holdSeconds);
            } else if (id === 'thermal-target-plan') {
                cfg.targetPlan = planIds.includes(e.target.value) ? e.target.value : 'powerSaver';
            } else return;
            pushThermalSettings();
        });

        Host.on('thermalGuardChanged', (state) => {
            thermalState = state;
            if (settings && state) {
                const cfg = normalizeThermalGuard();
                if (typeof state.enabled === 'boolean') cfg.enabled = state.enabled;
                if (typeof state.thresholdCelsius === 'number') cfg.thresholdCelsius = state.thresholdCelsius;
                if (typeof state.coolThresholdCelsius === 'number') cfg.coolThresholdCelsius = state.coolThresholdCelsius;
                if (typeof state.holdSeconds === 'number') cfg.holdSeconds = state.holdSeconds;
                if (state.targetPlan) cfg.targetPlan = state.targetPlan;
                if (typeof state.watchGpu === 'boolean') cfg.watchGpu = state.watchGpu;
            }
            renderThermalState(state);
            syncThermalUi();
        });
        thermalWired = true;
    }

    function wireIdlePowerGuardUi() {
        if (idleWired) return;

        document.addEventListener('click', async (e) => {
            if (!settings) return;
            if (e.target.closest('#pref-idle-main')) {
                const cfg = normalizeIdlePowerGuard();
                cfg.enabled = !cfg.enabled;
                setToggle(document.getElementById('toggle-idle-main'), cfg.enabled);
                if (Host.available) {
                    try {
                        idleState = await Host.call('setIdlePowerGuardEnabled', { enabled: cfg.enabled });
                        if (idleState && typeof idleState.enabled === 'boolean')
                            cfg.enabled = idleState.enabled;
                        renderIdleState(idleState);
                    } catch (err) {
                        cfg.enabled = !cfg.enabled;
                        setToggle(document.getElementById('toggle-idle-main'), cfg.enabled);
                        const status = document.getElementById('idle-status');
                        Host.fail(err, (msg) => {
                            if (status) status.textContent = msg;
                        });
                    }
                } else scheduleSave();
                return;
            }
            if (e.target.closest('#pref-idle-battery')) {
                const cfg = normalizeIdlePowerGuard();
                cfg.onlyOnBattery = !cfg.onlyOnBattery;
                setToggle(document.getElementById('toggle-idle-battery'), cfg.onlyOnBattery);
                await pushIdleSettings();
            }
        });

        document.addEventListener('change', (e) => {
            if (!settings) return;
            const id = e.target && e.target.id;
            if (id === 'idle-minutes-input') {
                const cfg = normalizeIdlePowerGuard();
                cfg.idleMinutes = Math.round(clamp(e.target.value, 1, 120, 10));
                e.target.value = String(cfg.idleMinutes);
                pushIdleSettings();
            } else if (id === 'idle-target-plan') {
                const cfg = normalizeIdlePowerGuard();
                cfg.targetPlan = planIds.includes(e.target.value) ? e.target.value : 'powerSaver';
                pushIdleSettings();
            }
        });

        Host.on('idlePowerGuardChanged', (state) => {
            idleState = state;
            if (settings && state) {
                const cfg = normalizeIdlePowerGuard();
                if (typeof state.enabled === 'boolean') cfg.enabled = state.enabled;
                if (typeof state.idleMinutes === 'number') cfg.idleMinutes = state.idleMinutes;
                if (state.targetPlan) cfg.targetPlan = state.targetPlan;
                if (typeof state.onlyOnBattery === 'boolean') cfg.onlyOnBattery = state.onlyOnBattery;
            }
            renderIdleState(state);
            syncIdleUi();
        });
        idleWired = true;
    }

    function loadIntoUi() {
        ruleIds.forEach(id => {
            const rule = ruleById(id);
            if (!rule) return;
            document.getElementById('rule-' + id + '-threshold').value = rule.thresholdPct;
            document.getElementById('rule-' + id + '-minutes').value = rule.durationMinutes;
            document.getElementById('rule-' + id + '-toggle').checked = rule.enabled;
        });

        document.getElementById('master-toggle').checked = settings.masterAutomationEnabled;
        const cpuAutomation = normalizeCpuAutomation();
        const sampleInput = document.getElementById('cpu-sample-interval');
        if (sampleInput) sampleInput.value = cpuAutomation.sampleIntervalSeconds;
        mountAppPowerProfileUi();
        mountHeavyAppUi();
        mountKeepAwakeUi();
        mountThermalGuardUi();
        mountIdlePowerGuardUi();
        syncAppPowerProfileUi();
        syncHeavyAppUi();
        syncKeepAwakeUi();
        syncThermalUi();
        syncIdleUi();
        wireAppPowerProfileUi();
        wireHeavyAppUi();
        wireKeepAwakeUi();
        wireThermalGuardUi();
        wireIdlePowerGuardUi();

        Host.call('getAppPowerProfileStatus').then(status => {
            appProfileStatus = status;
            renderAppPowerProfileStatus(status);
            renderAppPowerProfiles();
        }).catch(err => console.error('getAppPowerProfileStatus failed', err));

        Host.call('getHeavyAppStatus').then(status => {
            heavyAppStatus = status;
            renderHeavyAppStatus(status);
        }).catch(err => console.error('getHeavyAppStatus failed', err));

        Host.call('getThermalGuardState').then(state => {
            thermalState = state;
            renderThermalState(state);
            syncThermalUi();
        }).catch(err => console.error('getThermalGuardState failed', err));

        Host.call('getIdlePowerGuardState').then(state => {
            idleState = state;
            renderIdleState(state);
            syncIdleUi();
        }).catch(err => console.error('getIdlePowerGuardState failed', err));

        keepAwakeState = { enabled: normalizeKeepAwake().enabled, applied: normalizeKeepAwake().enabled };
        renderKeepAwakeState(keepAwakeState);
    }

    function saveSettingsNow() {
        clearTimeout(saveTimer);
        if (window.I18n && I18n.getLang && settings) settings.language = I18n.getLang();
        return Host.call('saveSettings', settings)
            .then(() => Host.call('getAppPowerProfileStatus'))
            .then(status => {
                appProfileStatus = status;
                renderAppPowerProfileStatus(status);
                renderAppPowerProfiles();
                return Host.call('getHeavyAppStatus');
            })
            .then(status => {
                heavyAppStatus = status;
                renderHeavyAppStatus(status);
            });
    }

    function scheduleSave() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(() => {
            saveSettingsNow().catch(err => console.error('saveSettings failed', err));
        }, 400);
    }

    function clamp(value, min, max, fallback) {
        const n = Number(value);
        if (!isFinite(n) || n < min || n > max) return fallback;
        return n;
    }

    function wireUi() {
        ruleIds.forEach(id => {
            document.getElementById('rule-' + id + '-threshold').addEventListener('change', (e) => {
                const rule = ruleById(id);
                rule.thresholdPct = clamp(e.target.value, 1, 99, rule.thresholdPct);
                e.target.value = rule.thresholdPct;
                scheduleSave();
            });
            document.getElementById('rule-' + id + '-minutes').addEventListener('change', (e) => {
                const rule = ruleById(id);
                rule.durationMinutes = clamp(e.target.value, 1, 60, rule.durationMinutes);
                e.target.value = rule.durationMinutes;
                scheduleSave();
            });
            document.getElementById('rule-' + id + '-toggle').addEventListener('change', (e) => {
                ruleById(id).enabled = e.target.checked;
                scheduleSave();
            });
        });
        document.getElementById('master-toggle').addEventListener('change', (e) => {
            settings.masterAutomationEnabled = e.target.checked;
            scheduleSave();
        });

        const sampleInput = document.getElementById('cpu-sample-interval');
        if (sampleInput) {
            sampleInput.addEventListener('change', (e) => {
                const cfg = normalizeCpuAutomation();
                cfg.sampleIntervalSeconds = Math.round(clamp(e.target.value, 1, 60, cfg.sampleIntervalSeconds));
                e.target.value = cfg.sampleIntervalSeconds;
                scheduleSave();
            });
        }
    }

    Host.call('getSettings').then(res => {
        settings = res.settings;
        if (window.I18n && I18n.initFromSettings) I18n.initFromSettings(res);
        if (window.I18n && I18n.getLang && settings) settings.language = I18n.getLang();
        window.__voltThemeCatalog = res.themeCatalog || {};
        window.__voltThemeState = res.theme || null;
        if (window.VoltTheme && VoltTheme.apply) {
            settings.themeColor = VoltTheme.apply(
                settings.themeColor || (res.theme && res.theme.themeColor),
                res.theme && res.theme.palette);
        }
        if (window.VoltFont && VoltFont.apply && settings) {
            settings.font = VoltFont.apply(settings.font);
        }
        loadIntoUi();
        wireUi();
        window.__voltSettings = {
            get: () => settings,
            save: scheduleSave,
            saveNow: () => saveSettingsNow().catch(err => {
                console.error('saveSettings failed', err);
                throw err;
            }),
            startWithWindows: res.startWithWindows,
        };
        document.dispatchEvent(new CustomEvent('settingsloaded'));
    }).catch(err => console.error('getSettings failed', err));

    document.addEventListener('langchanged', refreshPowerLabels);

    // Accordion: collapse/expand the power feature groups.
    ensurePowerStyles();
    document.addEventListener('click', (e) => {
        const header = e.target.closest('#view-power .vm-acc-header');
        if (!header) return;
        const item = header.closest('.vm-acc-item');
        if (item) item.dataset.open = item.dataset.open === 'true' ? 'false' : 'true';
    });

    // Sub-nav (pm-seg) switching — defensively (re)mount the JS-driven panels.
    // They normally mount during loadIntoUi, but if getSettings fails or the
    // segment is selected before init completes, mount on demand here.
    document.addEventListener('click', (e) => {
        const seg = e.target.closest('#view-power .pm-seg');
        if (!seg) return;
        setTimeout(() => {
            switch (seg.dataset.pm) {
                case 'apps':
                    mountAppPowerProfileUi();
                    wireAppPowerProfileUi();
                    if (settings) syncAppPowerProfileUi();
                    break;
                case 'games':
                    mountHeavyAppUi();
                    wireHeavyAppUi();
                    if (settings) syncHeavyAppUi();
                    break;
                // keep-awake (awake) moved to the dedicated energy tab; mount
                // happens via loadIntoUi() at settings boot, no segment here.
            }
        }, 20);
    });
})();
