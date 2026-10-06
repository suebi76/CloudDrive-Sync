# CloudDrive-Sync

Hält Ordner auf deinem Windows-PC mit **Nextcloud**, **IServ** und anderen **WebDAV**-Speichern aktuell – in beide
Richtungen, mit einer Oberfläche im Stil von Windows 11.

> **Status:** Version 0.2; Version 0.3 mit „Dateien bei Bedarf“ gibt es als Testversion (*Einstellungen › Updates ›
> Testversionen erhalten*). Der Kern ist mit Unit- und Integrationstests abgesichert, darunter alle üblichen
> Dateivorgänge gegen einen IServ-ähnlichen Testserver. Probiere es zuerst mit einem kleinen Ordner aus.

## Was es kann

- **Konten verbinden:** Nextcloud (Anmeldung im Browser, auch mit Zwei-Faktor-Anmeldung, oder mit App-Passwort),
  IServ (Adresse der Schule genügt, CloudDrive-Sync nutzt `webdav.<schule>`) und andere WebDAV-Speicher.
- **Ordner synchronisieren:** einen Cloud-Ordner, bei IServ auch alle Gruppen auf einmal, oder das ganze Konto – alles oder nur ausgewählte Ordner und Dateien –
  in einen frei wählbaren Ordner auf diesem PC. Nichts ist verboten: Bei ungewöhnlichen Speicherorten (Netzlaufwerk,
  USB-Stick, Ordner eines anderen Sync-Programms …) gibt es Hinweise, die Entscheidung triffst du.
- **Dateien bei Bedarf (ab 0.3):** Alle Dateien erscheinen sofort im Explorer, belegen aber erst Platz, wenn du sie
  öffnest – wie bei OneDrive.
  - Symbole im Explorer zeigen, was nur online liegt. Im Kontextmenü stehen „Immer auf diesem Gerät beibehalten“ und
    „Speicherplatz freigeben“, und jede Synchronisation hat einen Eintrag im Navigationsbereich.
  - Auf Wunsch geben Dateien, die du länger nicht geöffnet hast, ihren Platz von selbst frei (Standard: nie).
  - Bestehende Synchronisationen lassen sich umstellen, ohne dass etwas erneut heruntergeladen wird – und zurück.
  - Das geht auf NTFS-Laufwerken dieses PCs; sonst bleibt „Alle Dateien auf diesem PC“.
- **In beide Richtungen:** Änderungen am PC gehen kurz nach dem Speichern hoch, Änderungen in der Cloud kommen im
  gewählten Abstand (Standard: alle 5 Minuten). Technisch arbeitet [rclone](https://rclone.org): bei „Alle Dateien auf
  diesem PC“ mit `bisync`, bei „Dateien bei Bedarf“ mit einem eigenen Abgleich über die Cloud-Files-Schnittstelle von
  Windows.
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
  - Dateien bei Bedarf: Freigegeben wird nur, was hochgeladen ist. Ändert sich eine Datei in der Cloud, während sie
    geladen wird, kommt nie eine Mischung aus zwei Fassungen an. Ein Ordner, den der Server nicht lesen lässt, bleibt
    außen vor, statt als gelöscht zu gelten.
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
- Was CloudDrive-Sync mit wem austauscht, steht in der [Datenschutzerklärung](PRIVACY.md): keine Telemetrie, nur deine
  Server, GitHub für Updates und einmal rclone.org für rclone.

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
(`%LOCALAPPDATA%\CloudDrive-Sync`) sowie deine synchronisierten Ordner bleiben erhalten. Bei „Dateien bei Bedarf“
bleiben heruntergeladene Dateien als normale Dateien; was nur online lag, verschwindet vom PC und bleibt in der Cloud.

## Code signing policy

Das Installationsprogramm ist noch nicht digital signiert. CloudDrive-Sync bewirbt sich um die kostenlose Codesignatur
der [SignPath Foundation](https://signpath.org). Für signierte Versionen gilt:

- Jede Version wird auf GitHub aus dem Quellcode dieses Repositorys gebaut
  ([Release-Workflow](.github/workflows/release.yml)), nicht auf einem privaten PC.
- Jede Signatur wird einzeln freigegeben.
- Rollen / roles:
  - Committer und Prüfer / committers and reviewers: [Steffen Schwabe](https://github.com/suebi76)
  - Freigabe / approvers: [Steffen Schwabe](https://github.com/suebi76)
- Datenschutz / privacy policy: [PRIVACY.md](PRIVACY.md)

*The setup program is not signed yet; CloudDrive-Sync is applying for free code signing by the SignPath Foundation.
Every release is built on GitHub from the source code in this repository, and every signature is approved one by one.*

## Voraussetzungen

- Windows 10 oder 11, 64 Bit
- „Dateien bei Bedarf“: Windows 10 Version 1809 oder neuer und ein NTFS-Laufwerk dieses PCs
- rclone 1.75.1 lädt CloudDrive-Sync beim ersten Start selbst herunter (Prüfsumme SHA256 fest hinterlegt).

## Entwickeln

```powershell
dotnet build CloudDriveSync.slnx
dotnet test tests/CloudDriveSync.Core.Tests                # Unit-Tests
dotnet test tests/CloudDriveSync.Core.IntegrationTests     # echtes rclone gegen einen lokalen WebDAV-Server
```

- **Aufbau** – Bausteine, Ablauf einer Synchronisation, Schutzmechanismen, Eigenheiten von rclone und den Servern:
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- **Entwickler-Handbuch** – einrichten, bauen, testen, veröffentlichen: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)
- **Regeln für Beiträge und Code:** [CONTRIBUTING.md](CONTRIBUTING.md)

## English summary

CloudDrive-Sync keeps folders on a Windows PC in step with Nextcloud, IServ and other WebDAV storage, in both
directions, built on [rclone](https://rclone.org) bisync with its own safety net (sentinel files, a deletion guard,
conflict copies, a recycle bin on the PC). From version 0.3 (now a test version) it offers files on demand like
OneDrive: every file shows in Explorer at once and takes space only when opened, through Windows' Cloud Files API.
Install it from the [releases](https://github.com/suebi76/CloudDrive-Sync/releases) (`CloudDrive-Sync-Setup.exe`, per
user, no administrator rights). The user interface is German. Developer documentation in English:
[architecture](docs/ARCHITECTURE.en.md), [developer handbook](docs/DEVELOPMENT.en.md),
[contributing and code rules](CONTRIBUTING.en.md).

## Lizenz

© 2026 Steffen Schwabe · [Projekt auf GitHub](https://github.com/suebi76/CloudDrive-Sync)

- **Programm** (Quellcode und Programmsymbol): [MIT-Lizenz](LICENSE). Du darfst es frei nutzen, verändern und
  weitergeben, solange der Copyright-Hinweis und die Lizenz erhalten bleiben.
- **Dokumentation** (die Texte in diesem Repository außerhalb des Programms):
  [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/deed.de). Bei Weitergabe nennst du „Steffen Schwabe,
  CloudDrive-Sync“ und verlinkst die Lizenz.
- **Verwendete Bausteine** anderer Projekte und ihre Lizenzen: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
