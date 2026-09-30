# Changelog

Alle nennenswerten Änderungen an der Windows-App (und am gemeinsamen Kern) werden in dieser Datei dokumentiert.

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [Unreleased]

- **Neue Ablauf-Übersicht zum Testen** (Einstellungen → „Neue Ablauf-Übersicht
  testen“, wirkt beim nächsten Start): das Übersichtsfenster nutzt denselben
  Editor wie der Ablauf im Browser und das Obsidian-Plugin – mit Schwärzen,
  Drucken/PDF, Suche und Anleitung. Rechtsklick auf einen Schritt: „Hier
  weiter aufnehmen“, auf eine Abzweigung: „Neuer Pfad ab hier“; der
  Schritt, an den der nächste Klick anschließt, ist grün markiert (Schein um
  die Karte, Rahmen am Screenshot). „Zurück“ nimmt auch aufgenommene Klicks
  zurück, „Wiederholen“ holt sie wieder. Eine
  Änderung in der Übersicht, die sich mit einem gerade aufgenommenen Klick
  überschneidet, wird abgelehnt statt den Klick zu verlieren.
- **Schwärzen mit Vorschau, nachträglich änderbar, auch Weichzeichnen** (im
  Editor: Ablauf im Browser, neue Ablauf-Übersicht, Obsidian-Plugin): Flächen
  werden live gezeigt, lassen sich verschieben, in der Größe ändern, zwischen
  Schwärzen und Weichzeichnen umschalten und wieder entfernen. Das Original
  bleibt im Attachments-Ordner, deshalb geht das auch später noch; die
  .html-Datei enthält nur das bearbeitete Bild. Ein nur eingebettetes Bild
  (z. B. per „Bild ersetzen“ eingefügt) wird dafür als Original unter
  `Attachments/Originale/` abgelegt.

## [1.16.0] - 2026-09-30

- **Screenshots als WebP** (Standard; JPEG und PNG wählbar in den
  Einstellungen): etwa viermal kleiner (typisches Fenster: 38 KB statt 162 KB).
- **Lange Aufnahmen deutlich schneller** (gemessen mit 100 Klicks und echten
  Screenshots): Ein `.html`-Ablauf wird während des Klickens mit Verweisen auf
  die Screenshots geschrieben und erst nach 3 s Ruhe (oder bei Pause/Stopp/
  Beenden) mit eingebetteten Bildern – statt ~2 GB werden ~50 MB geschrieben,
  die späten Klicks dauern 14 ms statt 337 ms. Die Ablauf-Übersicht bekommt
  jeden Screenshot nur noch einmal (40 MB statt 1,9 GB übertragen).
- **Aufnahme aus Obsidian steuern**: Das Plugin kann die Aufnahme in ein
  Diagramm starten, pausieren und Abzweigungen setzen. Gekoppelt über einen
  Schlüssel, den DocuClick in den Vault schreibt; nur über 127.0.0.1.
- **Schwärzen** von Bereichen in Screenshots und **Drucken / PDF** der
  Anleitung im Ablauf (Browser, Obsidian).
- Automatische Installation des Obsidian-Plugins ist abschaltbar
  (Einstellungen).
- Ändert das Obsidian-Plugin ein Diagramm genau zwischen Einlesen und
  Speichern, bleibt dessen Stand als Kopie erhalten statt verloren zu gehen.
- Das Bild von direkt vor dem Klick wird sofort nach der Aufnahme
  freigegeben (weniger Speicher). Manuell eingefügte JPEG-Bilder werden mit
  dem richtigen Bildtyp eingebettet. Datei- und Assembly-Version entsprechen
  wieder der App-Version.
- Entwicklung: `tools/release.py` (Versionen, Changelog-Datum, Tags),
  Messung `RecordingBenchmark`, CI-Actions auf Node 24.

## [1.15.0] - 2026-09-29

