window.I18n = (function() {
    const translations = {
        en: {
            "app_title": "VoltManager",
            "app_subtitle": "System Optimized",
            "nav_home": "Home",
            "nav_power": "Power Management",
            "nav_settings": "Settings & Info",
            "nav_monitoring": "Monitoring Active",
            "dash_title": "Dashboard",
            "dash_subtitle": "Real-time hardware stress and performance metrics.",
            "dash_task_manager": "Task Manager",
            "dash_cpu_core": "CPU Core",
            "dash_gpu_load": "GPU Load",
            "dash_memory": "Memory",
            "dash_disk": "Disk I/O",
            "dash_disk_sub": "Total disk activity",
            "dash_detecting_cpu": "Detecting CPU…",
            "dash_detecting_gpu": "Detecting GPU…",
            "dash_gpu_unavailable": "GPU counters unavailable",
            "dash_active_plan": "ACTIVE POWER PLAN",
            "dash_plan_saver": "Power Efficiency",
            "dash_plan_balanced": "Balanced",
            "dash_plan_performance": "Performance",
            "power_title": "Power Management",
            "power_subtitle": "Configure automation rules to optimize system performance and power consumption.",
            "power_rule_if": "If CPU &lt;",
            "power_rule_if_gt": "If CPU &gt;",
            "power_rule_for": "% for",
            "power_rule_min": "minute",
            "power_rule_saver": "Power Efficiency",
            "power_rule_balanced": "Balanced",
            "power_rule_performance": "Performance",
            "power_master_title": "Enable Background Automation",
            "power_master_sub": "Let VoltManager apply rules silently.",
            "set_title": "Settings & Info",
            "set_subtitle": "Manage application preferences, updates, and system information.",
            "set_updates_title": "Software Updates",
            "set_updates_sub": "Keep VoltManager optimized with the latest performance enhancements.",
            "set_updates_curr": "Current Version: ",
            "set_btn_check": "Check for updates from GitHub (Main Branch)",
            "set_btn_download": "Download and install",
            "set_changelog_title": "Release Notes",
            "set_changelog_latest": "Latest",
            "set_changelog_empty": "Press \"Check for updates\" to load release notes and latest commits.",
            "set_sys_system": "System",
            "set_pref_title": "General Preferences",
            "set_pref_autostart": "Start with Windows",
            "set_pref_autostart_sub": "Minimized automatic startup",
            "set_pref_tray": "Close to notification area",
            "set_pref_tray_sub": "Closing hides the app, automation active",
            "set_pref_lang": "Language",
            "set_pref_lang_sub": "Application interface language",
            "setup_req": "Setup Required",
            "setup_req_sub": "Default power plans not detected. It is necessary to install them to optimize system performance.",
            "setup_btn_exit": "No, exit",
            "setup_btn_install": "Install base plans",
            "setup_admin": "This operation requires administrator privileges.",
            "msg_check_update": "Checking for updates…",
            "msg_check_err": "Error checking updates.",
            "msg_err": "Error: ",
            "msg_dl_fail": "Download failed: ",
            "msg_dl_prog": "Downloading… ",
            "msg_latest_commits": "Latest commits (main)",
            "msg_no_info": "No information available.",
            "msg_dl_install": "Download and install v",
            "msg_installing": "Installing power plans…",
            "msg_install_ok": "Plans installed successfully.",
            "msg_install_part": "Partial installation: some plans were not created. Try again.",
            "upd_banner_title": "Update available: v",
            "upd_banner_sub": "A new version of VoltManager is ready to install.",
            "upd_banner_install": "Install now",
            "upd_banner_later": "Later"
        },
        it: {
            "app_title": "VoltManager",
            "app_subtitle": "Sistema Ottimizzato",
            "nav_home": "Home",
            "nav_power": "Gestione Energetica",
            "nav_settings": "Impostazioni e Info",
            "nav_monitoring": "Monitoraggio Attivo",
            "dash_title": "Dashboard",
            "dash_subtitle": "Metriche di stress e prestazioni hardware in tempo reale.",
            "dash_task_manager": "Gestione Attività",
            "dash_cpu_core": "CPU Core",
            "dash_gpu_load": "Carico GPU",
            "dash_memory": "Memoria",
            "dash_disk": "I/O Disco",
            "dash_disk_sub": "Attività disco totale",
            "dash_detecting_cpu": "Rilevamento CPU…",
            "dash_detecting_gpu": "Rilevamento GPU…",
            "dash_gpu_unavailable": "Contatori GPU non disponibili",
            "dash_active_plan": "PIANO ENERGETICO ATTIVO",
            "dash_plan_saver": "Risparmio Energetico",
            "dash_plan_balanced": "Bilanciato",
            "dash_plan_performance": "Prestazioni alte",
            "power_title": "Gestione Energetica",
            "power_subtitle": "Configura le regole di automazione per ottimizzare le prestazioni e il consumo del sistema.",
            "power_rule_if": "Se CPU &lt;",
            "power_rule_if_gt": "Se CPU &gt;",
            "power_rule_for": "% per",
            "power_rule_min": "minuto",
            "power_rule_saver": "Risparmio Energetico",
            "power_rule_balanced": "Bilanciato",
            "power_rule_performance": "Prestazioni alte",
            "power_master_title": "Attiva Gestione Automatica in Background",
            "power_master_sub": "Lascia che VoltManager applichi le regole in modo silenzioso.",
            "set_title": "Impostazioni e Info",
            "set_subtitle": "Gestisci preferenze dell'applicazione, aggiornamenti e informazioni di sistema.",
            "set_updates_title": "Aggiornamenti Software",
            "set_updates_sub": "Mantieni VoltManager ottimizzato con i miglioramenti prestazionali più recenti.",
            "set_updates_curr": "Versione Corrente: ",
            "set_btn_check": "Cerca aggiornamenti (Main Branch)",
            "set_btn_download": "Scarica e installa",
            "set_changelog_title": "Note di Rilascio",
            "set_changelog_latest": "Recente",
            "set_changelog_empty": "Premi \"Cerca aggiornamenti\" per caricare le note di rilascio e gli ultimi commit.",
            "set_sys_system": "Sistema",
            "set_pref_title": "Preferenze Generali",
            "set_pref_autostart": "Avvio con Windows",
            "set_pref_autostart_sub": "Avvio automatico ridotto a icona",
            "set_pref_tray": "Chiudi nell'area di notifica",
            "set_pref_tray_sub": "La chiusura nasconde l'app, automazione attiva",
            "set_pref_lang": "Lingua",
            "set_pref_lang_sub": "Lingua dell'interfaccia",
            "setup_req": "Setup Richiesto",
            "setup_req_sub": "Piani energetici di base non rilevati. È necessario installarli per ottimizzare le prestazioni del sistema.",
            "setup_btn_exit": "No, esci",
            "setup_btn_install": "Installa piani di base",
            "setup_admin": "Questa operazione richiede i privilegi di amministratore.",
            "msg_check_update": "Controllo aggiornamenti in corso…",
            "msg_check_err": "Errore durante il controllo.",
            "msg_err": "Errore: ",
            "msg_dl_fail": "Download fallito: ",
            "msg_dl_prog": "Download… ",
            "msg_latest_commits": "Ultimi commit (main)",
            "msg_no_info": "Nessuna informazione disponibile.",
            "msg_dl_install": "Scarica e installa v",
            "msg_installing": "Installazione dei piani energetici in corso…",
            "msg_install_ok": "Piani installati correttamente.",
            "msg_install_part": "Installazione parziale: alcuni piani non sono stati creati. Riprova.",
            "upd_banner_title": "Aggiornamento disponibile: v",
            "upd_banner_sub": "Una nuova versione di VoltManager è pronta per l'installazione.",
            "upd_banner_install": "Installa ora",
            "upd_banner_later": "Più tardi"
        }
    };

    let lang = localStorage.getItem('volt_lang') || 'it';

    function setLang(l) {
        lang = l;
        localStorage.setItem('volt_lang', l);
        apply();
        document.dispatchEvent(new CustomEvent('langchanged', { detail: l }));
    }

    function getLang() { return lang; }

    function t(key) {
        if (translations[lang] && translations[lang][key]) {
            return translations[lang][key];
        }
        if (translations['en'] && translations['en'][key]) {
            return translations['en'][key];
        }
        return key;
    }

    function apply() {
        document.documentElement.lang = lang;
        document.querySelectorAll('[data-i18n]').forEach(el => {
            const key = el.getAttribute('data-i18n');
            if (el.tagName === 'INPUT' && el.type === 'button') {
                el.value = t(key);
            } else {
                el.innerHTML = t(key);
            }
        });
    }

    // Run initially
    document.addEventListener('DOMContentLoaded', apply);

    return { setLang, getLang, t, apply };
})();
