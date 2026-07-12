# Piano tecnico di implementazione

## Pianificazione spegnimento e sospensione in VoltManager

### Obiettivo

Introdurre in VoltManager una funzionalità centralizzata che permetta all’utente di programmare:

* lo spegnimento del PC;
* la sospensione del PC;
* opzionalmente il riavvio, mantenendo la funzione già esistente.

La pianificazione dovrà essere disponibile da:

1. interfaccia grafica WebView2;
2. menu contestuale della tray icon di Windows;
3. opzionalmente Jump List della taskbar per alcuni preset rapidi.

I preset iniziali saranno:

* 30 minuti;
* 45 minuti;
* 1 ora;
* 2 ore;
* 4 ore;
* tempo personalizzato.

L’utente sceglierà prima la durata e successivamente l’azione da eseguire.

---

# 1. Architettura attuale

VoltManager utilizza:

* WPF su .NET 8;
* WebView2 per l’interfaccia principale;
* `H.NotifyIcon.Wpf` per la tray icon;
* un bridge JSON-RPC tra JavaScript e C#;
* un helper non elevato per i comandi della Jump List;
* impostazioni persistenti in `%APPDATA%\VoltManager\settings.json`.

La repository contiene già una pianificazione giornaliera basata su:

```json
{
  "enabled": true,
  "action": "shutdown",
  "time": "23:00",
  "lastTriggeredLocalDate": "2026-07-12"
}
```

Il backend controlla ogni 15 secondi se l’orario corrente corrisponde a quello configurato e, in caso positivo, esegue spegnimento, riavvio o sospensione.

Questa implementazione deve essere estesa, non duplicata.

---

# 2. Decisione architetturale

La logica di pianificazione non deve essere implementata separatamente nella GUI e nella tray icon.

Entrambe le superfici devono utilizzare un unico servizio:

```text
ScheduledPowerActionService
```

Il servizio sarà l’unica fonte di verità per:

* pianificazione;
* cancellazione;
* stato corrente;
* persistenza;
* validazione;
* esecuzione;
* notifiche alle interfacce.

Architettura prevista:

```text
WebView2 GUI
     │
     │ HostBridge JSON-RPC
     ▼
ScheduledPowerActionService
     ▲
     │
Tray Context Menu
     │
     ▼
PowerActionExecutor
     │
     ├── shutdown.exe
     └── SetSuspendState
```

---

# 3. Nuovi componenti

Creare i seguenti file:

```text
src/VoltManager/Services/ScheduledPowerActionService.cs
src/VoltManager/Services/PowerActionExecutor.cs
```

Per rendere il codice facilmente testabile, è consigliato introdurre anche:

```text
src/VoltManager/Services/IPowerActionExecutor.cs
src/VoltManager/Services/ISystemClock.cs
```

Il clock astratto evita di dipendere direttamente da `DateTime.UtcNow` nei test.

---

# 4. Modello dati

## 4.1 Enumerazioni

Aggiungere in `Models.cs`:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduledPowerActionType
{
    Shutdown,
    Restart,
    Sleep
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduledPowerMode
{
    Relative,
    Daily
}
```

L’enum `Restart` deve essere mantenuto per compatibilità con la funzionalità giornaliera esistente, anche se nei preset rapidi iniziali saranno esposti soltanto spegnimento e sospensione.

---

## 4.2 Estensione di `AutoShutdownSettings`

Il nome `AutoShutdownSettings` e la proprietà JSON `autoShutdown` devono essere mantenuti per non rompere i file `settings.json` esistenti. La repository utilizza già esplicitamente questo nome per compatibilità.

Modello consigliato:

```csharp
public class AutoShutdownSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("mode")]
    public ScheduledPowerMode Mode { get; set; } = ScheduledPowerMode.Daily;

    [JsonPropertyName("action")]
    public ScheduledPowerActionType Action { get; set; } =
        ScheduledPowerActionType.Shutdown;

    // Compatibilità con la pianificazione giornaliera esistente.
    [JsonPropertyName("time")]
    public string Time { get; set; } = "23:00";

    // Momento assoluto di esecuzione per le pianificazioni relative.
    [JsonPropertyName("executeAtUtc")]
    public DateTime? ExecuteAtUtc { get; set; }

    // Valore informativo usato dalla GUI.
    [JsonPropertyName("delayMinutes")]
    public int? DelayMinutes { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime? CreatedAtUtc { get; set; }

    [JsonPropertyName("lastTriggeredLocalDate")]
    public string? LastTriggeredLocalDate { get; set; }
}
```

Non bisogna salvare un countdown decrementale nelle impostazioni.

Lo stato residuo deve essere calcolato con:

```csharp
remaining = ExecuteAtUtc - DateTime.UtcNow;
```

---

## 4.3 Stato pubblico

Creare un record separato per GUI e tray:

```csharp
public record ScheduledPowerActionState
{
    public bool Enabled { get; init; }