- **Aufnehmen in einen Obsidian-Vault**: Liegt der gewählte Speicherort in
  einem Vault (Ordner `.obsidian`), legt DocuClick eine Diagramm-Notiz (`.md`)
  für das Plugin „DocuClick Diagrams“ an statt einer `.html`-Datei: eine
  Notiz mit Schrittliste als Text und den Diagrammdaten, die das Plugin als
  Diagramm öffnet. Eigener Text in der Notiz bleibt beim Aufnehmen erhalten.
  Der Session-Dialog zeigt das an. Screenshots bleiben PNG-Dateien im
  `Attachments`-Unterordner und werden nicht bei jedem Klick neu eingebettet.
- **Obsidian-Plugin wird automatisch installiert**: Beim Start einer Aufnahme
  in einem Vault installiert bzw. aktualisiert DocuClick das Plugin
  „DocuClick Diagrams“ im Vault (nie auf eine ältere Version, Einstellungen
  bleiben) und schaltet es bei der Erstinstallation ein. Die App bringt das
  Plugin mit (`ObsidianPlugin/`).
- Während der Aufnahme in Obsidian bearbeitete Diagramme werden vor dem
  nächsten Klick neu eingelesen statt überschrieben.
- „Bestehende Datei fortsetzen“, „Ablauf öffnen“ und der draw.io-Export
  akzeptieren Diagramm-Notizen und `.docuclick`-Dateien (gewöhnliche Notizen
  werden abgelehnt, nicht überschrieben) (auch mit Bildern, die das Plugin
  eingebettet oder in einen eigenen Bildordner gelegt hat).
- Kern (`DocuClick.Core`): neues `DocuClickDiagramIo` (Lesen/Schreiben des
  Plugin-Formats), `DiagramNote` (Notiz mit Schrittliste) und `ObsidianVault` (Vault-Erkennung); Kern-Tests laufen
  jetzt auch unter Linux.

## [1.14.0] - 2026-09-28

- **Speichern aus dem Browser ohne „Mit Datei verbinden“**: Ein im Browser
  geöffneter Ablauf speichert Änderungen direkt in seine eigene `.html`-Datei,
  solange DocuClick läuft (lokaler Speicherdienst auf `127.0.0.1:47811`,
  abgesichert über einen dateieigenen Schlüssel). Funktioniert in allen
  Browsern, auch Safari und Firefox; ohne laufendes DocuClick bleibt
  „Kopie herunterladen“.
- Plattformunabhängiger Kern `DocuClick.Core` (Speicherformat, Ablauf-Editor,
  Session-Logik) aus der App herausgelöst; gemeinsam mit der macOS-App und
  dem Obsidian-Plugin. Neue Ordnerstruktur im Repo: `windows/`, `core/`,
  `macos/`, `obsidian/`. Mit Tests (`core/DocuClick.Core.Tests`).
- HTML-Editor aus `HtmlViewerBuilder.cs` in eine gemeinsame WebAsset-Vorlage
  ausgelagert (`core/DocuClick.Core/WebAssets/viewer.template.html`); wird auch
  vom Obsidian-Plugin verwendet.
- Undo/Redo im gemeinsamen HTML-Editor, mit begrenzter Sitzungshistorie.
- HTML-Textbearbeitung aktualisiert wieder zuverlässig die Dokumentdaten;
  beim Verschieben bleiben Bild- und Gruppenknoten an ihrer Beschriftung.
- Änderungen während eines laufenden HTML-Speichervorgangs lösen anschließend
  einen weiteren Speichervorgang aus.

## [1.13.0] - 2026-09-15

