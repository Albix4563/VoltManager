using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VoltManager.Setup.Engine
{
    public static class I18n
    {
        private static readonly Dictionary<string, string> It = new()
        {
            ["welcome_title"]       = "Benvenuto",
            ["welcome_subtitle"]    = "VoltManager ottimizza automaticamente il piano energetico del tuo PC in base all'utilizzo CPU, riducendo i consumi senza compromettere le prestazioni.",
            ["welcome_info"]        = "Clicca Avanti per configurare l'installazione.",
            ["feat1_t"]             = "Ottimizzazione automatica",
            ["feat1_d"]            = "Cambia il piano energetico in tempo reale in base al carico CPU.",
            ["feat2_t"]             = "Consumi ridotti",
            ["feat2_d"]            = "Meno energia e meno calore quando il PC è inattivo.",
            ["feat3_t"]             = "Prestazioni intatte",
            ["feat3_d"]            = "Piena potenza quando serve, senza compromessi.",
            ["options_title"]       = "Opzioni di installazione",
            ["options_folder"]      = "Cartella di installazione",
            ["options_browse"]      = "Sfoglia…",
            ["options_desktop"]     = "Crea collegamento sul desktop",
            ["options_desktop_d"]  = "Aggiunge un'icona di avvio sul desktop.",
            ["options_startup"]     = "Avvia con Windows",
            ["options_startup_d"]  = "VoltManager parte automaticamente all'accesso.",
            ["options_widgets"]     = "Attiva widget desktop",
            ["options_widgets_d"]  = "Mostra i widget separati sul desktop al primo avvio dell'app.",
            ["options_widgets_select"] = "Seleziona i widget da attivare:",
            ["widget_clock"]       = "Orologio",
            ["widget_calendar"]    = "Calendario",
            ["widget_usage"]       = "Utilizzo",
            ["widget_temps"]       = "Temperature",
            ["widget_power"]       = "Consumo",
            ["widget_plans"]       = "Piani energetici",
            ["options_launch"]      = "Avvia VoltManager al termine dell'installazione",
            ["options_launch_d"]   = "Apre l'app non appena l'installazione è completata.",
            ["progress_title"]      = "Installazione in corso…",
            ["progress_wait"]       = "Attendere, non chiudere questa finestra.",
            ["done_title"]          = "Installazione completata",
            ["done_title_err"]      = "Installazione non riuscita",
            ["done_sub"]            = "VoltManager è stato installato correttamente.",
            ["done_launch"]         = "Avvia VoltManager",
            ["uninst_title"]        = "Disinstallazione",
            ["uninst_confirm"]      = "Rimuovere VoltManager dal computer?",
            ["uninst_sub"]          = "Tutti i file dell'applicazione verranno eliminati.",
            ["uninst_item1"]        = "File del programma e collegamenti",
            ["uninst_item2"]        = "Voce in App e funzionalità",
            ["uninst_item3"]        = "Piano energetico ripristinato al predefinito",
            ["uninst_warn"]         = "Operazione non reversibile. Il piano energetico di Windows verrà ripristinato a quello predefinito.",
            ["uninst_progress"]     = "Rimozione in corso…",
            ["uninst_done"]         = "VoltManager è stato disinstallato.",
            ["btn_back"]            = "← Indietro",
            ["btn_next"]            = "Avanti →",
            ["btn_install"]         = "Installa",
            ["btn_cancel"]          = "Annulla",
            ["btn_close"]           = "Chiudi",
            ["btn_finish"]          = "Fine",
            ["btn_uninstall"]       = "Disinstalla",
            ["status_kill"]         = "Chiusura VoltManager in esecuzione…",
            ["status_migrate"]      = "Rimozione installazione precedente…",
            ["status_extract"]      = "Estrazione file…",
            ["status_webview"]      = "Installazione WebView2 Runtime…",
            ["status_shortcuts"]    = "Creazione collegamenti…",
            ["status_startup"]      = "Configurazione avvio con Windows…",
            ["status_registry"]     = "Registrazione nel sistema…",
            ["status_uninst_kill"]  = "Chiusura VoltManager…",
            ["status_uninst_files"] = "Eliminazione file…",
            ["status_uninst_reg"]   = "Rimozione voce da Programmi…",
        };

        private static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            ["welcome_title"]       = "Welcome",
            ["welcome_subtitle"]    = "VoltManager automatically optimizes your PC's power plan based on CPU usage, reducing energy consumption without sacrificing performance.",
            ["welcome_info"]        = "Click Next to configure installation.",
            ["feat1_t"]             = "Automatic optimization",
            ["feat1_d"]            = "Switches the power plan in real time based on CPU load.",
            ["feat2_t"]             = "Lower consumption",
            ["feat2_d"]            = "Less energy and heat while your PC is idle.",
            ["feat3_t"]             = "Performance intact",
            ["feat3_d"]            = "Full power when you need it, no compromise.",
            ["options_title"]       = "Installation options",
            ["options_folder"]      = "Installation folder",
            ["options_browse"]      = "Browse…",
            ["options_desktop"]     = "Create desktop shortcut",
            ["options_desktop_d"]  = "Adds a launch icon on your desktop.",
            ["options_startup"]     = "Start with Windows",
            ["options_startup_d"]  = "VoltManager starts automatically at sign-in.",
            ["options_widgets"]     = "Enable desktop widgets",
            ["options_widgets_d"]  = "Shows the separate desktop widgets on the app's first launch.",
            ["options_widgets_select"] = "Select widgets to enable:",
            ["widget_clock"]       = "Clock",
            ["widget_calendar"]    = "Calendar",
            ["widget_usage"]       = "Usage",
            ["widget_temps"]       = "Temps",
            ["widget_power"]       = "Power",
            ["widget_plans"]       = "Power plans",
            ["options_launch"]      = "Launch VoltManager after installation",
            ["options_launch_d"]   = "Opens the app as soon as setup completes.",
            ["progress_title"]      = "Installing…",
            ["progress_wait"]       = "Please wait, do not close this window.",
            ["done_title"]          = "Installation complete",
            ["done_title_err"]      = "Installation failed",
            ["done_sub"]            = "VoltManager has been installed successfully.",
            ["done_launch"]         = "Launch VoltManager",
            ["uninst_title"]        = "Uninstall",
            ["uninst_confirm"]      = "Remove VoltManager from your computer?",
            ["uninst_sub"]          = "All application files will be deleted.",
            ["uninst_item1"]        = "Program files and shortcuts",
            ["uninst_item2"]        = "Entry in Apps & features",
            ["uninst_item3"]        = "Power plan restored to default",
            ["uninst_warn"]         = "This action cannot be undone. Windows will revert to its default power plan.",
            ["uninst_progress"]     = "Removing…",
            ["uninst_done"]         = "VoltManager has been uninstalled.",
            ["btn_back"]            = "← Back",
            ["btn_next"]            = "Next →",
            ["btn_install"]         = "Install",
            ["btn_cancel"]          = "Cancel",
            ["btn_close"]           = "Close",
            ["btn_finish"]          = "Finish",
            ["btn_uninstall"]       = "Uninstall",
            ["status_kill"]         = "Closing running VoltManager…",
            ["status_migrate"]      = "Removing previous installation…",
            ["status_extract"]      = "Extracting files…",
            ["status_webview"]      = "Installing WebView2 Runtime…",
            ["status_shortcuts"]    = "Creating shortcuts…",
            ["status_startup"]      = "Configuring Windows startup…",
            ["status_registry"]     = "Registering in system…",
            ["status_uninst_kill"]  = "Closing VoltManager…",
            ["status_uninst_files"] = "Deleting files…",
            ["status_uninst_reg"]   = "Removing from Programs…",
        };

        private static readonly Dictionary<string, string> Zh = new Dictionary<string, string>
        {
            ["welcome_title"]       = "欢迎",
            ["welcome_subtitle"]    = "VoltManager 会根据 CPU 使用率自动优化电脑的电源计划，在不牺牲性能的情况下降低能耗。",
            ["welcome_info"]        = "点击下一步以配置安装。",
            ["feat1_t"]             = "自动优化",
            ["feat1_d"]            = "根据 CPU 负载实时切换电源计划。",
            ["feat2_t"]             = "更低能耗",
            ["feat2_d"]            = "电脑空闲时更省电、更少发热。",
            ["feat3_t"]             = "性能不减",
            ["feat3_d"]            = "需要时火力全开，毫不妥协。",
            ["options_title"]       = "安装选项",
            ["options_folder"]      = "安装文件夹",
            ["options_browse"]      = "浏览…",
            ["options_desktop"]     = "创建桌面快捷方式",
            ["options_desktop_d"]  = "在桌面添加启动图标。",
            ["options_startup"]     = "随 Windows 启动",
            ["options_startup_d"]  = "登录时自动启动 VoltManager。",
            ["options_widgets"]     = "启用桌面小部件",
            ["options_widgets_d"]  = "首次启动应用时显示独立的桌面小部件。",
            ["options_widgets_select"] = "选择要启用的小部件：",
            ["widget_clock"]       = "时钟",
            ["widget_calendar"]    = "日历",
            ["widget_usage"]       = "使用情况",
            ["widget_temps"]       = "温度",
            ["widget_power"]       = "功耗",
            ["widget_plans"]       = "电源计划",
            ["options_launch"]      = "安装完成后启动 VoltManager",
            ["options_launch_d"]   = "安装完成后立即打开应用。",
            ["progress_title"]      = "正在安装…",
            ["progress_wait"]       = "请稍候，不要关闭此窗口。",
            ["done_title"]          = "安装完成",
            ["done_title_err"]      = "安装失败",
            ["done_sub"]            = "VoltManager 已成功安装。",
            ["done_launch"]         = "启动 VoltManager",
            ["uninst_title"]        = "卸载",
            ["uninst_confirm"]      = "要从电脑中移除 VoltManager 吗？",
            ["uninst_sub"]          = "所有应用程序文件都将被删除。",
            ["uninst_item1"]        = "程序文件和快捷方式",
            ["uninst_item2"]        = "“应用和功能”中的条目",
            ["uninst_item3"]        = "电源计划恢复为默认",
            ["uninst_warn"]         = "此操作无法撤销。Windows 将恢复为默认电源计划。",
            ["uninst_progress"]     = "正在移除…",
            ["uninst_done"]         = "VoltManager 已卸载。",
            ["btn_back"]            = "← 返回",
            ["btn_next"]            = "下一步 →",
            ["btn_install"]         = "安装",
            ["btn_cancel"]          = "取消",
            ["btn_close"]           = "关闭",
            ["btn_finish"]          = "完成",
            ["btn_uninstall"]       = "卸载",
            ["status_kill"]         = "正在关闭运行中的 VoltManager…",
            ["status_migrate"]      = "正在移除之前的安装…",
            ["status_extract"]      = "正在解压文件…",
            ["status_webview"]      = "正在安装 WebView2 Runtime…",
            ["status_shortcuts"]    = "正在创建快捷方式…",
            ["status_startup"]      = "正在配置 Windows 启动项…",
            ["status_registry"]     = "正在注册到系统…",
            ["status_uninst_kill"]  = "正在关闭 VoltManager…",
            ["status_uninst_files"] = "正在删除文件…",
            ["status_uninst_reg"]   = "正在从程序列表中移除…",
        };

        private static readonly Dictionary<string, string> Es = new()
        {
            ["welcome_title"]       = "Bienvenido",
            ["welcome_subtitle"]    = "VoltManager optimiza automáticamente el plan de energía de tu PC según el uso de CPU, reduciendo el consumo sin sacrificar el rendimiento.",
            ["welcome_info"]        = "Haz clic en Siguiente para configurar la instalación.",
            ["feat1_t"]             = "Optimización automática",
            ["feat1_d"]            = "Cambia el plan de energía en tiempo real según la carga de la CPU.",
            ["feat2_t"]             = "Consumo reducido",
            ["feat2_d"]            = "Menos energía y menos calor cuando el PC está inactivo.",
            ["feat3_t"]             = "Rendimiento intacto",
            ["feat3_d"]            = "Máxima potencia cuando la necesitas, sin compromisos.",
            ["options_title"]       = "Opciones de instalación",
            ["options_folder"]      = "Carpeta de instalación",
            ["options_browse"]      = "Examinar…",
            ["options_desktop"]     = "Crear acceso directo en el escritorio",
            ["options_desktop_d"]  = "Añade un icono de inicio en el escritorio.",
            ["options_startup"]     = "Iniciar con Windows",
            ["options_startup_d"]  = "VoltManager se inicia automáticamente al iniciar sesión.",
            ["options_widgets"]     = "Activar widgets de escritorio",
            ["options_widgets_d"]  = "Muestra los widgets independientes en el escritorio al iniciar la aplicación por primera vez.",
            ["options_widgets_select"] = "Selecciona los widgets a activar:",
            ["widget_clock"]       = "Reloj",
            ["widget_calendar"]    = "Calendario",
            ["widget_usage"]       = "Uso",
            ["widget_temps"]       = "Temperaturas",
            ["widget_power"]       = "Energía",
            ["widget_plans"]       = "Planes energéticos",
            ["options_launch"]      = "Iniciar VoltManager al terminar la instalación",
            ["options_launch_d"]   = "Abre la aplicación en cuanto se complete la instalación.",
            ["progress_title"]      = "Instalando…",
            ["progress_wait"]       = "Espera, no cierres esta ventana.",
            ["done_title"]          = "Instalación completada",
            ["done_title_err"]      = "Instalación fallida",
            ["done_sub"]            = "VoltManager se ha instalado correctamente.",
            ["done_launch"]         = "Iniciar VoltManager",
            ["uninst_title"]        = "Desinstalación",
            ["uninst_confirm"]      = "¿Eliminar VoltManager del equipo?",
            ["uninst_sub"]          = "Se eliminarán todos los archivos de la aplicación.",
            ["uninst_item1"]        = "Archivos del programa y accesos directos",
            ["uninst_item2"]        = "Entrada en Aplicaciones y características",
            ["uninst_item3"]        = "Plan de energía restaurado al predeterminado",
            ["uninst_warn"]         = "Esta acción no se puede deshacer. Windows volverá a su plan de energía predeterminado.",
            ["uninst_progress"]     = "Eliminando…",
            ["uninst_done"]         = "VoltManager se ha desinstalado.",
            ["btn_back"]            = "← Atrás",
            ["btn_next"]            = "Siguiente →",
            ["btn_install"]         = "Instalar",
            ["btn_cancel"]          = "Cancelar",
            ["btn_close"]           = "Cerrar",
            ["btn_finish"]          = "Finalizar",
            ["btn_uninstall"]       = "Desinstalar",
            ["status_kill"]         = "Cerrando VoltManager en ejecución…",
            ["status_migrate"]      = "Eliminando instalación anterior…",
            ["status_extract"]      = "Extrayendo archivos…",
            ["status_webview"]      = "Instalando WebView2 Runtime…",
            ["status_shortcuts"]    = "Creando accesos directos…",
            ["status_startup"]      = "Configurando inicio con Windows…",
            ["status_registry"]     = "Registrando en el sistema…",
            ["status_uninst_kill"]  = "Cerrando VoltManager…",
            ["status_uninst_files"] = "Eliminando archivos…",
            ["status_uninst_reg"]   = "Eliminando de Programas…",
        };

        private static string _language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        public static string Language => _language;

        public static string T(string key)
        {
            var dict = ResolveDictionary(_language);
            return dict.TryGetValue(key, out var v) ? v : key;
        }

        private static Dictionary<string, string> ResolveDictionary(string lang)
        {
            if (lang.StartsWith("it", StringComparison.OrdinalIgnoreCase)) return It;
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Zh;
            if (lang.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return Es;
            return En;
        }

        /// <summary>
        /// Resolves the language with precedence: explicit --lang arg > saved settings > OS culture > English.
        /// Sets the internal _language field so T() works correctly.
        /// </summary>
        public static string Initialize(string? explicitLang = null, string? settingsLang = null)
        {
            string resolved;
            // 1. Explicit --lang argument
            if (!string.IsNullOrEmpty(explicitLang) && IsSupported(explicitLang))
            {
                resolved = Normalize(explicitLang);
            }
            // 2. Saved settings language
            else if (!string.IsNullOrEmpty(settingsLang) && IsSupported(settingsLang))
            {
                resolved = Normalize(settingsLang);
            }
            // 3. OS culture
            else
            {
                var osName = CultureInfo.CurrentUICulture.Name;
                if (osName.StartsWith("es", StringComparison.OrdinalIgnoreCase)) resolved = "es";
                else if (osName.StartsWith("it", StringComparison.OrdinalIgnoreCase)) resolved = "it";
                else if (osName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) resolved = "zh";
                else resolved = "en";
            }

            // Update the readonly field via reflection (pragmatic for setup tool).
            typeof(I18n).GetField("_language",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, resolved);
            return resolved;
        }

        /// <summary>
        /// Reads the language from %APPDATA%\VoltManager\settings.json, returns null if not found/unreadable.
        /// </summary>
        public static string? TryReadSavedLanguage()
        {
            try
            {
                var settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VoltManager", "settings.json");
                if (!File.Exists(settingsPath)) return null;
                var match = Regex.Match(
                    File.ReadAllText(settingsPath),
                    "\"language\"\\s*:\\s*\"(?<lang>[^\"]*)\"",
                    RegexOptions.IgnoreCase);
                if (!match.Success) return null;
                var val = match.Groups["lang"].Value;
                return string.IsNullOrEmpty(val) ? null : val;
            }
            catch { /* best-effort */ }
            return null;
        }

        private static bool IsSupported(string? code)
        {
            var n = Normalize(code);
            return n == "it" || n == "en" || n == "zh" || n == "es";
        }

        private static string Normalize(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "en";
            var trimmed = code!.Trim().Replace('_', '-');
            if (trimmed.StartsWith("it", StringComparison.OrdinalIgnoreCase)) return "it";
            if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (trimmed.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "es";
            return "";
        }
    }
}