    public ScheduledPowerMode Mode { get; init; }

    public ScheduledPowerActionType Action { get; init; }

    public DateTime? ExecuteAtUtc { get; init; }

    public int? DelayMinutes { get; init; }

    public long RemainingSeconds { get; init; }

    public string? DailyTime { get; init; }

    public bool Expired { get; init; }
}
```

Questo evita di esporre direttamente il modello persistente e permette di aggiungere in futuro campi derivati senza modificare `settings.json`.

---

# 5. `ScheduledPowerActionService`

## 5.1 Responsabilità

Il servizio deve:

* leggere lo stato iniziale dalle impostazioni;
* migrare la vecchia configurazione;
* pianificare un’azione relativa;
* pianificare un’azione giornaliera;
* cancellare la pianificazione;
* sostituire la pianificazione attiva;
* notificare GUI e tray;
* impedire doppie esecuzioni;
* eseguire l’azione anche quando la finestra principale è nascosta;
* riarmare il timer dopo il riavvio dell’applicazione.

Interfaccia consigliata:

```csharp
public sealed class ScheduledPowerActionService : IDisposable
{
    public event Action<ScheduledPowerActionState>? StateChanged;

    public ScheduledPowerActionState GetState();

    public ScheduledPowerActionState ScheduleAfter(
        TimeSpan delay,
        ScheduledPowerActionType action);

    public ScheduledPowerActionState ScheduleDaily(
        TimeOnly time,
        ScheduledPowerActionType action);

    public ScheduledPowerActionState Cancel();

    public void Start();

    public void Dispose();
}
```

---

## 5.2 Dipendenze

```csharp
private readonly SettingsService _settings;
private readonly IPowerActionExecutor _executor;
private readonly ISystemClock _clock;
private readonly object _sync = new();

