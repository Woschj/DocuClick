#!/usr/bin/env python3
"""Prepares releases of the three parts: versions, changelog dates, tags.

    python3 tools/release.py windows 1.16.0 obsidian 0.7.0 [--date 2026-10-01] [--tag]

- windows  -> windows/DocuClick/DocuClick.csproj <Version>, windows/CHANGELOG.md, tag v<version>
- macos    -> macos/DocuClick.Mac/DocuClick.Mac.csproj <Version>, macos/CHANGELOG.md, tag macos-v<version>
- obsidian -> obsidian/manifest.json "version", obsidian/CHANGELOG.md, tag obsidian-v<version>

The changelog's "## [Unreleased]" heading becomes "## [<version>] - <date>".
Without --tag only the files change (review, commit, merge to main); with
--tag it also creates the annotated tags on the current commit - run that
on main after the merge, then `git push origin <tags>` starts the release
workflows. Nothing is pushed by this script.
"""
import argparse
import datetime
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PARTS = {
    "windows": ("windows/DocuClick/DocuClick.csproj", "windows/CHANGELOG.md", "v"),
    "macos": ("macos/DocuClick.Mac/DocuClick.Mac.csproj", "macos/CHANGELOG.md", "macos-v"),
    "obsidian": ("obsidian/manifest.json", "obsidian/CHANGELOG.md", "obsidian-v"),
}


def set_version(path: Path, version: str) -> None:
    text = path.read_text(encoding="utf-8")
    if path.suffix == ".json":
        data = json.loads(text)
        data["version"] = version
        path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        return
    new, count = re.subn(r"<Version>[^<]*</Version>", f"<Version>{version}</Version>", text, count=1)
    if not count:
        sys.exit(f"{path}: kein <Version> gefunden")
    path.write_text(new, encoding="utf-8")


def date_changelog(path: Path, version: str, date: str) -> None:
    text = path.read_text(encoding="utf-8")
    if f"## [{version}]" in text:
        return  # already dated
    new, count = re.subn(r"^## \[Unreleased\].*$", f"## [{version}] - {date}", text, count=1, flags=re.M)
    if not count:
        sys.exit(f"{path}: kein Abschnitt '## [Unreleased]' - bitte zuerst die Änderungen dort eintragen")
    path.write_text(new, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("pairs", nargs="+", help="part version [part version ...]")
    parser.add_argument("--date", default=datetime.date.today().isoformat())
    parser.add_argument("--tag", action="store_true", help="also create the annotated git tags")
    args = parser.parse_args()
    if len(args.pairs) % 2:
        sys.exit("Erwartet: Teil Version [Teil Version ...]")
    tags = []
    for part, version in zip(args.pairs[::2], args.pairs[1::2]):
        if part not in PARTS or not re.fullmatch(r"\d+\.\d+\.\d+", version):
            sys.exit(f"Unbekannter Teil oder Version: {part} {version}")
        project, changelog, prefix = PARTS[part]
        set_version(ROOT / project, version)
        date_changelog(ROOT / changelog, version, args.date)
        tags.append(prefix + version)
        print(f"{part}: {version} ({args.date})")
    if args.tag:
        for tag in tags:
            subprocess.run(["git", "tag", "-a", tag, "-m", tag], cwd=ROOT, check=True)
        print("Tags angelegt. Veröffentlichen mit: git push origin " + " ".join(tags))
    else:
        print("Dateien angepasst. Nach dem Merge auf main mit --tag die Tags anlegen: " + " ".join(tags))


if __name__ == "__main__":
    main()
