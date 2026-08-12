<p align="center">
  <img src="src/VoltManager/wwwroot/images/project_logo.png" alt="Logo VoltManager" width="140" />
</p>

<h1 align="center">VoltManager</h1>

<p align="center"><strong>Gestisci batteria, prestazioni e consumi del PC Windows in un unico posto.</strong></p>

<p align="center">
  Cambia piano energetico in un clic, automatizza le situazioni tipiche di un portatile e tieni d’occhio CPU, GPU, temperature e memoria — senza scavare nelle impostazioni avanzate di Windows.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows&logoColor=white" alt="Platform" />
  <img src="https://img.shields.io/badge/Architecture-x64-555555" alt="Architecture" />
  <img src="https://img.shields.io/badge/Lingue-IT%20%2F%20EN%20%2F%20ES%20%2F%20ZH-orange" alt="Languages" />
  <img src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white" alt=".NET" />
  <a href="https://voltmanager.freebuff.app/"><img src="https://img.shields.io/badge/Sito%20ufficiale-voltmanager.freebuff.app-512BD4?logo=globe&logoColor=white" alt="Sito ufficiale" /></a>
</p>

<p align="center">
  <a href="#sito-ufficiale">Sito ufficiale</a> ·
  <a href="#installazione">Installazione</a> ·
  <a href="#cosa-puoi-fare">Cosa puoi fare</a> ·
  <a href="#primo-avvio">Primo avvio</a> ·
  <a href="#risoluzione-dei-problemi">Problemi comuni</a>
</p>

---

## Sito ufficiale

Il sito ufficiale dell'app è **[https://voltmanager.freebuff.app/](https://voltmanager.freebuff.app/)** — fonte ufficiale per download, informazioni e novità su VoltManager.

---

## A chi è utile

VoltManager è pensato per chi usa un PC Windows (soprattutto un **portatile**) e vuole:

- **far durare di più la batteria** quando sei in mobilità;
- **avere più prestazioni** quando colleghi l’alimentatore o avvii un gioco;
- **vedere in tempo reale** quanto sta lavorando l’hardware;
- **automatizzare** le operazioni che di solito fai a mano nelle impostazioni di Windows.

Non serve essere esperti di sistemi: le funzioni automatiche sono opzionali e si possono disattivare in qualsiasi momento.

---

## Cosa puoi fare

### In pratica, giorno per giorno

| Situazione | Cosa fa VoltManager |
|---|---|
| Scolleghi il caricabatterie | Può passare da solo al piano di risparmio che hai scelto |
| Ricolleghi l’alimentatore | Ripristina il piano previsto per la rete elettrica |
| Avvii un gioco o un’app pesante | Applica il profilo o la modalità gaming associati |
| Guardi un film o fai un download lungo | Tiene il PC attivo senza cambiare in modo permanente i timeout di Windows |
| Chiudi la finestra | L’app resta nell’area di notifica e continua a lavorare in background |

### Funzioni principali

**Controllo energia**

- **Piani rapidi** — Risparmio, Bilanciato e Prestazioni dalla Home, dall’icona nell’area di notifica o dalla jump list della barra delle applicazioni.
- **Comportamento su alimentazione / batteria** — regole separate per quando sei collegato alla corrente e quando sei a batteria.
- **Modalità gaming** — mantiene le prestazioni elevate e ti propone di tornare alla modalità automatica quando il carico cala.
- **Mantieni PC attivo** — evita sospensione e spegnimento dello schermo durante film, presentazioni o attività lunghe.

**Monitoraggio e desktop**

- **Dashboard hardware** — CPU, GPU, RAM, disco, temperature, frequenze, ventole e processi principali (quando i driver le espongono).
- **Widget desktop** — orologio, calendario, utilizzo, temperature, alimentazione e piani energetici.
- **Interfaccia multilingua** — italiano, inglese, spagnolo e cinese semplificato.

**Automazioni e strumenti**

