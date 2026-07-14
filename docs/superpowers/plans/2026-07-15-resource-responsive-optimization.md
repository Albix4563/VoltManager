# Ottimizzazione risorse e responsività Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ridurre misurabilmente CPU/allocazioni; rendere VoltManager utilizzabile da 640×480 senza regressioni.

**Architecture:** Conservare il timer host dell’automazione, sostituire WMI RAM frequente con Win32, diradare fallback costosi. Fermare solo rendering/poll UI invisibili; applicare CSS e motion per tier esistente.

**Tech Stack:** .NET 8 WPF, WebView2, JavaScript/CSS, PowerShell, Node.js.

## Global Constraints

- Nessuna nuova dipendenza.
- Nessuna modifica a piani energetici, priorità automazioni o sicurezza.
- Build e sintassi moduli verdi.
- Massimo cinque cicli verifica.
- Widget e automazioni attivi con finestra nascosta.
- Finestra utilizzabile da 640×480.

### Task 1: Sampling host

- [x] Creare benchmark WMI/Win32 RAM.
- [x] Sostituire query RAM per tick con `GlobalMemoryStatusEx`.
- [x] Cache CPU clock WMI 10 secondi; disporre oggetti WMI.
- [x] Scansione processi adattiva 3/6/10 tick.
- [x] Build app Release.

### Task 2: UI invisibile

- [x] Inoltrare metriche WebView solo se visibile tramite flag thread-safe.
- [x] Fermare poll processi, power flow, battery history e override countdown su `document.hidden`.
- [x] Evitare richieste concorrenti.
- [x] Allineare polling processi al tier 3/6/10 secondi.

### Task 3: Motion e rendering

- [x] Listener pointer/ripple idempotenti, solo tier `full`.
- [x] Reagire a tier, RAM pressure, visibilità, reduced-motion.
- [x] Riutilizzare aurora nascosta.
- [x] Rimuovere layer permanenti e motion continuo in balanced/lite.

### Task 4: Layout 640×480

- [x] Impostare `MinWidth="640" MinHeight="480"`.
- [x] Sidebar rail sotto 900 px.
- [x] Griglie fluide sotto 900/700/560 px.
- [x] Validare XAML e build.

### Task 5: Verifica e requisiti

- [x] Benchmark RAM.
- [x] Build soluzione Release.
- [x] Sintassi di tutti i moduli JS.
- [x] Avvio reale, resize 640×480, minimizzazione, log pulito.
- [x] Aggiornare README requisiti minimi.
- [x] Pulire artefatti temporanei; `git diff --check`.
