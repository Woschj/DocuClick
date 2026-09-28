# DocuClick Diagrams für Obsidian

> Teil des DocuClick-Repos: [Übersicht](../README.md) · Windows-App in
> [windows/](../windows/README.md) · macOS-App in [macos/](../macos/README.md).
> Eigene Version (`manifest.json`), eigener [Changelog](CHANGELOG.md), Releases unter `obsidian-v*`.

Eigenständiger Diagrammeditor ohne Screenshot-Aufnahme und ohne laufende
DocuClick-App. Erste Desktop-Version für Obsidian ab 1.5.

## Installation

1. `python3 obsidian/build.py` im Repository ausführen (oder die ZIP aus einem
   Release `obsidian-v…` verwenden).
2. Aus `dist/obsidian-docuclick/docuclick-diagrams.zip` den Ordner
   `docuclick-diagrams` nach `<Vault>/.obsidian/plugins/` kopieren.
   Darin müssen `main.js`, `manifest.json` und `styles.css` liegen.
3. Obsidian neu laden. Unter **Einstellungen → Community-Erweiterungen**
   **DocuClick Diagrams** aktivieren.

Das Build benötigt nur Python 3; Cytoscape wird aus den vorhandenen lokalen
WebAssets eingebunden. Es gibt keine CDN-Abhängigkeit. Das Plugin wird nicht
automatisch in einen vorhandenen Vault installiert.

## Benutzung

Die zusätzliche Menüleiste über dem Diagramm ist seit 0.1.1 entfernt.
Alle Diagrammfunktionen liegen in der bestehenden schwebenden Werkzeugleiste.

- Ribbon-Symbol oder Befehl **DocuClick Diagrams: Neues Ablaufdiagramm**:
  Dateinamen eingeben, anschließend über **Element** Formen oder Bilder anlegen.
- Eine `.docuclick`-Datei öffnet sich als Diagramm-Tab.
- Knoten ziehen; Verbindungspunkte ziehen, um Knoten zu verbinden.
- Rechtsklick auf Knoten, Verbindung oder Hintergrund öffnet die Bearbeitung.
- **↶ / ↷**, `Strg+Z` / `Strg+Umschalt+Z` (Mac: `⌘`) machen Änderungen
  rückgängig bzw. wiederholen sie. Texteingaben behalten ihre eigene Historie.
- Änderungen werden automatisch über die Obsidian-Vault-API gespeichert.
  `Strg+S` / `⌘S` wiederholt einen fehlgeschlagenen Speicherversuch.
- **Anleitung** zeigt die vorhandene Schritt-für-Schritt-Ansicht.
- **HTML-Ansicht** in der Diagramm-Werkzeugleiste erstellt eine eigenständige
  Datei `… – Ansicht.html` neben dem Diagramm. Sie enthält Suche, Zoom, Bildansicht
  und Anleitung, aber keine Bearbeitungs- oder Speicherfunktionen. Empfänger
  benötigen nur einen Browser, weder Obsidian noch DocuClick.
- Befehl **DocuClick-HTML importieren** liest eine lokale HTML-Datei. Für HTML
  im Vault gibt es zusätzlich einen Eintrag im Datei-Kontextmenü.

Import und Export überschreiben keine vorhandenen Dateien. Gleichnamige Dateien
erhalten eine laufende Nummer. Importierte HTML-Skripte werden nicht ausgeführt;
nur die eingebetteten JSON-Daten und Rasterbilder werden übernommen.

## Speicherung und Konflikte

`.docuclick` ist JSON mit `format: "docuclick-diagram"`, `version: 1`, dem
Canvas-Dokument und den Darstellungsdaten (`flow`). Die Darstellungsdaten sichern
insbesondere die bereits eingebetteten Screenshots aus bestehenden HTML-Dateien.
In dieser ersten Version bleiben Bilder als Data-URIs eingebettet. Externe
Attachment-Verwaltung, ein vollständig normalisiertes Datenmodell und Migrationen
folgen separat. Grenze: 64 MB pro Datei, maximal 10.000 sichtbare Knoten.

Jeder Schreibvorgang vergleicht innerhalb von `Vault.process()` den bisherigen
Dateistand. Wurde die Datei in einem anderen Tab oder extern geändert, bleibt
das Original erhalten; lokale Änderungen werden in einer neuen Datei
`… – lokale Änderungen.docuclick` gesichert. Die Ansicht zeigt den Konflikt an.
Beide Fassungen können dann verglichen werden. Es gibt kein automatisches
Zusammenführen. Zum erneuten Laden des Originals den Diagramm-Tab schließen
und die Datei wieder öffnen. Die lokale Sicherung bleibt dabei erhalten.

Undo/Redo gilt pro geöffnetem Tab und bleibt nicht über Neustarts erhalten.
Die Historie ist auf 60 Zustände bzw. ungefähr 32 MB begrenzt (mindestens zwei
Zustände). Sehr große Einzelbilder können diese Speichergrenze überschreiten.

## Architektur

- `src/document.js`: Formatprüfung, sicherer HTML-Import, HTML-Erzeugung.
- `src/main.js`: Obsidian-Ansicht, Dateiverwaltung und Speicherbrücke.
- `../core/DocuClick.Core/WebAssets/viewer.template.html`: gemeinsame
  HTML-Editorvorlage für DocuClick-Ausgaben und dieses Plugin.
- `build.py`: erzeugt ein installierbares Plugin und ZIP ohne npm-Schritt.

Der Editor läuft pro Tab in einem Sandbox-Iframe ohne `allow-same-origin`.
Eine Content Security Policy blockiert Netzwerkzugriffe. Nachrichten sind an
das konkrete Frame-Fenster und einen zufälligen Kanal gebunden; der Editor kann
keinen Zielpfad angeben. Die Host-Seite validiert jedes gespeicherte Dokument.

Noch nicht enthalten: native Obsidian-Notizlinks/Backlinks, Einbettung in Markdown,
Mobile-/Touch-Anpassung, draw.io-Export im Plugin, separate Bildanhänge und ein
helles Editor-Theme. Die Desktop-App verwendet für ihre eigene Ablaufübersicht
weiterhin `flow.js`; deren vollständige Zusammenführung ist ein weiterer Umbau.

## Tests und Release

```sh
node --test obsidian/tests/*.test.js
python3 obsidian/build.py
node obsidian/tests/browser.cjs /path/to/chromium
dotnet test --project core/DocuClick.Core.Tests --configuration Release
```

Browser-Tests verwenden ein temporäres Profil und einen simulierten Obsidian-Host.
Eine manuelle Prüfung in einem echten Obsidian-Vault bleibt vor einem Release
notwendig. Das Plugin ist eine erste testbare Version, kein Community-Store-Release.

API-Grundlage: [Obsidian API](https://github.com/obsidianmd/obsidian-api),
[Vault API](https://github.com/obsidianmd/obsidian-developer-docs/blob/main/en/Plugins/Vault.md).
Cytoscapes MIT-Lizenz liegt im gebauten Paket als `THIRD-PARTY-NOTICES.txt` bei.

Release: Version in `manifest.json` und [CHANGELOG.md](CHANGELOG.md) anheben,
dann `git tag obsidian-v0.1.1 && git push origin obsidian-v0.1.1`. Die CI
([.github/workflows/obsidian.yml](../.github/workflows/obsidian.yml)) hängt
ZIP sowie `main.js`, `manifest.json` und `styles.css` an das Release.
