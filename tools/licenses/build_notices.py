"""Writes THIRD-PARTY-NOTICES.txt for the Windows client.

Reads the packages the client actually resolves (project.assets.json from a restore), their
nuspec metadata and bundled license files from the local NuGet cache, the .NET runtime packs
bundled by self-contained publishes, and the font licenses in the repo.

    python tools/licenses/build_notices.py <path to CloudLauncher's project.assets.json>

Run it after changing packages or the runtime version, then commit the result.
"""
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
NUGET = os.environ.get("NUGET_PACKAGES") or os.path.join(os.path.expanduser("~"), ".nuget", "packages")
RUNTIME_PATCH = "10.0.12"

MIT = """Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."""

BSD2 = """Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions
   and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE."""

# Copyright lines for packages whose nuspec has none.
COPYRIGHT_FALLBACK = {
    "cmllib.core": "Copyright (c) 2023 AlphaBs",
    "cmllib.core.commons": "Copyright (c) 2024 AlphaBs",
    "cmllib.core.auth.microsoft": "Copyright (c) 2023 AlphaBs",
    "cmllib.core.auth.microsoft.ui.wpf": "Copyright (c) 2021 AlphaBs",
    "cmllib.core.installer.forge": "Copyright (c) 2023 CmlLib",
    "cmllib.core.installer.neoforge": "Copyright (c) 2026 GamerVII",
    "xboxauthnet": "Copyright (c) 2023 AlphaBs",
    "xboxauthnet.game": "Copyright (c) 2023 AlphaBs",
    "htmlagilitypack": "Copyright (c) ZZZ Projects Inc.",
    "sharpziplib": "Copyright (c) 2000-2022 SharpZipLib Contributors",
    "lzma-sdk": "Copyright (c) 2020 Mihir Mone. LZMA SDK by Igor Pavlov (public domain).",
    "markdig": "Copyright (c) 2018-2019, Alexandre Mutel",
}


def read(path):
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        return f.read().replace("\r\n", "\n").strip()


def nuspec_info(pkg_dir, pkg_id):
    info = {"license": None, "copyright": None, "url": None, "files": []}
    for name in os.listdir(pkg_dir):
        if name.lower().endswith(".nuspec"):
            text = read(os.path.join(pkg_dir, name))
            text = re.sub(r'\sxmlns="[^"]+"', "", text, count=1)
            root = ET.fromstring(text)
            meta = root.find("metadata")
            lic = meta.find("license")
            if lic is not None:
                info["license"] = (lic.get("type"), (lic.text or "").strip())
            elif meta.find("licenseUrl") is not None:
                info["license"] = ("url", meta.find("licenseUrl").text.strip())
            if meta.find("copyright") is not None and meta.find("copyright").text:
                info["copyright"] = meta.find("copyright").text.strip()
            repo = meta.find("repository")
            url = meta.find("projectUrl")
            info["url"] = (url.text.strip() if url is not None and url.text else None) or (repo.get("url") if repo is not None else None)
    for name in os.listdir(pkg_dir):
        low = name.lower()
        if os.path.isfile(os.path.join(pkg_dir, name)) and (low.startswith("license") or "notice" in low):
            info["files"].append(os.path.join(pkg_dir, name))
    if not info["copyright"]:
        info["copyright"] = COPYRIGHT_FALLBACK.get(pkg_id)
    return info


