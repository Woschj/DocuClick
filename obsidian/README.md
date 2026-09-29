# DocuClick Diagrams für Obsidian

> Teil des DocuClick-Repos: [Übersicht](../README.md) · Windows-App in
> [windows/](../windows/README.md) · macOS-App in [macos/](../macos/README.md).
> Eigene Version (`manifest.json`), eigener [Changelog](CHANGELOG.md), Releases unter `obsidian-v*`.

Eigenständiger Diagrammeditor ohne Screenshot-Aufnahme und ohne laufende
DocuClick-App. Erste Desktop-Version für Obsidian ab 1.5.

## Installation

Das Plugin ist (noch) nicht im Community-Plugin-Verzeichnis von Obsidian;
es wird von Hand installiert:

1. Von der [Release-Seite](https://github.com/Woschj/DocuClick/releases) das
   neueste Release **`obsidian-v…`** öffnen und `docuclick-diagrams.zip`
   herunterladen.
2. Entpacken und den Ordner `docuclick-diagrams` in den Plugin-Ordner des
   Vaults kopieren: `<Vault>/.obsidian/plugins/docuclick-diagrams/`. Darin
   müssen `main.js`, `manifest.json` und `styles.css` liegen. (`.obsidian`
   ist ein versteckter Ordner: im Finder mit **⌘⇧.** einblenden, im
   Windows-Explorer über **Ansicht → Ausgeblendete Elemente**.)
3. Obsidian neu laden (oder **Einstellungen → Community-Plugins** →
   Aktualisieren). Falls nötig zuerst den **eingeschränkten Modus**
   ausschalten, dann **DocuClick Diagrams** aktivieren.

Update: Ordnerinhalt durch die Dateien des neuen Releases ersetzen und
Obsidian neu laden. Die `.docuclick`-Dateien im Vault bleiben unverändert.

Selbst bauen statt Release: `python3 obsidian/build.py` im Repository
ausführen (nur Python 3 nötig, keine Internetverbindung, Cytoscape kommt aus
den lokalen WebAssets). Ergebnis: `dist/obsidian-docuclick/docuclick-diagrams/`
bzw. die ZIP daneben.

## Benutzung

Alle Funktionen liegen in der schwebenden Werkzeugleiste unten im
Diagramm-Tab.

- Ribbon-Symbol oder Befehl **DocuClick Diagrams: Neues Ablaufdiagramm**:
  Dateinamen und Ordner eingeben (Vorschläge aus dem Vault, fehlende Ordner
  werden angelegt), anschließend über **Element** Formen oder Bilder anlegen.
- Eine `.docuclick`-Datei öffnet sich als Diagramm-Tab.
- Knoten ziehen; Verbindungspunkte ziehen, um Knoten zu verbinden.
- Rechtsklick auf Knoten, Verbindung oder Hintergrund öffnet die Bearbeitung.
- **Zurück / Wiederholen**, `Strg+Z` / `Strg+Umschalt+Z` (Mac: `⌘`) machen Änderungen
  rückgängig bzw. wiederholen sie. Texteingaben behalten ihre eigene Historie.
- Änderungen werden automatisch über die Obsidian-Vault-API gespeichert.
  `Strg+S` / `⌘S` wiederholt einen fehlgeschlagenen Speicherversuch.
- **Anleitung** zeigt die vorhandene Schritt-für-Schritt-Ansicht.
- **HTML-Ansicht** in der Diagramm-Werkzeugleiste erstellt eine eigenständige
  Datei `… – Ansicht.html` neben dem Diagramm. Sie enthält Suche, Zoom, Bildansicht
  und Anleitung, aber keine Bearbeitungs- oder Speicherfunktionen. Empfänger
  benötigen nur einen Browser, weder Obsidian noch DocuClick.
- Befehl **DocuClick-HTML importieren** liest eine lokale HTML-Datei. Für HTML
  im Vault gibt es zusätzlich einen Eintrag im Datei-Kontextmenü.

Import und Export überschreiben keine vorhandenen Dateien. Gleichnamige Dateien
erhalten eine laufende Nummer. Importierte HTML-Skripte werden nicht ausgeführt;
nur die eingebetteten JSON-Daten und Rasterbilder werden übernommen.

### Mit DocuClick für Windows/macOS direkt in den Vault aufnehmen

Das Plugin ist der Ort, an dem Abläufe liegen und bearbeitet werden; die
Apps [DocuClick für Windows](../windows/README.md) und
[für macOS](../macos/README.md) liefern die Aufnahme dazu:

1. In der App eine neue Session starten und als **Speicherort einen Ordner
   im Vault** wählen. Die App erkennt den Vault (am Ordner `.obsidian`) und
   legt statt einer `.html`-Datei ein **`.docuclick`-Diagramm** an; der
   Hinweis im Dialog zeigt das an.
2. Das Diagramm in Obsidian öffnen, gern schon während der Aufnahme: Jeder
   Klick erscheint nach kurzer Zeit im geöffneten Tab (die Ansicht lädt die
   Datei neu, sobald die App sie ändert).
3. In Obsidian bearbeiten (umbenennen, verschieben, verbinden …). Die App
   übernimmt diese Änderungen vor dem nächsten Klick; sie werden nicht
   überschrieben. Zum Weiteraufnehmen in der App „Bestehende Datei
   fortsetzen“ und die `.docuclick`-Datei wählen.

Screenshots legt die App als PNG-Dateien in `Attachments/<Ablaufname>/`
neben dem Diagramm ab; das Diagramm verweist darauf (`images`) und bleibt
klein. Diese Bilder bleiben auch bei der Einstellung „In der Datei“ Dateien.
Zum Weitergeben dient wie immer die **HTML-Ansicht**.

Gleichzeitig in App und Obsidian dieselbe Stelle ändern sollte man
vermeiden: Schreiben beide im selben Moment, sichert das Plugin seine
Fassung als `… – lokale Änderungen.docuclick` (siehe unten).

### Ältere DocuClick-Aufnahmen übernehmen

Abläufe, die außerhalb eines Vaults als `.html` aufgenommen wurden, lassen
sich importieren: HTML-Datei in den Vault legen → Rechtsklick → **Als
DocuClick-Diagramm importieren** (oder Befehl **DocuClick-HTML importieren**
für eine Datei außerhalb des Vaults). Es entsteht eine neue `.docuclick`-
Datei mit allen Schritten und Screenshots; die HTML-Datei bleibt unverändert.

### Speicherort neuer Abläufe

**Einstellungen → Community-Plugins → DocuClick Diagrams → Neue Abläufe**:

- **Standardordner**: Ziel für neue Abläufe und für den Befehl „DocuClick-HTML
  importieren“, z. B. `Prozesse`. Leer = Ordner der gerade geöffneten Datei
  (ohne geöffnete Datei der Vault-Hauptordner).
- **Ordner beim Anlegen abfragen** (Standard: an): Der Dialog zeigt ein
  Ordnerfeld, vorausgefüllt mit dem Standardordner. Aus: Abläufe landen ohne
  Nachfrage im Standardordner.

Importe per Rechtsklick auf eine HTML-Datei im Vault landen immer neben dieser
Datei.

### Screenshots speichern

Einstellung „Screenshots speichern“:

- **In der Datei** (Standard): Screenshots stecken einmal als Data-URI in der
  `.docuclick`-Datei. Eine Datei lässt sich einfach teilen.
- **Als Dateien im Vault**: Screenshots liegen als eigene Dateien im
  **Bildordner** (Dateiname = Prüfsumme, gleiche Bilder nur einmal). Die
  Diagrammdatei bleibt klein, Speichern und Sync werden schneller. Beim Öffnen
  werden die Bilder wieder eingelesen; der HTML-Export bettet sie ein. Nicht mehr
  verwendete Bilddateien werden nicht automatisch gelöscht. Ältere Plugin-Versionen
  (vor 0.5.0) zeigen solche Diagramme ohne Bilder.

Die Einstellung gilt für neu gespeicherte Diagramme; bestehende Dateien werden
erst bei der nächsten Änderung umgestellt.

### Farben an das eigene Theme anpassen

![Diagrammeditor mit hellem Farbschema](../docs/screenshots/obsidian-hell.png)
*Beispiel-Ablauf mit hellem Hintergrund und violetter Detailfarbe.*

**Einstellungen → Community-Plugins → DocuClick Diagrams** (Zahnrad):

- **Farbschema**
  - *DocuClick (dunkel)*: Standard-Aussehen der DocuClick-Apps.
  - *Obsidian-Theme übernehmen*: Hintergrund und Akzentfarbe kommen aus dem
    aktiven Obsidian-Theme und wechseln automatisch mit (Hell/Dunkel, anderes
    Theme, andere Akzentfarbe).
  - *Eigene Farben*: **Hintergrundfarbe** (Fläche hinter dem Diagramm) und
    **Detailfarbe** (Schaltflächen, Auswahl, Markierungen, Nummern) frei
    wählen. „Übernehmen“ setzt beide einmalig auf die Theme-Farben als
    Ausgangspunkt.
- **Farben auch für die HTML-Ansicht**: exportierte Ansichten sehen genauso
  aus (aus: immer DocuClick-Standard).
- **Auf Standard zurücksetzen**.

Leisten, Menüs, Dialoge und Schrift werden aus den zwei Farben abgeleitet;
bei hellem Hintergrund wird die Schrift automatisch dunkel. Die Farben der
einzelnen Schritte und Pfade gehören zum Diagramm und bleiben erhalten.
Änderungen wirken sofort in allen offenen Diagramm-Tabs.

## Speicherung und Konflikte

`.docuclick` ist JSON mit `format: "docuclick-diagram"`, `version: 1`, dem
Canvas-Dokument und den Darstellungsdaten (`flow`). Die Darstellungsdaten sichern
insbesondere die bereits eingebetteten Screenshots aus bestehenden HTML-Dateien.
Bilder liegen standardmäßig einmal als Data-URI in der Datei; optional können
sie als Vault-Dateien ausgelagert werden (dann steht in `images` je Schritt der
Pfad relativ zum Vault, siehe „Screenshots speichern“). Die DocuClick-Apps
schreiben dasselbe Format (`core/DocuClick.Core/Services/DocuClickDiagramIo.cs`);
`tests/fixtures/app-recording.docuclick` ist eine echte App-Aufnahme, die beide
Testsuiten prüfen. Grenze: 64 MB pro Datei, maximal 10.000
sichtbare Knoten.

Jeder Schreibvorgang vergleicht innerhalb von `Vault.process()` den bisherigen
Dateistand. Wurde die Datei in einem anderen Tab oder extern geändert, bleibt
das Original erhalten; lokale Änderungen werden in einer neuen Datei
`… – lokale Änderungen.docuclick` gesichert. Die Ansicht zeigt den Konflikt an.
Beide Fassungen können dann verglichen werden. Es gibt kein automatisches
Zusammenführen. Ändert ein anderes Programm (Sync, Git) die Datei, während lokal
nichts Ungesichertes vorliegt, lädt die Ansicht sie automatisch neu. Sonst zeigt
ein Hinweis den Konflikt an; „Neu laden“ übernimmt den Dateistand, die lokale
Sicherung bleibt erhalten.

Undo/Redo gilt pro geöffnetem Tab und bleibt nicht über Neustarts erhalten.
Die Historie ist auf 60 Zustände bzw. ungefähr 32 MB begrenzt (mindestens zwei
Zustände). Sehr große Einzelbilder können diese Speichergrenze überschreiten.

## Architektur

- `src/document.js`: Formatprüfung, sicherer HTML-Import, HTML-Erzeugung.
- `src/main.js`: Obsidian-Ansicht, Dateiverwaltung und Speicherbrücke.
- `../core/DocuClick.Core/WebAssets/viewer.template.html`: gemeinsame
  HTML-Editorvorlage für DocuClick-Ausgaben und dieses Plugin.
- `build.py`: erzeugt ein installierbares Plugin und ZIP ohne npm-Schritt.

Der Editor läuft pro Tab in einem Sandbox-Iframe ohne `allow-same-origin`.
Eine Content Security Policy blockiert Netzwerkzugriffe. Nachrichten sind an
das konkrete Frame-Fenster und einen zufälligen Kanal gebunden; der Editor kann
keinen Zielpfad angeben. Die Host-Seite validiert jedes gespeicherte Dokument.

Noch nicht enthalten: native Obsidian-Notizlinks/Backlinks, Einbettung in Markdown,
Mobile-/Touch-Anpassung, draw.io-Export im Plugin und separate Bildanhänge.
Die Desktop-App verwendet für ihre eigene Ablaufübersicht weiterhin `flow.js`; deren vollständige Zusammenführung ist ein weiterer Umbau.

## Tests und Release

```sh
node --test obsidian/tests/*.test.js
python3 obsidian/build.py
node obsidian/tests/browser.cjs /path/to/chromium
dotnet test --project core/DocuClick.Core.Tests --configuration Release
```

Browser-Tests verwenden ein temporäres Profil und einen simulierten Obsidian-Host.
Eine manuelle Prüfung in einem echten Obsidian-Vault bleibt vor einem Release
notwendig. Das Plugin ist eine erste testbare Version, kein Community-Store-Release.

API-Grundlage: [Obsidian API](https://github.com/obsidianmd/obsidian-api),
[Vault API](https://github.com/obsidianmd/obsidian-developer-docs/blob/main/en/Plugins/Vault.md).
Cytoscapes MIT-Lizenz liegt im gebauten Paket als `THIRD-PARTY-NOTICES.txt` bei.

Release: Version in `manifest.json` und [CHANGELOG.md](CHANGELOG.md) anheben,
dann `git tag obsidian-v0.1.1 && git push origin obsidian-v0.1.1`. Die CI
([.github/workflows/obsidian.yml](../.github/workflows/obsidian.yml)) hängt
ZIP sowie `main.js`, `manifest.json` und `styles.css` an das Release.
