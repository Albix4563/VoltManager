using System.Collections.Generic;
using System.Globalization;

namespace VoltManager.Setup.Engine
{
    public static class I18n
    {
        private static readonly Dictionary<string, string> It = new Dictionary<string, string>
        {
            ["welcome_title"]       = "Benvenuto",
            ["welcome_subtitle"]    = "VoltManager ottimizza automaticamente il piano energetico del tuo PC in base all'utilizzo CPU, riducendo i consumi senza compromettere le prestazioni.",
            ["welcome_info"]        = "Clicca Avanti per configurare l'installazione.",
            ["options_title"]       = "Opzioni di installazione",
            ["options_folder"]      = "Cartella di installazione",
            ["options_browse"]      = "Sfoglia…",
            ["options_desktop"]     = "Crea collegamento sul desktop",
            ["options_startup"]     = "Avvia con Windows",
            ["options_launch"]      = "Avvia VoltManager al termine dell'installazione",
            ["progress_title"]      = "Installazione in corso…",
            ["progress_wait"]       = "Attendere, non chiudere questa finestra.",
            ["done_title"]          = "Installazione completata",
            ["done_title_err"]      = "Installazione non riuscita",
            ["done_sub"]            = "VoltManager è stato installato correttamente.",
            ["done_launch"]         = "Avvia VoltManager",
            ["uninst_title"]        = "Disinstallazione",
            ["uninst_confirm"]      = "Rimuovere VoltManager dal computer?",
            ["uninst_sub"]          = "Tutti i file dell'applicazione verranno eliminati.",
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
            ["options_title"]       = "Installation options",
            ["options_folder"]      = "Installation folder",
            ["options_browse"]      = "Browse…",
            ["options_desktop"]     = "Create desktop shortcut",
            ["options_startup"]     = "Start with Windows",
            ["options_launch"]      = "Launch VoltManager after installation",
            ["progress_title"]      = "Installing…",
            ["progress_wait"]       = "Please wait, do not close this window.",
            ["done_title"]          = "Installation complete",
            ["done_title_err"]      = "Installation failed",
            ["done_sub"]            = "VoltManager has been installed successfully.",
            ["done_launch"]         = "Launch VoltManager",
            ["uninst_title"]        = "Uninstall",
            ["uninst_confirm"]      = "Remove VoltManager from your computer?",
            ["uninst_sub"]          = "All application files will be deleted.",
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
            ["options_title"]       = "安装选项",
            ["options_folder"]      = "安装文件夹",
            ["options_browse"]      = "浏览…",
            ["options_desktop"]     = "创建桌面快捷方式",
            ["options_startup"]     = "随 Windows 启动",
            ["options_launch"]      = "安装完成后启动 VoltManager",
            ["progress_title"]      = "正在安装…",
            ["progress_wait"]       = "请稍候，不要关闭此窗口。",
            ["done_title"]          = "安装完成",
            ["done_title_err"]      = "安装失败",
            ["done_sub"]            = "VoltManager 已成功安装。",
            ["done_launch"]         = "启动 VoltManager",
            ["uninst_title"]        = "卸载",
            ["uninst_confirm"]      = "要从电脑中移除 VoltManager 吗？",
            ["uninst_sub"]          = "所有应用程序文件都将被删除。",
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

        private static readonly string _language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        public static string T(string key)
        {
            var dict = _language.StartsWith("it", System.StringComparison.OrdinalIgnoreCase)
                ? It
                : _language.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase)
                    ? Zh
                    : En;
            return dict.TryGetValue(key, out var v) ? v : key;
        }
    }
}
