# VoltManager

App desktop Windows per la gestione dei piani energetici con monitoraggio hardware in tempo reale, automazione in background e aggiornamenti da GitHub.

- **UI**: WPF (.NET 8) + WebView2, SPA offline in `src/VoltManager/wwwroot` (Tailwind compilato, font vendorizzati — nessuna connessione richiesta).
- **Privilegi**: l'app richiede amministratore (manifest `requireAdministrator`) perché cambia i piani energetici in modo continuo.

## Funzionalità

1. **Setup**: al primo avvio verifica i piani energetici predefiniti di Windows; se mancanti li ripristina con `powercfg -duplicatescheme` (i nuovi GUID vengono salvati in `planGuidMap`).
2. **Task Manager**: stress CPU/GPU/RAM/Disco in tempo reale (performance counters, 1s). GPU = somma contatori "GPU Engine" engtype_3D; se assenti mostra N/D.
3. **Cambio piano manuale**: selettore Power Efficiency / Balanced / Performance nella Home.
4. **Automazione background**: regole configurabili (soglia %, minuti) in Gestione Energetica; media mobile 5 campioni, priorità a soglia più alta, cooldown anti-flapping 15s. Attiva anche con finestra nascosta (tray).
5. **Aggiornamenti**: Settings → controlla `releases/latest` + commit del branch main del repo configurato in `%APPDATA%\VoltManager\settings.json` (`updateRepo`).

## Build

Prerequisiti: .NET 8 SDK, Inno Setup 6 (per l'installer).

```powershell
.\build.ps1                # test + publish + installer -> dist\VoltManagerSetup-1.0.0.exe
.\build.ps1 -SkipInstaller # solo portable -> publish\
```

CSS: se modifichi `index.html`/classi Tailwind, ricompila con:

```powershell
cd .build_tools
.\tailwindcss.exe -c tailwind.config.js -i tailwind.input.css -o ..\src\VoltManager\wwwroot\css\app.css --minify
```

## Test

```powershell
dotnet test -c Release                                  # 28 unit test (engine, parser powercfg, settings, semver)
powershell -File scripts\smoke_test.ps1                 # smoke test installazione (RICHIEDE ELEVAZIONE)
```

Lo smoke test esegue: install silenziosa → avvio → verifica processo/WebView2 → switch piano → uninstall → ripristino piano originale. Risultati in `scripts\smoke_test_results.txt`.

## Distribuzione

- `dist\VoltManagerSetup-1.0.0.exe` — installer (include bootstrapper WebView2 per macchine senza runtime, es. LTSC).
- `publish\` — cartella portable self-contained (richiede WebView2 Runtime già presente sul PC di destinazione).

## Note tecniche

- Output `powercfg` analizzato solo per GUID (mai per nome: localizzato).
- Avvio automatico con Windows: scheduled task `VoltManagerAutostart` (`/rl HIGHEST`), perché la chiave Run è bloccata per app elevate.
- Singola istanza: mutex + EventWaitHandle (la seconda istanza riporta in primo piano la prima).
- Chiusura → riduzione nell'area di notifica (configurabile in Settings).
