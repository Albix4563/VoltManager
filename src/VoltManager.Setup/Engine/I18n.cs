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

        private static bool _isIt = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .StartsWith("it", System.StringComparison.OrdinalIgnoreCase);

        public static string T(string key)
        {
            var dict = _isIt ? It : En;
            return dict.TryGetValue(key, out var v) ? v : key;
        }
    }
}
