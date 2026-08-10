# DocuClick

Windows-Screenshot-Tool, das bei jedem Mausklick (optional auch bei
Rechtsklick und Enter) automatisch einen Screenshot mit Klick-Markierung
erstellt und samt Beschreibungstext in eine Obsidian-Notiz oder ein
Obsidian-Canvas einfügt. Aus einer Canvas-Session lässt sich jederzeit
zusätzlich ein editierbares draw.io-Flowchart exportieren. Details zu
beiden Aufnahme-Modi und dem draw.io-Export weiter unten.

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

Für beide Aufnahme-Modi (Notiz und Canvas) wird [Obsidian](https://obsidian.md)
empfohlen — kostenlos, kein Account nötig, öffnet einfach einen lokalen
Ordner als "Vault". Kein Plugin erforderlich: DocuClick schreibt reine
Markdown- bzw. JSON-Dateien direkt auf die Festplatte.

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
  Session-Start](#start-vs-neue-session-zieldatei)) — damit landet
  jede Aufnahme direkt dort, wo sie in der Vault-Struktur hingehört,
  statt alles im Wurzelordner zu sammeln.
- Für länger geplante Abläufe lohnt es sich, vorher eine Vorlage aus
  `02 Vorlagen/` zu kopieren und mit Titel/Zweck auszufüllen, dann beim
  Session-Start "Bestehende Datei fortsetzen" wählen.
- Verzweigt sich ein Ablauf (z. B. Fehlerfall vs. Erfolgsfall), im
  Canvas-Modus über die Ablauf-Übersicht einen Abzweigungspunkt setzen
  und benannte Pfade anlegen (siehe [Abzweigungen im
  Canvas-Modus](#abzweigungen-im-canvas-modus)).

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
   Einstellungen) inkl. Fallback auf Fenstertitel + Zeitstempel. Erkennt UI
   Automation dabei ein Passwortfeld, wird die Erfassung komplett
   übersprungen (kein Screenshot, kein Eintrag) — der einzige Fall, den die
   App automatisch erkennen kann; für alles andere Sensible gibt es die
   manuelle Skip-Taste (siehe [Klicks überspringen](#klicks-überspringen)).
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
   die Session-Notiz im Obsidian-Vault (im Canvas-Modus stattdessen als
   verbundener Knoten, siehe unten)

Klicks auf DocuClicks eigene Fenster (Top-Leiste, Ablauf-Übersicht,
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
  Vault-Pfad) — Vorschläge kommen aus allen bereits vorhandenen
  Unterordnern, der Namensvorschlag passt sich beim Ordnerwechsel
  automatisch an, solange der Name nicht von Hand geändert wurde. So
  landen Aufnahmen direkt in der Vault-Struktur (z. B.
  `Prozesse/IT-Support`) statt immer im Wurzelordner, und die laufende
  Nummer verhindert, dass ein zweiter Klick auf "Neue Session" am selben
  Tag versehentlich eine bestehende Datei fortsetzt.
- **Bestehende Datei fortsetzen**: Auswahl aus allen vorhandenen Dateien
  mit passender Endung im konfigurierten Vault (inkl. Unterordner),
  neueste zuerst. Neue Klicks werden an diese Datei angehängt (im
  Canvas-Modus ab dem bisherigen Cursor-Stand, siehe Abzweigungs-Logik
  unten).

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

### Top-Leiste, Ablauf-Übersicht und "Neue Session"

Eine kleine, mittig oben schwebende Pille (wie die TeamViewer-Session-Leiste
— nicht bildschirmbreit, sonst würde sie Fenster ziehen/Menüs/Snap-Zonen
blockieren) ist sichtbar, solange die App läuft, und zeigt auf einen Blick
den Aufnahmestatus. Frei verschiebbar per Ziehen an der Kopfzeile. Sie
enthält vier Bereiche:

- **Start/Stop**: entspricht dem Tray-Menüpunkt bzw. dem Start/Stop-Hotkey
  — setzt die zuletzt verwendete Datei ohne Rückfrage fort.
- **Übersicht**: öffnet die Ablauf-Übersicht wieder, falls sie über ihr
  eigenes ✕ geschlossen wurde (siehe unten).
- **Neue Session**: immer klickbar, fragt **immer** nach der Zieldatei
  (anders als Start). Läuft gerade keine Aufnahme, startet sie damit neu.
  Läuft eine Aufnahme, schließt es die aktuelle Datei ab und startet
  direkt danach die neue (siehe vorheriger Abschnitt).
- **Zoom: Aus/An** plus Radius-Schieberegler: schaltet "Zoom-auf-Cursor" um
  (entspricht dem gleichnamigen Hotkey, siehe unten) — bei "An" erfassen
  die nächsten Screenshots nur den Bereich um den Mauszeiger statt des
  ganzen Fensters, direkt hier pro Screenshot umschaltbar statt nur global
  über die Einstellungen; der Regler passt die Größe dieses Bereichs live
  an (mit Vorschau-Rahmen um den Cursor).

Im **Canvas-Modus** öffnet sich zusätzlich automatisch die
**Ablauf-Übersicht** — ein frei verschiebbares, größenveränderliches
Panel mit einer Miniaturkarte des gesamten Ablaufs (per Maus frei
zoom-/schwenkbar): der aktuelle Knoten ist rot hervorgehoben, jeder Pfad
bekommt seine eigene Farbe aus einer festen Palette. Darüber lässt sich
der Ablauf direkt bearbeiten:

- **Rechtsklick auf einen Knoten**: Kontextmenü mit "→ Weiter" (Aufnahme
  dorthin springen), "+ Neuer Pfad ab hier" (neuen benannten Pfad
  abzweigen), allen bereits vorhandenen Pfaden ab diesem Punkt,
  "Umbenennen" und "Löschen" (löscht bei mehreren abzweigenden Pfaden
  nach Rückfrage den gesamten nachfolgenden Ast).
- **Doppelklick auf einen Knoten**: direkt umbenennen.
- **Ziehen von einem Knoten auf einen anderen**: manuelle Querverbindung
  ("Verbinden") — für Rücksprünge/Referenzen, die der lineare Ablauf sonst
  nicht abbilden kann. Rein additiv (verändert nichts an der bestehenden
  Struktur), per Rechtsklick auf die Verbindungslinie wieder entfernbar.
- **Umschalt+Ziehen** wählt mehrere Knoten per Rahmen aus, **Entf** löscht
  die Auswahl gesammelt.

Ein Klick auf einen Knoten in der Ablauf-Übersicht bei **gestoppter**
Aufnahme markiert diesen Knoten stattdessen als Ansatzpunkt für die
*nächste* Session (siehe [Ablauf nachträglich
fortsetzen](#ablauf-nachträglich-fortsetzen-an-einem-bestimmten-punkt-statt-am-dateiende)).

Anders als die Ablauf-Übersicht ist die Top-Leiste **nicht**
klick-durchlässig, da sie echte Buttons hostet — deshalb ist sie bewusst
content-groß statt bildschirmbreit. Klicks auf die Top-Leiste, die
Ablauf-Übersicht oder auf das Tray-Icon selbst werden nie als Aufnahme
gewertet (kein Screenshot, kein Eintrag) — die App erkennt und filtert
das automatisch, und beide Fenster werden aktiv aus Screenshots
ausgeschlossen, tauchen also nie selbst im aufgenommenen Bild auf.

## Ausgabeformat: Notiz oder Canvas, draw.io als Export

In den Einstellungen lässt sich eines von zwei Live-Aufnahmeformaten
wählen:

- **Notiz**: linearer Markdown-Text + Bild-Link, an eine `.md`-Datei
  angehängt (Standard).
- **Obsidian-Canvas**: jeder Klick wird ein verbundener Knoten auf einer
  Fläche in einer `.canvas`-Datei (reines JSON, kein Obsidian-Plugin
  nötig) — der einzige Modus, der Abzweigungen/Pfade unterstützt (siehe
  [Abzweigungen im Canvas-Modus](#abzweigungen-im-canvas-modus)).
  Screenshots werden als eigener Datei-Node (Canvas' natives
  Embed-Format) statt als `![[wikilink]]` im Text abgelegt —
  Drittanbieter-Exporttools für Canvas kennen diesen Node-Typ meist,
  Obsidians Wikilink-Auflösung dagegen nicht, weshalb Bilder beim Export
  in andere Formate sonst fehlten.

Aus einer bestehenden `.canvas`-Datei lässt sich jederzeit ein
**draw.io-Flowchart** exportieren — über das Tray-Menü "Nach draw.io
exportieren...", nicht als eigener Aufnahme-Modus (frühere Versionen
schrieben draw.io live mit; das wurde durch diesen Ein-Schritt-Export
ersetzt, da das Neuschreiben der kompletten XML-Datei bei jedem einzelnen
Klick mit wachsender Sitzungslänge spürbar langsamer wurde). Der Export
baut ein echtes Flussdiagramm: jeder Klick wird eine "Karte" (abgerundeter
Rahmen mit Schatten, nummeriertes Badge, Beschriftung und Screenshot als
eine zusammen verschiebbare Einheit), jeder Pfad bekommt eine eigene
Akzentfarbe (Rahmen, Nummer-Badge und Pfeile), Abzweigungspunkte werden
als Raute markiert, manuelle Querverbindungen als graue Linie. Screenshots
werden direkt als Base64 eingebettet (kein separater Attachments-Ordner
nötig) und im Kartenlayout klein dargestellt — **einfach mit der Maus über
den Screenshot fahren**, um ihn sofort deutlich größer als Vorschau
angezeigt zu bekommen (kein Klick nötig); für die volle
Original-Auflösung zusätzlich auf das kleine Link-Symbol klicken, das
draw.io beim Überfahren am Rand der Karte einblendet (öffnet in einem
neuen Tab). Öffnet in der kostenlosen
[draw.io-/diagrams.net-App](https://www.drawio.com/) (Desktop, Web oder
VS-Code-Extension) — kein Obsidian nötig, voll editierbar (Karten frei
verschiebbar, Farben/Text anpassbar), lässt sich von dort aus auch nach
Visio (`.vsdx`) exportieren.

### Abzweigungen im Canvas-Modus

Ein Ablauf verzweigt sich in der Realität oft (z. B. Fehlerfall vs.
Erfolgsfall) — im Canvas-Modus lässt sich das direkt abbilden:

- **Abzweigungspunkt setzen** (Hotkey, Standard `F9`): fragt sofort nach
  dem Namen des ersten Pfads (z. B. "Login-Fehler") und legt eine kleine,
  sichtbare **"◆ Abzweigung"**-Raute an, verbunden mit dem zuletzt
  aufgezeichneten Knoten, plus direkt den ersten benannten Pfad als eigene
  Spalte — der nächste Klick knüpft dort an. Es gibt bewusst keine
  unbenannte "einfach weiter"-Fortsetzung: jeder von einer Abzweigung
  ausgehende Pfad ist von Anfang an ein echtes, benanntes, in der
  Ablauf-Übersicht auswählbares Objekt.
- **Weitere Pfade**: über die Ablauf-Übersicht (Rechtsklick auf die Raute
  oder einen beliebigen anderen Knoten → "+ Neuer Pfad ab hier") lassen
  sich jederzeit zusätzliche benannte Pfade abzweigen — nicht nur von
  einer Abzweigungs-Raute aus, sondern von jedem beliebigen bereits
  aufgezeichneten Knoten.
- **Einen Pfad fortsetzen**: Rechtsklick auf den Ursprungsknoten in der
  Ablauf-Übersicht zeigt alle davon abzweigenden Pfade zur Auswahl — die
  Aufnahme knüpft dann genau dort an, wo dieser Pfad zuletzt endete, egal
  wie viele andere Klicks zwischenzeitlich aufgezeichnet wurden.

Da Pfade und Abzweigungspunkte als echte, sichtbare Knoten in der Datei
stehen, übersteht die Struktur auch ein Stoppen und erneutes Starten der
Aufnahme (auf derselben Datei) — DocuClick liest sie beim nächsten Start
einfach wieder aus der Datei ein, ohne dass ein separater Speicherzustand
nötig wäre.

Änderungen an den Hotkeys gelten sofort nach "Speichern" in den
Einstellungen (kein Neustart nötig).

### Ablauf nachträglich fortsetzen (an einem bestimmten Punkt statt am Dateiende)

Bei **gestoppter** Aufnahme im Canvas-Modus zeigt die Ablauf-Übersicht
weiterhin die zuletzt bearbeitete Datei — ein Klick auf einen beliebigen
Knoten dort markiert ihn als Ansatzpunkt für die *nächste* Aufnahme-Session
(Balloon-Tip bestätigt die Auswahl). Neue Klicks werden dann als neue
Spalte mit genau diesem Punkt verbunden statt an das Dateiende angehängt,
unabhängig davon, wie lange die ursprüngliche Aufzeichnung schon
zurückliegt. Der nächste Session-Start-Dialog wählt danach automatisch
dieselbe Datei vor.

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
- Klick übersprungen (Modifier-Taste gedrückt oder Passwortfeld erkannt) →
  dezenter Windows-Systemton
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
die Aufnahme ab. Betrifft Start/Stop, "Abzweigungspunkt setzen" und
"Zoom-auf-Cursor umschalten".

## Klicks überspringen

In den Einstellungen lässt sich eine Modifier-Taste (Umschalt/Strg/Alt)
festlegen: Ist sie bei einem Linksklick gedrückt, wird dieser Klick
komplett ignoriert (kein Screenshot, kein Notiz-Eintrag). Nützlich, um
z. B. sensible Inhalte gezielt aus der Aufzeichnung auszuschließen.
Erkennt UI Automation den Klick dagegen selbst als Passwortfeld, wird er
automatisch übersprungen, ganz ohne gedrückte Taste (siehe
[Funktionsumfang](#funktionsumfang)) — deckt aber nur echte, bei UI
Automation als solche registrierte Passwortfelder ab, keine
selbstgebauten "versteckten" Eingabefelder.

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
git tag v1.10.0
git push origin v1.10.0
```

Offene Punkte: Feinschliff bei Multi-Monitor/DPI-Kantenfällen, robustere
Fehlerbehandlung in Randfällen.
