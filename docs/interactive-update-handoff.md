# Aggiornamento interattivo VoltManager

## Contratto operativo

L'host continua a scaricare e verificare il Setup tramite la pipeline SHA-256 esistente.
Il Setup di una release che include `VoltManagerUpdateUiProtocol-v1.txt` dichiara
il supporto al protocollo interattivo. Il workflow di pubblicazione allega
il marker insieme all'installer e a `SHA256SUMS`. Per le release prive del
marker, l'host usa gli argomenti `/update --pid --lang` senza handshake.

Nel nuovo protocollo, la palette RGB validata e l'ID di sessione casuale
vengono passati attraverso `--update-palette` e `--update-session`.
L'ID è un GUID di 32 caratteri esadecimali. Lo snapshot contiene esattamente
10 colori `#RRGGBB`, nell'ordine previsto da `ThemeWebPalette`.
Il Setup applica la palette prima di mostrare la finestra; in assenza
dello snapshot usa il tema salvato, mentre un argomento esplicitamente
presente ma malformato causa il rifiuto del nuovo handoff.

Il passaggio avviene attraverso eventi Windows `Local\VoltManager_Update_...`
scoped alla sessione utente, con ACL per utente corrente, Administrators e
SYSTEM. Non sono utilizzati socket, servizi o nomi di sessione statici.

1. L'host verifica il download e crea gli eventi `Visible`, `Proceed`,
   `Accepted`, `Abort` e `Ready`.
2. Il Setup apre gli eventi e presenta la propria `UpdateWindow`.
   Solo `ContentRendered` segnala `Visible`.
3. L'host invia `Proceed`. Il Setup verifica l'assenza di annullamenti,
   disabilita la chiusura della finestra e segnala `Accepted`.
4. Solo dopo `Accepted` l'host chiude VoltManager. Il coordinatore attende
   la terminazione reale del PID e verifica nuovamente eventuali abort
   **prima** della pulizia della cache e della sostituzione dei file.
5. Il Setup esegue validazione, estrazione e finalizzazione tramite gli
   step esistenti; segnala il progresso nelle quattro fasi della finestra.
6. Dopo l'installazione, `RelaunchUpdatedApp` esegue VoltManager con
   `--updated --update-session <id>`. La nuova applicazione segnala
   `Ready` solo dopo caricamento del documento `https://app.local/index.html`
   e bootstrap della dashboard con RPC operativo. A quel punto il Setup
   può chiudersi.

I timeout sono finiti e le attese degli eventi si svolgono fuori dal thread
Dispatcher WPF. Prima di `Accepted`, l'abort prevale sugli altri eventi:
nessuna richiesta annullata può avviare l'installazione. Dopo `Accepted`,
un processo originale ancora attivo blocca il nuovo workflow.

## Esiti ed errori

Il completamento della sostituzione dei file è distinto dalla conferma della
riapertura. Un errore di rilancio o il mancato segnale `Ready` lascia visibile
la finestra con messaggio, log e pulsante per ritentare **solo** il rilancio.
Il retry usa lo stesso `InstallEngine` già completato e gli stessi eventi
di sessione; non ripete estrazione né scrittura dei file.

Il Setup recente mostra la nuova finestra anche quando viene avviato con la
CLI legacy `/update --pid --lang`, ma senza attendere eventi che un host
precedente non sa produrre. In tale caso l'esito UI indica che il rilancio è
stato richiesto, senza promettere una conferma `Ready`.

## Verifiche

```powershell
dotnet build src/VoltManager.Setup/VoltManager.Setup.csproj -c Release -v:minimal
dotnet build src/VoltManager/VoltManager.csproj -c Release -v:minimal
dotnet test tests/VoltManager.Setup.Tests/VoltManager.Setup.Tests.csproj -c Release -v:minimal
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
```

Il test end-to-end su installazione reale va eseguito **solo in VM isolata**
con Setup prodotto dalla nuova pipeline di release. Verificare entrambi gli
ingressi di aggiornamento, app ancora visibile finché il Setup non è pronto,
quattro step, colori di tutti i temi e un colore personalizzato,
DPI 100/150/200%, EN/IT/ES/ZH, focus da tastiera e animazioni disabilitate.
Esercitare inoltre UAC negato, host che non termina, finestra non pronta,
abort prima di `Accepted`, estrazione fallita, binario mancante, timeout
di riapertura e retry senza reinstallazione.

La sola suite automatica, pur verificando i contratti su processi e directory
simulate, non dimostra la completa integrazione Windows con elevazione UAC
e WebView2: registrare separatamente l'esito della prova in VM.
