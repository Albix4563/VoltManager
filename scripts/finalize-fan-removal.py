from pathlib import Path

path = Path("src/VoltManager/Models/Models.cs")
text = path.read_text(encoding="utf-8-sig")
legacy = (
    "ControlAvailable",
    "ControlIdentifier",
    "ControlMode",
    "ControlPercent",
    "ControlMin",
    "ControlMax",
)
lines = text.splitlines()
removed = [line for line in lines if any(name in line for name in legacy)]
if len(removed) != 6:
    raise RuntimeError(f"Expected six legacy control properties, found {len(removed)}")
path.write_text(
    "\n".join(line for line in lines if not any(name in line for name in legacy)) + "\n",
    encoding="utf-8",
    newline="\n",
)
