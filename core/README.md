# DocuClick.Core – gemeinsamer Kern

> Teil des DocuClick-Repos: [Übersicht](../README.md)

Plattformunabhängiger Teil (net10.0), den die [Windows-App](../windows/README.md)
und die [macOS-App](../macos/README.md) verwenden. Das
[Obsidian-Plugin](../obsidian/README.md) bündelt beim Build die HTML-Vorlage
aus `WebAssets/`. Änderungen hier betreffen also alle drei Bereiche: vor dem
Zusammenführen beide Apps und das Plugin prüfen.

- `Services/`: Session-Logik (`SessionManager`), Ablauf-Datei
  (`CanvasFlowWriter`, `CanvasDocumentIo`, `HtmlViewerBuilder`), draw.io-Export,
  Speicherdienst für Browser-Änderungen (`LocalSaveService`), Anbindung der
  Ablauf-Übersicht (`EditorPageHost`), Konfiguration, Log.
- `Platform/`: Schnittstellen, die jede App implementiert (Eingabe,
  Screenshots, Element-Erkennung, Vordergrundfenster, Töne).
- `WebAssets/`: der gemeinsame Editor (`viewer.template.html`) – Vorlage jeder
  Ablauf-Datei, Ablauf-Übersicht beider Apps und Ansicht im Obsidian-Plugin –
  und Cytoscape.js.

Tests (laufen auf jedem Betriebssystem):

```bash
dotnet test --project core/DocuClick.Core.Tests
```

Änderungen am Kern stehen im [Windows-Changelog](../windows/CHANGELOG.md).
