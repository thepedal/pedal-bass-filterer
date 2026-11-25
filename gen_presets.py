#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 thepedal.
"""Generate 'Pedal Bass Filterer.prs.xml' (Build §3). Not deployed.

    python gen_presets.py          write the bundle
    python gen_presets.py --check  verify declarations and ranges against PedalBassFilterer.cs
"""
import re
import sys
from pathlib import Path
from xml.sax.saxutils import quoteattr

HERE = Path(__file__).resolve().parent
MACHINE = "Pedal Bass Filterer"
OUT = HERE / f"{MACHINE}.prs.xml"
SRC = HERE / "PedalBassFilterer.cs"

# Global parameters in declaration order: name -> (min, max, default)
PARAMS = {
    "Cutoff": (0, 240, 120),
    "Resonance": (0, 240, 120),
    "Filter": (0, 7, 6),
    "LFO rate": (0, 254, 30),
    "LFO mod": (0, 240, 60),
    "LFO Shape": (0, 16, 0),
    "Inertia": (0, 60, 10),
    "Set phase": (0, 127, 0),
    "Thev factor": (0, 20, 0),
    "Channels": (0, 1, 0),
}

# Filter: 0 Multipeak, 1 Separated, 2 HiSquelch, 3 TwinPeaks, 4 FrontFlt, 5 LaserOne, 6 FMish, 7 Notchez
# LFO rate 240+ is a cycle in ticks: 240=1 241=2 242=3 243=4 244=6 245=8 246=12 247=16 248=24 249=28 250=32 251=48 252=64
PRESETS = {
    "Init": {},
    # The FSM Philta settings of the bass in amanita_aliena_26_072 (pairs with the
    # SquareSpace2 preset of the same name).
    "pedal_bass_001": dict(Cutoff=28, Resonance=0, Filter=2, **{"LFO rate": 247, "LFO mod": 80,
                           "LFO Shape": 0, "Inertia": 0, "Set phase": 0}),
    "Static Low-pass": dict(Cutoff=60, Resonance=40, Filter=2, **{"LFO mod": 0, "Inertia": 0}),
    "Squelch Sweep": dict(Cutoff=80, Resonance=200, Filter=2, **{"LFO rate": 245, "LFO mod": 120, "Inertia": 0}),
    "Slow Multipeak": dict(Cutoff=100, Resonance=160, Filter=0, **{"LFO rate": 252, "LFO mod": 100, "Inertia": 20}),
    "Twin Peaks Wobble": dict(Cutoff=110, Resonance=200, Filter=3, **{"LFO rate": 243, "LFO mod": 90,
                              "LFO Shape": 4, "Inertia": 0}),
    "Laser Steps": dict(Cutoff=140, Resonance=180, Filter=5, **{"LFO rate": 247, "LFO mod": 120,
                        "LFO Shape": 9, "Inertia": 0}),
    "FMish Vowel": dict(Cutoff=100, Resonance=150, Filter=6, **{"LFO rate": 249, "LFO mod": 160, "Inertia": 4}),
    "Notch Drift": dict(Cutoff=120, Resonance=60, Filter=7, **{"LFO rate": 20, "LFO mod": 200, "Inertia": 30}),
    "SnH Chatter": dict(Cutoff=130, Resonance=120, Filter=1, **{"LFO rate": 241, "LFO mod": 140,
                        "LFO Shape": 15, "Inertia": 2}),
}


def values(preset):
    v = {k: d for k, (_, _, d) in PARAMS.items()}
    v.update(preset)
    return v


def parse_decls():
    text = SRC.read_text(encoding="utf-8")
    decls = []
    for m in re.finditer(r"\[ParameterDecl\((.*?)\)\]", text, re.S):
        body = m.group(1)
        if re.search(r"IsStateless\s*=\s*true", body):
            continue
        decls.append(re.search(r'Name\s*=\s*"([^"]+)"', body).group(1))
    return decls


def check():
    ok = True
    names = parse_decls()
    if names != list(PARAMS):
        print(f"declaration order mismatch: source {names} vs script {list(PARAMS)}")
        ok = False
    for preset, over in PRESETS.items():
        for k in over:
            if k not in PARAMS:
                print(f"{preset}: unknown parameter {k}")
                ok = False
        for name, v in values(over).items():
            lo, hi, _ = PARAMS[name]
            if not lo <= v <= hi:
                print(f"{preset}: {name}={v} outside {lo}..{hi}")
                ok = False
    print("check OK" if ok else "check FAILED")
    return ok


def write():
    lines = ['<?xml version="1.0" encoding="utf-8"?>', "<PresetDictionary>"]
    for key, over in PRESETS.items():
        v = values(over)
        lines.append(f"  <Item Key={quoteattr(key)}>")
        lines.append(f"    <Preset Machine={quoteattr(MACHINE)}>")
        lines.append("      <Parameters>")
        for idx, name in enumerate(PARAMS):
            lines.append(f'        <Parameter Name={quoteattr(name)} Group="1" Index="{idx}" Track="0" Value="{v[name]}" />')
        lines.append("      </Parameters>")
        lines.append("      <Attributes />")
        lines.append("      <Comment></Comment>")
        lines.append("    </Preset>")
        lines.append("  </Item>")
    lines.append("</PresetDictionary>")
    with open(OUT, "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"wrote {OUT.name} ({len(PRESETS)} presets)")


if __name__ == "__main__":
    if not check():
        sys.exit(1)
    if "--check" not in sys.argv:
        write()