def main():
    assets = json.load(open(sys.argv[1], encoding="utf-8"))
    target = next(k for k in assets["targets"] if k.startswith("net10.0-windows"))
    packages = []
    for key, val in assets["targets"][target].items():
        if val.get("type") != "package":
            continue
        pkg_id, version = key.split("/")
        runtime = [p for p in val.get("runtime", {}) if not p.endswith("_._")]
        if not runtime and not val.get("runtimeTargets") and not val.get("native"):
            continue  # nothing of it ships
        packages.append((pkg_id, version))
    packages.sort(key=lambda p: p[0].lower())

    out = []
    out.append("CloudLauncher - third-party notices")
    out.append("")
    out.append("CloudLauncher includes the components listed below. Each is used under its own")
    out.append("license, reproduced or referenced here. CloudLauncher itself is covered by LICENSE.txt.")
    out.append("")
    out.append("=" * 78)
    out.append("Components")
    out.append("=" * 78)
    details = []
    for pkg_id, version in packages:
        pkg_dir = os.path.join(NUGET, pkg_id.lower(), version.lower())
        if not os.path.isdir(pkg_dir):
            sys.exit(f"missing from the NuGet cache: {pkg_id} {version} (run a restore first)")
        info = nuspec_info(pkg_dir, pkg_id.lower())
        kind, value = info["license"] or ("?", "?")
        lic_name = value if kind == "expression" else ("see below" if kind == "file" else value)
        out.append(f"- {pkg_id} {version}  ({lic_name})")
        if info["url"]:
            out.append(f"  {info['url']}")
        details.append((pkg_id, version, kind, value, info))

    out.append("- .NET runtime and Windows Desktop runtime " + RUNTIME_PATCH + "  (MIT)")
    out.append("  https://github.com/dotnet/runtime, https://github.com/dotnet/wpf")
    out.append("- Pixeloid Sans font  (SIL Open Font License 1.1)")
    out.append("  https://ggbot.itch.io/pixeloid-font")
    out.append("- Monocraft font  (SIL Open Font License 1.1)")
    out.append("  https://github.com/IdreesInc/Monocraft")
    out.append("- Pixelify Sans font  (SIL Open Font License 1.1)")
    out.append("  https://github.com/eifetx/Pixelify-Sans")
    out.append("- Slate Icons font  (MIT)")
    out.append("")

    for pkg_id, version, kind, value, info in details:
        out.append("=" * 78)
        out.append(f"{pkg_id} {version}")
        out.append("=" * 78)
        if info["copyright"]:
            out.append(info["copyright"])
            out.append("")
        if kind == "expression" and value == "MIT" or (kind == "url" and "html-agility-pack" in value):
            out.append("MIT License")
            out.append("")
            out.append(MIT)
        elif kind == "expression" and value == "BSD-2-Clause":
            out.append("BSD 2-Clause License")
            out.append("")
            out.append(BSD2)
        for f in info["files"]:
            if f.lower().endswith((".txt", ".md")) or os.path.basename(f).upper() in ("LICENSE", "NOTICE"):
                out.append("")
                out.append(f"--- {os.path.basename(f)} ---")
                out.append(read(f))
        out.append("")

    for pack in ("microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64"):
        pack_dir = os.path.join(NUGET, pack, RUNTIME_PATCH)
        if not os.path.isdir(pack_dir):
            sys.exit(f"missing runtime pack {pack} {RUNTIME_PATCH}: publish the client self-contained once to restore it")
        out.append("=" * 78)
        out.append(f"{pack} {RUNTIME_PATCH}")
        out.append("=" * 78)
        for name in sorted(os.listdir(pack_dir)):
            low = name.lower()
            if low.startswith("license") or "notice" in low:
                out.append(f"--- {name} ---")
                out.append(read(os.path.join(pack_dir, name)))
                out.append("")

    fonts = os.path.join(ROOT, "CloudLauncher", "Assets", "Fonts")
    for title, name in (("Pixeloid Sans", "PixeloidSans-OFL.txt"), ("Monocraft", "Monocraft-OFL.txt")):
        out.append("=" * 78)
        out.append(title)
        out.append("=" * 78)
        out.append(read(os.path.join(fonts, name)))
        out.append("")

    out.append("=" * 78)
    out.append("Pixelify Sans")
    out.append("=" * 78)
    out.append(read(os.path.join(ROOT, "CloudLauncher.Server", "wwwroot", "fonts", "PixelifySans-OFL.txt")))
    out.append("")
    out.append("=" * 78)
    out.append("Slate Icons")
    out.append("=" * 78)
    out.append(read(os.path.join(ROOT, "CloudLauncher.Server", "wwwroot", "fonts", "SlateIcons-LICENSE.txt")))
    out.append("")

    path = os.path.join(ROOT, "THIRD-PARTY-NOTICES.txt")
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("\n".join(out))
    print(f"wrote {path}: {len(packages)} packages")


if __name__ == "__main__":
    main()
