# Changelog

Tutte le modifiche rilevanti a VoltManager sono documentate in questo file.
Il formato segue [Keep a Changelog](https://keepachangelog.com/it/1.1.0/).

## [Non rilasciato]

### Aggiunto

- **Widget launcher più personalizzabili e leggibili.** I launcher di gioco e le applicazioni hanno ora due widget separati. Basta trascinare un collegamento di Windows (.lnk), un .url o un .exe sul widget per aggiungerlo, fino a 10 elementi per widget; clic destro su un elemento per rimuoverlo o nasconderlo. Orientamento orizzontale/verticale e taglia mini/media/grande sono controlli separati; le icone mantengono una dimensione fissa e il ridimensionamento modifica solo la lunghezza della barra entro limiti leggibili.
- **Cronologia dei cambi piano energetico.** La sezione Piani energetici mostra fino agli ultimi 500 eventi della sessione con data e ora, piano precedente/richiesto/rilevato, origine, motivo ed esito. Include automazioni, selezioni manuali, cambi esterni e problemi; supporta filtri e caricamento progressivo. I dati restano solo in memoria, sopravvivono alla riduzione nell’area di notifica e vengono azzerati alla chiusura o al riavvio di VoltManager.
- **Controllo batteria e automazioni più trasparenti.** La soglia di batteria scarica è ora configurabile; la cronologia batteria offre intervalli 6/24/48 ore, potenza, temperatura, stato AC/DC ed export CSV. La UI mostra inoltre quale automazione sta controllando il piano energetico attivo.
- **Scorciatoie globali configurabili.** È possibile associare combinazioni globali ai piani energetici, alla modalità automatica e al toggle Mantieni PC attivo.
- **Profili app estesi.** Ogni profilo per applicazione può richiedere Mantieni PC attivo insieme al piano energetico, senza sovrascrivere la preferenza manuale permanente.

### Corretto

- **Cronologia piani — aggiornamenti e cause affidabili.** Gli eventi arrivati durante caricamento o cancellazione non vengono più persi; la cronologia si riallinea al ritorno in primo piano. I tentativi ripetuti della stessa automazione vengono raggruppati anche quando cambiano le letture dei sensori; i motivi distinguono disattivazione e ripristino senza dichiarare riusciti i comandi falliti. La modifica dei parametri avanzati non riattiva più un piano cambiato nel frattempo e segnala una riapplicazione non verificabile.

- **Sidebar — indicatore della voce attiva disallineato durante compress/expand.**
  Quando si passava dalla barra laterale estesa a quella compatta (o viceversa), il
  selettore luminoso poteva posizionarsi più in basso rispetto alla voce selezionata
  e apparire eccessivamente allungato. Causa: la posizione veniva misurata dentro un
  `requestAnimationFrame` mentre la transizione di larghezza della sidebar (280ms) era
  ancora in corso; con la larghezza ridotta le etichette andavano a capo, producendo
  un'altezza/posizione temporanea che veniva fissata nell'indicatore. Ora le etichette
  di navigazione restano su una sola riga (`white-space: nowrap`) e l'indicatore viene
  rimisurato al termine della transizione di larghezza.