private System.Threading.Timer? _relativeTimer;
private System.Threading.Timer? _dailyTimer;
private long _generation;
```

Il campo `_generation` serve a invalidare callback obsolete quando una pianificazione viene sostituita.

Esempio:

```csharp
long generation = ++_generation;
```

La callback eseguirà l’azione soltanto se:

```csharp
generation == _generation
```

---

## 5.3 Validazione

Definire limiti centralizzati:

```csharp
public static readonly TimeSpan MinDelay = TimeSpan.FromMinutes(1);
public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(7);
```

Validazione:

```csharp
private static void ValidateDelay(TimeSpan delay)
{
    if (delay < MinDelay || delay > MaxDelay)
        throw new ArgumentOutOfRangeException(
            nameof(delay),
            "La durata deve essere compresa tra 1 minuto e 7 giorni.");
}
```

Il backend deve validare sempre i dati ricevuti dalla GUI.

La sola validazione JavaScript non è sufficiente.

---

## 5.4 Pianificazione relativa

Implementazione indicativa:

```csharp
public ScheduledPowerActionState ScheduleAfter(
    TimeSpan delay,
    ScheduledPowerActionType action)
{
    ValidateDelay(delay);

    ScheduledPowerActionState state;

    lock (_sync)
    {
        CancelTimersUnsafe();

        DateTime now = _clock.UtcNow;
        DateTime executeAt = now.Add(delay);

        var config = _settings.Current.AutoShutdown;
        config.Enabled = true;
        config.Mode = ScheduledPowerMode.Relative;
        config.Action = action;
        config.CreatedAtUtc = now;
        config.ExecuteAtUtc = executeAt;
        config.DelayMinutes = (int)Math.Ceiling(delay.TotalMinutes);
        config.LastTriggeredLocalDate = null;

        _settings.Save();

        long generation = ++_generation;

        _relativeTimer = new System.Threading.Timer(
            _ => ExecuteRelativeCallback(generation),
            null,
            delay,
            Timeout.InfiniteTimeSpan);

        state = CreateStateUnsafe();
    }

    PublishState(state);
    return state;
}
```

---

## 5.5 Callback di esecuzione

La configurazione deve essere disattivata e salvata prima di avviare lo spegnimento o la sospensione.

```csharp
private void ExecuteRelativeCallback(long generation)
{
    ScheduledPowerActionType action;

    lock (_sync)
    {
        if (generation != _generation)
            return;

        var config = _settings.Current.AutoShutdown;

        if (!config.Enabled ||
            config.Mode != ScheduledPowerMode.Relative)
            return;

        action = config.Action;

        config.Enabled = false;
        config.ExecuteAtUtc = null;
        config.DelayMinutes = null;
        config.CreatedAtUtc = null;

        _settings.Save();

        _relativeTimer?.Dispose();
        _relativeTimer = null;

        ++_generation;
    }

    PublishState(GetState());

    try
    {
        _executor.Execute(action);
    }
    catch (Exception ex)
    {
        Logger.Error("Scheduled power action failed", ex);
    }
}
```

Disattivare prima lo stato evita che l’azione venga eseguita nuovamente al successivo avvio.

---

## 5.6 Ripristino dopo riavvio

Nel metodo `Start()`:

```csharp
public void Start()
{
    lock (_sync)
    {
        var config = _settings.Current.AutoShutdown;

        if (!config.Enabled)
            return;

        if (config.Mode == ScheduledPowerMode.Relative)
            RestoreRelativeScheduleUnsafe(config);
        else
            StartDailyTimerUnsafe();
    }

    PublishState(GetState());
}
```

Ripristino relativo:

```csharp
private void RestoreRelativeScheduleUnsafe(
    AutoShutdownSettings config)
{
    if (config.ExecuteAtUtc is not DateTime executeAt)
    {
        DisableInvalidScheduleUnsafe();
        return;
    }

    TimeSpan remaining = executeAt - _clock.UtcNow;

    if (remaining <= TimeSpan.Zero)
    {
        DisableInvalidScheduleUnsafe();
        return;
    }

    long generation = ++_generation;

    _relativeTimer = new System.Threading.Timer(
        _ => ExecuteRelativeCallback(generation),
        null,
        remaining,
        Timeout.InfiniteTimeSpan);
}
```

Una pianificazione già scaduta non dovrebbe spegnere immediatamente il PC dopo il riavvio dell’applicazione.

Il comportamento più sicuro è cancellarla e segnalarla come scaduta.

---

# 6. `PowerActionExecutor`

Il codice di esecuzione attualmente presente in `App.xaml.cs` deve essere spostato in un componente dedicato.

Interfaccia:

```csharp
public interface IPowerActionExecutor
{
    void Execute(ScheduledPowerActionType action);
}
```

Implementazione:

```csharp
public sealed class PowerActionExecutor : IPowerActionExecutor
{
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(
        bool hibernate,
        bool forceCritical,
        bool disableWakeEvent);

    public void Execute(ScheduledPowerActionType action)
    {
        switch (action)
        {
            case ScheduledPowerActionType.Sleep:
                ExecuteSleep();
                break;

            case ScheduledPowerActionType.Restart:
                StartShutdownProcess("/r /t 0");
                break;

            default:
                StartShutdownProcess("/s /t 0");
                break;
        }
    }

    private static void ExecuteSleep()
    {
        bool success = SetSuspendState(
            hibernate: false,
            forceCritical: false,
            disableWakeEvent: false);

        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void StartShutdownProcess(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process == null)
            throw new InvalidOperationException(
                "Impossibile avviare shutdown.exe.");
    }
}
```

Non aggiungere l’opzione `/f`.

Forzare la chiusura delle applicazioni aumenterebbe il rischio di perdita dei dati non salvati.

---

# 7. Integrazione in `App.xaml.cs`

Aggiungere:

```csharp
public ScheduledPowerActionService ScheduledPowerActions
{
    get;
    private set;
} = null!;
```

Durante il bootstrap:

```csharp
var powerActionExecutor = new PowerActionExecutor();

ScheduledPowerActions = new ScheduledPowerActionService(
    Settings,
    powerActionExecutor,
    new SystemClock());

ScheduledPowerActions.Start();
```

Rimuovere progressivamente:

```csharp
private System.Threading.Timer? _scheduledPowerActionTimer;
```

e i metodi:

```text
StartScheduledPowerActionLoop
TryParseScheduledPowerTime
ExecuteScheduledPowerAction
NormalizeScheduledPowerAction
StartShutdownCommand
```

La gestione giornaliera verrà incorporata nel nuovo servizio.

Nel cleanup:

```csharp
SafeCleanup(
    "scheduled power action service",
    ScheduledPowerActions.Dispose);
