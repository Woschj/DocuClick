# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden in dieser Datei dokumentiert.

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [1.11.0] - 2026-09-10

### 🎨 Modernisiertes Frontend & UI-Redesign
- **Dark Glassmorphism HUD**: Die obere Steuerungsleiste ("TopBar") wurde vollständig modernisiert mit semi-transparentem Acryl-/Glassmorphism-Hintergrund (`backdrop-filter: blur(16px)`), feinen Akzenträndern, animiertem Session-Status-Badge und eleganten Pill-Buttons.
- **Konsistentes dunkles Design**: Alle Dialogfenster (Session-Start, Einstellungen, Pause/Fortsetzen, Bestätigungsdialoge) nutzen nun ein einheitliches, hochkontrastreiches Dark-Theme.
- **Bearbeitung bei pausierter Session**: Abläufe können jetzt direkt im pausierten Aufnahmemodus im Canvas verschoben, editiert, neu verdrahtet oder mit neuen Schritten versehen werden, ohne die Aufnahme erst stoppen zu müssen.

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
  - WebView2 aktualisiert nun nur noch gezielt die tatsächlich veränderten DOM- und Cytoscape-Elemente (`diffElements`), statt den gesamten Canvas bei jeder Aktion neu aufzubauen.
  - Renderzeiten sanken von ~150 ms auf unter **0.5 ms** – kein Ruckeln oder Flackern mehr bei Klicks, Verschieben oder Hinzufügen von Elementen.
- **Entprelltes Hintergrund-Speichern**:
  - Canvas-Änderungen werden mit einer 150 ms Entprellung (`ScheduleBackgroundSave`) asynchron in die `.html`-Datei persistiert. Schnelles Ziehen oder Tippen blockiert die UI nicht mehr.
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
