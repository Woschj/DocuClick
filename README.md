# DocuClick

Windows-Screenshot-Tool, das bei jedem Mausklick automatisch einen Screenshot
mit Klick-Markierung erstellt und samt Beschreibungstext in eine
Obsidian-Notiz einfügt. Siehe Projektspezifikation für den vollständigen
Funktionsumfang.

## Build (nur unter Windows)

Voraussetzung: .NET 8 SDK.

```
dotnet build DocuClick.sln
```

Start:

```
dotnet run --project src/DocuClick/DocuClick.csproj
```

> Hinweis: WPF-Projekte lassen sich nur unter Windows kompilieren
> (der XAML-Compiler ist Windows-only). Dieses Repo wurde auf macOS
> geschrieben und dort **nicht gebaut** – vor dem ersten Release bitte
> auf einer Windows-Maschine `dotnet build` ausführen.

## Fertige .exe herunterladen

Es wird keine kompilierte `.exe` im Repo mitversioniert. Stattdessen baut
[.github/workflows/build.yml](.github/workflows/build.yml) bei jedem Push
automatisch auf einem Windows-Runner:

- Bei jedem Push nach `main`: Artefakt "DocuClick-win-x64" im jeweiligen
  [Actions-Lauf](../../actions) herunterladbar (self-contained, läuft ohne
  separat installiertes .NET).
- Bei einem Tag wie `v1.0.0`: zusätzlich ein
  [GitHub Release](../../releases) mit `DocuClick.exe` als Anhang.

Neues Release erstellen:

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Stand

- [x] Schritt 1: Grundgerüst mit Tray-Icon, Start/Stop-Menü
- [x] Schritt 2: Globaler Mouse-Hook (WH_MOUSE_LL)
- [x] Schritt 3: Screenshot-Erstellung (richtiger Monitor bei Multi-Monitor)
- [x] Schritt 4: Highlighter (Kreis)
- [x] Schritt 5: Obsidian-Schreiblogik (Bild + Text an Notiz anhängen)
- [x] Schritt 6: UI-Automation-Beschreibungstexte (mit Fallback)
- [x] Schritt 7: Bounding-Box-Highlighter (statt Kreis, wenn UIA-Element vorhanden)
- [x] Schritt 8: Einstellungsfenster + JSON-Konfiguration
- [x] Globale Hotkeys (Aufnahme starten/stoppen + Canvas-Branch-Steuerung)
- [x] Canvas-Flow-Modus mit Abzweigungen (siehe unten)
- [x] Akustisches Feedback pro aufgezeichnetem/übersprungenem Klick
- [ ] Feinschliff: Multi-Monitor/DPI-Kantenfälle, robustere Fehlerbehandlung

## Funktionsumfang (erste funktionale Version)

Tray-Icon-Bedienung: **Linksklick öffnet die Einstellungen** (ein versehentlicher
Klick darf nie ungefragt eine Aufnahme starten), **Rechtsklick öffnet das
Kontextmenü** mit "Aufnahme starten/stoppen" — oder einfach den Start/Stop-Hotkey
verwenden (Standard `Strg+Alt+R`).

Solange die Aufnahme aktiv ist, löst jeder Linksklick aus:

1. UI-Automation-Lookup des Elements unter dem Cursor (abschaltbar in den
   Einstellungen) inkl. Fallback auf Fenstertitel + Zeitstempel
2. Screenshot **nur des Fensters, in dem geklickt wurde** (nicht des ganzen
   Monitors) — ermittelt über das Fenster unter der Klickposition
3. Markierung: Bounding-Box des Elements, falls vorhanden und deutlich
   kleiner als das Fenster, sonst roter Kreis um die Klickposition (ein zu
   großes UIA-Bounding-Rect — z. B. wenn die Automation die Fensterfläche
   selbst zurückgibt — fällt automatisch auf den Kreis zurück, damit nicht
   ganze Fenster rot eingerahmt werden)
4. Speichern des Bilds im konfigurierten Attachments-Ordner und Anhängen von
   Beschreibung + `![[bild.png]]` an die Session-Notiz im Obsidian-Vault

Konfiguration über das Tray-Menü ("Einstellungen...") oder direkt in
`%APPDATA%/DocuClick/config.json`.

### Overlays während der Aufnahme

- Ein kleiner roter Punkt oben links auf dem primären Bildschirm zeigt an,
  dass die Aufnahme läuft.
- Im Canvas-Modus zeigt ein zweites, kleines Overlay direkt darunter die
  aktuelle Position im Ablauf (Branch-Tiefe + letzter Knoten).

Beide Overlays sind klick-durchlässig (stören keine Bedienung) und werden
aktiv aus Screenshots ausgeschlossen (`SetWindowDisplayAffinity`), tauchen
also nie selbst im aufgenommenen Bild auf.

## Canvas-Flow-Modus

Statt an eine lineare Notiz anzuhängen, kann DocuClick jeden Klick als
verbundenen Knoten in eine Obsidian-`.canvas`-Datei schreiben (Einstellungen
→ "Canvas-Modus"). Eine `.canvas`-Datei ist reines JSON, es braucht dafür
kein Obsidian-Plugin. Der Hauptablauf läuft **vertikal** (von oben nach
unten in einer Spalte); Abzweigungen öffnen jeweils eine neue Spalte rechts
daneben.