```

---

# 8. Eventi condivisi

`App` o direttamente `ScheduledPowerActionService` deve esporre:

```csharp
public event Action<ScheduledPowerActionState>? StateChanged;
```

In `MainWindow.WireWebViewCore`, durante `firstBoot`:

```csharp
_app.ScheduledPowerActions.StateChanged += state =>
{
    _bridge?.PushEvent(
        "scheduledPowerActionChanged",
        state);

    Dispatcher.Invoke(() =>
    {
        RefreshScheduledPowerTrayState(state);
    });
};
```

In questo modo:

* una pianificazione creata dalla GUI aggiorna la tray;
* una pianificazione creata dalla tray aggiorna la GUI;
* una cancellazione aggiorna entrambe;
* la scadenza aggiorna entrambe.

---

# 9. API WebView2

In `HostBridge.DispatchAsync` aggiungere tre metodi.

## 9.1 Lettura dello stato

```csharp
case "getScheduledPowerAction":
    return _app.ScheduledPowerActions.GetState();
```

## 9.2 Pianificazione

Payload relativo:

```json
{
  "mode": "relative",
  "action": "shutdown",
  "delayMinutes": 45
}
```

Payload giornaliero:

```json
{
  "mode": "daily",
  "action": "sleep",
  "time": "23:30"
}
```

Implementazione:

```csharp
case "schedulePowerAction":
{
    string modeText =
        payload.GetProperty("mode").GetString() ?? "";

    string actionText =
        payload.GetProperty("action").GetString() ?? "";

    if (!Enum.TryParse<ScheduledPowerActionType>(
            actionText,
            ignoreCase: true,
            out var action))
    {
        throw new ArgumentException(
            _loc.T("Error_InvalidPowerAction"));
    }

    if (string.Equals(
            modeText,
            "relative",
            StringComparison.OrdinalIgnoreCase))
    {
        int delayMinutes =
            payload.GetProperty("delayMinutes").GetInt32();

        return _app.ScheduledPowerActions.ScheduleAfter(
            TimeSpan.FromMinutes(delayMinutes),
            action);
    }

    if (string.Equals(
            modeText,
            "daily",
            StringComparison.OrdinalIgnoreCase))
    {
        string timeText =
            payload.GetProperty("time").GetString() ?? "";

        if (!TimeOnly.TryParseExact(
                timeText,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            throw new ArgumentException(
                _loc.T("Error_InvalidPowerTime"));
        }

        return _app.ScheduledPowerActions.ScheduleDaily(
            time,
            action);
    }

    throw new ArgumentException(
        _loc.T("Error_InvalidScheduleMode"));
}
```

## 9.3 Cancellazione

```csharp
case "cancelScheduledPowerAction":
    return _app.ScheduledPowerActions.Cancel();
```

---

# 10. Protezione durante `saveSettings`

Attualmente la GUI salva un oggetto generale contenente tutte le impostazioni.

La pianificazione deve essere considerata stato runtime-owned.

In `HostBridge.PreserveRuntimeOwnedSettings` sostituire la sola conservazione di `LastTriggeredLocalDate` con:

```csharp
settings.AutoShutdown = current.AutoShutdown;
```

Questo evita il seguente race condition:

1. l’utente programma lo spegnimento dalla tray;
2. la GUI possiede ancora una copia vecchia di `autoShutdown`;
3. l’utente modifica il tema;
4. `saveSettings` sovrascrive la nuova pianificazione con quella vecchia.

La pianificazione deve essere modificabile esclusivamente tramite le API dedicate.

---

# 11. Interfaccia grafica WebView2

La sezione attuale viene generata dinamicamente da `systemViewHtml()` in `wwwroot/js/app.js`.

La nuova card dovrebbe contenere due modalità:

```text
[ Tra un intervallo ] [ A un orario ]
```

## 11.1 Modalità relativa

Layout:

```text
Azione automatica del PC

Quando:
[30 min] [45 min] [1 ora] [2 ore] [4 ore] [Personalizzato]

Azione:
[ Spegni ] [ Sospendi ]

Il PC verrà spento alle 18:45.

[ Programma azione ]
```

Per il tempo personalizzato:

```text
Ore:    [ 2 ]
Minuti: [ 30 ]
```

La GUI deve convertire ore e minuti in:

```javascript
delayMinutes = hours * 60 + minutes;
```

---

## 11.2 Modalità giornaliera

Preservare la funzionalità esistente:

```text
Ogni giorno alle: [23:00]

Azione:
[ Spegni ] [ Riavvia ] [ Sospendi ]
```

---

## 11.3 Pianificazione attiva

Quando è presente una pianificazione:

```text
Azione programmata

Sospensione tra 1 ora e 42 minuti
Esecuzione prevista: oggi alle 20:15

[ Modifica ] [ Annulla ]
```

Il countdown deve essere aggiornato lato JavaScript:

```javascript
let activeSchedule = null;
let countdownTimer = null;

function startCountdown(state) {
    activeSchedule = state;

    clearInterval(countdownTimer);

    renderScheduleState();

    if (!state || !state.enabled || !state.executeAtUtc)
        return;

    countdownTimer = setInterval(() => {
        renderScheduleState();
    }, 1000);
}
```

Calcolo:

```javascript
function remainingSeconds() {
    if (!activeSchedule?.executeAtUtc)
        return 0;

    const executeAt =
        new Date(activeSchedule.executeAtUtc).getTime();

    return Math.max(
        0,
        Math.floor((executeAt - Date.now()) / 1000)
    );
}
```

Il countdown non deve essere persistito.

---

## 11.4 Chiamate al bridge

Programmazione:

```javascript
await Host.call('schedulePowerAction', {
    mode: 'relative',
    action: selectedAction,
    delayMinutes: selectedMinutes
});
```

Cancellazione:

```javascript
await Host.call('cancelScheduledPowerAction');
```

Caricamento iniziale:

```javascript
const state =
    await Host.call('getScheduledPowerAction');

applyScheduledPowerActionState(state);
```

Evento:

```javascript
Host.on(
    'scheduledPowerActionChanged',
    applyScheduledPowerActionState
);
```

---

# 12. Menu contestuale della tray icon

Il menu attuale è definito in `MainWindow.xaml` e contiene già cambio piano, gaming mode, keep-awake e automazione.

Aggiungere:

```xml
<Separator />

<MenuItem
    x:Name="TraySchedulePowerItem"
    Header="Programma azione PC">

    <MenuItem Header="30 minuti" Tag="30">
        <MenuItem
            Header="Spegni"
            Tag="shutdown|30"
            Click="TraySchedulePreset_Click" />

        <MenuItem
            Header="Sospendi"
            Tag="sleep|30"
            Click="TraySchedulePreset_Click" />
    </MenuItem>

    <MenuItem Header="45 minuti" Tag="45">
        ...
    </MenuItem>

    <MenuItem Header="1 ora">
        ...
    </MenuItem>

    <MenuItem Header="2 ore">
        ...
    </MenuItem>

    <MenuItem Header="4 ore">
        ...
    </MenuItem>

    <Separator />

    <MenuItem
        x:Name="TrayScheduleCustomItem"
        Header="Tempo personalizzato..."
        Click="TrayScheduleCustom_Click" />
</MenuItem>

<MenuItem
    x:Name="TrayScheduledStateItem"
    Header="Nessuna azione programmata"
    IsEnabled="False" />

<MenuItem
    x:Name="TrayCancelScheduledItem"
    Header="Annulla azione programmata"
    Visibility="Collapsed"
    Click="TrayCancelScheduled_Click" />
```

Per evitare XAML ripetitivo, è preferibile generare dinamicamente i preset in `MainWindow.xaml.cs`.

Esempio:

```csharp
private static readonly int[] SchedulePresetsMinutes =
{
    30,
    45,
    60,
    120,
    240
};
```

---

## 12.1 Handler dei preset

```csharp
private void TraySchedulePreset_Click(
    object sender,
    RoutedEventArgs e)
{
    if (sender is not MenuItem { Tag: string tag })
        return;

    string[] parts = tag.Split('|');

    if (parts.Length != 2 ||
        !int.TryParse(parts[1], out int minutes))
    {
        return;
    }

    if (!Enum.TryParse<ScheduledPowerActionType>(
            parts[0],
            true,
            out var action))
    {
        return;
    }

    ScheduleFromTray(
        TimeSpan.FromMinutes(minutes),
        action);
}
```

---

## 12.2 Sostituzione della pianificazione attiva

Prima di sostituire una pianificazione esistente:

```csharp
private bool ConfirmScheduleReplacement()
{
    var current =
        _app.ScheduledPowerActions.GetState();

    if (!current.Enabled)
        return true;

    return MessageBox.Show(
        _app.Loc.T("Dialog_ReplaceScheduledAction"),
        _app.Loc.T("Dialog_VoltManagerTitle"),
        MessageBoxButton.YesNo,
        MessageBoxImage.Question)
        == MessageBoxResult.Yes;
}
```

---

## 12.3 Aggiornamento del menu

In `TrayMenu_Opened`:

```csharp
private void RefreshScheduledPowerTrayState(
    ScheduledPowerActionState state)
{
    TrayCancelScheduledItem.Visibility =
        state.Enabled
            ? Visibility.Visible
            : Visibility.Collapsed;

    if (!state.Enabled)
    {
        TrayScheduledStateItem.Header =
            _app.Loc.T("Tray_NoScheduledAction");

        return;
    }

    TrayScheduledStateItem.Header =
        BuildScheduledActionTrayText(state);
}
```

Esempio testo:

```text
Sospensione programmata tra 42 min
```

Il valore residuo deve essere ricalcolato quando il menu viene aperto, non aggiornato continuamente ogni secondo.

---

# 13. Finestra WPF per il tempo personalizzato

Creare:

```text
src/VoltManager/Windows/SchedulePowerActionWindow.xaml
src/VoltManager/Windows/SchedulePowerActionWindow.xaml.cs
```

La finestra deve contenere:

* input ore;
* input minuti;
* selezione spegnimento/sospensione;
* riepilogo dell’orario finale;
* pulsante Conferma;
* pulsante Annulla.

Proprietà pubbliche:

```csharp
public TimeSpan SelectedDelay { get; private set; }

public ScheduledPowerActionType SelectedAction
{
    get;
    private set;
}
```

Validazione:

```csharp
private bool TryGetDelay(out TimeSpan delay)
{
    delay = TimeSpan.Zero;

    if (!int.TryParse(HoursTextBox.Text, out int hours))
        return false;

    if (!int.TryParse(MinutesTextBox.Text, out int minutes))
        return false;

    if (hours < 0 || minutes < 0 || minutes > 59)
        return false;

    delay = TimeSpan.FromHours(hours)
          + TimeSpan.FromMinutes(minutes);

    return delay >= ScheduledPowerActionService.MinDelay &&
           delay <= ScheduledPowerActionService.MaxDelay;
}
```

---

# 14. Localizzazione

La GUI supporta italiano, inglese, spagnolo e cinese.

Aggiungere stringhe per:

```text
Schedule_Title
Schedule_Relative
Schedule_Daily
Schedule_After
Schedule_Action
Schedule_Shutdown
Schedule_Sleep
Schedule_Restart
Schedule_Custom
Schedule_Hours
Schedule_Minutes
Schedule_Confirm
Schedule_Cancel
Schedule_Active
Schedule_Replace
Schedule_ExecutionAt
Schedule_InvalidDuration
Schedule_Expired
```

Per la tray utilizzare `NativeStrings*.resx`, perché `LocalizeTrayMenu()` usa `LocalizationService`.

Per la GUI è consigliato usare `i18n.js`.

Le etichette locali attualmente presenti direttamente dentro `app.js` possono essere mantenute inizialmente, ma la soluzione più pulita è spostarle nel sistema globale di localizzazione.

---

# 15. Jump List della taskbar

La Jump List esistente utilizza `VoltManagerPlanSwitch.exe`, `RemoteCommandProtocol` e named events.

Il protocollo attuale supporta soltanto chiavi statiche.

Per questo motivo la Jump List può supportare soltanto preset predefiniti, ad esempio:

```text
Timer PC
- Spegni tra 30 minuti
- Spegni tra 1 ora
- Sospendi tra 30 minuti
- Sospendi tra 1 ora
- Apri pianificazione
```

Aggiungere chiavi:

```csharp
public const string Shutdown30Key =
    "scheduleShutdown30";

public const string Shutdown60Key =
    "scheduleShutdown60";

public const string Sleep30Key =
    "scheduleSleep30";

public const string Sleep60Key =
    "scheduleSleep60";

public const string OpenSchedulerKey =
    "openScheduler";
```

In `ApplyRemoteCommand`:

```csharp
case RemoteCommandProtocol.Shutdown30Key:
    ScheduledPowerActions.ScheduleAfter(
        TimeSpan.FromMinutes(30),
        ScheduledPowerActionType.Shutdown);
    break;

case RemoteCommandProtocol.Sleep60Key:
    ScheduledPowerActions.ScheduleAfter(
        TimeSpan.FromMinutes(60),
        ScheduledPowerActionType.Sleep);
    break;

case RemoteCommandProtocol.OpenSchedulerKey:
    Dispatcher.Invoke(() =>
    {
        _mainWindow?.ShowFromTray();
        _mainWindow?.NavigateToSystemScheduler();
    });
    break;
```

Il tempo personalizzato non deve essere codificato nella Jump List, perché il protocollo a named events non trasporta payload dinamici.

Per supportare parametri arbitrari sarebbe necessario sostituire il protocollo con:

* named pipe;
* socket locale;
* memoria condivisa;
* file di comando temporaneo.

Questa modifica non è necessaria per la prima versione.

---

# 16. Chiusura dell’applicazione

La pianificazione relativa dipende dal processo VoltManager.

Quando l’utente seleziona “Esci” e una pianificazione è attiva, mostrare:

```text
È presente un’azione programmata.

Uscendo da VoltManager la pianificazione verrà annullata.

Vuoi uscire comunque?
```

Azioni:

```text
[ Rimani nella tray ]
[ Annulla pianificazione ed esci ]
```

La normale chiusura verso la tray non deve annullare il timer.

---

# 17. Concorrenza e race condition

Tutte le operazioni sullo scheduler devono essere protette dallo stesso lock:

```csharp
private readonly object _sync = new();
```

Casi da gestire:

1. l’utente annulla mentre il timer sta scadendo;
2. l’utente sostituisce la pianificazione mentre parte la callback;
3. GUI e tray inviano comandi contemporaneamente;
4. `Settings.Save()` genera eventi mentre il servizio aggiorna il proprio stato;
5. un vecchio timer richiama una callback dopo essere stato sostituito.

La combinazione di:

```text
lock + generation token + disattivazione prima dell’esecuzione
```

impedisce doppie esecuzioni.

Gli eventi `StateChanged` devono essere emessi fuori dal lock per evitare deadlock con subscriber che richiamano il servizio.

---

# 18. Logging

Aggiungere log strutturati:

```csharp
Logger.Info(
    $"Scheduled action created: action={action}, " +
    $"executeAtUtc={executeAt:O}, " +
    $"delayMinutes={delay.TotalMinutes}");

Logger.Info(
    $"Scheduled action cancelled: action={action}");

Logger.Info(
    $"Executing scheduled action: action={action}");

Logger.Warn(
    $"Expired scheduled action discarded: " +
    $"executeAtUtc={executeAt:O}");
```

Non registrare continuamente il countdown.

---

# 19. Test automatici

## 19.1 `ScheduledPowerActionServiceTests`

Creare:

```text
tests/VoltManager.Tests/ScheduledPowerActionServiceTests.cs
```

Test richiesti:

```text
ScheduleAfter_PersistsRelativeSchedule
ScheduleAfter_CalculatesExecuteAtUtc
ScheduleAfter_RejectsDelayBelowMinimum
ScheduleAfter_RejectsDelayAboveMaximum
ScheduleAfter_ReplacesExistingSchedule
Cancel_DisablesAndClearsRelativeSchedule
Start_RearmsFutureRelativeSchedule
Start_DiscardsExpiredRelativeSchedule
TimerCallback_ExecutesActionOnce
TimerCallback_ClearsStateBeforeExecution
StaleTimerCallback_DoesNotExecute
DailySchedule_TriggersOnlyOncePerDay
StateChanged_FiresAfterSchedule
StateChanged_FiresAfterCancel
```

Utilizzare:

```csharp
FakeClock
FakePowerActionExecutor
FakeTimerScheduler
```

`FakePowerActionExecutor`:

```csharp
public sealed class FakePowerActionExecutor
    : IPowerActionExecutor
{
    public List<ScheduledPowerActionType> Executed
    {
        get;
    } = new();

    public void Execute(
        ScheduledPowerActionType action)
    {
        Executed.Add(action);
    }
}
```

I test non devono mai invocare realmente `shutdown.exe` o `SetSuspendState`.

---

## 19.2 `SettingsServiceTests`

I test esistenti verificano già valori predefiniti e round-trip di `AutoShutdown`.

Aggiornare i test per verificare:

```text
Mode
ExecuteAtUtc
DelayMinutes
CreatedAtUtc
Action enum
```

Aggiungere test di migrazione:

```text
LegacyAutoShutdownWithoutMode_DefaultsToDaily
RelativeScheduleWithoutExecuteAt_IsDisabled
InvalidDelayMinutes_IsNormalized
InvalidAction_DefaultsToShutdown
```

---

## 19.3 Test bridge

Testare:

```text
schedulePowerAction relative valido
schedulePowerAction daily valido
azione sconosciuta
modalità sconosciuta
tempo giornaliero non valido
delay negativo
delay troppo elevato
cancelScheduledPowerAction
getScheduledPowerAction
```

---

# 20. Test manuali

## Scenario 1 — GUI

1. Aprire Gestione Energetica.
2. Selezionare 30 minuti.
3. Selezionare Sospendi.
4. Confermare.
5. Verificare countdown e orario finale.
6. Aprire la tray.
7. Verificare che la stessa pianificazione sia visibile.

## Scenario 2 — Tray

1. Tasto destro sulla tray icon.
2. Selezionare:

   * 45 minuti;
   * Spegni.
3. Aprire la GUI.
4. Verificare che la pianificazione sia mostrata.

## Scenario 3 — Sostituzione

1. Programmare sospensione tra due ore.
2. Programmare spegnimento tra 30 minuti.
3. Verificare la richiesta di sostituzione.
4. Confermare.
5. Verificare che esista una sola pianificazione.

## Scenario 4 — Cancellazione

1. Programmare un’azione.
2. Annullarla dalla tray.
3. Verificare che la GUI si aggiorni immediatamente.

## Scenario 5 — Riavvio dell’app

1. Programmare un’azione tra due ore.
2. Riavviare VoltManager.
3. Verificare che il timer venga riarmato con la scadenza originale.

## Scenario 6 — Pianificazione scaduta

1. Inserire nelle impostazioni una scadenza passata.
2. Avviare VoltManager.
3. Verificare che il PC non venga spento.
4. Verificare che la pianificazione venga cancellata.

---

# 21. Sequenza di implementazione

## Fase 1 — Dominio e persistenza

* estendere `AutoShutdownSettings`;
* aggiungere enum e stato pubblico;
* aggiornare normalizzazione e migrazione;
* aggiornare test delle impostazioni.

## Fase 2 — Scheduler

* creare `IPowerActionExecutor`;
* creare `PowerActionExecutor`;
* creare `ScheduledPowerActionService`;
* implementare timer relativo e giornaliero;
* aggiungere unit test.

## Fase 3 — Integrazione applicativa

* registrare il servizio in `App.xaml.cs`;
* rimuovere il vecchio timer;
* collegare eventi e cleanup;
* proteggere `AutoShutdown` durante `saveSettings`.

## Fase 4 — Bridge e GUI

* aggiungere i tre metodi JSON-RPC;
* modificare la sezione sistema;
* implementare preset, custom time e countdown;
* collegare l’evento `scheduledPowerActionChanged`.

## Fase 5 — Tray

* aggiungere il menu dinamico;
* aggiungere custom dialog;
* mostrare stato e cancellazione;
* sincronizzare tramite `StateChanged`.

## Fase 6 — Jump List e rifiniture

* aggiungere alcuni preset statici;
* aggiungere `openScheduler`;
* completare localizzazioni;
* eseguire smoke test.

---

# 22. Criteri di accettazione

La funzionalità è completata quando:

* l’utente può scegliere 30, 45, 60, 120 e 240 minuti;
* l’utente può inserire un tempo personalizzato;
* l’utente può scegliere spegnimento o sospensione;
* la GUI mostra il countdown e l’orario finale;
* la tray mostra la pianificazione attiva;
* GUI e tray restano sincronizzate;
* una nuova pianificazione sostituisce correttamente quella precedente;
* la pianificazione può essere annullata da entrambe le superfici;
* il timer funziona con la finestra nascosta;
* la normale chiusura verso la tray non annulla il timer;
* l’uscita completa avvisa l’utente;
* la pianificazione viene ripristinata dopo il riavvio di VoltManager;
* una pianificazione scaduta non provoca uno spegnimento inatteso;
* non possono avvenire doppie esecuzioni;
* la configurazione è persistita atomicamente;
* tutti i test vengono eseguiti senza richiamare realmente azioni di sistema.