### 🗂️ Freie Ordnerwahl statt globalem Ausgabeordner
- **Kein zentral konfigurierter Ausgabeordner mehr**: Jede Session wählt ihren eigenen Ordner frei beim Start (per "Durchsuchen..."-Dialog) — dort landen sowohl die `.html`-Datei als auch ihr `Attachments`-Unterordner. Die Einstellungen-Karte "Speicherort" ist komplett entfernt; `AppConfig.OutputPath` gibt es nicht mehr (eine alte `config.json` mit `VaultPath`/`OutputPath` wird beim Laden einfach ignoriert). Zuletzt verwendete Ordner werden weiterhin gemerkt, dienen aber nur noch als Vorauswahl im Ordner-Dialog.
- **Session-Start-Dialog neu gebaut**: "Neue Datei anlegen" und "Bestehende Datei fortsetzen" haben jetzt jeweils genau eine Bedienung — ein schreibgeschütztes Textfeld plus "Durchsuchen..."-Button (nativer Ordner- bzw. Dateidialog) — statt der bisherigen Freitext-Eingabe mit Root-Abgleich.
- **Neuer TopBar-Button "Ordner"**: öffnet den Ordner der aktuellen Session (bzw. den zuletzt verwendeten, falls nichts geladen ist) direkt im Explorer.

### 🧹 Redundanz entfernt
- **Keine Begleitdateien mehr**: Jede Aufnahme legte bisher zusätzlich zur `.html` noch eine bare-JSON `.canvas`-Datei und eine `.md`-Notiz mit Frontmatter/`html-embed`-Codeblock an — reine Obsidian-Vault-Ära-Redundanz, da die `.html`-Datei längst vollständig eigenständig ist. Beides ist ersatzlos entfernt.
- **Obsidian-Einbindung entfernt**: Der TopBar-Button zum Einbetten in eine Obsidian-Notiz ist entfernt. Recherche ergab, dass die naheliegende Alternative (Community-Plugin *HTML Reader*) DocuClicks Cytoscape.js-basierte, vollständig skriptabhängige Abläufe in keinem seiner Modi zuverlässig ausführt — die Abläufe bleiben als eigenständige `.html`-Dateien in jedem Browser nutzbar, mit oder ohne Obsidian.
- Kleinere Code-Aufräumarbeiten: dreifach duplizierte Ordner-Fallback-Logik in `App.xaml.cs` zu einer Hilfsmethode zusammengefasst; die seit der Einzel-Writer-Architektur immer `true` liefernde `SessionManager.SupportsBranching`-Property und ihr toter Durchreiche-Parameter in `TopBarWindow.UpdateStatus` entfernt.

### 🎨 Highlighter-Einstellungen überarbeitet
- Die "Zoom-auf-Cursor"-Einstellung ist aus den Settings entfernt (wird bereits über den Regler in der TopBar gesteuert).
- Neue Live-Vorschau für Radius und Strichstärke des Highlighter-Kreises direkt im Einstellungsfenster.
- Rendering-Fehler behoben, bei dem der ausgewählte Farb-Swatch teilweise blass/leer statt farbig erschien (WPF-Eigenheit bei `Border`+`CornerRadius`+dynamisch wechselnder `BorderThickness` — behoben durch einen separaten, konstant dicken Auswahlrahmen statt Änderung der Swatch-eigenen Randstärke).

## [1.12.4] - 2026-09-14

