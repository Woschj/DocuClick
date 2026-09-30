#!/usr/bin/env python3
"""Prepares a DocuClick release: one version for all three parts, one tag.

    python3 tools/release.py 1.18.0 [--date 2026-10-01]

Sets the version of the Windows app (windows/DocuClick/DocuClick.csproj),
the macOS app (macos/DocuClick.Mac/DocuClick.Mac.csproj) and the Obsidian
plugin (obsidian/manifest.json), and dates each changelog: its
"## [Unreleased]" heading becomes "## [<version>] - <date>" (a part without
own changes gets a short entry saying so). Review, commit, merge to main;
then on main:

    git tag -a v1.18.0 -m v1.18.0
    git push origin v1.18.0

The tag starts .github/workflows/release.yml: it builds all three parts and
publishes one GitHub release with all downloads. Nothing is pushed by this
script.

Used by that workflow too:
    --check 1.18.0   fails unless all three parts have this version and a
                     changelog section for it
    --notes 1.18.0   prints the release notes (the three changelog sections)
"""
import argparse
import datetime
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
# part: (title in the release notes, project file, changelog)
PARTS = {
    "windows": ("Windows", "windows/DocuClick/DocuClick.csproj", "windows/CHANGELOG.md"),
    "macos": ("macOS", "macos/DocuClick.Mac/DocuClick.Mac.csproj", "macos/CHANGELOG.md"),
    "obsidian": ("Obsidian-Plugin", "obsidian/manifest.json", "obsidian/CHANGELOG.md"),
}
NO_CHANGES = "- Keine eigenen Änderungen; Version an die anderen Teile angeglichen."


def read_version(path: Path) -> str:
    text = path.read_text(encoding="utf-8")
    if path.suffix == ".json":
        return json.loads(text)["version"]
    match = re.search(r"<Version>([^<]*)</Version>", text)
    if not match:
        sys.exit(f"{path}: kein <Version> gefunden")
    return match.group(1)


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
    heading = f"## [{version}] - {date}"
    new, count = re.subn(r"^## \[Unreleased\].*$", heading, text, count=1, flags=re.M)
    if not count:
        # No own changes: an entry anyway, so every part has this version's section.
        first = re.search(r"^## \[", text, flags=re.M)
        if not first:
            sys.exit(f"{path}: keine Versionsabschnitte gefunden")
        new = f"{text[:first.start()]}{heading}\n\n{NO_CHANGES}\n\n{text[first.start():]}"
    path.write_text(new, encoding="utf-8")


def section(path: Path, version: str) -> str | None:
    """The changelog entries of this version (without the heading), or None."""
    text = path.read_text(encoding="utf-8")
    match = re.search(rf"^## \[{re.escape(version)}\][^\n]*\n(.*?)(?=^## \[|\Z)", text, flags=re.M | re.S)
    return match.group(1).strip() if match else None


def check(version: str) -> None:
    problems = []
    for title, project, changelog in PARTS.values():
        found = read_version(ROOT / project)
        if found != version:
            problems.append(f"{title}: Version {found} in {project}, erwartet {version}")
        if section(ROOT / changelog, version) is None:
            problems.append(f"{title}: kein Abschnitt '## [{version}]' in {changelog}")
    if problems:
        sys.exit("Release passt nicht zum Tag:\n" + "\n".join(problems))


def notes(version: str) -> str:
    parts = [f"## {title}\n\n{section(ROOT / changelog, version) or NO_CHANGES}" for title, _, changelog in PARTS.values()]
    return "\n\n".join(parts) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("version", help="e.g. 1.18.0")
    parser.add_argument("--date", default=datetime.date.today().isoformat())
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="only check that everything has this version")
    mode.add_argument("--notes", action="store_true", help="only print the release notes")
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        sys.exit(f"Ungültige Version: {args.version} (erwartet z. B. 1.18.0)")
    if args.check:
        check(args.version)
        print(f"Alle Teile haben Version {args.version}.")
        return
    if args.notes:
        sys.stdout.write(notes(args.version))
        return
    for title, project, changelog in PARTS.values():
        set_version(ROOT / project, args.version)
        date_changelog(ROOT / changelog, args.version, args.date)
    print(f"Version {args.version} ({args.date}) für Windows, macOS und das Obsidian-Plugin gesetzt.")
    print(f"Nach dem Merge auf main: git tag -a v{args.version} -m v{args.version}  und  git push origin v{args.version}")


if __name__ == "__main__":
    main()
