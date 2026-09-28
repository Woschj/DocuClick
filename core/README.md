# DocuClick.Core – gemeinsamer Kern

> Teil des DocuClick-Repos: [Übersicht](../README.md)

Plattformunabhängiger Teil (net10.0), den die [Windows-App](../windows/README.md)
und die [macOS-App](../macos/README.md) verwenden. Das
[Obsidian-Plugin](../obsidian/README.md) bündelt beim Build die HTML-Vorlage
aus `WebAssets/`. Änderungen hier betreffen also alle drei Bereiche: vor dem
Zusammenführen beide Apps und das Plugin prüfen.

- `Services/`: Session-Logik (`SessionManager`), Ablauf-Datei
  (`CanvasFlowWriter`, `CanvasDocumentIo`, `HtmlViewerBuilder`), draw.io-Export,
  Speicherdienst für Browser-Änderungen (`LocalSaveService`), Protokoll der
  Ablauf-Übersicht (`FlowEditorBridge`), Konfiguration, Log.
- `Platform/`: Schnittstellen, die jede App implementiert (Eingabe,
  Screenshots, Element-Erkennung, Vordergrundfenster, Töne).
- `WebAssets/`: Ablauf-Übersicht (`index.html`, `flow.js`, `flow.css`), HTML-
  Vorlage der Ablauf-Datei (`viewer.template.html`), Cytoscape.js.

Tests (laufen auf jedem Betriebssystem):

```bash
dotnet test --project core/DocuClick.Core.Tests
```

Änderungen am Kern stehen im [Windows-Changelog](../windows/CHANGELOG.md).
