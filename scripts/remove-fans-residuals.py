from pathlib import Path
import re


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8-sig")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace(path: str, old: str, new: str) -> None:
    text = read(path)
    if old not in text:
        raise RuntimeError(f"Expected text missing in {path}: {old}")
    write(path, text.replace(old, new))


replace(
    "build.ps1",
    "# The app only enables software fan writes when this process is available.",
    "# The app prefers this isolated process for hardware sensor reads.",
)
replace(
    "src/VoltManager.Setup/Engine/HardenedInstallEngine.cs",
    "            // watcher can restore every software-owned fan channel before exiting.\n",
    "",
)
replace(
    "src/VoltManager/App.AdaptiveResources.cs",
    "        // MonitorService interval used by fan/thermal/power automation.",
    "        // MonitorService interval used by thermal and power automation.",
)

# Remove the secondary reliability cleanup path for the deleted service.
path = "src/VoltManager/App.Reliability.cs"
text = read(path)
text = "\n".join(line for line in text.splitlines() if 'new CleanupStep("fan management"' not in line) + "\n"
write(path, text)

replace(
    "src/VoltManager/Performance/ResourcePressureState.cs",
    "/// fan control and thermal automation do not depend on this profile.",
    "/// thermal automation does not depend on this profile.",
)
replace(
    "src/VoltManager/Services/MonitorService.cs",
    "    // Prefer live temps/fans; drop secondary clocks. Hard ceiling keeps the",
    "    // Prefer live temperatures; drop secondary clocks. Hard ceiling keeps the",
)
replace(
    "src/VoltManager/Services/MonitorService.cs",
    '            if (r.Type is "temp" or "fan") preferred.Add(r);',
    '            if (r.Type == "temp") preferred.Add(r);',
)
replace(
    "src/VoltManager/Services/PowerPlanGuardService.cs",
    '        "eco", "profile", "thermal", "fancontrol"',
    '        "eco", "profile", "thermal"',
)
replace(
    "src/VoltManager/wwwroot/js/bridge.js",
    "    // host resource policy permits. Safety/thermal/fan RPCs never pass this gate.",
    "    // host resource policy permits. Safety and thermal RPCs never pass this gate.",
)
replace(
    "src/VoltManager/wwwroot/js/changelog.js",
    "js/ui-reorganization.i18n.js?v=fans1",
    "js/ui-reorganization.i18n.js?v=reorg1",
)
replace(
    "src/VoltManager/wwwroot/js/changelog.js",
    "js/ui-reorganization.layout.js?v=fans1",
    "js/ui-reorganization.layout.js?v=reorg1",
)

# Temperature monitoring remains, but all airflow-specific presentation is removed.
path = "src/VoltManager/wwwroot/index.html"
text = read(path)
text = text.replace("<!-- Temperatures & Fans (hidden until live sensors exist) -->", "<!-- Temperatures (hidden until live sensors exist) -->")
text = text.replace("Temperatures &amp; Fans", "Temperatures")
write(path, text)

path = "src/VoltManager/wwwroot/js/dashboard.js"
text = read(path)
text = text.replace("    // ----- Temperatures & fans -----", "    // ----- Temperatures -----")
text = text.replace("        return s.type === 'fan' ? Math.round(s.value) + ' RPM' : Math.round(s.value) + '°C';", "        return Math.round(s.value) + '°C';")
write(path, text)

# Rename dashboard strings in every supported locale without touching the generic thermal safety feature.
path = "src/VoltManager/wwwroot/js/i18n.js"
text = read(path)
replacements = {
    '"dash_temps_title": "Temperatures & Fans"': '"dash_temps_title": "Temperatures"',
    '"dash_temps_title": "Temperature e Ventole"': '"dash_temps_title": "Temperature"',
    '"dash_temps_title": "Temperaturas y ventiladores"': '"dash_temps_title": "Temperaturas"',
    '"dash_temps_title": "Temperaturas y Ventiladores"': '"dash_temps_title": "Temperaturas"',
}
for old, new in replacements.items():
    text = text.replace(old, new)
write(path, text)

# Keep thermal protection terminology, but stop calling it cooling.
path = "src/VoltManager/wwwroot/js/power.js"
text = read(path)
text = text.replace("thermalBadgeActive: 'Raffreddamento attivo'", "thermalBadgeActive: 'Protezione termica attiva'")
text = text.replace("thermalBadgeActive: 'Cooling active'", "thermalBadgeActive: 'Thermal protection active'")
write(path, text)

# Remove tests that only asserted the now-deleted RPC names were excluded from a pressure gate.
path = "tests/resource-pressure.test.mjs"
text = read(path)
text = "\n".join(
    line for line in text.splitlines()
    if "getFan" not in line and "getFanControlState" not in line
) + "\n"
write(path, text)
