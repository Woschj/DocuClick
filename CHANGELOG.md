# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden in dieser Datei dokumentiert.

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [1.12.1] - 2026-09-11

### 📚 Dokumentation
- **README ergänzt**: SOP-Guide, Kanten-Farbpalette/Linienstil-Menü, manuelles Bild-Einfügen und ein neuer Abschnitt ["In Obsidian einbinden"](README.md#in-obsidian-einbinden) waren in v1.12.0 im Code fertig, aber nicht dokumentiert — nachgeholt, inkl. Schritt-für-Schritt-Anleitung zur Installation des dafür nötigen Obsidian-Plugins (*HTML Embed* oder *Local HTML Embed*).
- **Changelog korrigiert**: der in v1.11.1 dokumentierte Obsidian-Einbindungscode entsprach nicht der tatsächlich kopierten Syntax (`file:`/`height:`-Schlüssel statt der echten schlüssellosen Zeilen); ein dort ebenfalls erwähntes Obsidian-Modal im Standalone-HTML-Viewer existiert noch nicht — beides richtiggestellt.

## [1.12.0] - 2026-09-11

### 📖 Schritt-für-Schritt-Anleitung (SOP Guide) mit Multi-Ablauf-Unterstützung
- **Interaktive Schritt-für-Schritt-Leiste (SOP Guide)**:
  - Neuer Button **📖 Anleitung** in der oberen HUD-Leiste öffnet/schließt die Seitenleiste sowohl im Standalone-HTML-Viewer als auch in der Desktop-App (`FlowPreviewOverlay`).
  - **Dynamischer Pfadfilter**: Dropdown ermöglicht das Umschalten zwischen dem gesamten Ablauf (`🌐`) und einzelnen benannten Zweigen (`↳ Hauptablauf`, `↳ Pfad: ...`).
  - **Pfad-Highlighting & Fokussierung**: Bei Auswahl eines Pfades werden dessen Knoten und Kanten hervorgehoben (`.path-highlighted`), während andere Pfade dezent abgeblendet werden (`.path-dimmed`). Die Ansicht zentriert sich automatisch auf den aktiven Pfad.
  - **Interaktive Entscheidungskarten (`◆ Abzweigung`)**: Verzweigungen werden als markante Bernsteinkarten dargestellt und bieten Direkt-Buttons für jeden abgehenden Pfad (z. B. `↳ Option A`, `↳ Option B`), die per Klick den Zielknoten anspringen und die Anleitung umschalten.
  - **Pfad-Banner & Zusammenführungen**: Farbige Pfadbannertrenner und `⇄ Zusammenführung`-Badges an Konvergenzknoten.
  - **Filter-Tabs**: Schnelles Umschalten zwischen „Alle Schritte“ und „Nur Screenshots“.

### 🎨 Pfeilstile, Farbpaletten & Kanten-Kontextmenü
- **Standardmäßig durchgezogen & farblich passend**:
  - Manuell gezogene Kanten und Verbindungspfeile sind nun standardmäßig durchgezogen (`lineStyle: "solid"`) und übernehmen automatisch die Akzentfarbe des Quellknotens statt gestricheltem Grau.
- **Vollwertiges Kanten-Rechtsklickmenü (App & HTML)**:
  - **Farbpalette**: 9 vordefinierte Akzentfarben zur direkten Auswahl über runde Color-Dots.
  - **Linienstil**: Umschalten zwischen durchgezogener (`solid`) und gestrichelter (`dashed`) Linie.
  - **Richtung umkehren**: Dreht Pfeilanfang und -ende per Klick um.
  - **Verbindung löschen**: Entfernt die Kante sicher.
- **Persistenz im Canvas-Modell**:
  - `Color` und `LineStyle` werden dauerhaft in `CanvasEdge` und dem Canvas-JSON gespeichert.

### 📷 Manuelle Bildelemente & App-Parität
- **Bilder ohne Screenshot einfügen**:
  - Neue Option *"📷 Bild einfügen..."* im Canvas-Rechtsklick-Menü erlaubt das Auswählen lokaler Bilddateien (PNG/JPG/JPEG/WEBP/BMP) — in der Desktop-App über den nativen Windows-Dateidialog, im Standalone-HTML-Viewer über den Datei-Auswahldialog des Browsers.
- **Feature-Parität**:
  - SOP-Guide, Kanten-Kontextmenü und Flowchart-Elemente-Palette funktionieren in der Desktop-App und im autarken HTML-Viewer funktional identisch (unabhängige Implementierungen, kleinere Wortlaut-Unterschiede möglich).

## [1.11.1] - 2026-09-11

### 📑 Interaktive Obsidian-Einbindung
- **Autarke HTML-Dateien mit In-Memory Base64-Screenshots**:
  - Alle Screenshots werden während der Aufnahme direkt im Arbeitsspeicher Base64-codiert und gecacht (`_base64Cache`).
  - Der Live-`.html`-Ablauf speichert die Bilder als eingebettete `data:image/png;base64,...`-URIs. Dadurch sind die erzeugten HTML-Abläufe 100% autark und funktionieren in Obsidian (unter iframes / Plugins) ohne gebrochene relative Pfade oder Dateisystem-Blockaden.
- **Neuer "Obsidian"-Button in der Top-Leiste**:
  - Kopiert mit einem Klick den fertigen Einbindungscode (` ```html-embed\n<filename>.html\n750\n``` `, kompatibel mit dem Plugin *Local HTML Embed*) für die aktuelle Session in die Zwischenablage. Liegt der konfigurierte Ausgabeordner selbst in einem Obsidian-Vault, öffnet der Button die Datei stattdessen direkt in Obsidian statt nur zu kopieren.
  - Ein eigenes Obsidian-Modal im Standalone-HTML-Viewer (analog zum App-Button) ist noch nicht umgesetzt — siehe README für die manuelle Anpassung des Codeblocks, falls stattdessen das Plugin *HTML Embed* (`file:`/`height:`-Syntax) genutzt wird.
- **Iframe- & Einbettungs-Anpassung**:
  - Erkennt automatisch (`body.embedded-in-iframe`), wenn der Ablauf in einem Obsidian-Iframe gerendert wird, blendet ungeeignete Desktop-File-Picker aus und optimiert die Abstände des Docks und der Titelleiste.

### ⚡ 60-144 FPS Performance-Engine & flüssiges Verschieben
- **Beseitigung des 1.000 Hz `mousemove`-Bottlenecks**:
  - Der CPU-intensive Bounding-Box-Scan bei jeder Mausbewegung in `flow.js` und `HtmlViewerBuilder.cs` wurde vollständig entfernt. Verbindungspunkte (Handles) werden stattdessen über hocheffiziente, event-basierte Hover-Listener mit 200 ms Hysterese ein- und ausgeblendet.
- **Zentraler `requestAnimationFrame`-Scheduler & Viewport-Culling**:
  - Alle DOM-Overlays (Bilder, Labels, Verbindungspunkte, Badges) werden nun über einen gemeinsamen RAF-Zyklus synchronisiert.
  - Elemente außerhalb des sichtbaren Bereichs (Viewport Culling) werden während schnellem Pan/Zoom komplett übersprungen und nicht im DOM neu berechnet.
- **Batch-Dragging bei Mehrfachauswahl**:
  - Das gleichzeitige Verschieben mehrerer ausgewählter Knoten nutzt nun atomare `cy.batch()`-Updates und sendet auf C#-Seite eine einzige `moveBatch`-Nachricht, statt für jeden Knoten eine separate Aktualisierungsrunde auszulösen.

## [1.11.0] - 2026-09-10

### 🎨 Modernisiertes Frontend & UI-Redesign
- **Glassmorphism-Werkzeugleiste in der Ablauf-Übersicht**: Das große Editier-Fenster hat oben jetzt eine eigene, in die WebView2-Fläche eingebettete Werkzeugleiste mit semi-transparentem Glassmorphism-Hintergrund (`backdrop-filter: blur(...)`), Schrittzähler, Suchfeld, "+ Element"-Button und Zoom-Controls. Die separate, native Top-Leiste (die schwebende Pille außerhalb des Editier-Fensters) bleibt ihr bisheriges dunkles WPF-Design mit neu gestalteten Pill-Buttons für Aufnahme/Stopp/Fortsetzen.
- **Bearbeitung bei pausierter Session**: Abläufe können jetzt direkt im pausierten Aufnahmemodus im Canvas verschoben, editiert, neu verdrahtet oder mit neuen Schritten versehen werden, ohne die Aufnahme erst zu beenden — der "Stopp"-Button pausiert dafür jetzt tatsächlich nur (Zieldatei/Cursor bleiben erhalten), ein echtes Beenden passiert nur noch über "Neue Session".

### 📐 Flowchart-Elemente-Palette
- **6 neue Diagrammknoten-Typen** zur Erstellung ganzheitlicher Ablauf- und Flussdiagramme (BPMN / Flowchart-Standard):
  - 🟢 **Start / Ende**: Abgerundete Ellipse für Start- und Endpunkte (Soft-Green)
  - 🟦 **Prozessschritt**: Klassischer Aktionsschritt mit abgerundeten Ecken (Soft-Blue)
  - 🔶 **Entscheidung / Verzweigung**: Raute / Diamond für Ja/Nein-Entscheidungen (Amber-Gold)
  - 🔷 **Eingabe / Ausgabe**: Parallelogramm für Daten- und IO-Schritte (Cyan)
  - 📑 **Dokument**: Dokumentensymbol für Berichte, Belege und Vorlagen (Violett)
  - 📝 **Notiz / Kommentar**: Notizblock-Form für erläuternde Randnotizen (Slate)
- **Flexibles Einfügen**: Knoten können jederzeit über das neue Canvas-Rechtsklick-Menü (*"Element einfügen..."*) oder den neuen `+ Element`-Button in der HUD-Leiste an beliebiger Position eingefügt werden.
- **Voller Draw.io-Export**: Alle neuen Flowchart-Shapes werden beim Export nach `.drawio` nativ als Vektorformen (`rhombus`, `shape=parallelogram`, `shape=document`, `shape=note`, etc.) inklusive ihrer Akzentfarben abgebildet.

### 🖱️ Draw.io / Visio Canvas-Interaktionen
- **Magnetische Ports & Snap-Radien**:
  - Jeder Knoten besitzt 4 magnetische Einrastpunkte (Oben, Rechts, Unten, Links) mit vergrößerter Klickzone (22px) und 35px Einrast-Radius.
  - Das Ziehen neuer Verbindungspfeile fühlt sich präzise und fehlerfrei an wie in Draw.io oder Microsoft Visio.
- **Multi-Node Dragging**:
  - Werden mehrere Knoten per Rahmenauswahl (Box-Select) markiert, lassen sich alle ausgewählten Knoten synchron gemeinsam über den Canvas verschieben.
- **Inline-Textbearbeitung mit echten Zeilenumbrüchen**:
  - Ein Klick direkt in die Textbox eines Screenshot- oder Flowchart-Knotens öffnet sofort das mehrzeilige Textbearbeitungsfeld.
  - Zeilenumbrüche werden mit <kbd>Shift</kbd> + <kbd>Enter</kbd> erzeugt, Speichern erfolgt mit <kbd>Enter</kbd> oder Klick außerhalb.
  - Textboxen skalieren beim Herauszoomen proportional mit und bleiben jederzeit lesbar, ohne den Canvas zu überlagern.
- **Löschen per Taste & Aufräumen**:
  - Verbindungen oder Knoten können nach Klick direkt mit der <kbd>Entf</kbd>- / <kbd>Backspace</kbd>-Taste gelöscht werden.
  - Der überflüssige rote "Verbindung trennen"-Button wurde aus der UI entfernt.

### ⚡ Performance & Stabilität
- **In-Place Graph- & DOM-Diffing**:
  - `render()` gleicht neue gegen bestehende Knoten/Kanten ab (hinzugefügt/entfernt/nur-Daten-geändert) statt bei jeder Aktion den gesamten Canvas neu aufzubauen; dieselbe Diff-Logik läuft separat für die Bild-, Label- und Verbindungspunkt-Overlays.
  - Renderzeiten sanken von ~150 ms auf unter **0.5 ms** – kein Ruckeln oder Flackern mehr bei Klicks, Verschieben oder Hinzufügen von Elementen.
- **Entprelltes Hintergrund-Speichern**:
  - Verschieben von Knoten und das Platzieren neuer Flowchart-Elemente werden mit einer 150 ms Entprellung (`ScheduleBackgroundSave`) asynchron in die `.html`-Datei persistiert, statt bei jedem Zwischenschritt synchron zu speichern. Schnelles Ziehen blockiert die UI nicht mehr; alle anderen Aktionen (Klicks, Umbenennen, Löschen, Verbinden) sowie Pausieren/Neue Session speichern weiterhin sofort.
- **Kamera-Zentrierung stabilisiert**:
  - Das unruhige Springen des Viewports zu alten Klickmarkern bei manuellen Editiervorgängen wurde behoben. Die Kamera fokussiert nur noch dann automatisch, wenn im Aufnahmemodus ein neuer Screenshot erfasst wurde (`isRecordedClick`).

---

## [1.10.0] - 2026-09-09
- **Große Ablauf-Bearbeitung im Canvas**: Vollbild-Bearbeitungsmodus mit Screenshots direkt auf den Knoten-Karten.
- **Port-Handles**: 4 Verbindungspunkte für intuitives Drag-to-Connect.
- **HTML-Ablauf-Format**: Umstellung auf vollwertige, eigenständige interaktive HTML-Abläufe.

## [1.9.1] - 2026-08-20
- **Zuverlässigkeits- & Sicherheits-Hardening**: Bessere Fehlerbehandlung bei Hooks und UI Automation Timeouts.
- **Draw.io Layout-Fixes**: Verbesserte Knoten- und Pfeil-Platzierung beim Export.

## [1.8.0] - 2026-08-10
- **Zoom-auf-Cursor**: Einstellbarer Radius mit Live-Vorschau in der Top-Leiste.
