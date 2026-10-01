# Changelog – DocuClick Diagrams (Obsidian-Plugin)

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [Unreleased]

- **Bildpfade folgen Verschiebungen**: Werden Screenshots oder ganze
  Bildordner in Obsidian verschoben oder umbenannt, passt das Plugin die
  Pfade in allen betroffenen Diagrammen automatisch an, auch in geöffneten.
- **Bildordner wie in Obsidian**: Ohne eigenen Bildordner speichert das
  Plugin Bilder dort, wo Obsidian Anhänge ablegt (Einstellungen → Dateien und
  Links), in einem Unterordner pro Diagramm – derselbe Ort, an dem die
  DocuClick-Apps ihre Screenshots ablegen. Ein fest eingetragener Bildordner
  gilt weiter; der frühere Standard `DocuClick-Bilder` wird dabei zu „wie in
  Obsidian“ (dort gespeicherte Bilder bleiben und werden weiter gefunden).

## [1.19.1] - 2026-10-01

- Keine eigenen Änderungen; Version an die anderen Teile angeglichen.

## [1.19.0] - 2026-09-30

- Keine eigenen Änderungen; Version an die anderen Teile angeglichen.

## [1.18.0] - 2026-09-30

- **Ein Release für alles**: Windows-App, macOS-App und Obsidian-Plugin
  erscheinen ab jetzt zusammen in einem Release mit derselben Versionsnummer
  (Tag `v…`).

## [0.8.0] - 2026-09-30

- **Schwärzen mit Vorschau, nachträglich änderbar, auch Weichzeichnen**: Die
  Flächen werden live im Bild gezeigt, lassen sich verschieben, in der Größe
  ändern, zwischen Schwärzen und Weichzeichnen umschalten und wieder
  entfernen – auch später noch, weil das Original erhalten bleibt. Die
  HTML-Ansicht und heruntergeladene Kopien enthalten nur das bearbeitete Bild.

## [0.7.0] - 2026-09-30

- **Aufnahme mit der DocuClick-App steuern**: Knopf „Mit DocuClick
  aufnehmen“ im Diagramm-Tab, Befehle für Start, Pause und Abzweigung,
  Aufnahme-Status in der Statusleiste.
- Änderungen von außen (z. B. jeder Klick einer laufenden Aufnahme) erscheinen
  im geöffneten Diagramm, ohne es neu zu laden: Zoom und Ausschnitt bleiben.
- Befehl **Nicht mehr verwendete Bilder aufräumen** (mit Liste und
  Bestätigung, Papierkorb).
- **Schwärzen** von Bereichen in Screenshots, **Drucken / PDF** der Anleitung.
- Neu angelegte Diagramm-Notizen wechseln zur Diagrammansicht, sobald
  Obsidian sie erfasst hat.

## [0.6.0] - 2026-09-29

- **Ein Ablauf = eine Notiz**: Neue und importierte Abläufe sind
  Diagramm-Notizen (`.md` mit Eigenschaft `docuclick: diagramm`). Sie öffnen
  sich direkt als Diagramm-Tab wie bisher (inkl. HTML-Ansicht); **Als Notiz
  anzeigen** zeigt den Text im Lesemodus. Die Notiz enthält eigenen Text, eine
  automatisch erzeugte Schrittliste (durchsuchbar, verlinkbar) und die
  Diagrammdaten in einem unsichtbaren Kommentar. Eigener Text bleibt bei
  jedem Speichern erhalten.
- Diagramm-Notizen öffnen sich direkt als Diagramm, ohne kurz den Text zu
  zeigen (sofern Obsidian die Notiz schon indiziert hat).
- Die DocuClick-Apps installieren das Plugin automatisch in einen Vault, in
  den sie aufnehmen.
- `.docuclick`-Dateien öffnen sich weiterhin; **In Diagramm-Notiz
  umwandeln** (Kontextmenü) macht daraus eine Notiz.
- **Diagramm in andere Notizen einbetten** mit einem Codeblock `docuclick`
  (Pfad, optional `hoehe`): nur lesen, mit Zoom, Suche, Bildansicht und
  Anleitung; aktualisiert sich bei Änderungen.
- **Zusammenspiel mit den DocuClick-Apps**: Die Apps für Windows und macOS
  nehmen direkt in Diagramm-Notizen im Vault auf. Ein geöffnetes Diagramm
  zeigt neue Klicks live an.
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
