# DocuClick Ausgabeordner-Vorlage

Blanko-Ausgabeordner, vorbereitet für die Nutzung mit
[DocuClick](../README.md) — enthält keine echten Inhalte, nur Struktur und
eine Vorlage für Ablaufdiagramme.

## Wichtig: erst kopieren, dann benutzen

**Diesen Ordner nicht direkt im DocuClick-Repo mit echten Aufnahmen
befüllen.** Screenshots können sensible Bildschirminhalte zeigen — landet
das hier, wird es beim nächsten `git push` in diesem öffentlichen Repo
mitveröffentlicht. Stattdessen:

1. Diesen `OutputTemplate`-Ordner an einen Ort außerhalb des Repos kopieren
   (z. B. `%USERPROFILE%\Documents\Prozess-Ablaeufe`).
2. Beim nächsten "Neue Session" in DocuClick im Session-Start-Dialog über
   **"Durchsuchen..."** direkt diesen kopierten Ordner (oder einen seiner
   Unterordner, z. B. `01 Prozesse/IT-Support`) als Speicherort wählen —
   DocuClick verlangt keinen vorher global konfigurierten Ausgabeordner
   mehr, jede Session wählt ihren Ordner frei bei ihrem eigenen Start.

Die mitgelieferte `.gitignore` verhindert zusätzlich, dass in den
Arbeitsordnern erzeugte Dateien versehentlich committet werden, falls der
Ordner doch mal in-place benutzt wird — ersetzt aber nicht den Schritt oben.

## Obsidian-Nutzung (optional)

Dieser Ordner öffnet sich direkt als Obsidian-Vault (**"Open folder as
vault"**) und zeigt `.html`-Dateien im Dateibaum an (`.obsidian/app.json`
setzt dafür `"showUnsupportedFiles": true`). Weil er einen `.obsidian`-Ordner
enthält, legt DocuClick neue Aufnahmen hier als `.docuclick`-Diagramme an,
die das Plugin [DocuClick Diagrams](../obsidian/README.md) öffnet und
bearbeitet (siehe dort, „Direkt in den Vault aufnehmen“). Ohne Plugin
bleiben die Diagramme lesbar, aber nicht anzeigbar; wer nur mit dem Browser
arbeiten will, löscht den Ordner `.obsidian` in der Kopie.

## Struktur

```
OutputTemplate/
├── .obsidian/                   app.json (zeigt .html-Dateien im Dateibaum)
├── 00 Start.md                  Startseite / Übersicht
├── 00 Inbox/                    Standard-Zielordner für neue Aufnahmen
├── 01 Prozesse/                 fertig einsortierte Prozessdokumentation
├── 02 Vorlagen/
│   └── Leere-Ablauf-Vorlage.html
├── 03 MOCs/                     Übersichtsseiten (Map of Content)
├── 99 Archiv/                   abgelöste/alte Prozesse
└── Attachments/                 Screenshots
```

`Leere-Ablauf-Vorlage.html` sieht vor dem ersten Klick noch wie eine
leere/rohe Textdatei aus, wenn man sie direkt öffnet — DocuClick schreibt
sie beim ersten aufgezeichneten Klick automatisch zur vollständigen,
interaktiven Ablauf-Seite um (siehe [Workflow unten](#workflow-vorlage-nutzen-und-docuclick-daran-fortsetzen-lassen)).

## Workflow: Zielordner beim Aufnahme-Start wählen

Der Session-Start-Dialog fragt bei jeder neuen Aufnahme nach dem
**Speicherort** — per "Durchsuchen..." direkt in einen der vorbereiteten
Unterordner navigieren (z. B. `01 Prozesse/IT-Support`), statt alles in
einem gemeinsamen Wurzelordner zu sammeln. `00 Inbox` eignet sich für noch
nicht einsortierte Aufnahmen.

## Workflow: Vorlage nutzen und DocuClick daran fortsetzen lassen

1. `Leere-Ablauf-Vorlage.html` aus `02 Vorlagen/` in den gewünschten
   Zielordner kopieren und umbenennen.
2. In DocuClick eine Aufnahme starten → im Session-Start-Dialog
   **"Bestehende Datei fortsetzen"** wählen → per "Durchsuchen..." die
   vorbereitete Datei auswählen.
3. Jeder Klick wird automatisch als neuer Knoten an die vorbereitete Datei
   angehängt.
