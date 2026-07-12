# Correzione menu tray pianificazione

## Causa

`TraySchedulePowerItem.Items` contiene un `Separator`, ma `LocalizeTrayMenu()` enumera gli elementi come `MenuItem`. Durante `InitializeComponent()`, il metodo viene chiamato e il cast del separatore genera `InvalidCastException`.

## Soluzione

Rimuovere il separatore dal sottomenu. È soltanto decorativo; gli elementi rimanenti sono tutti `MenuItem` e sono compatibili con la localizzazione esistente.

## Verifica

Aggiungere una regressione che carichi il markup e verifichi che il sottomenu contenga solo `MenuItem`. Eseguire `dotnet test -c Release` e la build Release di VoltManager.

## Escluso

Nessun filtro runtime o refactoring: non necessario finché il markup contiene soltanto le voci previste.
