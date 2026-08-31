# Changelog

Tutte le modifiche rilevanti a VoltManager sono documentate in questo file.
Il formato segue [Keep a Changelog](https://keepachangelog.com/it/1.1.0/).

## [Non rilasciato]

### Aggiunto

- **Controllo batteria e automazioni più trasparenti.** La soglia di batteria scarica è ora configurabile; la cronologia batteria offre intervalli 6/24/48 ore, potenza, temperatura, stato AC/DC ed export CSV. La UI mostra inoltre quale automazione sta controllando il piano energetico attivo.
- **Scorciatoie globali configurabili.** È possibile associare combinazioni globali ai piani energetici, alla modalità automatica e al toggle Mantieni PC attivo.
- **Profili app estesi.** Ogni profilo per applicazione può richiedere Mantieni PC attivo insieme al piano energetico, senza sovrascrivere la preferenza manuale permanente.

### Corretto

- **Sidebar — indicatore della voce attiva disallineato durante compress/expand.**
  Quando si passava dalla barra laterale estesa a quella compatta (o viceversa), il
  selettore luminoso poteva posizionarsi più in basso rispetto alla voce selezionata
  e apparire eccessivamente allungato. Causa: la posizione veniva misurata dentro un
  `requestAnimationFrame` mentre la transizione di larghezza della sidebar (280ms) era
  ancora in corso; con la larghezza ridotta le etichette andavano a capo, producendo
  un'altezza/posizione temporanea che veniva fissata nell'indicatore. Ora le etichette
  di navigazione restano su una sola riga (`white-space: nowrap`) e l'indicatore viene
  rimisurato al termine della transizione di larghezza.
