# Developer handbook

*Deutsche Fassung: [DEVELOPMENT.md](DEVELOPMENT.md)*

How to set up, build, test and publish CloudDrive-Sync. How the program is built is described in
[ARCHITECTURE.en.md](ARCHITECTURE.en.md), the rules for new code in [CONTRIBUTING.en.md](../CONTRIBUTING.en.md).

## Prerequisites

- Windows 10 or 11, 64-bit
- [.NET SDK 10](https://dotnet.microsoft.com/download) (`global.json` pins 10.0.100 with newer feature bands allowed)
- Git; PowerShell 7 (`pwsh`) for the scripts in `tools/`
- To publish: the [GitHub CLI](https://cli.github.com) (`gh`), signed in with the right to create releases
- You do not need to install rclone: the program and the tests fetch the pinned version 1.75.1 themselves and check its
  checksum. With `CLOUDDRIVE_SYNC_RCLONE=<path to rclone.exe>` they use an existing file.

## Build and run

```powershell
git clone https://github.com/suebi76/CloudDrive-Sync.git
cd CloudDrive-Sync
dotnet build CloudDriveSync.slnx
```

The build output goes **outside the source folder**, to `%USERPROFILE%\.clouddrive-sync-build\<project>\` (see
`Directory.Build.props`). The reason: the source folder may live in a OneDrive folder without thousands of build files
being synchronised. The program is then:

```text
%USERPROFILE%\.clouddrive-sync-build\CloudDriveSync.App\bin\Debug\net10.0-windows\CloudDrive-Sync.exe
```

**Run with test data of its own:** `CLOUDDRIVE_SYNC_HOME` chooses another data folder. It gets entries of its own in the
Windows Credential Manager; the real installation stays untouched. One instance runs per data folder.

```powershell
$env:CLOUDDRIVE_SYNC_HOME = "$env:TEMP\cds-test"
& "$env:USERPROFILE\.clouddrive-sync-build\CloudDriveSync.App\bin\Debug\net10.0-windows\CloudDrive-Sync.exe"
```

A copy you built yourself does not update itself and does not register itself in Windows; only the version installed
with the setup program does.

## Testing

```powershell
dotnet test CloudDriveSync.slnx                              # everything
dotnet test tests/CloudDriveSync.Core.Tests                  # unit tests, a few seconds
dotnet test tests/CloudDriveSync.Core.IntegrationTests       # integration tests, a few minutes
```

- **Unit tests** (`CloudDriveSync.Core.Tests`) run without network and without rclone. One file per topic, e.g.
  `DeleteGuardTests.cs`. `SourceFileTests` checks the rules for source files (see below).
- **Integration tests** (`CloudDriveSync.Core.IntegrationTests`) start real rclone and a WebDAV test server
  (`rclone serve webdav`) on a random local port. Every test gets a world of its own in `%TEMP%\clouddrive-sync-it\…`
  with a data folder, a PC folder and a server folder (`SyncWorld`). The test server behaves like IServ: no modification
  times of its own, no recycle bin, upper and lower case are told apart. A read-only variant checks refused uploads.
- **Looking into a failure:** `CLOUDDRIVE_SYNC_KEEP_TEST_WORLDS=1` keeps every test world. bisync's report is then in
  `home\sync\<id>\last-run.txt`, its listings in `home\sync\<id>\bisync\`. Delete the folder by hand afterwards - it
  quickly grows to several gigabytes.
- Three tests need a folder that tells upper and lower case apart (`fsutil file setCaseSensitiveInfo`). Where that is
  not possible, exactly these three fail with a clear message.
- **Files on demand** (`CloudFilesTests`, `TestSyncRoot`): these tests register real folders with Windows - under
  `CloudDriveSyncTest-<checksum>!…`, never under the installation's `CloudDriveSync!…` - and unregister them at the end.
  They need NTFS (Windows' `%TEMP%` will do). When a test run breaks off hard, a registration may stay behind;
  `SyncRoots.RemoveTestRegistrations()` removes all `CloudDriveSyncTest-…` of the user. Windows' own queries
  (`GetCurrentSyncRoots`) do not show folders in the temp folder - that is why the code reads the registrations from
  the registry.
- **Nextcloud** cannot be emulated locally. Changes to the Nextcloud sign-in or to Nextcloud specifics need a test with a
  real account.
- **On GitHub** every push and pull request runs the build, the unit tests and a secret scan (gitleaks,
  `.github/workflows/ci.yml`). The integration tests run locally: they need folders that tell upper and lower case
  apart, and they take a few minutes.

Good tests read like a sentence (`Changing_a_file_on_the_PC_without_changing_its_size`) and check behaviour, not an
implementation.

## Checking the user interface

- `CLOUDDRIVE_SYNC_SNAPSHOTS=<folder>` makes CloudDrive-Sync save a picture of each of its **own** open windows every
  second, named after the window (`MainWindow.png` …, `WindowSnapshots`). Layouts can be checked this way without taking
  pictures of the screen.
- Every control has a name for screen readers (`AutomationProperties.Name`). UI Automation can use it to drive the
  interface, too.

## Source files

All text files are UTF-8 without BOM with Windows line ends; PowerShell scripts have a BOM (Windows PowerShell 5.1
misreads umlauts otherwise), YAML files Unix line ends. `tools/Format-SourceFiles.ps1` puts every file into that shape,
`-Check` only reports. The unit test `SourceFileTests` checks the same on every test run.

```powershell
pwsh tools/Format-SourceFiles.ps1
```

`TreatWarningsAsErrors` is on: a compiler warning fails the build.

## Publishing

1. Set the version in `Directory.Build.props` (`<Version>`), e.g. `0.2.1`, or for a test version `0.3.0-preview.1`.
2. Add a section `## [<version>] – <date>` to `CHANGELOG.md`. It becomes the release notes.
3. Commit and push to `main`.
4. `pwsh tools/New-Release.ps1 -Publish` puts the tag `v<version>` on the commit and pushes it. **GitHub** does the rest
   in the release workflow (`.github/workflows/release.yml`) - releases never come about on a private PC:
   - Unit tests, then the program is published self-contained for 64-bit Windows (.NET included).
   - [Velopack](https://velopack.io) (`vpk`, a local .NET tool in `dotnet-tools.json`) packs `CloudDrive-Sync-Setup.exe`,
     the update packages (with a small delta to the previous version) and `releases.win.json`.
   - The GitHub release of the tag holds these files.
   - **Versions with a suffix** such as `-preview.1` become pre-releases: test versions that only reach those who
     switched on "Testversionen erhalten" (receive test versions).

**Trial:** `pwsh tools/New-Release.ps1` builds locally into `%USERPROFILE%\.clouddrive-sync-build\releases` without
publishing anything. On GitHub, "Run workflow" on the release workflow does the same and keeps the files as an artifact.

Installed programs find the update themselves (Einstellungen › Updates). The setup program is not signed yet, so
SmartScreen asks on its first start. Code signing through the SignPath Foundation is being applied for; it will be added
to the release workflow between building and publishing (see "Code signing policy" in the README).

## Troubleshooting

- Logs: `%LOCALAPPDATA%\CloudDrive-Sync\logs\` (or in your own data folder) - `clouddrive-sync-<date>.log` and
  `rclone.log`.
- Per synchronisation: `sync\<id>\last-run.txt` (bisync's report), `state.json`, `runs.jsonl`.
- In the interface: "Aktivität" (activity) and "Abgleich überprüfen" (verify).

## Where to start

1. Read [ARCHITECTURE.en.md](ARCHITECTURE.en.md).
2. `src/CloudDriveSync.Core/CloudDriveSyncHost.cs` - everything is put together here.
3. `Sync/SyncService.cs` and `Sync/SyncService.PairWorker.cs` - when a run starts.
4. `Sync/SyncRunner.cs` - what a run does.
5. `tests/CloudDriveSync.Core.IntegrationTests/SyncWorld.cs` and `OperationTests.cs` - how it is all checked together.

## Common changes

- **A new error code:** an entry in `Errors/ErrorCatalog.cs` (title and fix in German and English, a recognition pattern
  where needed) and a test in `ErrorCatalogTests.cs`. When the code asks for a decision, it belongs in the mapping at the
  end of `SyncRunner.RunCoreAsync`.
- **A new program setting:** a property in `Settings/AppSettings.cs` (`Preferences`) with a sensible default - missing
  values in older settings files get it automatically -, plus `SettingsViewModel` and `Views/Pages/SettingsPage.xaml`.
- **A new setting of a synchronisation:** `SyncPairSettings`, `SyncSettingsViewModel` and `Views/SyncSettingsWindow.xaml`.
  When it changes the filter, `SyncService.Update` triggers a rebuild.
- **A new page:** a value in `Page` and an entry in `MainViewModel.Navigation`, a file in `Views/Pages` and an entry in
  the page area of `Views/MainWindow.xaml`.
