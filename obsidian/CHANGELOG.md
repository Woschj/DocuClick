# Changelog – DocuClick Diagrams (Obsidian-Plugin)

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [0.2.0] – unveröffentlicht

- **Farbschema in den Plugin-Einstellungen**: „DocuClick (dunkel)“,
  „Obsidian-Theme übernehmen“ (folgt Hell/Dunkel und Akzentfarbe des Themes
  automatisch) oder „Eigene Farben“ mit Hintergrund- und Detailfarbe.
  Leisten, Menüs, Dialoge, Beschriftungen und Schrift passen sich an, ein
  heller Hintergrund bekommt automatisch dunkle Schrift. Änderungen gelten
  sofort in allen offenen Diagrammen.
- Exportierte HTML-Ansichten übernehmen das Farbschema (abschaltbar).
- Gemeinsame HTML-Vorlage: Farben laufen über CSS-Variablen; die Standard-
  darstellung der DocuClick-Apps bleibt unverändert.

## [0.1.1] – unveröffentlicht

- Zusätzliche obere Menüleiste entfernt. Der neue Button „HTML-Ansicht“ in
  der Diagramm-Werkzeugleiste exportiert eigenständiges HTML mit Suche, Zoom,
  Bildansicht und Anleitung, ohne Bearbeitungs- und Speichercode.

## [0.1.0] – unveröffentlicht

- Erstes eigenständiges Obsidian-Desktop-Plugin: Diagramme
  erstellen/bearbeiten, HTML-Import/-Export, versionierte `.docuclick`-Dateien,
  Vault-Speicherung mit Konfliktsicherung.
- Nutzt den gemeinsamen HTML-Editor (`core/DocuClick.Core/WebAssets/
  viewer.template.html`), inklusive Undo/Redo.
