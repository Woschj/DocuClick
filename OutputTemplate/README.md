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
2. In DocuClick unter Einstellungen → Speicherort den Ausgabeordner auf
   den kopierten Ordner selbst setzen (Attachments-Unterordner bleibt auf
   dem Standard `Attachments`, landet also direkt im Ordner-Root).

Die mitgelieferte `.gitignore` verhindert zusätzlich, dass in den
Arbeitsordnern erzeugte Dateien versehentlich committet werden, falls der
Ordner doch mal in-place benutzt wird — ersetzt aber nicht den Schritt oben.

## Struktur

```
OutputTemplate/
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

Der Session-Start-Dialog fragt bei jeder Aufnahme neben dem Dateinamen
auch nach dem **Zielordner** (relativ zum Ausgabeordner) — Vorschläge kommen
aus allen bereits vorhandenen Unterordnern. So landen neue Aufnahmen
direkt dort, wo sie hingehören (z. B. `01 Prozesse/IT-Support`), statt
immer im Ordner-Root. Leer lassen = Ordner-Root; `00 Inbox` eignet sich für
noch nicht einsortierte Aufnahmen.

## Workflow: Vorlage nutzen und DocuClick daran fortsetzen lassen

1. `Leere-Ablauf-Vorlage.html` aus `02 Vorlagen/` in den gewünschten
   Zielordner kopieren und umbenennen.
2. In DocuClick eine Aufnahme starten → im Session-Start-Dialog
   **"Bestehende Datei fortsetzen"** wählen → die vorbereitete Datei
   auswählen (Liste zeigt auch den Unterordner mit an).
3. Jeder Klick wird automatisch als neuer Knoten an die vorbereitete Datei
   angehängt.
