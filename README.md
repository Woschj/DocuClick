# DocuClick

Windows-Screenshot-Tool, das bei jedem Mausklick (optional auch bei Enter)
automatisch einen Screenshot mit Klick-Markierung erstellt und samt
Beschreibungstext in eine Obsidian-Notiz, ein Obsidian-Canvas, ein
Word-Dokument, eine PowerPoint-Folie oder (experimentell) ein
Excalidraw-Sketch einfügt. Details zu allen fünf Ausgabeformaten weiter
unten.

App-Icon: [Assets/app.ico](src/DocuClick/Assets/app.ico) (im selben
Rot-auf-Dunkel-Stil wie das Tray-Icon).

## Installation

Fertige `.exe` von den [Releases](../../releases) herunterladen
(`DocuClick-win-x64.zip`), entpacken und `DocuClick.exe` starten —
self-contained, kein separat installiertes .NET nötig.

> **SmartScreen-Warnung beim ersten Start:** Die `.exe` ist aktuell nicht
> code-signiert, daher zeigt Windows SmartScreen eine Warnung ("Windows
> hat den Start dieser App verhindert" o. Ä.) — normal für unsignierte,
> neue Software, keine Fehlfunktion. Wegklicken über "Weitere
> Informationen" → "Trotzdem ausführen".

Nach dem Start läuft DocuClick als Tray-Icon im Infobereich der
Taskleiste — kein sichtbares Fenster, siehe [Funktionsumfang](#funktionsumfang)
für die Bedienung.

## Obsidian einrichten und den Vault nutzen

Für die Notiz-, Canvas- und Excalidraw-Ausgabeformate (nicht für Word/PowerPoint) wird
[Obsidian](https://obsidian.md) empfohlen — kostenlos, kein Account nötig,
öffnet einfach einen lokalen Ordner als "Vault". Für Notiz/Canvas ist kein
Plugin erforderlich, DocuClick schreibt reine Markdown-/JSON-Dateien direkt
auf die Festplatte; für Excalidraw wird zusätzlich das kostenlose
Excalidraw-Community-Plugin gebraucht (siehe
[Ausgabeformat](#ausgabeformat-notiz-canvas-word-powerpoint-oder-excalidraw)).

1. **Obsidian installieren**: Installer von [obsidian.md](https://obsidian.md/download)
   herunterladen und ausführen.
2. **Vault vorbereiten**: [VaultTemplate/](VaultTemplate/) aus diesem Repo
   an einen Ort außerhalb des Repos kopieren (z. B.
   `%USERPROFILE%\Documents\Prozess-Vault`) — Details und der Grund dafür
   (Screenshots landen sonst im öffentlichen Git-Verlauf) in
   [VaultTemplate/README.md](VaultTemplate/README.md).
3. **Als Vault öffnen**: In Obsidian "Open folder as vault" → den kopierten
   Ordner auswählen. Das mitgelieferte Theme (inkl. automatischer
   Ordnerfärbung) wird direkt übernommen.
4. **DocuClick verbinden**: In den DocuClick-Einstellungen den
   Vault-Pfad auf denselben kopierten Ordner setzen, Ausgabeformat auf
   Notiz oder Canvas stellen.

Danach läuft die Aufnahme unabhängig von Obsidian — die App muss beim
Aufzeichnen nicht mal geöffnet sein, DocuClick schreibt direkt in die
Dateien. Obsidian wird nur zum Ansehen/Bearbeiten der Ergebnisse gebraucht
und aktualisiert offene Notizen/Canvases automatisch, sobald sich die
Datei auf der Festplatte ändert.

Alltags-Workflow:

- Beim Start einer Aufnahme fragt DocuClick nach Zieldatei **und
  -ordner** innerhalb des Vaults (siehe [Zieldatei bei jedem
  Session-Start](#zieldatei-bei-jedem-session-start)) — damit landet
  jede Aufnahme direkt dort, wo sie in der Vault-Struktur hingehört,
  statt alles im Wurzelordner zu sammeln.
- Für länger geplante Abläufe lohnt es sich, vorher eine Vorlage aus
  `02 Vorlagen/` zu kopieren und mit Titel/Zweck auszufüllen, dann beim
  Session-Start "Bestehende Datei fortsetzen" wählen.
- Verzweigt sich ein Ablauf (z. B. Fehlerfall vs. Erfolgsfall), mit
  "Branch setzen" einen Namen vergeben und später über "Branch
  auswählen" gezielt dorthin zurückspringen (siehe
  [Ausgabeformat](#ausgabeformat-notiz-canvas-word-powerpoint-oder-excalidraw)).

## Funktionsumfang

Tray-Icon-Bedienung: **Linksklick öffnet die Einstellungen** (ein versehentlicher
Klick darf nie ungefragt eine Aufnahme starten), **Rechtsklick öffnet das
Kontextmenü** mit "Aufnahme starten/stoppen" — oder einfach den Start/Stop-Hotkey
verwenden (Standard `Strg+Alt+R`).

Solange die Aufnahme aktiv ist, löst jeder Links- **und Rechtsklick** aus
(Rechtsklick-Erfassung abschaltbar in den Einstellungen, Standard: an;
die Beschreibung unterscheidet "Linksklick auf ..." von "Rechtsklick
auf ..."):

1. UI-Automation-Lookup des Elements unter dem Cursor (abschaltbar in den
   Einstellungen) inkl. Fallback auf Fenstertitel + Zeitstempel
2. Screenshot **nur des Fensters, in dem geklickt wurde** (nicht des ganzen
   Monitors) — ermittelt über das Fenster unter der Klickposition
3. Markierung: Bounding-Box des Elements, falls vorhanden und deutlich
   kleiner als das Fenster, sonst roter Kreis um die Klickposition (ein zu
   großes UIA-Bounding-Rect — z. B. wenn die Automation die Fensterfläche
   selbst zurückgibt — fällt automatisch auf den Kreis zurück, damit nicht
   ganze Fenster rot eingerahmt werden)
4. Speichern des Bilds im konfigurierten Attachments-Ordner (in einem
   Unterordner benannt nach der Zieldatei, z. B.
   `Attachments/Onboarding-Flow/screenshot_....png`, statt alles flach zu
   sammeln) und Anhängen von Beschreibung +
   `![bild.png](relativer/Pfad.png)` (Standard-Markdown, kein
   Obsidian-spezifisches Wikilink — funktioniert daher auch in GitHub-/
   GitLab-Wikis und anderen Markdown-Renderern, nicht nur in Obsidian) an
   die Session-Notiz im Obsidian-Vault (im Canvas-/Word-/PowerPoint-/Excalidraw-Modus
   stattdessen als Knoten bzw. Abschnitt, siehe unten)

Klicks auf DocuClicks eigene Fenster (Top-Leiste, Branch-Dialoge,
Session-Start, Einstellungen, ...) sowie auf das Tray-Icon selbst zählen
nie als Aufnahme — automatisch erkannt und gefiltert.

Konfiguration über das Tray-Menü ("Einstellungen...") oder direkt in
`%APPDATA%/DocuClick/config.json`.

### Start vs. "Neue Session": Zieldatei

**Start** (Tray-Menü, Start/Stop-Hotkey, Top-Leiste) setzt die zuletzt
verwendete Aufnahme direkt fort — ohne Rückfrage. **Neue Session**
(Top-Leiste) fragt dagegen immer nach der Zieldatei, egal ob gerade
aufgezeichnet wird oder nicht — sie ist der einzige Weg, um bewusst zu
einer anderen bzw. neuen Datei zu wechseln:

- **Neue Datei anlegen**: Ein Name wird automatisch vorgeschlagen
  (**Zielordner-Name + Datum + laufende Nummer**, z. B.
  `IT-Support 2026-08-04 (1)`, statt eines generischen "Screenshots"), lässt
  sich aber frei überschreiben. Endung ergibt sich aus dem gewählten
  Ausgabeformat. Optional ein **Zielordner** wählen (relativ zum
  Vault-/Zielordner-Pfad) — Vorschläge kommen aus allen bereits
  vorhandenen Unterordnern, der Namensvorschlag passt sich beim
  Ordnerwechsel automatisch an, solange der Name nicht von Hand geändert
  wurde. So landen Aufnahmen direkt in der Vault-Struktur (z. B.
  `Prozesse/IT-Support`) statt immer im Wurzelordner, und die laufende
  Nummer verhindert, dass ein zweiter Klick auf "Neue Session" am selben
  Tag versehentlich eine bestehende Datei fortsetzt.
- **Bestehende Datei fortsetzen**: Auswahl aus allen vorhandenen Dateien
  mit passender Endung im konfigurierten Ordner (inkl. Unterordner),
  neueste zuerst. Neue Klicks werden an diese Datei angehängt (im
  Canvas-/Word-/PowerPoint-/Excalidraw-Modus ab dem bisherigen Cursor-Stand, siehe
  Branch-Logik unten).

Der Dialog erscheint außerdem beim allerersten "Start" nach Installation
(noch keine Datei zum Fortsetzen vorhanden) oder wenn das Ausgabeformat
seit der letzten Aufnahme gewechselt wurde. Wird der Dialog abgebrochen,
bleibt die Aufnahme aus (bzw. bei "Neue Session" während einer laufenden
Aufnahme: die laufende Session bleibt unverändert bestehen).

### Vault-Template für Prozessdokumentation

[VaultTemplate/](VaultTemplate/) enthält eine leere, für DocuClick
vorbereitete Obsidian-Vault-Struktur (Zielordner, Attachments-Unterordner,
Blanko-Vorlagen für Prozessnotizen/-canvases) als Startpunkt für eine
Knowledge Base. **Vor echter Nutzung außerhalb dieses Repos kopieren** —
siehe [VaultTemplate/README.md](VaultTemplate/README.md) für Details und
den Grund dafür (Screenshots landen sonst im öffentlichen Git-Verlauf).

### Top-Leiste, Overlays und "Neue Session"

Eine kleine, mittig oben schwebende Pille (wie die TeamViewer-Session-Leiste
— nicht bildschirmbreit, sonst würde sie Fenster ziehen/Menüs/Snap-Zonen
blockieren) ist sichtbar, solange die App läuft, und zeigt auf einen Blick
den Aufnahmestatus (inkl. aktuellem Branch-Namen, falls einer aktiv ist).
Frei verschiebbar per Ziehen. Sie enthält vier Buttons:

- **Start/Stop**: entspricht dem Tray-Menüpunkt bzw. dem Start/Stop-Hotkey
  — setzt die zuletzt verwendete Datei ohne Rückfrage fort.
- **Branch setzen** / **Branch auswählen**: entsprechen den beiden
  Branch-Hotkeys (siehe unten), nur aktiv während einer laufenden Aufnahme
  im Canvas-, Word-, PowerPoint- oder Excalidraw-Modus.
- **Neue Session**: immer klickbar, fragt **immer** nach der Zieldatei
  (anders als Start). Läuft gerade keine Aufnahme, startet sie damit neu.
  Läuft eine Aufnahme, schließt es die aktuelle Datei ab und startet
  direkt danach die neue (siehe vorheriger Abschnitt).

Anders als die beiden folgenden Overlays ist die Leiste **nicht**
klick-durchlässig, da sie echte Buttons hostet — deshalb ist sie bewusst
content-groß statt bildschirmbreit. Klicks auf die Top-Leiste oder auf das
Tray-Icon selbst werden nie als Aufnahme gewertet (kein Screenshot, kein
Eintrag) — die App erkennt und filtert das automatisch.

Zusätzlich, nur während einer laufenden Aufnahme:

- Ein kleiner roter Punkt (unterhalb der Top-Leiste) zeigt an, dass die
  Aufnahme läuft.
- Im Canvas-/Word-/PowerPoint-Modus zeigt ein zweites, kleines Overlay direkt darunter
  die aktuelle Position im Ablauf (aktueller Branch, alle gesetzten
  Branches, letzter Knoten).

Diese beiden Overlays sind klick-durchlässig (stören keine Bedienung) und
werden wie die Top-Leiste aktiv aus Screenshots ausgeschlossen, tauchen
also nie selbst im aufgenommenen Bild auf.

## Ausgabeformat: Notiz, Canvas, Word, PowerPoint oder Excalidraw

In den Einstellungen lässt sich eines von fünf Formaten wählen:

- **Notiz**: linearer Markdown-Text + Bild-Link, an eine `.md`-Datei angehängt (Standard).
- **Obsidian-Canvas**: jeder Klick wird ein verbundener Knoten auf einer
  Fläche in einer `.canvas`-Datei (reines JSON, kein Obsidian-Plugin
  nötig). Gut für kurze bis mittlere Abläufe; bei sehr langen Abläufen wird
  eine feste Fläche schnell unübersichtlich. Screenshots werden als
  eigener Datei-Node (Canvas' natives Embed-Format) statt als
  `![[wikilink]]` im Text abgelegt — Drittanbieter-Exporttools für Canvas
  kennen diesen Node-Typ meist, Obsidians Wikilink-Auflösung dagegen nicht,
  weshalb Bilder beim Export in andere Formate sonst fehlten. Betrifft nur
  neu aufgezeichnete Klicks; bereits bestehende `.canvas`-Dateien werden
  nicht automatisch migriert.
- **Word**: jeder Klick wird eine Heading3-Überschrift + Screenshot,
  fortlaufend an eine `.docx`-Datei angehängt — kein Canvas-Größenlimit,
  beliebig lange Abläufe bleiben lesbar. Da ein Word-Dokument keine
  räumlichen Koordinaten kennt, macht die Gliederung die Abzweigungen
  navigierbar statt sie räumlich zu platzieren: Hauptablauf = Heading1,
  jede Abzweigung ein eigenes Heading2-„Abzweigung: Name“, jeder Klick
  darunter ein Heading3 — Words eigener Navigationsbereich (Ansicht →
  Navigationsbereich) wird dadurch zur klickbaren Gliederung des ganzen
  Ablaufs. Zusätzlich steht direkt an der Abzweigungsstelle selbst ein
  „→ siehe Abzweigung 'Name'“-Verweis (nicht erst am Dokumentende), und
  der neue Abschnitt verlinkt mit „Ausgangspunkt: ...“ zurück — beide
  Richtungen sind einen Klick entfernt. Screenshots werden direkt
  eingebettet (kein separater Attachments-Ordner nötig). Voll editierbar
  in Microsoft Word, SharePoint zeigt/bearbeitet `.docx` nativ ohne
  zusätzliches Plugin.
- **PowerPoint**: ein echtes Kästchen-und-Pfeile-Flowchart statt nur einer
  Gliederung — anders als Word kennt eine `.pptx`-Folie tatsächliche
  x/y-Koordinaten. Da eine einzelne Folie aber eine feste Größe hat (keine
  unendliche Fläche wie Canvas), bekommt jede Spalte ihre eigene Folie: der
  Hauptablauf eine Folie „Hauptablauf“, jede Abzweigung eine eigene Folie
  „Abzweigung: Name“ (erst beim ersten Sprung dorthin angelegt). Navigation
  zwischen Folien läuft über anklickbare Foliensprung-Links (PowerPoint
  kann nur auf eine ganze Folie verlinken, nicht auf eine Position
  innerhalb einer Folie): am Abzweigungspunkt selbst erscheint ein „→ siehe
  Folie ‚Abzweigung: Name'“-Verweis, die neue Folie verlinkt mit „↩
  Ausgangspunkt: ...“ zurück. Screenshots werden direkt eingebettet (kein
  separater Attachments-Ordner nötig). Voll editierbar in PowerPoint,
  SharePoint zeigt/bearbeitet `.pptx` nativ ohne zusätzliches Plugin.
- **Excalidraw** *(experimentell)*: funktioniert wie Canvas (Knoten +
  Abzweigungen als neue Spalte), aber im freien Skizzen-Look statt fester
  Boxen — jeder Klick wird eine abgerundete Karte (Beschreibung +
  eingebetteter Screenshot) in einer `.excalidraw`-Datei. Braucht
  zusätzlich das kostenlose [Excalidraw-Community-Plugin](https://github.com/zsviczian/obsidian-excalidraw-plugin)
  für Obsidian (anders als Canvas, das bereits eingebaut ist). Textbeschriftungen
  nutzen bewusst Excalidraws eingebaute "Normal"-Schriftart statt der
  Standard-Handschrift-Schrift "Virgil", für einen saubereren Look — eine
  eigene Schriftdatei lässt sich in eine `.excalidraw`-Datei nicht sinnvoll
  einbetten (das Rendering hängt vom Plugin lokal ab, nicht vom File).

Das Pfad-Feld in den Einstellungen passt sich dem gewählten Format an: bei
Notiz/Canvas/Excalidraw heißt es "Obsidian-Vault" (Attachments-Unterordner
nur bei Notiz/Canvas sichtbar, da Word/PowerPoint/Excalidraw Bilder direkt
einbetten); bei Word und PowerPoint heißt es "Zielordner" und ist nicht an
einen Obsidian-Vault gebunden — es kann jeder beliebige Ordner sein (z. B.
ein SharePoint-Sync-Ordner).

Canvas, Word, PowerPoint und Excalidraw unterstützen dieselbe Branch-Logik,
nur mit unterschiedlicher Darstellung: Canvas und Excalidraw legen
Abzweigungen als neue Spalte rechts neben dem Hauptablauf an; Word hängt
sie als neuen Heading2-Abschnitt ans Dokumentende an; PowerPoint legt eine
neue Folie an. Word/PowerPoint können neuen Inhalt nur anhängen bzw. nur
ganze Folien verlinken, nicht frei räumlich platzieren wie Canvas.

Abzweigungen werden benannt und über zwei globale Hotkeys gesteuert
(Standard: `F9` / `F10`, änderbar in den Einstellungen):

- **Branch setzen** (`F9`): fragt nach einem Namen (z. B. "Login-Fehler")
  und legt dafür ein eigenes, sichtbares **"Branch: Login-Fehler"**-Objekt
  an (Knoten in Canvas/Excalidraw, Absatz in Word), verbunden mit dem
  zuletzt erstellten Knoten/Abschnitt — kein verstecktes Metadatenfeld,
  sondern ein normales Element in der Datei. Der laufende Ablauf wird dabei
  nicht unterbrochen, der nächste Klick hängt sich weiterhin ganz normal an
  den zuletzt aufgezeichneten Punkt. Ein bereits vergebener Name bekommt
  beim erneuten Setzen ein weiteres, aktuelleres Marker-Objekt (das neueste
  gilt).
- **Branch auswählen** (`F10`): öffnet eine Liste aller aktuell benannten
  Branches — die Auswahl setzt den "Cursor" auf das Marker-Objekt zurück
  (beliebig oft wiederholbar, auch nachdem bereits andere Klicks
  dazwischen aufgezeichnet wurden). Der nächste Klick beginnt dann eine
  neue Spalte (Canvas/Excalidraw) bzw. einen neuen Abschnitt mit
  Rücksprung-Link (Word), verbunden mit dem gewählten Branch statt mit dem
  zuletzt aufgezeichneten Klick.

Da Branches als echte, sichtbare Objekte in der Datei stehen, übersteht die
Liste der verfügbaren Branches auch ein Stoppen und erneutes Starten der
Aufnahme (auf derselben Datei) — DocuClick liest sie beim nächsten Start
einfach wieder aus der Datei ein, ohne dass ein separater Speicherzustand
nötig wäre.

Nach jeder Aktion zeigt DocuClick, wo man gerade steht: ein Balloon-Tip mit
Branch-Namen und der Beschreibung des betroffenen Knotens, und der aktuelle
Branch bleibt zusätzlich dauerhaft im Tray-Icon-Tooltip (Anzahl gesetzter
Branches) und in der Top-Leiste sichtbar (z. B. "DocuClick – Aufnahme
läuft · Branch: Login-Fehler").

Änderungen an den Hotkeys gelten sofort nach "Speichern" in den
Einstellungen (keine Neustart nötig).

### Ablauf nachträglich fortsetzen (an einem bestimmten Punkt statt am Dateiende)

Über das Tray-Menü "Ablauf fortsetzen ab Punkt..." (nur verfügbar im
Canvas-, Word- oder PowerPoint-Modus, bei gestoppter Aufnahme) öffnet sich eine Liste
aller bereits vorhandenen Knoten/Abschnitte in der zuletzt bearbeiteten
Datei. Die Auswahl legt fest, an welchem Punkt die *nächste*
Aufnahme-Session ansetzt — neue Klicks werden dann (im Canvas als neue
Spalte, in Word als neuer Abschnitt mit Rücksprung-Link) mit genau diesem
Punkt verbunden statt an das Dateiende angehängt, unabhängig davon, wie
lange die ursprüngliche Aufzeichnung schon zurückliegt. Der
Session-Start-Dialog (siehe oben) wählt danach automatisch dieselbe Datei
vor.

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

## Rechtsklick und Enter-Taste als weitere Trigger

Neben Linksklicks kann DocuClick auch bei **Rechtsklicks** auslösen
(Einstellungen → "Auch bei Rechtsklick aufzeichnen", Standard: an) — z. B.
um das Öffnen eines Kontextmenüs zu dokumentieren. Die Beschreibung
unterscheidet "Rechtsklick auf ..." von "Linksklick auf ...".

Zusätzlich kann DocuClick bei jedem Druck der **Enter-Taste** auslösen
(Einstellungen → "Auch bei Enter-Taste aufzeichnen", Standard: an).
Erfasst wird dann das aktive Fenster plus das aktuell fokussierte
UI-Automation-Element (z. B. ein abgeschicktes Formularfeld) statt einer
Klickposition — ohne Bounding-Box wird der Screenshot unmarkiert
gespeichert, es gibt keinen "Blindkreis".

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

### Bilder fehlen in einer bestehenden Notiz/Canvas ("... konnte nicht gefunden werden")

Bis zur entsprechenden Fix-Version enthielt der automatische Namensvorschlag
beim Session-Start ein `#` (z. B. `IT-Support 2026-08-04 #1`). Da dieser Name
auch als Attachments-Unterordner verwendet wird, das `#` in Obsidian-Links
aber als Trenner für Überschriften-/Block-Anker gilt, wurde alles nach dem
`#` als Anker statt als Teil des Pfads interpretiert — die Bild-Referenz
zeigt dann ins Leere, obwohl die Datei tatsächlich am angezeigten Ort liegt.
Ab der Fix-Version wird `(1)` statt `#1` vorgeschlagen und ein manuell
eingegebenes `#` zusätzlich automatisch ersetzt; **bereits erzeugte Dateien
und Ordner mit `#` im Namen bleiben davon unberührt** und müssen händisch
repariert werden:

1. Den betroffenen Attachments-Unterordner (z. B.
   `Attachments/Mein Vault 2026-08-05 #1`) umbenennen — `#` durch z. B. `(1)`
   ersetzen.
2. Die zugehörige `.canvas`- bzw. `.md`-Datei in einem Texteditor öffnen und
   den alten Ordnernamen per Suchen-und-Ersetzen durch den neuen ersetzen.

---

## Für Entwickler

Voraussetzung: .NET 8 SDK (WPF/XAML-Compiler ist Windows-only, lässt sich
also nur unter Windows bauen).

```bash
dotnet build DocuClick.sln
dotnet run --project src/DocuClick/DocuClick.csproj
```

Es wird keine kompilierte `.exe` im Repo mitversioniert — stattdessen baut
[.github/workflows/build.yml](.github/workflows/build.yml) bei jedem Push
nach `main` automatisch auf einem Windows-Runner (Artefakt im jeweiligen
[Actions-Lauf](../../actions)). Bewusst kein Single-File-Publish: Der
Self-Extract-Mechanismus ist bei unsignierten Binaries ein häufiger
Auslöser für Windows-Defender-ML-Fehlalarme.

Neues Release erstellen (baut automatisch und hängt das Zip an ein neues
GitHub Release):

```bash
git tag v1.0.0
git push origin v1.0.0
```

Offene Punkte: Feinschliff bei Multi-Monitor/DPI-Kantenfällen, robustere
Fehlerbehandlung in Randfällen.

### Hinweis zu PowerPointFlowWriter

Der PowerPoint-Writer wurde ohne Zugriff auf echtes PowerPoint entwickelt
(macOS-Entwicklungsumgebung). Zur Absicherung wurde die komplette
OOXML-Struktur (Theme, Slide-Master/-Layout, Shapes, Bilder,
foliensprung-Hyperlinks, nachträgliches Wachstum der Foliengröße über
mehrere Sessions) lokal per `dotnet` + `DocumentFormat.OpenXml`s
`OpenXmlValidator` gegen einen End-to-End-Testlauf (3 Sessions, Branch
setzen/springen/zweimal besuchen, Fortsetzen ab einem früheren Punkt)
geprüft — alle Durchläufe fehlerfrei. Das bestätigt Schema-Validität,
ersetzt aber keinen echten Test in PowerPoint selbst (Layout-Feinheiten,
Hyperlink-Klickverhalten). Bitte beim ersten echten Einsatz kurz prüfen.
