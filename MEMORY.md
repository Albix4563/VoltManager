# VoltManager Memory

```json
{
  "project": "VoltManager",
  "branch": "Dev",
  "decisions": [
    {
      "date": "2026-11-03",
      "topic": "Redesign multi-schermata (disposizione contenuti)",
      "summary": "Approvata la proposta di riequilibrare le schermate escludendo la Home. La Home reale e' overview(): va preservata insieme a header, scorciatoie e comportamento; il redesign non deve alterarne il markup. Widget si divide nelle subview Widget/Aspetto/Launcher usando subnav() e panel(); Aggiornamenti e Info diventano compatti; Piani energetici, Automazioni, Strumenti, Controllo remoto e Monitoraggio vengono rifiniti senza cambiare le logiche di sistema. Non fondere Piano attivo e Mantieni PC attivo, non eliminare controlli, non aggiungere router o dipendenze."
    },
    {
      "date": "2026-11-03",
      "topic": "Esecuzione dei test statici",
      "summary": "Eseguire i test statici solo per modifiche aggressive che rischiano di introdurre regressioni, non automaticamente per ogni modifica."
    },
    {
      "date": "2026-11-02",
      "topic": "UI motion & transitions",
      "summary": "Le animazioni dell'applicazione erano eccessivamente rapide e scattose, in particolare la chiusura della barra laterale che nascondeva bruscamente i testi prima del completamento del movimento. I parametri di transizione e le curve di easing sono stati resi più lenti, fluidi e tranquilli in motion.css, redesign.css, polish.css e ui-reorganization.css."
    }
  ],
  "fixes": [
    {
      "date": "2026-11-03",
      "topic": "Dimensione minima finestra principale",
      "summary": "La dimensione minima desiderata della finestra principale è 1100×700 unità WPF per ridurre i tagli nell'interfaccia; resta adattabile all'area di lavoro dei monitor più piccoli. Mantenere coerenti XAML, costanti C# e test."
    }
  ]
}
```
