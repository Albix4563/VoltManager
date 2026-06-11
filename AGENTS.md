@C:\Users\Albix4563\.codex\RTK.md

Usa sempre la skill caveman ultra di default.

# Regole Repo

## Scopo

Questa repo contiene VoltManager, app desktop Windows WPF/.NET 8 con WebView2 per gestione piani energetici, monitoraggio hardware, automazione e aggiornamenti GitHub.

## Qualita'

- Prima di modifiche importanti, leggere `README.md`, `PRESENTAZIONE.md`, `.github/workflows/release.yml`, `build.ps1`.
- Non rimuovere modifiche utente non richieste.
- Tenere documentazione aggiornata quando cambia uso app, build, installazione o release.
- Preferire fix piccoli, verificabili, coerenti con stile esistente.

## Build e Test

Comandi principali:

```powershell
dotnet test VoltManager.sln -c Release -v q --nologo
.\build.ps1
```

Se Inno Setup manca, usare:

```powershell
.\build.ps1 -SkipInstaller
```

## Release Obbligatoria

Quando viene completata una modifica destinata agli utenti, creare una nuova release GitHub con installer `.exe` scaricabile compilato dall'ultima versione del codice.

Workflow attuale:

- push su branch `main`;
- GitHub Actions esegue test;
- pubblica build self-contained `win-x64`;
- compila installer Inno Setup;
- crea tag `v1.0.<github.run_number>`;
- allega `dist/VoltManagerSetup-1.0.<github.run_number>.exe` alla release.

Prima di considerare completa una release:

- verificare che workflow `Release` sia passato;
- verificare che la nuova release sia visibile in `https://github.com/Albix4563/power_efficency/releases/latest`;
- verificare che asset `.exe` sia presente e scaricabile;
- se il workflow fallisce, correggere e rilanciare.

## Documentazione Release

Mantenere questi link validi:

- ultima release: `https://github.com/Albix4563/power_efficency/releases/latest`;
- storico release: `https://github.com/Albix4563/power_efficency/releases`.