### 🔒 Sicherheit & Code-Reduktion (Teil 2)
- **"Nach HTML exportieren..." entfernt**: Der einstige Grund für diesen Export — die Live-Session-Datei referenzierte Screenshots per relativem Pfad und war daher nicht eigenständig weiterzugeben — entfällt seit v1.11.1: die Live-Datei bettet Screenshots bereits direkt als Base64 ein und ist von Anfang an vollständig eigenständig. Der separate Export (`HtmlFlowExporter.cs`, eigener Tray-Menüpunkt, eigenes Layout-System mit dritter Kartengröße) war dadurch reine Redundanz und ist komplett entfernt (Datei, Menüpunkt, Wiring, Doku).
- **Client-seitige Vault-Schreib-Lücke entfernt**: Die exportierte/live `.html`-Datei versuchte beim Speichern selbstständig `window.parent.app.vault.adapter.write(...)` aufzurufen — ganz ohne Prüfung, ob `window.parent` überhaupt echtes Obsidian ist, und mit demselben ungeprüften Zielpfad-Muster wie das in v1.12.3 entfernte Plugin. Da die aktuell unterstützte Obsidian-Einbindung (*HTML Embed*/*Local HTML Embed*) ohnehin nur eine Nur-Lese-Ansicht in einem regulären, nicht privilegierten iframe ist, ist dieser Codepfad ersatzlos entfernt worden (inkl. der zugehörigen `postMessage`-Bridge, die ebenfalls nie einen Empfänger hatte). Einziger verbleibender Speicherweg bleibt die File System Access API (expliziter "Mit Datei verbinden"-Klick, Browser-Sicherheitsanforderung).

## [1.12.3] - 2026-09-14

### 🔒 Sicherheit & Code-Reduktion
- **Gebündeltes Obsidian-Plugin entfernt**: Der in v1.12.2 hinzugefügte, gepatchte `obsidian-html-plugin`-Ordner (~24.400 Zeilen fremder Plugin-Code) ist entfernt worden. Der Patch hing einen `window.addEventListener("message", ...)`-Listener ohne `event.origin`-Prüfung ein und schrieb den darin übergebenen Dateinamen ungeprüft per `vault.adapter.write(...)` — erreichbar von jedem Inhalt, den Obsidian jemals rendert, nicht nur von echten DocuClick-Dateien. In Kombination mit dem zusätzlich aufgeweichten iframe-Sandbox (`allow-scripts allow-same-origin`, CSP entfernt) ergab das einen Pfad zu beliebigem Schreibzugriff im Vault (bis hin zum Überschreiben anderer Plugins). Empfohlener Weg für die Obsidian-Einbindung bleibt ausschließlich der bereits dokumentierte, ungepatchte Weg über die offiziellen Community-Plugins *HTML Embed*/*Local HTML Embed* (siehe [In Obsidian einbinden](README.md#in-obsidian-einbinden)).
- **`.github/workflows/build.yml` bereinigt**: Die drei Build-/Release-Schritte, die das jetzt entfernte Plugin zippten und als eigenes Release-Asset veröffentlichten, sind entfernt.
- **Toter Code entfernt**: der seit der Pause/Stop-Überarbeitung (v1.11.0) unerreichbare "Ansatzpunkt für die nächste Session"-Mechanismus (`SetResumeAnchor`, `ListResumableCanvasNodes`, zugehörige Felder in `SessionManager`/`CanvasFlowWriter`/`App.xaml.cs`) ist entfernt.

## [1.12.2] - 2026-09-11

### 📑 Vorkonfigurierte Obsidian-Vault-Integration & Plugin-Bundle
- **Vorkonfiguriertes Obsidian-Plugin (`obsidian-html-plugin`)**:
  - `OutputTemplate` enthält nun den Ordner `.obsidian/plugins/obsidian-html-plugin` **bereits vollständig vorkonfiguriert und gepatcht**.
  - Der Script-Sandbox-Patch (`allow-scripts allow-same-origin`) und der Vault-Save-Listener (`docuclick-save`) sind bereits integriert — beim Öffnen des Ausgabeordners als Obsidian-Vault funktioniert alles direkt ohne manuelles Frickeln am Code!
  - `OutputTemplate/.obsidian/app.json` aktiviert `"showUnsupportedFiles": true`, sodass `.html`-Abläufe sofort im Obsidian-Dateibaum sichtbar sind.
  - Das vorkonfigurierte Plugin wird bei jedem Release als separates `obsidian-html-plugin.zip` bereitgestellt, um es einfach in bestehende Vaults entpacken zu können.
- **Umfassende Anleitung in `README.md` und `OutputTemplate/README.md`**:
  - Schritt-für-Schritt-Anleitung für Out-of-the-Box Zero-Setup, Reinkopieren in bestehende Vaults sowie alternative manuelle Installation aus dem Obsidian Community Store.

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
