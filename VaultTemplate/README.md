# DocuClick Vault-Template

Blanko-Obsidian-Vault, vorbereitet für die Nutzung mit
[DocuClick](../README.md) — enthält keine echten Inhalte und keine
Community-Plugins, nur Struktur, Theme und Vorlagen für
Prozessbeschreibungen/Arbeitsabläufe.

Theme: **PLN** (inkl. der darin enthaltenen automatischen
Regenbogen-Ordnerfärbung im Dateibaum — rein CSS-basiert nach
Ordner-Position, keine manuelle Konfiguration pro Ordner nötig).

## Wichtig: erst kopieren, dann benutzen

**Diese Vault nicht direkt im DocuClick-Repo mit echten Aufnahmen
befüllen.** Screenshots können sensible Bildschirminhalte zeigen — landet
das hier, wird es beim nächsten `git push` in diesem öffentlichen Repo
mitveröffentlicht. Stattdessen:

1. Diesen `VaultTemplate`-Ordner an einen Ort außerhalb des Repos kopieren
   (z. B. `%USERPROFILE%\Documents\Prozess-Vault`).
2. Den kopierten Ordner in Obsidian öffnen ("Ordner als Vault öffnen").
3. In DocuClick unter Einstellungen → Obsidian-Vault den Vault-Pfad auf
   den kopierten Ordner selbst setzen (Attachments-Unterordner bleibt auf
   dem Standard `Attachments`, landet also direkt im Vault-Root).

Die mitgelieferte `.gitignore` verhindert zusätzlich, dass in den
Arbeitsordnern erzeugte Dateien versehentlich committet werden, falls die
Vault doch mal in-place benutzt wird — ersetzt aber nicht den Schritt oben.

## Struktur

```
VaultTemplate/
├── .obsidian/
│   ├── appearance.json          Theme + Akzentfarbe (PLN, lila)
│   └── themes/PLN/               Theme-CSS (Regenbogen-Ordnerfärbung inklusive)
├── 00 Start.md                  Startseite / Übersicht
├── 00 Inbox/                    Standard-Zielordner für neue Aufnahmen
├── 01 Prozesse/                 fertig einsortierte Prozessdokumentation
├── 02 Vorlagen/
│   ├── Prozess-Notiz-Vorlage.md
│   └── Leere-Canvas-Vorlage.canvas
├── 03 MOCs/                     Übersichtsseiten (Map of Content)
├── 99 Archiv/                   abgelöste/alte Prozesse
└── Attachments/                 Screenshots (Notiz-/Canvas-Modus)
```

## Workflow: Zielordner beim Aufnahme-Start wählen

Der Session-Start-Dialog fragt bei jeder Aufnahme neben dem Dateinamen
auch nach dem **Zielordner** (relativ zum Vault-Pfad) — Vorschläge kommen
aus allen bereits vorhandenen Unterordnern. So landen neue Aufnahmen
direkt dort, wo sie hingehören (z. B. `01 Prozesse/IT-Support`), statt
immer im Vault-Root. Leer lassen = Vault-Root; `00 Inbox` eignet sich für
noch nicht einsortierte Aufnahmen.

## Workflow: Vorlage nutzen und DocuClick daran fortsetzen lassen

1. Eine Vorlage aus `02 Vorlagen/` in den gewünschten Zielordner kopieren,
   umbenennen und Metadaten (Zweck, Verantwortlich, Status, ...) ausfüllen.
2. In DocuClick eine Aufnahme starten → im Session-Start-Dialog
   **"Bestehende Datei fortsetzen"** wählen → die vorbereitete Datei
   auswählen (Liste zeigt auch den Unterordner mit an).
3. Jeder Klick wird automatisch an die vorbereitete Datei angehängt
   (Notiz) bzw. dort verankert (Canvas) — die von Hand eingetragenen
   Metadaten am Dateianfang bleiben erhalten.

Für den Word-Modus (`.docx`) gibt es keine Vorlage hier, da Word-Dateien
ein Binärformat sind. Im Session-Start-Dialog einfach "Neue Datei
anlegen" wählen; Titel/Formatierung lassen sich danach direkt in Word
ergänzen.
