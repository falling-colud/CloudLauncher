"""
Builds the static Pixelify Sans weights the launcher embeds (Assets/Fonts/PixelifySans-*.ttf).

Pixelify Sans ships as one variable font (wght 400-700). WPF only exposes a variable font's default
instance, so a SemiBold heading would be simulated (smeared faux bold, which ruins a pixel face).
Static instances give WPF a real face per weight under one family name.

Source: the variable PixelifySans TTF from https://github.com/eifetx/Pixelify-Sans, passed as the
first argument or through the PIXELIFY_TTF environment variable.
License: SIL OFL 1.1, no reserved font name, so the instances keep the family name.
Run: python tools/fonts/build_pixelify.py path/to/PixelifySans.ttf   (needs fontTools)
"""
import os
import sys

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

SRC = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("PIXELIFY_TTF", "PixelifySans.ttf")
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "CloudLauncher", "Assets", "Fonts")

WEIGHTS = [(400, "Regular"), (500, "Medium"), (600, "SemiBold"), (700, "Bold")]


def set_names(font, style, weight):
    name = font["name"]
    family = "Pixelify Sans"
    full = family if style == "Regular" else f"{family} {style}"
    ps = "PixelifySans-" + style
    # WPF groups faces by the typographic family (16/17) when present, else by 1/2. Write both so
    # every weight lands in one family and is chosen by FontWeight, never simulated.
    legacy_family = family if style in ("Regular", "Bold") else f"{family} {style}"
    legacy_sub = style if style in ("Regular", "Bold") else "Regular"
    for rec in list(name.names):
        if rec.nameID in (1, 2, 3, 4, 6, 16, 17, 25):
            name.removeNames(nameID=rec.nameID)
    name.setName(legacy_family, 1, 3, 1, 0x409)
    name.setName(legacy_sub, 2, 3, 1, 0x409)
    name.setName(f"{ps};CloudLauncher-static", 3, 3, 1, 0x409)
    name.setName(full, 4, 3, 1, 0x409)
    name.setName(ps, 6, 3, 1, 0x409)
    name.setName(family, 16, 3, 1, 0x409)
    name.setName(style, 17, 3, 1, 0x409)
    os2 = font["OS/2"]
    os2.usWeightClass = weight
    # fsSelection: bit 0 italic, bit 5 bold, bit 6 regular.
    sel = os2.fsSelection & ~((1 << 0) | (1 << 5) | (1 << 6))
    sel |= (1 << 5) if weight >= 700 else (1 << 6) if weight == 400 else 0
    os2.fsSelection = sel
    head = font["head"]
    head.macStyle = (head.macStyle & ~1) | (1 if weight >= 700 else 0)


def drop_standard_ligatures(font):
    """Removes the 'liga' feature, which WPF applies by default.

    Pixelify's fi and fl ligatures run the hook and the dot together into something that reads as a
    capital A ("Config" shows as "ConAg"). Only the feature record is dropped; its lookups stay,
    unreferenced.
    """
    if "GSUB" not in font:
        return
    gsub = font["GSUB"].table
    records = gsub.FeatureList.FeatureRecord
    keep = [i for i, rec in enumerate(records) if rec.FeatureTag != "liga"]
    remap = {old: new for new, old in enumerate(keep)}
    gsub.FeatureList.FeatureRecord = [records[i] for i in keep]
    gsub.FeatureList.FeatureCount = len(keep)
    for script in gsub.ScriptList.ScriptRecord:
        systems = [script.Script.DefaultLangSys] + [rec.LangSys for rec in script.Script.LangSysRecord]
        for lang in systems:
            if lang is None:
                continue
            lang.FeatureIndex = [remap[i] for i in lang.FeatureIndex if i in remap]
            lang.FeatureCount = len(lang.FeatureIndex)
            if lang.ReqFeatureIndex != 0xFFFF:
                lang.ReqFeatureIndex = remap.get(lang.ReqFeatureIndex, 0xFFFF)


def main():
    os.makedirs(OUT, exist_ok=True)
    for weight, style in WEIGHTS:
        var = TTFont(SRC)
        inst = instancer.instantiateVariableFont(var, {"wght": weight})
        for tag in ("STAT", "fvar", "avar", "gvar", "HVAR", "MVAR"):
            if tag in inst:
                del inst[tag]
        set_names(inst, style, weight)
        drop_standard_ligatures(inst)
        path = os.path.abspath(os.path.join(OUT, f"PixelifySans-{style}.ttf"))
        inst.save(path)
        print("wrote", path, os.path.getsize(path))


if __name__ == "__main__":
    sys.exit(main())
