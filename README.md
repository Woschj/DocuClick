# DocuClick

Windows-Screenshot-Tool, das bei jedem Mausklick (optional auch bei Enter)
automatisch einen Screenshot mit Klick-Markierung erstellt und samt
Beschreibungstext in eine Obsidian-Notiz, ein Obsidian-Canvas oder ein
Word-Dokument einfügt. Details zu allen drei Ausgabeformaten weiter unten.

App-Icon: [Assets/app.ico](src/DocuClick/Assets/app.ico) (im selben
Rot-auf-Dunkel-Stil wie das Tray-Icon).

## Windows-Sicherheitswarnung beim Download

Die `.exe` ist aktuell **nicht code-signiert**, daher zeigt Windows
SmartScreen beim ersten Ausführen eine Warnung ("Windows hat den Start
dieser App verhindert" o. Ä.) — das ist normal für unsignierte, neue
Software und keine Fehlfunktion. Ein Code-Signing-Zertifikat kostet
laufend Geld und erfordert in der Regel eine verifizierte Firma (siehe
CA/Browser-Forum-Anforderungen); solange keines eingerichtet ist, bleibt
die Warnung bestehen. Wer die Warnung wegklicken will: "Weitere
Informationen" → "Trotzdem ausführen".

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

- Bei jedem Push nach `main`: Artefakt "DocuClick-win-x64" (ein Zip mit
  `DocuClick.exe` + den benötigten DLLs) im jeweiligen
  [Actions-Lauf](../../actions) herunterladbar (self-contained, läuft ohne
  separat installiertes .NET).
- Bei einem Tag wie `v1.0.0`: zusätzlich ein
  [GitHub Release](../../releases) mit `DocuClick-win-x64.zip` als Anhang.

Bewusst kein Single-File-Publish: Der Self-Extract-Mechanismus (Entpacken
in einen Temp-Ordner beim Start) ist bei unsignierten Binaries ein
häufiger Auslöser für Windows-Defender-ML-Fehlalarme (z. B.
`Wacatac.B!ml`). Nach dem Download muss das Zip entpackt werden;
`DocuClick.exe` startet dann direkt aus dem entpackten Ordner.

Neues Release erstellen:

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Stand

Funktional vollständig für den Alltagsgebrauch:

- Tray-Icon mit Start/Stop, globale Maus-/Tastatur-Hooks, fenstergenaue
  Screenshots (richtiger Monitor bei Multi-Monitor)
- Markierung: Bounding-Box (UI-Automation) oder roter Kreis als Fallback
- Drei Ausgabeformate: Notiz, Obsidian-Canvas, Word (siehe unten)
- Einstellungsfenster mit JSON-Konfiguration, änderbare globale Hotkeys
- Branch-Logik (Abzweigungen) für Canvas und Word, inkl. "Ablauf
  fortsetzen ab Punkt..."
- Immer sichtbare Top-Leiste mit Aufnahmestatus + "Neue Session"-Button
- Akustisches Feedback pro aufgezeichnetem/übersprungenem/fehlgeschlagenem Klick

Offen: Feinschliff bei Multi-Monitor/DPI-Kantenfällen, robustere
Fehlerbehandlung in Randfällen.

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
   (im Canvas-/Word-Modus stattdessen als Knoten bzw. Abschnitt, siehe unten)

Konfiguration über das Tray-Menü ("Einstellungen...") oder direkt in
`%APPDATA%/DocuClick/config.json`.

### Top-Leiste, Overlays und "Neue Session"

Eine schmale Leiste am oberen Bildschirmrand ist sichtbar, solange die App
läuft (nicht nur während einer Aufnahme), und zeigt auf einen Blick den
Aufnahmestatus (inkl. Branch-Tiefe, falls > 0). Ihr Button **"Neue
Session"** ist nur aktiv, während eine Aufnahme läuft: Ein Klick beendet
die laufende Session (Datei wird abgeschlossen) und startet sofort eine
neue mit garantiert neuer Zieldatei — auch wenn in den Einstellungen eine
feste Zieldatei konfiguriert ist. Anders als die beiden folgenden Overlays
ist die Leiste **nicht** klick-durchlässig, da sie einen echten Button
hostet.

Zusätzlich, nur während einer laufenden Aufnahme:

- Ein kleiner roter Punkt (unterhalb der Top-Leiste) zeigt an, dass die
  Aufnahme läuft.
- Im Canvas-/Word-Modus zeigt ein zweites, kleines Overlay direkt darunter
  die aktuelle Position im Ablauf (Branch-Tiefe + letzter Knoten).

Diese beiden Overlays sind klick-durchlässig (stören keine Bedienung) und
werden wie die Top-Leiste aktiv aus Screenshots ausgeschlossen
(`SetWindowDisplayAffinity`), tauchen also nie selbst im aufgenommenen Bild
auf.

## Ausgabeformat: Notiz, Canvas oder Word

In den Einstellungen lässt sich eines von drei Formaten wählen:

- **Notiz**: linearer Markdown-Text + Bild-Link, an eine `.md`-Datei angehängt (Standard).
- **Obsidian-Canvas**: jeder Klick wird ein verbundener Knoten auf einer
  Fläche in einer `.canvas`-Datei (reines JSON, kein Obsidian-Plugin
  nötig). Gut für kurze bis mittlere Abläufe; bei sehr langen Abläufen wird
  eine feste Fläche schnell unübersichtlich.
- **Word**: jeder Klick wird ein eigener Abschnitt (Überschrift +
  Screenshot), fortlaufend an eine `.docx`-Datei angehängt — kein
  Canvas-Größenlimit, beliebig lange Abläufe bleiben lesbar. Abzweigungen
  werden als Rücksprung-Link im Dokument dargestellt statt als eigene
  Spalte. Screenshots werden direkt eingebettet (kein separater
  Attachments-Ordner nötig). Voll editierbar in Microsoft Word, SharePoint
  zeigt/bearbeitet `.docx` nativ ohne zusätzliches Plugin.

Das Pfad-Feld in den Einstellungen passt sich dem gewählten Format an: bei
Notiz/Canvas heißt es "Obsidian-Vault" (inkl. Attachments-Unterordner); bei
Word heißt es "Zielordner" und der Attachments-Unterordner wird
ausgeblendet, da Word Bilder direkt einbettet und keinen Obsidian-Vault
braucht — es kann jeder beliebige Ordner sein (z. B. ein SharePoint-Sync-Ordner).

Canvas und Word unterstützen dieselbe Branch-Logik, nur mit
unterschiedlicher Darstellung: Canvas legt Abzweigungen als neue Spalte
rechts neben dem Hauptablauf an; Word hängt sie stattdessen als neuen
Abschnitt mit Rücksprung-Link ans Dokumentende an, da ein Word-Dokument
keine räumlichen Koordinaten kennt.

Abzweigungen werden über zwei globale Hotkeys gesteuert (Standard: `F9` /
`F10`, änderbar in den Einstellungen):

- **Abzweigungspunkt setzen** (`F9`): merkt sich den zuletzt erstellten
  Knoten/Abschnitt als Anker (im Canvas-Modus wird der Knoten zur
  Kennzeichnung eingefärbt) und legt ihn auf einen Stack.
- **Zu letztem Abzweigungspunkt springen** (`F10`): setzt den "Cursor"
  zurück auf den obersten Anker im Stack (ohne ihn zu entfernen — man kann
  also mehrfach vom selben Punkt abzweigen). Der nächste Klick beginnt dann
  eine neue Spalte (Canvas) bzw. einen neuen Abschnitt mit Rücksprung-Link
  (Word), verbunden mit dem Anker statt mit dem zuletzt aufgezeichneten
  Klick.

Nach jeder Aktion zeigt DocuClick, wo man gerade steht: ein Balloon-Tip mit
der Beschreibung des betroffenen Knotens sowie der aktuellen Branch-Tiefe,
und die Branch-Tiefe bleibt zusätzlich dauerhaft im Tray-Icon-Tooltip und
in der Top-Leiste sichtbar (z. B. "DocuClick – Aufnahme läuft · Branch-Tiefe 2").

Änderungen an den Hotkeys gelten sofort nach "Speichern" in den
Einstellungen (keine Neustart nötig).

### Ablauf nachträglich fortsetzen

Über das Tray-Menü "Ablauf fortsetzen ab Punkt..." (nur verfügbar im
Canvas- oder Word-Modus, bei gestoppter Aufnahme) öffnet sich eine Liste
aller bereits vorhandenen Knoten/Abschnitte in der aktuellen Datei. Die
Auswahl legt fest, an welchem Punkt die *nächste* Aufnahme-Session ansetzt
— neue Klicks werden dann (im Canvas als neue Spalte, in Word als neuer
Abschnitt mit Rücksprung-Link) mit genau diesem Punkt verbunden,
unabhängig davon, wie lange die ursprüngliche Aufzeichnung schon
zurückliegt. Das funktioniert nur zuverlässig, solange "Neue Notiz/Canvas
pro Aufnahme-Session" deaktiviert ist (eine feste Zieldatei), da sich die
Auswahl auf die Knoten/Abschnitte in exakt dieser einen Datei bezieht.

## Start/Stopp per Hotkey

Neben dem Tray-Menü/Icon-Klick lässt sich die Aufnahme auch über einen
globalen Hotkey starten/stoppen (Standard: `Strg+Alt+R`, änderbar in den
Einstellungen).

## Feedback beim Aufzeichnen

Der eigentliche Screenshot läuft absichtlich unsichtbar im Hintergrund
(kein Bildschirm-Flackern). Damit trotzdem klar ist, dass etwas passiert,
spielt DocuClick bei aktivierter Option "Signalton bei jedem aufgezeichneten
Klick" (Standard: an) einen kurzen Ton:

- normaler Klick aufgezeichnet → ein synthetischer Kamera-Klick (zwei
  kurze, schnell abklingende Impulse) statt eines Windows-Systemtons, damit
  es sich nach Bestätigung statt nach Fehlermeldung anhört
- Klick übersprungen (Modifier-Taste gedrückt) → dezenter Windows-Systemton
- Fehler bei der Verarbeitung → Fehler-Systemton + Balloon-Tip am Tray-Icon

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
