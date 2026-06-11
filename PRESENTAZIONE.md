# VoltManager

VoltManager e' una app desktop Windows per gestire i piani energetici, monitorare CPU/GPU/RAM/Disco in tempo reale e cambiare profilo in automatico in base al carico del PC.

## Download

Scarica sempre l'ultima versione dalla pagina release:

https://github.com/Albix4563/power_efficency/releases/latest

File da scaricare:

```text
VoltManagerSetup-<versione>.exe
```

Per vedere anche le versioni vecchie, apri lo storico release:

https://github.com/Albix4563/power_efficency/releases

## Requisiti

- Windows x64.
- Permessi amministratore.
- WebView2 Runtime. L'installer lo installa in automatico se manca.

## Installazione

1. Apri la pagina `releases/latest`.
2. Scarica `VoltManagerSetup-<versione>.exe`.
3. Avvia l'installer come amministratore.
4. Apri VoltManager dal menu Start o dal collegamento desktop.

## Uso App

### Home

La Home mostra stato del PC e selezione rapida del piano energetico:

- Power Efficiency: minori consumi.
- Balanced: uso normale.
- Performance: prestazioni massime.

### Task Manager

Mostra uso in tempo reale di:

- CPU.
- GPU.
- RAM.
- Disco.

I dati si aggiornano circa ogni secondo.

### Gestione Energetica

Qui configuri regole automatiche. VoltManager osserva il carico del PC e cambia piano energetico quando le soglie restano attive per il tempo impostato.

Esempio:

- basso carico -> Power Efficiency.
- carico medio -> Balanced.
- carico alto -> Performance.

### Settings

Da Settings puoi:

- controllare aggiornamenti da GitHub;
- cambiare repository aggiornamenti;
- configurare avvio automatico con Windows;
- scegliere comportamento quando chiudi la finestra.

## Aggiornamenti

Ogni nuova versione pubblicata su GitHub include un installer `.exe` scaricabile. Per aggiornare:

1. Apri `releases/latest`.
2. Scarica il nuovo `VoltManagerSetup-<versione>.exe`.
3. Installa sopra la versione esistente.

## Build Locale

Prerequisiti:

- .NET 8 SDK.
- Inno Setup 6, solo per creare installer.

Comandi:

```powershell
.\build.ps1
```

Output:

```text
dist\VoltManagerSetup-<versione>.exe
publish\
```
