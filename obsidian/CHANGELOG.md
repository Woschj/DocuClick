# Changelog – DocuClick Diagrams (Obsidian-Plugin)

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [Unreleased]

- **Zusammenspiel mit den DocuClick-Apps**: Die Apps für Windows und macOS
  nehmen direkt in `.docuclick`-Diagramme im Vault auf. Ein geöffnetes
  Diagramm zeigt neue Klicks live an.
- Screenshots, die schon als Vault-Dateien vorliegen (z. B. von der App
  aufgenommen), bleiben beim Speichern Dateien, auch bei der Einstellung
  „In der Datei“. Zwei Schritte mit identischem Bild behalten ihre eigenen
  Dateien.

## [0.5.0] - 2026-09-29

- **Screenshots optional als Vault-Dateien** (Einstellung „Screenshots
  speichern“, Standard weiterhin „In der Datei“): Bilder liegen im Bildordner
  (Dateiname = Prüfsumme, keine Duplikate), die Diagrammdatei bleibt klein.
  Ältere Plugin-Versionen zeigen solche Diagramme ohne Bilder.
- Eine Ansicht ohne Änderungen schreibt die Datei nicht mehr um
  (kein unnötiger Sync-Verkehr beim bloßen Öffnen).
- Import: Zu große HTML-Dateien werden vor dem Einlesen abgelehnt.

## [0.4.0] - 2026-09-29

- **Externe Änderungen**: Ändert Sync, Git oder ein anderes Programm die
  geöffnete Datei, lädt die Ansicht sie automatisch neu, sofern lokal nichts
  Ungesichertes vorliegt. Sonst erscheint ein Hinweis mit „Neu laden“
  (eigene Änderungen liegen dann bereits in der „– lokale Änderungen“-Datei).
- **Schnelleres Speichern**: Pro Bearbeitung wird nur noch ein Snapshot
  geprüft und serialisiert (bisher zweimal, weil der Editor „Änderung“ und
  „Speichern“ getrennt meldet).
- **Kleinere Dateien**: Ein Screenshot wird in `.docuclick`-Dateien nur noch
  einmal gespeichert (bisher doppelt). Beim Öffnen wird die Canvas-Seite aus
  dem Diagramm rekonstruiert; ältere Dateien und ältere Plugin-Versionen
  bleiben lesbar.

## [0.3.1] - 2026-09-29

- Diagramme mit sehr vielen Schritten werden schneller geprüft (keine
  quadratischen Suchvorgänge mehr in der Validierung).
- Einstellungen: Der Standardordner wird verzögert gespeichert statt bei jedem
  Tastendruck; ein ungültiger Ordner wird jetzt rot markiert.

## [0.3.0] - 2026-09-28

- **Ordner für neue Abläufe**: Der Dialog „Neues Ablaufdiagramm“ fragt jetzt
  neben dem Dateinamen auch den Ordner ab (mit Vorschlägen aus dem Vault;
  fehlende Ordner werden angelegt).
- Neue Einstellungen unter „Neue Abläufe“: **Standardordner** (vorausgefüllt
  im Dialog, auch Ziel für „DocuClick-HTML importieren“) und **Ordner beim
  Anlegen abfragen** (aus: direkt im Standardordner anlegen). Ohne
  Standardordner gilt wie bisher der Ordner der geöffneten Datei.

## [0.2.0] - 2026-09-28

- **Farbschema in den Plugin-Einstellungen**: „DocuClick (dunkel)“,
  „Obsidian-Theme übernehmen“ (folgt Hell/Dunkel und Akzentfarbe des Themes
  automatisch) oder „Eigene Farben“ mit Hintergrund- und Detailfarbe.
  Leisten, Menüs, Dialoge, Beschriftungen und Schrift passen sich an, ein
  heller Hintergrund bekommt automatisch dunkle Schrift. Änderungen gelten
  sofort in allen offenen Diagrammen.
- Exportierte HTML-Ansichten übernehmen das Farbschema (abschaltbar).
- Gemeinsame HTML-Vorlage: Farben laufen über CSS-Variablen; die Standard-
  darstellung der DocuClick-Apps bleibt unverändert.

## [0.1.1] – nicht einzeln veröffentlicht

- Zusätzliche obere Menüleiste entfernt. Der neue Button „HTML-Ansicht“ in
  der Diagramm-Werkzeugleiste exportiert eigenständiges HTML mit Suche, Zoom,
  Bildansicht und Anleitung, ohne Bearbeitungs- und Speichercode.

## [0.1.0] – nicht einzeln veröffentlicht

- Erstes eigenständiges Obsidian-Desktop-Plugin: Diagramme
  erstellen/bearbeiten, HTML-Import/-Export, versionierte `.docuclick`-Dateien,
  Vault-Speicherung mit Konfliktsicherung.
- Nutzt den gemeinsamen HTML-Editor (`core/DocuClick.Core/WebAssets/
  viewer.template.html`), inklusive Undo/Redo.
