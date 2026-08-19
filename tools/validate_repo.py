#!/usr/bin/env python3
import json
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
forbidden = ("using UnityEngine", "using Meta.", "using Oculus", "UnityEngine.", "OVR")
violations=[]
for path in CORE.rglob("*.cs"):
    text=path.read_text(encoding="utf-8")
    for marker in forbidden:
        if marker in text: violations.append(f"{path.relative_to(ROOT)}: {marker}")
manifest=json.loads((ROOT/"models"/"models.lock.json").read_text(encoding="utf-8"))
for model in manifest["candidates"]:
    if model.get("bundled") is not False: violations.append(f"model bundled too early: {model.get('id')}")
    for key in ("id","purpose","upstream","license","license_status","bundled"):
        if key not in model: violations.append(f"model missing {key}: {model}")
if violations: raise SystemExit("\n".join(violations))
print(f"PASS: {len(list(CORE.rglob('*.cs')))} core files; boundaries and model manifest validated")
