# Localization contracts

VoltManager supports Italian (`it`), English (`en`), Spanish (`es`) and Simplified Chinese (`zh`) through three platform adapters with automated parity checks.

## Web

All web dictionaries live in `src/VoltManager/wwwroot/js/i18n.catalogs.js`. Catalogs are split by feature namespace (`core`, `settings`, `power`, `planHistory`, `uiReorganization`, `updateSuspension`) while lookup, fallback and formatting live in `i18n.js`.

Add a web string by adding the same key to all four languages in the appropriate namespace. Keep placeholder names identical across languages. Feature code calls `I18n.feature(namespace, key)`; it must not embed per-language dictionaries. English is the explicit fallback, then the key/fallback supplied by the caller.

`I18n.format`, `I18n.number`, `I18n.date` and `I18n.plural` are the shared formatting entry points. `langchanged` remains the runtime signal used by eager, lazy and widget views.

## Host

Native host strings remain in `Localization/NativeStrings*.resx`, because .NET resource lookup is the platform adapter. `LocalizationService` owns fallback and culture-aware formatting. Add a key to the neutral English resource and every satellite resource.

## Setup

Setup remains a .NET Framework executable with its own `Engine/I18n.cs` adapter. Add a setup key to `En`, `It`, `Es` and `Zh` together. Setup falls back to the key for an unknown entry.

## Automated contract

`tests/i18n-contract.test.mjs` verifies web language/key/value/placeholder parity and prevents feature-local dictionaries. `LocalizationContractTests` validates host resources, and `SetupLocalizationContractTests` validates Setup dictionaries. Existing UI tests cover runtime language refresh, lazy views, widgets, localized dates/numbers and feature-specific navigation/search strings.
