# Changelog – DocuClick für macOS

Alle nennenswerten Änderungen an der macOS-App. Änderungen am gemeinsamen
Kern stehen im [Windows-Changelog](../windows/CHANGELOG.md).

Das Format basiert auf [Keep a Changelog](https://keepachangelog.com/de/1.0.0/)
und dieses Projekt hält sich an [Semantic Versioning](https://semver.org/lang/de/).

---

## [Unreleased] – 0.1.0

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
