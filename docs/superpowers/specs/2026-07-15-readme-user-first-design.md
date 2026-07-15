# README user-first — Design

**Data:** 2026-07-15
**Stato:** approvato

## Obiettivo

Trasformare `README.md` in una guida affidabile per utenti finali: capire rapidamente cosa fa VoltManager, verificare la compatibilità del PC, installarlo, usarlo e risolvere i problemi comuni. Conservare una sezione sviluppatori breve e aderente al repository.

## Struttura

1. Identità, descrizione breve, badge.
2. Funzioni principali, divise tra uso quotidiano e avanzato.
3. Requisiti minimi e raccomandati, prima dell’installazione.
4. Installazione tramite installer o portable.
5. Primo avvio e uso essenziale.
6. Adattamento automatico delle risorse.
7. Privacy, rete e privilegi con formulazioni non contraddittorie.
8. Risoluzione problemi essenziale.
9. Documentazione sviluppatori: stack, struttura, build, verifiche disponibili, distribuzione.

## Correzioni richieste

- Sostituire «100% offline» con «elaborazione locale»; gli aggiornamenti GitHub richiedono rete.
- Rimuovere riferimenti a test unitari, `scripts/smoke_test.ps1` e `.build_tools`, assenti dal repository.
- Documentare solo le verifiche presenti: build soluzione, sintassi JavaScript, self-check risorse, benchmark.
- Chiarire installer e portable self-contained; WebView2 resta necessario, bootstrapper incluso negli artefatti di release.
- Usare esclusivamente il nome VoltManager nel testo utente; non documentare il prefisso registry legacy delle voci gestite.
- Evitare versioni hardcoded nei nomi degli artefatti quando possibile.

## Requisiti documentati

- Windows 10 64 bit 1809+ o Windows 11.
- CPU x64 dual-core; 4 core raccomandati.
- 4 GB RAM; 8 GB raccomandati.
- Display 640×480; 1280×720 raccomandato.
- 250 MB di spazio libero.
- Microsoft Edge WebView2 Runtime.
- Privilegi amministratore.
- .NET 8 SDK solo per compilare; artefatti distribuiti self-contained.

## Accuratezza

Ogni comando deve corrispondere a un file o progetto presente. I link devono essere relativi e validi nel repository GitHub. Nessuna promessa assoluta su autonomia batteria, prestazioni o disponibilità sensori.

## Verifica

- Controllo link e path locali citati.
- Controllo comandi contro soluzione, csproj e script presenti.
- Scansione di riferimenti obsoleti: test rimossi, smoke test, `.build_tools`, Miliano, «100% offline».
- `git diff --check`.

## Fuori scope

Modifiche applicative, nuove immagini, screenshot, changelog storico, traduzione completa del README.