- **Profili per app** — associa un programma a un piano energetico e ripristina quello precedente alla chiusura.
- **Regole CPU** — cambia piano quando il processore resta sotto o sopra certe soglie (con cooldown anti-rimbalzo).
- **Rilevamento app pesanti** — riconosce carichi impegnativi e può applicare il profilo adatto.
- **Pulizia memoria** — mostra memoria in uso, standby e libera; pulizia manuale o programmata della standby list.
- **Azioni pianificate** — spegnimento, riavvio o sospensione dopo un intervallo o ogni giorno a un orario.
- **App di avvio** — consulta, abilita o disabilita le voci di avvio di Windows.
- **Parametri avanzati** — stato minimo/massimo CPU, boost e gestione energetica PCI Express (per chi li conosce).
- **Aggiornamenti integrati** — canali stable, preview e dev, con installazione silenziosa opzionale.

---

## Installazione

> 💡 **Download ufficiale:** la pagina ufficiale dell'app è [voltmanager.freebuff.app](https://voltmanager.freebuff.app/); i file firmati sono disponibili anche dalla pagina [Releases](../../releases).

### Installer (consigliato)

1. Apri la pagina [Releases](../../releases).
2. Scarica l’ultimo file `VoltManagerSetup-*.exe`.
3. Avvia l’installer e conferma il controllo account utente (UAC).
4. Apri **VoltManager** dal menu Start.

L’installer include tutto il necessario e, se manca, può preparare anche il runtime WebView2 (utile su alcuni PC LTSC o molto “puliti”).

### Versione portable

1. Scarica `VoltManager-portable-*-win-x64.zip` dalla pagina Releases.
2. Estrai **tutto** l’archivio in una cartella in cui puoi scrivere.
3. Avvia `VoltManager.exe`.

> Non avviare l’app direttamente dallo ZIP: estraila prima in una cartella.

---

## Requisiti di sistema

