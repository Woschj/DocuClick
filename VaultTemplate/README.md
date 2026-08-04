# DocuClick Vault-Template

Blanko-Obsidian-Vault, vorbereitet für die Nutzung mit
[DocuClick](../README.md) — enthält keine echten Inhalte, nur Struktur und
Vorlagen für Prozessbeschreibungen/Arbeitsabläufe.

## Wichtig: erst kopieren, dann benutzen

**Diese Vault nicht direkt im DocuClick-Repo mit echten Aufnahmen
befüllen.** Screenshots können sensible Bildschirminhalte zeigen — landet
das hier, wird es beim nächsten `git push` in diesem öffentlichen Repo
mitveröffentlicht. Stattdessen:

1. Diesen `VaultTemplate`-Ordner an einen Ort außerhalb des Repos kopieren
   (z. B. `%USERPROFILE%\Documents\Prozess-Vault`).
2. Den kopierten Ordner in Obsidian öffnen ("Ordner als Vault öffnen").
3. In DocuClick unter Einstellungen → Obsidian-Vault den Vault-Pfad auf
   `<kopierter Ordner>\Prozesse` setzen (Attachments-Unterordner bleibt
   auf dem Standard `Attachments`).

Die mitgelieferte `.gitignore` verhindert zusätzlich, dass in `Prozesse/`
erzeugte Dateien versehentlich committet werden, falls die Vault doch mal
in-place benutzt wird — ersetzt aber nicht den Schritt oben.

## Struktur

```
VaultTemplate/
├── Prozesse/                        <- DocuClick "Vault-Pfad" zeigt hierher
│   └── Attachments/                 <- Screenshots (Notiz-/Canvas-Modus)
├── Vorlagen/
│   ├── Prozess-Notiz-Vorlage.md     Blanko-Notiz zum manuellen Vorbereiten
│   └── Leere-Canvas-Vorlage.canvas  Blanko-Canvas mit Titel-Karte
└── Index.md                         Startseite mit Links zu allen Prozessen
```

## Workflow: Vorlage nutzen und DocuClick daran fortsetzen lassen

1. Eine Vorlage aus `Vorlagen/` nach `Prozesse/` kopieren, umbenennen und
   Metadaten (Zweck, Verantwortlich, Status, ...) ausfüllen.
2. In DocuClick eine Aufnahme starten → im Session-Start-Dialog
   **"Bestehende Datei fortsetzen"** wählen → die vorbereitete Datei
   auswählen.
3. Jeder Klick wird automatisch an die vorbereitete Datei angehängt
   (Notiz) bzw. dort verankert (Canvas) — die von Hand eingetragenen
   Metadaten am Dateianfang bleiben erhalten.

Alternativ direkt "Neue Datei anlegen" wählen, wenn keine Vorbereitung
nötig ist — der Dialog fragt bei jeder Aufnahme danach, es gibt keinen
automatisch generierten Namen.

Für den Word-Modus (`.docx`) gibt es keine Vorlage hier, da Word-Dateien
ein Binärformat sind. Im Session-Start-Dialog einfach "Neue Datei
anlegen" wählen; Titel/Formatierung lassen sich danach direkt in Word
ergänzen.
