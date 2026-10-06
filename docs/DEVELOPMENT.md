# Entwickler-Handbuch

*English version: [DEVELOPMENT.en.md](DEVELOPMENT.en.md)*

Wie du CloudDrive-Sync einrichtest, baust, testest und veröffentlichst. Wie das Programm aufgebaut ist, steht in
[ARCHITECTURE.md](ARCHITECTURE.md), die Regeln für neuen Code in [CONTRIBUTING.md](../CONTRIBUTING.md).

## Voraussetzungen

- Windows 10 oder 11, 64 Bit
- [.NET SDK 10](https://dotnet.microsoft.com/download) (`global.json` legt 10.0.100 mit neueren Feature-Ständen fest)
- Git; für die Werkzeuge in `tools/` PowerShell 7 (`pwsh`)
- Zum Veröffentlichen: die [GitHub CLI](https://cli.github.com) (`gh`), angemeldet mit dem Recht, Releases anzulegen
- rclone musst du nicht installieren: Programm und Tests holen die festgelegte Version 1.75.1 selbst und prüfen ihre
  Prüfsumme. Mit `CLOUDDRIVE_SYNC_RCLONE=<Pfad zu rclone.exe>` nehmen sie eine vorhandene Datei.

## Bauen und starten

```powershell
git clone https://github.com/suebi76/CloudDrive-Sync.git
cd CloudDrive-Sync
dotnet build CloudDriveSync.slnx
```

Die Build-Ausgabe landet **außerhalb des Quellordners** unter `%USERPROFILE%\.clouddrive-sync-build\<Projekt>\` (siehe
`Directory.Build.props`). Der Grund: Der Quellordner darf in einem OneDrive-Ordner liegen, ohne dass Tausende
Build-Dateien synchronisiert werden. Das Programm selbst ist dann:

```text
%USERPROFILE%\.clouddrive-sync-build\CloudDriveSync.App\bin\Debug\net10.0-windows\CloudDrive-Sync.exe
```

**Mit eigenen Testdaten starten:** `CLOUDDRIVE_SYNC_HOME` legt einen anderen Datenordner fest. Er bekommt eigene
Einträge in der Windows-Anmeldeinformationsverwaltung; die echte Installation bleibt unberührt. Pro Datenordner läuft
nur eine Instanz.

```powershell
$env:CLOUDDRIVE_SYNC_HOME = "$env:TEMP\cds-test"
& "$env:USERPROFILE\.clouddrive-sync-build\CloudDriveSync.App\bin\Debug\net10.0-windows\CloudDrive-Sync.exe"
```

Eine selbst gebaute Kopie aktualisiert sich nicht selbst und trägt sich nicht in Windows ein; das tut nur die mit dem
Installationsprogramm eingerichtete Version.

## Testen

```powershell
dotnet test CloudDriveSync.slnx                              # alles
dotnet test tests/CloudDriveSync.Core.Tests                  # Unit-Tests, wenige Sekunden
dotnet test tests/CloudDriveSync.Core.IntegrationTests       # Integrationstests, einige Minuten
```

- **Unit-Tests** (`CloudDriveSync.Core.Tests`) laufen ohne Netz und ohne rclone. Pro Thema eine Datei, z. B.
  `DeleteGuardTests.cs`. `SourceFileTests` prüft die Regeln für Quelldateien (siehe unten).
- **Integrationstests** (`CloudDriveSync.Core.IntegrationTests`) starten echtes rclone und einen WebDAV-Testserver
  (`rclone serve webdav`) auf einem zufälligen lokalen Port. Jeder Test bekommt eine eigene Welt in
  `%TEMP%\clouddrive-sync-it\…` mit Datenordner, PC-Ordner und Server-Ordner (`SyncWorld`). Der Testserver verhält sich
  wie IServ: keine eigenen Änderungszeiten, kein Papierkorb, Groß- und Kleinschreibung werden unterschieden. Eine
  Variante nur zum Lesen prüft abgelehnte Uploads.
- Drei Tests brauchen einen Ordner, der Groß- und Kleinschreibung unterscheidet (`fsutil file setCaseSensitiveInfo`).
  Geht das auf einem PC nicht, schlagen genau diese drei mit einem klaren Hinweis fehl.
- **Nextcloud** lässt sich lokal nicht nachbilden. Änderungen an der Nextcloud-Anmeldung oder an Nextcloud-Besonderheiten
  brauchen einen Test mit einem echten Zugang.

Gute Tests lesen sich wie ein Satz (`Changing_a_file_on_the_PC_without_changing_its_size`) und prüfen ein Verhalten,
nicht eine Umsetzung.

## Die Oberfläche prüfen

- `CLOUDDRIVE_SYNC_SNAPSHOTS=<Ordner>` lässt CloudDrive-Sync jede Sekunde ein Bild jedes **eigenen** offenen Fensters
  speichern, benannt nach dem Fenster (`MainWindow.png` …, `WindowSnapshots`). Oberflächen lassen sich so prüfen, ohne
  den Bildschirm aufzunehmen.
- Alle Bedienelemente tragen einen Namen für Bildschirmleser (`AutomationProperties.Name`). Darüber lassen sie sich
  auch mit UI Automation ansteuern.

## Quelldateien

Alle Textdateien sind UTF-8 ohne BOM mit Windows-Zeilenenden; PowerShell-Skripte haben ein BOM (Windows PowerShell 5.1
liest sonst Umlaute falsch), YAML-Dateien Unix-Zeilenenden. `tools/Format-SourceFiles.ps1` bringt alle Dateien in
diese Form, `-Check` meldet nur. Der Unit-Test `SourceFileTests` prüft dasselbe bei jedem Testlauf.

```powershell
pwsh tools/Format-SourceFiles.ps1
```

`TreatWarningsAsErrors` ist eingeschaltet: Eine Warnung des Compilers bricht den Build ab.

## Veröffentlichen

1. Version in `Directory.Build.props` setzen (`<Version>`), z. B. `0.2.1` oder für eine Testversion `0.3.0-preview.1`.
2. Abschnitt `## [<Version>] – <Datum>` in `CHANGELOG.md` anlegen. Er wird zu den Release-Notizen.
3. Committen und pushen.
4. `pwsh tools/New-Release.ps1` baut zur Probe nach `%USERPROFILE%\.clouddrive-sync-build\releases`;
   `pwsh tools/New-Release.ps1 -Publish` baut und veröffentlicht:
   - Das Programm wird für 64-Bit-Windows eigenständig veröffentlicht (.NET ist enthalten).
   - [Velopack](https://velopack.io) (`vpk`, als lokales .NET-Werkzeug in `dotnet-tools.json`) packt
     `CloudDrive-Sync-Setup.exe`, die Update-Pakete (mit einem kleinen Delta zur vorigen Version) und `releases.win.json`.
   - Der Commit bekommt den Tag `v<Version>`; das GitHub-Release enthält diese Dateien.
   - **Versionen mit Zusatz** wie `-preview.1` werden Pre-Releases: Testversionen, die nur bekommt, wer
     „Testversionen erhalten“ eingeschaltet hat.

Installierte Programme finden das Update selbst (Einstellungen › Updates). Das Installationsprogramm ist noch nicht
signiert; SmartScreen fragt deshalb beim ersten Start nach.

## Fehlersuche

- Protokolle: `%LOCALAPPDATA%\CloudDrive-Sync\logs\` (bzw. im eigenen Datenordner) – `clouddrive-sync-<Datum>.log` und
  `rclone.log`.
- Zu jeder Synchronisation: `sync\<id>\last-run.txt` (Bericht von bisync), `state.json`, `runs.jsonl`.
- In der Oberfläche: „Aktivität“ und „Abgleich überprüfen“.

## Wo anfangen?

1. [ARCHITECTURE.md](ARCHITECTURE.md) lesen.
2. `src/CloudDriveSync.Core/CloudDriveSyncHost.cs` – hier wird alles zusammengesetzt.
3. `Sync/SyncService.cs` und `Sync/SyncService.PairWorker.cs` – wann ein Lauf startet.
4. `Sync/SyncRunner.cs` – was ein Lauf tut.
5. `tests/CloudDriveSync.Core.IntegrationTests/SyncWorld.cs` und `OperationTests.cs` – wie das alles zusammen geprüft wird.

## Häufige Änderungen

- **Ein neuer Fehlercode:** Eintrag in `Errors/ErrorCatalog.cs` (Titel und Lösung auf Deutsch und Englisch, bei Bedarf
  ein Erkennungsmuster) und ein Test in `ErrorCatalogTests.cs`. Verlangt der Code eine Entscheidung, gehört er in die
  Zuordnung am Ende von `SyncRunner.RunCoreAsync`.
- **Eine neue Einstellung des Programms:** Eigenschaft in `Settings/AppSettings.cs` (`Preferences`) mit sinnvollem
  Standard – fehlende Werte älterer Einstellungsdateien bekommen ihn automatisch –, dazu `SettingsViewModel` und
  `Views/Pages/SettingsPage.xaml`.
- **Eine neue Einstellung einer Synchronisation:** `SyncPairSettings`, `SyncSettingsViewModel` und
  `Views/SyncSettingsWindow.xaml`. Ändert sie den Filter, löst `SyncService.Update` einen Neuaufbau aus.
- **Eine neue Seite:** Wert in `Page` und Eintrag in `MainViewModel.Navigation`, Datei in `Views/Pages` und ein Eintrag
  im Seitenbereich von `Views/MainWindow.xaml`.
