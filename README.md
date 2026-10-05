# CloudDrive-Sync

Hält Ordner auf deinem Windows-PC mit **Nextcloud**, **IServ** und anderen **WebDAV**-Speichern aktuell – in beide
Richtungen, mit einer Oberfläche im Stil von Windows 11.

> **Status:** Vorabversion. Der Kern ist mit Unit- und Integrationstests abgesichert, darunter alle üblichen
> Dateivorgänge gegen einen IServ-ähnlichen Testserver. Probiere es zuerst mit einem kleinen Ordner aus.

## Was es kann

- **Konten verbinden:** Nextcloud (Anmeldung im Browser, auch mit Zwei-Faktor-Anmeldung, oder mit App-Passwort),
  IServ (Adresse der Schule genügt, CloudDrive-Sync nutzt `webdav.<schule>`) und andere WebDAV-Speicher.
- **Ordner synchronisieren:** einen Cloud-Ordner, bei IServ auch alle Gruppen auf einmal, oder das ganze Konto – alles oder nur ausgewählte Ordner und Dateien –
  in einen frei wählbaren Ordner auf diesem PC. Nichts ist verboten: Bei ungewöhnlichen Speicherorten (Netzlaufwerk,
  USB-Stick, Ordner eines anderen Sync-Programms …) gibt es Hinweise, die Entscheidung triffst du.
- **In beide Richtungen:** Änderungen am PC gehen kurz nach dem Speichern hoch, Änderungen in der Cloud kommen im
  gewählten Abstand (Standard: alle 5 Minuten). Technisch arbeitet [rclone](https://rclone.org) `bisync`.
- **Nachvollziehbar:** Unter „Aktivität“ steht zu jedem Abgleich, welche Dateien hoch- oder heruntergeladen und wo
  gelöscht wurden. „Abgleich überprüfen“ vergleicht auf Knopfdruck jede Datei am PC mit der in der Cloud.
- **Nichts geht verloren:**
  - Löschschutz: Verschwinden auf einmal viele Dateien, hält CloudDrive-Sync an und fragt – „Dateien
    wiederherstellen“ oder „Löschungen übernehmen“.
  - Wächterdatei auf beiden Seiten: Ist ein Ordner weg oder verschoben (z. B. USB-Stick abgezogen), wird nichts
    gelöscht.
  - Konflikte: Wurde eine Datei auf beiden Seiten geändert, bleiben beide Fassungen erhalten.
  - Papierkorb: Was der Abgleich am PC löscht oder ersetzt, landet im versteckten Ordner `.clouddrive-papierkorb`.
    Was du selbst am PC löschst, liegt im Papierkorb von Windows; Nextcloud hat zusätzlich einen eigenen Papierkorb.
- **IServ-Besonderheit:** IServ speichert keine eigenen Änderungszeiten. CloudDrive-Sync merkt sich die Zeiten des
  Servers selbst und erkennt so auch Änderungen, die die Dateigröße nicht verändern.
- **Im Hintergrund:** Symbol im Infobereich mit Statuspunkt, Windows-Benachrichtigungen bei Konflikten und
  Entscheidungen, Start mit Windows.

## Sicherheit

- Zugangsdaten gibst du nur in CloudDrive-Sync ein. Sie liegen ausschließlich verschlüsselt auf deinem PC: in der
  verschlüsselten rclone-Konfiguration; deren Schlüssel steckt in der Windows-Anmeldeinformationsverwaltung.
- Bei Nextcloud erzeugt die Anmeldung im Browser ein eigenes App-Passwort, das du in Nextcloud jederzeit widerrufen
  kannst. Dein eigentliches Passwort sieht CloudDrive-Sync dabei nicht.
- Unverschlüsselte Verbindungen (`http://`) werden nur nach ausdrücklicher Bestätigung genutzt.
- Laufzeitdaten liegen nie in diesem Repository (`.gitignore` arbeitet mit einer Positivliste).

## Zusammenspiel mit CloudDrives

[CloudDrives](https://github.com/suebi76/CloudDrives) bindet Cloud-Speicher als **Laufwerke** ein (ohne lokale Kopie);
CloudDrive-Sync hält **lokale Ordner** aktuell. Beide Programme arbeiten unabhängig voneinander. Sind beide
installiert, öffnet jedes das andere über sein Symbol im Infobereich.

## Installation

1. Lade **CloudDrive-Sync-Setup.exe** von der [Release-Seite](https://github.com/suebi76/CloudDrive-Sync/releases)
   herunter.
2. Starte die Datei mit einem Doppelklick. Administratorrechte sind nicht nötig: CloudDrive-Sync wird nur für dein
   Windows-Konto installiert (nach `%LOCALAPPDATA%\CloudDriveSync`) und bekommt Einträge im Startmenü und auf dem
   Desktop. .NET ist enthalten.
3. Zeigt Windows „Der Computer wurde durch Windows geschützt“, klicke auf **Weitere Informationen** und dann auf
   **Trotzdem ausführen**. Die Meldung kommt, weil das Installationsprogramm noch nicht digital signiert ist.

**Updates:** Unter *Einstellungen › Updates* wählst du, wie neue Versionen ankommen – als Hinweis mit Installation per
Klick (Standard), automatisch in einem ruhigen Moment oder nur, wenn du nachsiehst. Mit „Testversionen erhalten“
bekommst du Vorabversionen zum Ausprobieren. Updates kommen nur aus diesem GitHub-Projekt; jedes Paket wird vor der
Installation anhand seiner Prüfsumme kontrolliert. Installation und Updates erledigt
[Velopack](https://velopack.io) (MIT-Lizenz), das Teil des Programms ist.

**Deinstallieren:** *Windows-Einstellungen › Apps › Installierte Apps › CloudDrive-Sync*. Einstellungen und Anmeldungen
(`%LOCALAPPDATA%\CloudDrive-Sync`) sowie deine synchronisierten Ordner bleiben erhalten.

## Voraussetzungen

- Windows 10 oder 11, 64 Bit
- rclone 1.75.1 lädt CloudDrive-Sync beim ersten Start selbst herunter (Prüfsumme SHA256 fest hinterlegt).

## Entwickeln

```powershell
dotnet build CloudDriveSync.slnx
dotnet test tests/CloudDriveSync.Core.Tests                # Unit-Tests
dotnet test tests/CloudDriveSync.Core.IntegrationTests     # echtes rclone gegen einen lokalen WebDAV-Server
```

- Daten: `%LOCALAPPDATA%\CloudDrive-Sync` – ein anderer Ordner über `CLOUDDRIVE_SYNC_HOME` (Tests, portable Kopie).
- Build-Ausgabe: `%USERPROFILE%\.clouddrive-sync-build` (außerhalb des Quellordners).
- Oberfläche prüfen ohne Bildschirmfotos: `CLOUDDRIVE_SYNC_SNAPSHOTS=<Ordner>` speichert Bilder der eigenen Fenster.

## Lizenz

© 2026 Steffen Schwabe · [Projekt auf GitHub](https://github.com/suebi76/CloudDrive-Sync)

- **Quellcode:** [MIT-Lizenz](LICENSE). Du darfst ihn frei nutzen, verändern und weitergeben, solange der
  Copyright-Hinweis und die Lizenz erhalten bleiben.
- **Dokumentation und Grafiken** (Texte in diesem Repository außerhalb des Quellcodes, das Programmsymbol):
  [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/deed.de). Bei Weitergabe nennst du „Steffen Schwabe,
  CloudDrive-Sync“ und verlinkst die Lizenz.
- **Verwendete Bausteine** anderer Projekte und ihre Lizenzen: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
