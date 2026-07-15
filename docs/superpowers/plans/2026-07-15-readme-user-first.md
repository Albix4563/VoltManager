# README user-first Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rendere `README.md` una guida accurata e immediata per utenti finali, mantenendo una sezione sviluppatori essenziale.

**Architecture:** Riscrivere un solo documento, ordinando i contenuti per percorso utente. Verificare ogni requisito, path e comando contro i file presenti nel repository.

**Tech Stack:** Markdown, PowerShell, .NET 8, WPF, WebView2.

## Global Constraints

- Modificare solo documentazione.
- Nessuna nuova immagine o screenshot.
- Nome prodotto sempre `VoltManager`.
- Nessun riferimento a test, smoke script o tool assenti.
- Nessuna promessa «100% offline»: aggiornamenti e download richiedono rete.
- Requisiti minimi: Windows 10 64 bit 1809+, x64 dual-core, 4 GB RAM, 640×480, 250 MB, WebView2, amministratore.

---

### Task 1: Riscrivere README

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: `build.ps1`, `VoltManager.sln`, csproj, workflow release, script presenti.
- Produces: guida user-first con sezione sviluppatori verificata.

- [x] Sostituire struttura corrente con: introduzione, funzionalità, requisiti, installazione, primo uso, adattamento risorse, privacy/rete, troubleshooting, sviluppatori.
- [x] Eliminare «100% offline», `scripts/smoke_test.ps1`, `.build_tools`, test unitari rimossi e riferimenti utente a `Miliano's App`.
- [x] Documentare installer e portable self-contained; WebView2 richiesto, bootstrapper incluso negli artefatti.
- [x] Documentare verifiche reali:

```powershell
dotnet build VoltManager.sln -c Release
Get-ChildItem src\VoltManager\wwwroot\js\*.js | ForEach-Object { node --check $_.FullName }
node scripts\check-resource-optimizations.mjs
powershell -File scripts\benchmark-monitor.ps1
```

### Task 2: Verificare documentazione

**Files:**
- Verify: `README.md`

- [x] Cercare riferimenti obsoleti:

```powershell
Select-String -Path README.md -Pattern '100% offline|smoke_test|\.build_tools|Miliano|dotnet test'
```

Expected: nessun match.

- [x] Verificare tutti i path in backtick contro il repository; escludere esempi, wildcard, registry e output generati.
- [x] Eseguire `git diff --check`.
- [x] Revisionare leggibilità: requisiti prima dell’installazione, dettagli tecnici dopo la guida utente, nessuna duplicazione.

## Self-review

- Copertura spec: completa.
- Placeholder: nessuno.
- Scope: solo documentazione.
- Comandi: aderenti ai file presenti.