Abzweigungen werden über zwei globale Hotkeys gesteuert (Standard: `F9` /
`F10`, änderbar in den Einstellungen):

- **Abzweigungspunkt setzen** (`F9`): merkt sich den zuletzt erstellten
  Knoten als Anker (der Knoten wird zur Kennzeichnung eingefärbt) und legt
  ihn auf einen Stack.
- **Zu letztem Abzweigungspunkt springen** (`F10`): setzt den "Cursor"
  zurück auf den obersten Anker im Stack (ohne ihn zu entfernen — man kann
  also mehrfach vom selben Punkt abzweigen). Der nächste Klick beginnt dann
  eine neue Spalte, verbunden mit dem Anker statt mit dem zuletzt
  aufgezeichneten Klick.

Nach jeder Aktion zeigt DocuClick, wo man gerade steht: ein Balloon-Tip mit
der Beschreibung des betroffenen Knotens sowie der aktuellen Branch-Tiefe,
und die Branch-Tiefe bleibt zusätzlich dauerhaft im Tray-Icon-Tooltip
sichtbar (z. B. "DocuClick - Aufnahme läuft · Branch-Tiefe: 2").

Änderungen an den Hotkeys gelten sofort nach "Speichern" in den
Einstellungen (keine Neustart nötig).

### Ablauf nachträglich fortsetzen

Über das Tray-Menü "Ablauf fortsetzen ab Punkt..." (nur verfügbar im
Canvas-Modus, bei gestoppter Aufnahme) öffnet sich eine Liste aller bereits
vorhandenen Knoten in der aktuellen Canvas-Datei. Die Auswahl legt fest, an
welchem Knoten die *nächste* Aufnahme-Session ansetzt — neue Klicks bilden
dann eine neue Spalte ab genau diesem Punkt, unabhängig davon, wie lange die
ursprüngliche Aufzeichnung schon zurückliegt. Das funktioniert nur
zuverlässig, solange "Neue Notiz/Canvas pro Aufnahme-Session" deaktiviert
ist (eine feste Zieldatei), da sich die Auswahl auf die Knoten in exakt
dieser einen Datei bezieht.

## Start/Stopp per Hotkey

Neben dem Tray-Menü/Icon-Klick lässt sich die Aufnahme auch über einen
globalen Hotkey starten/stoppen (Standard: `Strg+Alt+R`, änderbar in den
Einstellungen).

## Feedback beim Aufzeichnen

Der eigentliche Screenshot läuft absichtlich unsichtbar im Hintergrund
(kein Bildschirm-Flackern). Damit trotzdem klar ist, dass etwas passiert,
spielt DocuClick bei aktivierter Option "Signalton bei jedem aufgezeichneten
Klick" (Standard: an) einen kurzen Systemsound:

- normaler Klick aufgezeichnet → kurzer Klick-Sound
- Klick übersprungen (Modifier-Taste gedrückt) → anderer, dezenter Ton
- Fehler bei der Verarbeitung → Fehler-Sound + Balloon-Tip am Tray-Icon

## Enter-Taste als zweiter Trigger

Neben Linksklicks kann DocuClick auch bei jedem Druck der Enter-Taste
auslösen (Einstellungen → "Auch bei Enter-Taste aufzeichnen", Standard: an).
Erfasst wird dann das aktive Fenster plus das aktuell fokussierte
UI-Automation-Element (z. B. ein abgeschicktes Formularfeld) statt einer
Klickposition — ohne Bounding-Box wird der Screenshot unmarkiert
gespeichert, es gibt keinen "Blindkreis".

Der zugrunde liegende Tastatur-Hook (`WH_KEYBOARD_LL`) vergleicht
ausschließlich den virtuellen Tastencode gegen Enter (`VK_RETURN`); jede
andere Taste läuft unbeachtet durch (`CallNextHookEx`) und wird nicht
ausgelesen.

## Hotkeys per Tastendruck festlegen

In den Einstellungen auf "Ändern" neben einem Hotkey klicken und die
gewünschte Tastenkombination drücken (statt Text einzutippen) — Esc bricht
die Aufnahme ab. Betrifft Start/Stop sowie die beiden Branch-Hotkeys.

## Klicks überspringen

In den Einstellungen lässt sich eine Modifier-Taste (Umschalt/Strg/Alt)
festlegen: Ist sie bei einem Linksklick gedrückt, wird dieser Klick
komplett ignoriert (kein Screenshot, kein Notiz-Eintrag). Nützlich, um
z. B. sensible Inhalte gezielt aus der Aufzeichnung auszuschließen.

## Fehlersuche

Alle Ereignisse (Session-Start/-Stop, erkannte Klicks, geschriebene Einträge,
Fehler) landen in `%APPDATA%/DocuClick/log.txt`. Bei einem Fehler pro Klick
erscheint zusätzlich ein Balloon-Tip am Tray-Icon. Wenn nach einem Klick
weder im Log noch als Notiz etwas ankommt, wurde der Klick vom Mouse-Hook gar
nicht erst erkannt (Session nicht gestartet, oder der Hook konnte nicht
registriert werden — siehe Log-Zeile "Session gestartet").
