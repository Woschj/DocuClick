#!/usr/bin/env python3
"""Dependency-free build: bundle the shared viewer and CommonJS plugin sources."""
import json
from pathlib import Path
import shutil
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
ASSETS = ROOT / "core/DocuClick.Core/WebAssets"
OUT = ROOT / "dist/obsidian-docuclick/docuclick-diagrams"

def build():
    OUT.mkdir(parents=True, exist_ok=True)
    module = (HERE / "src/document.js").read_text()
    main = (HERE / "src/main.js").read_text()
    bundle = '"use strict";\nconst DocuClickDocument = (() => { const module = {exports: {}};\n' + module + '\nreturn module.exports; })();\n'
    bundle += "const VIEWER_TEMPLATE = " + json.dumps((ASSETS / "viewer.template.html").read_text(), ensure_ascii=True) + ";\n"
    bundle += "const CYTOSCAPE = " + json.dumps((ASSETS / "vendor/cytoscape.min.js").read_text(), ensure_ascii=True) + ";\n"
    bundle += main
    (OUT / "main.js").write_text(bundle)
    for name in ("manifest.json", "styles.css", "README.md"):
        shutil.copyfile(HERE / name, OUT / name)
    vendor = (ASSETS / "vendor/cytoscape.min.js").read_text()
    (OUT / "THIRD-PARTY-NOTICES.txt").write_text(vendor[:vendor.index("*/") + 2] + "\n")
    archive = OUT.parent / "docuclick-diagrams.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(OUT.iterdir()):
            z.write(path, f"{OUT.name}/{path.name}")
    print(archive)

if __name__ == "__main__":
    build()
