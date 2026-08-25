from pathlib import Path

path = Path("src/VoltManager/App.xaml.cs")
text = path.read_text(encoding="utf-8-sig")
old = "            ?? new HardwareAccessCoordinator(controlWritesAllowed: false);"
new = "            ?? new HardwareAccessCoordinator();"
if old not in text:
    raise RuntimeError("Expected obsolete HardwareAccessCoordinator constructor call was not found")
path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")
