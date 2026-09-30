# Changelog – DocuClick für macOS

Alle nennenswerten Änderungen an der macOS-App. Änderungen am gemeinsamen
Kern stehen im [Windows-Changelog](../windows/CHANGELOG.md).

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [Unreleased]

- **Die neue Ablauf-Übersicht ist jetzt die einzige**: der Schalter „Neue
  Ablauf-Übersicht testen“ entfällt, die alte Übersicht ist entfernt. Im
  Rechtsklick-Menü gibt es „Neuer Pfad ab hier …“ jetzt an jedem Schritt
  (wie in der alten Übersicht) und an einer Abzweigung „Pfad „…“
  fortsetzen“ für jeden Pfad.

## [1.18.0] - 2026-09-30

- **Ein Release für alles**: Windows-App, macOS-App und Obsidian-Plugin
  erscheinen ab jetzt zusammen in einem Release mit derselben Versionsnummer
  (Tag `v…`).

## [0.4.0] - 2026-09-30

- Testschalter „Neue Ablauf-Übersicht testen“ (Einstellungen): Die
  Ablauf-Übersicht nutzt denselben Editor wie die .html-Datei und das
  Obsidian-Plugin – mit Rückgängig/Wiederholen, Schwärzen und deutlich
  markiertem aktuellem Schritt. Seite und Screenshots liefert der lokale
  Dienst der App (nur 127.0.0.1, mit Zufallsschlüssel pro Start).
- Schwärzen mit Vorschau, nachträglich änderbar, auch Weichzeichnen (wie
  Windows, siehe [Windows-Changelog](../windows/CHANGELOG.md)).

## [0.3.0] - 2026-09-30

- Wie Windows (siehe [Windows-Changelog](../windows/CHANGELOG.md)): WebP-
  Screenshots, deutlich schnellere lange Aufnahmen, Steuerung aus Obsidian,
  Schwärzen und Drucken, abschaltbare Plugin-Installation. Startet Obsidian
  eine Aufnahme ohne erteilte Berechtigungen, öffnet sich der
  Berechtigungs-Assistent.

## [0.2.0] - 2026-09-29

- **Aufnehmen in einen Obsidian-Vault**: In einem Vault legt DocuClick eine
  Diagramm-Notiz (`.md`) für das Plugin „DocuClick Diagrams“ an; Details im
  [Windows-Changelog](../windows/CHANGELOG.md). Das Plugin wird dabei automatisch im Vault
  installiert bzw. aktualisiert. Die Dateiauswahl zeigt
  Diagramm-Notizen und `.docuclick`-Dateien.

## [0.1.0] - 2026-09-28

Erste macOS-Version, Funktionsstand DocuClick für Windows 1.13.0.

- Menüleisten-App (Avalonia) mit Top-Leiste, Session-Start, Einstellungen,
  Berechtigungs-Assistent und draw.io-Export.
- Ablauf-Übersicht: derselbe Web-Editor wie unter Windows, als
  durchscheinendes, rahmenloses Fenster; an allen Rändern und über den
  Griff unten rechts in der Größe veränderbar. Klicks auf die Übersicht und
  auf DocuClicks eigene Fenster werden nie aufgenommen.
- Screenshots per ScreenCaptureKit mit dem Zustand vor dem Klick, ohne
  DocuClicks eigene Fenster; Retina-Verkleinerung.
- Beschreibungstexte über die Bedienungshilfen-Schnittstelle; Klicks in
  Passwortfelder werden übersprungen.
- Speichern aus dem Browser direkt in die Ablauf-Datei, solange DocuClick läuft.
- Ohne kostenpflichtiges Apple-Developer-Konto: selbstsignierte Signatur,
  Berechtigungen bleiben über Updates erhalten.
