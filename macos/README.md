# DocuClick für macOS

> Teil des DocuClick-Repos: [Übersicht](../README.md) · Windows-App in
> [windows/](../windows/README.md) · Obsidian-Plugin in [obsidian/](../obsidian/README.md).

Menüleisten-App mit denselben Funktionen wie DocuClick für Windows: Jeder
Klick erzeugt einen Screenshot mit Klick-Markierung und einen Schritt im
**Ablauf** (eine eigenständige, interaktive `.html`-Datei). Speicherformat,
Ablauf-Editor und Session-Logik kommen aus dem gemeinsamen Kern
([core/](../core/README.md)), daher sind Abläufe zwischen Mac und Windows
austauschbar. Eigene Version und eigene Releases (`macos-v*`).

Voraussetzungen: Apple Silicon, macOS 14 oder neuer.

## Installation

1. `DocuClick-macos-arm64.zip` von den
   [Releases](https://github.com/Woschj/DocuClick/releases) (Tags `macos-v…`)
   herunterladen, entpacken und `DocuClick.app` nach **Programme** ziehen.
2. Beim ersten Öffnen meldet macOS, dass die App nicht überprüft werden kann.
   DocuClick ist nur selbst signiert (kein kostenpflichtiges Apple-Developer-
   Konto, keine Notarisierung). Einmalig freigeben: **Systemeinstellungen →
   Datenschutz & Sicherheit → „Trotzdem öffnen“**.
3. DocuClick erscheint als Symbol in der Menüleiste (kein Dock-Symbol) und
   fragt die Berechtigungen ab.

### Berechtigungen

Unter **Systemeinstellungen → Datenschutz & Sicherheit** braucht DocuClick:

| Berechtigung | Wofür |
|---|---|
| Bedienungshilfen | Name des angeklickten Elements für den Beschreibungstext |
| Eingabeüberwachung | Klicks und Enter erkennen, globale Tastenkürzel |
| Bildschirmaufnahme | Screenshots |

Nach dem Erteilen DocuClick neu starten (Menü **Berechtigungen … → Neu
starten**). Weil jede Version mit demselben Zertifikat signiert ist, bleiben
die Berechtigungen bei Updates erhalten.

## Nutzung

1. **Aufnahme starten**: Menüleisten-Symbol → **Aufnahme starten**, in der
   Top-Leiste oben auf **Aufnahme** oder **⌃⌥R**. (**Neu** fragt immer nach
   einer Zieldatei.) Beim ersten Start fragt
   DocuClick nach Ordner und Dateiname des Ablaufs (oder nach einer
   bestehenden Datei zum Fortsetzen).
2. **Klicken**: Jeder Links- und Rechtsklick sowie Enter (beides in den
   Einstellungen abschaltbar) wird ein Schritt mit Screenshot und
   Beschreibung, z. B. „Linksklick auf Taste „Sichern“ im Fenster „…““. Klicks mit gedrückter **⌥**-Taste und
   Klicks auf DocuClick selbst werden nicht aufgenommen.
3. **Pausieren / Fortsetzen**: **Stopp** in der Top-Leiste oder **⌃⌥R**
   pausiert nur; die Datei bleibt geladen. **Neu** schließt die Datei ab
   und beginnt eine neue Session.
4. **Ablauf-Übersicht**: schwebendes, durchscheinendes Fenster mit dem
   Ablauf. Verschieben an der Kopfzeile, Größe an allen Rändern oder am
   Griff unten rechts ändern, ⛶ passt die Ansicht ein, – klappt ein, ✕
   schließt (über **Ablauf** in der Top-Leiste wieder öffnen). Karten
   verschieben, per Doppelklick umbenennen, per Rechtsklick löschen,
   verbinden oder Abzweigungen anlegen.
5. **Abzweigung**: **⌃⌥D** fragt nach dem Namen des Pfads, setzt eine
   Abzweigungs-Raute hinter den letzten Schritt, und die nächsten Klicks
   landen im neuen Pfad. Weitere Pfade: Rechtsklick auf einen Schritt in
   der Übersicht → „+ Neuer Pfad ab hier“.
6. **Zoom-auf-Cursor**: **⌃⌥Z** oder **Zoom** in der Top-Leiste. Screenshots
   zeigen dann nur den Bereich um den Mauszeiger.
7. **Bestehenden Ablauf öffnen**: Menüleiste → **Ablauf öffnen …** (ohne
   Aufnahme). **Nach draw.io exportieren …** erzeugt ein editierbares
   draw.io-Diagramm.

### Im Browser bearbeiten

Die `.html`-Datei öffnet sich in jedem Browser (Safari, Firefox, Chrome …)
und lässt sich dort bearbeiten. Solange DocuClick läuft, landen die
Änderungen **direkt in der Datei**, ohne Dateiauswahl oder „Mit Datei
verbinden“. Läuft DocuClick nicht, bleibt „Kopie herunterladen“. Details:
[Windows-Anleitung](../windows/README.md#im-browser-bearbeiten-und-speichern).

Ausführliche Beschreibung aller Funktionen (Abzweigungen, Anleitung/SOP-
Ansicht, Fortsetzen an einem bestimmten Punkt, draw.io-Export): siehe
[Windows-Anleitung](../windows/README.md#funktionsumfang). Die Funktionen
sind auf dem Mac gleich, abgesehen von den folgenden Unterschieden.

### Fehlersuche

- **DocuClick fehlt in einer Berechtigungsliste**: Menüleiste →
  **Berechtigungen …** öffnet die passende Einstellungsseite und fragt die
  Berechtigung erneut an. Danach **Neu starten**.
- **Screenshots zeigen nur den Hintergrund**: Bildschirmaufnahme ist nicht
  erlaubt oder DocuClick wurde danach nicht neu gestartet.
- **Log**: `~/Library/Logs/DocuClick/log.txt`.

## Unterschiede zu Windows

- Bedienung über das Menüleisten-Symbol statt Tray-Icon; Top-Leiste und
  Ablauf-Übersicht wie unter Windows.
- Standard-Tastenkürzel: **⌃⌥R** Aufnahme starten/stoppen, **⌃⌥D**
  Abzweigung, **⌃⌥Z** Zoom-auf-Cursor. Klick mit gedrückter **⌥**-Taste wird
  nicht aufgenommen (⌃-Klick ist auf dem Mac ein Rechtsklick).
- Screenshots zeigen den Zustand **vor** dem Klick (fortlaufender
  Aufnahme-Stream während der Aufnahme); Retina-Aufnahmen werden auf
  Bildschirmpunkte verkleinert.
- Klicks in Passwortfelder werden nicht aufgenommen.
- Einstellungen: `~/Library/Application Support/DocuClick/`,
  Log: `~/Library/Logs/DocuClick/log.txt`.

## Für Entwickler

Voraussetzungen: .NET 10 SDK, Xcode-Befehlszeilenwerkzeuge (Swift).

```bash
dotnet build macos/DocuClick.Mac.slnx
dotnet test --project core/DocuClick.Core.Tests
```

Aufbau:

- `DocuClick.Mac/`: Avalonia-UI (Menüleiste, Top-Leiste, Dialoge,
  Ablauf-Übersicht im nativen WebKit-WebView), Plattform-Adapter.
- `DocuClickMacNative/`: Swift-Bibliothek `libDocuClickMac.dylib` mit
  C-Schnittstelle (`dc_*`): CGEventTap, Carbon-Hotkeys, ScreenCaptureKit,
  Accessibility, NSWindow-Hilfen. Wird beim `dotnet build` mitgebaut.
- `packaging/`: `Info.plist`, App-Icon, Build- und Signierskripte.

### App bauen und signieren

Einmalig die selbstsignierte Signier-Identität anlegen (liegt danach unter
`~/.docuclick-signing/`; `DocuClick-Signing.p12` und `p12-password.txt`
sichern! Geht das Zertifikat verloren, müssen alle Nutzer die
Berechtigungen neu erteilen):

```bash
macos/packaging/create-signing-identity.sh
```

App bauen (Ergebnis: `dist/DocuClick.app` und `dist/DocuClick-macos-arm64.zip`):

```bash
macos/packaging/build-app.sh
```

Die Version steht in `DocuClick.Mac/DocuClick.Mac.csproj` (`<Version>`).
Release: Version und [CHANGELOG.md](CHANGELOG.md) anheben, dann

```bash
git tag macos-v0.1.0
git push origin macos-v0.1.0
```

Die CI ([.github/workflows/macos.yml](../.github/workflows/macos.yml)) baut
und signiert nur, wenn die Repository-Secrets `MAC_SIGN_P12` (Base64 der
`.p12`) und `MAC_SIGN_P12_PASSWORD` gesetzt sind; sonst laufen nur Build
und Tests.
