# Ottimizzazione risorse e responsività — Design

**Data:** 2026-07-15
**Stato:** approvato autonomamente dal mandato `/goal`

## Obiettivo

Ridurre in modo misurabile CPU, allocazioni e lavoro WebView2 senza alterare automazioni energetiche; rendere la finestra utilizzabile da 640×480 in su; mantenere l’esperienza completa sull’hardware capace.

## Vincoli

- Nessuna nuova dipendenza.
- Nessuna modifica a piani energetici, priorità automazioni o sicurezza.
- Build e sintassi di tutti i moduli devono riuscire.
- Massimo cinque cicli implementazione/verifica.
- Le ottimizzazioni non devono interrompere widget o automazioni quando la finestra principale è nascosta.

## Approcci considerati

1. **Chirurgico, raccomandato:** sostituire query WMI frequenti con API Win32, diradare metriche lente, sospendere solo rendering/poll UI invisibili, aggiungere breakpoint compatti.
2. **Taglio visuale globale:** eliminare aurora, blur e tween per tutti; degrada inutilmente hardware potente.
3. **Ristrutturazione monitor/bridge:** scheduler per consumatori e metriche separate; troppo ampia e rischiosa.

Scelta: approccio 1.

## Architettura

### Sampling host

`MonitorService` conserva il timer configurabile usato dall’automazione:

- RAM tramite `GlobalMemoryStatusEx`, senza WMI per tick;
- CPU clock WMI in cache per 10 secondi, sensori hardware prioritari;
- scansione processi ogni 3/6/10 tick secondo capacità hardware;
- guard reentrante e logging throttled invariati.

Il flusso automazioni continua a ricevere ogni `MetricsSnapshot` alla frequenza configurata.

### Visibilità WebView

`MainWindow.OnMetricsUpdated` inoltra metriche solo con finestra visibile e non minimizzata. Gaming reminder, widget e automazioni host restano attivi.

La pagina ascolta `visibilitychange`: ferma i poll processi, power flow, battery history e countdown override quando nascosta; al ritorno esegue refresh immediato e riprende le cadenze.

### Effetti

`effects.js` attiva pointer tracking, ripple e motion solo su tier `full`, pagina visibile, reduced-motion disabilitato. `balanced` e `lite` rimuovono blur, layer permanenti e animazioni continue. L’aurora resta nel DOM nascosta, evitando ricreazioni.

### Layout compatto

La finestra passa da minimo 1000×600 a 640×480. Sotto 900 px la sidebar diventa rail da 84 px; padding e gap diminuiscono; griglie passano a due o una colonna; subnav e controlli larghi conservano overflow orizzontale.

## Error handling

- Fallimento API RAM: ultimo valore valido; warning una volta per streak.
- WMI clock fallito: valore cache/null; nessun blocco del tick.
- Hardware info assente: tier `full`, coerente con il fail-open esistente.

## Misurazione

Benchmark locale `scripts/benchmark-monitor.ps1`: confronta WMI e Win32 su tempo e memoria trattenuta. Obiettivo minimo: miglioramento del 20% in almeno una metrica primaria senza regressioni sostanziali.

## Verifica

- `dotnet build VoltManager.sln -c Release`
- `node --check` per ogni `wwwroot/js/*.js`
- `node scripts/check-resource-optimizations.mjs`
- benchmark RAM
- avvio reale, resize 640×480, minimizzazione, controllo log e processo.

## Requisiti minimi

- Windows 10 64 bit 1809+ / Windows 11;
- CPU x64 dual-core;
- 4 GB RAM, 8 GB raccomandati;
- .NET 8 Desktop Runtime per portable;
- Microsoft Edge WebView2 Runtime;
- 250 MB spazio libero;
- display 640×480 minimo, 1280×720 raccomandato;
- privilegi amministratore per gestione piani.

## Fuori scope

Nuove impostazioni utente, nuove dipendenze, redesign visuale, modifica algoritmi energetici, porting ARM64/x86.
