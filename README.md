<div align="center">

<img src="VoltManager_logo.png" alt="Logo VoltManager" width="140" />

# VoltManager

**Controllo semplice dei consumi, della batteria e delle prestazioni del PC Windows.**

VoltManager cambia piano energetico quando serve, mostra l’utilizzo hardware in tempo reale e automatizza le operazioni più comuni senza costringerti a cercare nelle impostazioni avanzate di Windows.

![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows&logoColor=white)
![Architecture](https://img.shields.io/badge/Architecture-x64-555555)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![UI](https://img.shields.io/badge/UI-WPF%20%2B%20WebView2-2C8EBB)
![Languages](https://img.shields.io/badge/Lingue-IT%20%2F%20EN%20%2F%20ES%20%2F%20ZH-orange)

</div>

---

## Perché usarlo

Windows offre diversi piani energetici, ma cambiarli manualmente nel momento giusto è scomodo. VoltManager li rende accessibili dalla finestra principale, dall’area di notifica e dalla jump list della barra delle applicazioni.

Esempi:

- scolleghi il portatile dalla corrente: passa al piano di risparmio configurato;
- ricolleghi l’alimentatore: ripristina il piano previsto;
- avvii un gioco o un’app pesante: applica il profilo associato;
- guardi un film o esegui un download: mantiene il PC attivo senza cambiare definitivamente i timeout di Windows;
- chiudi la finestra: automazioni, pianificazioni e widget continuano a funzionare nell’area di notifica.

Le funzioni automatiche sono configurabili e possono essere disattivate.

## Funzioni principali

### Uso quotidiano

- **Piani energetici rapidi** — Risparmio energia, Bilanciato e Prestazioni dalla Home, dall’area di notifica o dalla jump list.
- **Dashboard hardware** — CPU, GPU, RAM, disco, temperature, frequenze, ventole e processi principali, quando disponibili.
- **Cambio piano in base all’alimentazione** — comportamento separato per rete elettrica e batteria.
- **Modalità gaming** — mantiene il piano Prestazioni e propone il ritorno alla modalità automatica quando il carico diminuisce.
- **Mantieni PC attivo** — impedisce la sospensione automatica durante film, presentazioni o attività lunghe.
- **Widget desktop** — orologio, calendario, utilizzo, temperature, alimentazione e piani energetici.
- **Interfaccia multilingua** — italiano, inglese, spagnolo e cinese semplificato.

### Automazione e strumenti avanzati

- **Regole CPU** — soglie, durata, media mobile e cooldown anti-rimbalzo.
- **Profili per applicazione** — associa un eseguibile a un piano e ripristina il precedente alla chiusura.
- **Rilevamento app pesanti** — usa preferenze GPU, percorsi di installazione e consumo di memoria.
- **Pulizia memoria** — mostra memoria in uso, standby e libera; consente la pulizia manuale o programmata della standby list.
- **Azioni pianificate** — spegnimento, riavvio o sospensione dopo un intervallo oppure ogni giorno a un orario definito.
- **App di avvio** — consulta, abilita o disabilita le voci di avvio Windows; gestisce le voci create da VoltManager.
- **Parametri avanzati** — stato minimo/massimo CPU, boost e gestione energetica PCI Express.
- **Aggiornamenti integrati** — canali stable, preview e dev; installazione silenziosa opzionale, rinvio e salto versione.

## Requisiti di sistema

| Componente | Minimo | Raccomandato |
|---|---|---|
| Sistema operativo | Windows 10 64 bit 1809 | Windows 11 64 bit |
| CPU | x64 dual-core | 4 o più core logici |
| RAM | 4 GB | 8 GB o più |
| Display | 640×480 | 1280×720 o superiore |
| Spazio libero | 250 MB | 500 MB |
| WebView | Microsoft Edge WebView2 Runtime | Versione Evergreen aggiornata |
| Permessi | Account amministratore | Account amministratore |

### Adattamento automatico alle risorse

Il ridimensionamento dell’interfaccia è sempre attivo, indipendentemente dalla potenza del PC. La finestra supporta dimensioni da 640×480; sotto 900, 700 e 560 pixel layout, sidebar e griglie si adattano automaticamente.

Gli effetti visivi seguono invece la capacità hardware:

- **Lite** — meno di 8 GB RAM oppure fino a 2 core logici: animazioni, blur ed effetti costosi disattivati; processi aggiornati ogni 10 secondi.
- **Bilanciato** — meno di 16 GB RAM oppure fino a 4 core logici: effetti alleggeriti; processi aggiornati ogni 6 secondi.
- **Completo** — almeno 16 GB RAM e più di 4 core logici: esperienza visiva completa; processi aggiornati ogni 3 secondi.
- **Pressione RAM** — modalità Lite temporanea dall’85% di utilizzo; ritorno sotto il 75%.

Automazioni energetiche, monitoraggio host e widget restano operativi in ogni profilo.

## Installazione

### Installer, consigliato

1. Apri la pagina [Releases](../../releases).
2. Scarica l’ultimo `VoltManagerSetup-*.exe`.
3. Avvia l’installer e conferma il controllo account utente.
4. Apri VoltManager dal menu Start.

L’installer distribuisce l’applicazione self-contained e include il bootstrapper WebView2 per i PC privi del runtime, inclusi alcuni sistemi LTSC.

### Portable

1. Scarica `VoltManager-portable-*-win-x64.zip` dalla pagina Releases.
2. Estrai l’intero archivio in una cartella scrivibile.
3. Avvia `VoltManager.exe`.

Non eseguire l’app direttamente dall’archivio ZIP. Il pacchetto portable è self-contained; include il bootstrapper WebView2 da utilizzare se il runtime non è già installato.

## Primo avvio

1. Scegli lingua e tema.
2. Verifica i piani energetici rilevati.
3. Configura il comportamento con alimentatore collegato e a batteria.
4. Lascia attiva la modalità automatica oppure seleziona manualmente un piano.
5. Abilita widget, avvio con Windows e automazioni solo se necessari.

Al primo avvio VoltManager può ripristinare i piani Windows mancanti tramite `powercfg -duplicatescheme`; i GUID generati vengono salvati nelle impostazioni locali.

## Comportamento nell’area di notifica

Per impostazione predefinita, chiudere la finestra riduce VoltManager nell’area di notifica. In questo stato:

- il rendering della dashboard e i polling visivi si fermano;
- WebView2 riduce il proprio obiettivo di memoria;
- regole CPU, profili per app, cambio piano, widget e azioni pianificate restano attivi.

Usa **Esci** dal menu dell’icona per terminare completamente l’applicazione. Le azioni pianificate relative richiedono che VoltManager resti in esecuzione.

## Privacy, rete e privilegi

- Impostazioni e dati operativi restano nel PC; non è richiesto un account.
- Dashboard, automazioni e cambio piano funzionano localmente.
- La connessione Internet viene usata soltanto per controllare e scaricare aggiornamenti da GitHub, se la funzione è attiva.
- L’app richiede privilegi amministrativi perché usa le API e gli strumenti Windows necessari per gestire piani energetici, standby list e azioni di sistema.
- L’output di `powercfg` viene interpretato tramite GUID, non tramite nomi localizzati.

## Risoluzione dei problemi

### La finestra è vuota o WebView2 manca

Installa o aggiorna [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), quindi riavvia VoltManager. Gli artefatti ufficiali includono anche il bootstrapper.

### Una metrica mostra N/D

Alcuni contatori GPU, temperature, ventole o dati batteria non sono esposti da tutti i driver e firmware. Le altre funzioni continuano a operare.

### Il piano non cambia

- verifica di aver confermato il prompt amministratore;
- disattiva temporaneamente eventuali utility OEM che impongono un proprio profilo;
- usa la modalità automatica per rimuovere un override manuale;
- riavvia VoltManager per ripetere il controllo dei piani Windows.

### L’app resta aperta dopo la chiusura della finestra

È il comportamento **Chiudi nell’area di notifica**. Disabilitalo nelle impostazioni oppure scegli **Esci** dal menu dell’icona.

### File di configurazione e log

- Impostazioni: `%APPDATA%\VoltManager\settings.json`
- Log applicativo: `%APPDATA%\VoltManager\logs\voltmanager.log`
- Eventi supervisor: `%APPDATA%\VoltManager\logs\supervisor-events.jsonl`
- Crash report: `%APPDATA%\VoltManager\crashes\crash-*.json`

Per policy di riavvio, crash-loop recovery e rollback operativo, consulta [Automatic crash restart](docs/reliability/automatic-restart.md).

---

## Per sviluppatori

### Stack e struttura

- **App principale:** WPF su .NET 8, WebView2, SPA locale in `src/VoltManager/wwwroot`.
- **Supervisor:** processo esterno .NET 8 in `src/VoltManager.Supervisor`, responsabile del restart limitato dopo crash.
- **Installer:** WPF `net48`, progetto `src/VoltManager.Setup`.
- **Helper jump list:** `net48`, progetto `src/VoltManager.PlanSwitch`, eseguito come utente non elevato.
- **Test:** xUnit in `tests/VoltManager.Tests`.
- **Soluzione:** `VoltManager.sln` contiene applicazione, supervisor, installer, helper e test.

### Prerequisiti di sviluppo

- Windows 10/11 x64.
- .NET 8 SDK.
- PowerShell 5.1 o superiore.
- Node.js solo per il controllo sintattico dei moduli JavaScript.
- Connessione Internet per restore NuGet e download del bootstrapper WebView2.

### Build

```powershell
# Compilazione della soluzione

dotnet build VoltManager.sln -c Release

# Test automatici

dotnet test tests\VoltManager.Tests\VoltManager.Tests.csproj -c Release

# Portable self-contained e installer
.\build.ps1

# Solo portable self-contained
.\build.ps1 -SkipInstaller
```

`build.ps1` esegue i test, pubblica app e supervisor x64 self-contained in `publish\`, compila `VoltManagerPlanSwitch.exe`, scarica il bootstrapper WebView2 se assente e genera l’installer in `dist\`.

La suite reliability usa tempo e processi simulati in modo deterministico per verificare backoff, jitter, crash-loop breaker, reset stabile, istanza singola, cleanup limitato e stato persistente. Il runbook documenta i test manuali Windows ancora necessari.

### Verifiche disponibili

```powershell
# Sintassi di tutti i moduli JavaScript
Get-ChildItem src\VoltManager\wwwroot\js\*.js |
    ForEach-Object { node --check $_.FullName }
```

Non è configurato un linter C# separato: `dotnet build` esegue compilazione e type checking. I vecchi riferimenti a script di benchmark e self-check sono stati rimossi perché tali file non sono presenti nella repository.

### Distribuzione

La workflow `.github/workflows/release.yml` crea automaticamente release per:

- `main` — stable;
- `Preview` — beta;
- `Dev` — alpha.

Ogni release contiene:

- `VoltManagerSetup-<versione>.exe`;
- `VoltManager-portable-<versione>-win-x64.zip`.

### Note tecniche

- Istanza applicativa singola tramite mutex ed eventi nominati.
- Supervisor esterno singolo con backoff esponenziale, jitter, budget temporale e crash-loop breaker persistente.
- Jump list inoltrata tramite `VoltManagerPlanSwitch.exe` senza secondo prompt UAC quando l’app è già aperta.
- Avvio con Windows tramite attività pianificata `VoltManagerAutostart` con privilegi elevati.
- Chiusura nell’area di notifica configurabile.
- Voci di avvio personalizzate registrate in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; VoltManager modifica o rimuove solo quelle create dalla propria interfaccia.
- Stato delle voci Run e Startup folder gestito tramite `StartupApproved`, coerentemente con Gestione attività.