| | Minimo | Consigliato |
|---|---|---|
| Sistema | Windows 10 64 bit (1809+) | Windows 11 64 bit |
| Processore | x64 dual-core | 4 o più core logici |
| Memoria | 4 GB RAM | 8 GB o più |
| Schermo | 640×480 | 1280×720 o superiore |
| Spazio | ~250 MB | ~500 MB |
| Extra | [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | Versione aggiornata |
| Account | Amministratore | Amministratore |

**Perché serve l’amministratore?**  
VoltManager gestisce piani energetici, alcune operazioni di sistema e strumenti Windows che richiedono privilegi elevati.

**PC meno potenti**  
L’interfaccia si adatta da sola: su macchine con poca RAM o pochi core riduce effetti e frequenza di aggiornamento, ma automazioni, monitoraggio e widget restano disponibili.

---

## Primo avvio

1. Scegli **lingua** e **tema**.
2. Controlla i **piani energetici** rilevati (se ne manca qualcuno, VoltManager può provarne a ripristinare i predefiniti di Windows).
3. Imposta cosa fare **con alimentatore** e **a batteria**.
4. Lascia attiva la **modalità automatica**, oppure scegli un piano a mano.
5. Attiva solo ciò che ti serve: widget, avvio con Windows, regole e automazioni.

Suggerimento: inizia con le impostazioni base. Le automazioni avanzate si possono aggiungere dopo, quando hai chiaro cosa ti serve.

---

## Uso quotidiano

### Area di notifica

Di default, chiudere la finestra **non chiude** VoltManager: l’app resta nell’area di notifica (vicino all’orologio).

Mentre è ridotta a icona:

- la dashboard smette di aggiornare i grafici (meno consumo);
- regole, profili app, cambio piano, widget e azioni pianificate **continuano a funzionare**.

Per uscire del tutto: clic destro sull’icona → **Esci**.  
Le azioni pianificate funzionano solo se VoltManager è in esecuzione (anche solo in background).

### Dove cambiare piano in fretta

- **Home** dell’app  
- **Menu dell’icona** nell’area di notifica  
- **Jump list** (clic destro sull’icona di VoltManager nella barra delle applicazioni)

---

## Privacy e rete

- Tutto resta **sul tuo PC**: non serve un account VoltManager.
- Dashboard, automazioni e cambio piano funzionano **offline**.
- Internet viene usato solo per **controllare e scaricare aggiornamenti** da GitHub, se la funzione è attiva.
- Nessun invio di telemetria o dati di utilizzo a servizi esterni (oltre agli aggiornamenti che scegli tu).

---

## Risoluzione dei problemi

### La finestra è vuota

Installa o aggiorna [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), poi riavvia VoltManager.  
Gli installer e i pacchetti ufficiali includono anche un bootstrapper se il runtime non è presente.

### Una metrica mostra “N/D”

Alcune temperature, ventole, dati GPU o batteria non sono disponibili su tutti i PC (dipende da driver e firmware). Le altre funzioni continuano a funzionare normalmente.

### Il piano energetico non cambia

- conferma il prompt di amministratore all’avvio;
- disattiva temporaneamente utility OEM (Lenovo Vantage, MyASUS, Armoury Crate, ecc.) che possono imporre un proprio profilo;
- attiva la **modalità automatica** per togliere un override manuale;
- riavvia VoltManager.

### L’app resta aperta dopo aver chiuso la finestra

È il comportamento previsto: **chiudi nell’area di notifica**.  
Puoi disabilitarlo nelle impostazioni, oppure usare **Esci** dal menu dell’icona.

### Dove sono impostazioni e log

| Cosa | Percorso |
|---|---|
| Impostazioni | `%APPDATA%\VoltManager\settings.json` |
| Log | `%APPDATA%\VoltManager\logs\voltmanager.log` |
| Eventi di ripristino | `%APPDATA%\VoltManager\logs\supervisor-events.jsonl` |
| Report di crash | `%APPDATA%\VoltManager\crashes\crash-*.json` |

Policy di riavvio automatico e recovery: [Automatic crash restart](docs/reliability/automatic-restart.md).

---

## Per sviluppatori

Dettagli tecnici per chi contribuisce o compila dal codice sorgente.

### Stack

| Componente | Tecnologia |
|---|---|
| App principale | WPF · .NET 8 · WebView2 · UI in `src/VoltManager/wwwroot` |
| Supervisor | Processo esterno .NET 8 (`src/VoltManager.Supervisor`) per restart limitato dopo crash |
| Installer | WPF `net48` (`src/VoltManager.Setup`) |
| Jump list helper | `net48` (`src/VoltManager.PlanSwitch`), eseguito non elevato |

### Prerequisiti

- Windows 10/11 x64  
- .NET 8 SDK  
- PowerShell 5.1+  
- Node.js (solo per `node --check` sui moduli JS)  
- Rete per restore NuGet e bootstrapper WebView2  

### Build

```powershell
# Soluzione
dotnet build VoltManager.sln -c Release

# Portable self-contained + installer
.\build.ps1

# Solo portable
.\build.ps1 -SkipInstaller
```

### Verifica JavaScript

```powershell
Get-ChildItem src\VoltManager\wwwroot\js\*.js |
    ForEach-Object { node --check $_.FullName }
```

La compilazione C# e il type checking passano da `dotnet build` (nessun linter C# separato in repo).

### Note operative

- Istanza singola (mutex + eventi nominati).
- Supervisor con backoff, jitter, budget temporale e protezione anti crash-loop.
- Jump list tramite `VoltManagerPlanSwitch.exe` senza secondo UAC se l’app è già aperta.
- Avvio con Windows: attività pianificata `VoltManagerAutostart` con privilegi elevati.
- Voci di avvio personalizzate solo in `HKCU\...\Run` create da VoltManager; stato allineato a `StartupApproved` come in Gestione attività.
- I piani energetici sono gestiti tramite GUID (`powercfg`), non tramite nomi localizzati.
