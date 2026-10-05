# CloudDrive-Sync

Hält Ordner auf deinem Windows-PC mit **Nextcloud**, **IServ** und anderen **WebDAV**-Speichern aktuell – in beide
Richtungen, mit einer Oberfläche im Stil von Windows 11.

> **Status:** Vorabversion 0.1. Der Kern ist mit Unit- und Integrationstests abgesichert; die Oberfläche ist neu.
> Probiere es zuerst mit einem kleinen Ordner aus.

## Was es kann

- **Konten verbinden:** Nextcloud (Anmeldung im Browser, auch mit Zwei-Faktor-Anmeldung, oder mit App-Passwort),
  IServ (Adresse der Schule genügt, CloudDrive-Sync nutzt `webdav.<schule>`) und andere WebDAV-Speicher.
- **Ordner synchronisieren:** einen Cloud-Ordner oder das ganze Konto – alles oder nur ausgewählte Ordner und Dateien –
  in einen frei wählbaren Ordner auf diesem PC. Nichts ist verboten: Bei ungewöhnlichen Speicherorten (Netzlaufwerk,
  USB-Stick, Ordner eines anderen Sync-Programms …) gibt es Hinweise, die Entscheidung triffst du.
- **In beide Richtungen:** Änderungen am PC gehen kurz nach dem Speichern hoch, Änderungen in der Cloud kommen im
  gewählten Abstand (Standard: alle 5 Minuten). Technisch arbeitet [rclone](https://rclone.org) `bisync`.
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

## Voraussetzungen

- Windows 10 oder 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
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

[MIT](LICENSE)
