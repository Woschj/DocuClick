# DocuClick

Klick-Dokumentation per Screenshot: Jeder Mausklick erzeugt einen Screenshot
mit Markierung und einen Schritt in einem interaktiven **Ablauf**, einer
eigenständigen `.html`-Datei, die sich in jedem Browser öffnen und bearbeiten
lässt.

Das Repository enthält drei eigenständige Teile mit jeweils eigener Version,
eigenem Changelog, eigener CI und eigenen Releases:

| Bereich | Ordner | Release-Tags | Changelog |
|---|---|---|---|
| **DocuClick für Windows** (WPF) | [windows/](windows/README.md) | `v*` (z. B. `v1.14.0`) | [windows/CHANGELOG.md](windows/CHANGELOG.md) |
| **DocuClick für macOS** (Avalonia + Swift) | [macos/](macos/README.md) | `macos-v*` | [macos/CHANGELOG.md](macos/CHANGELOG.md) |
| **DocuClick Diagrams für Obsidian** (Plugin, ohne Screenshot-Aufnahme) | [obsidian/](obsidian/README.md) | `obsidian-v*` | [obsidian/CHANGELOG.md](obsidian/CHANGELOG.md) |

Gemeinsam genutzt:

- [core/](core/README.md): plattformunabhängiger Kern beider Apps
  (Speicherformat, Ablauf-Editor, Session-Logik) mit Tests. Das Plugin
  bündelt daraus die HTML-Vorlage, damit alle drei dasselbe Format sprechen.
- [OutputTemplate/](OutputTemplate/README.md): leere Ordnerstruktur für einen
  Dokumentations-Vault.

Downloads: [Releases](https://github.com/Woschj/DocuClick/releases).

## Entwicklung

```bash
dotnet test --project core/DocuClick.Core.Tests          # gemeinsamer Kern
dotnet build windows/DocuClick.Windows.sln               # Windows-App
dotnet build macos/DocuClick.Mac.slnx                    # macOS-App (nur auf dem Mac)
python3 obsidian/build.py                                # Obsidian-Plugin
```

Die CI-Workflows in [.github/workflows/](.github/workflows/) sind nach
Bereich getrennt (`windows.yml`, `macos.yml`, `obsidian.yml`). Bei Pull
Requests läuft jeder nur, wenn sein Bereich oder der gemeinsame Kern
betroffen ist.
