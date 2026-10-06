# Changelog

All notable changes to CloudDrive-Sync are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), versions follow [Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

### Added

- Developer documentation in German and English: architecture (`docs/ARCHITECTURE.md`), developer handbook
  (`docs/DEVELOPMENT.md`) and the rules for contributions and code (`CONTRIBUTING.md`)
- `tools/Format-SourceFiles.ps1` and the unit test `SourceFileTests` keep every source file UTF-8 without BOM
  (PowerShell scripts with BOM) with Windows line ends
- `PRIVACY.md` (German and English): no telemetry; CloudDrive-Sync talks only to the servers you enter, to GitHub
  for updates (as set) and once to rclone.org for rclone
- A "Code signing policy" in the README, linked from every release page: CloudDrive-Sync applies for free code
  signing by the SignPath Foundation
- Releases are built on GitHub from the tagged source (release workflow) instead of on a PC; every push and pull
  request is checked there (build, unit tests, secret scan)

### Changed

- The program icon is under the MIT License like the source code; only the documentation stays CC BY 4.0
- Code reorganised for readers, without a change in behaviour: the worker that keeps one synchronisation in step has
  a file of its own (`SyncService.PairWorker.cs`), the run history is a class of its own (`RunHistory`), every page of
  the main window has a file of its own (`Views/Pages`), the unit tests have one file per topic, and every public type
  has a documentation comment

### Fixed

- "CloudDrives öffnen" opens CloudDrives in its own console window with the CloudDrives symbol in the taskbar, as
  CloudDrives does itself - before, Windows 11 opened it in Windows Terminal first

## [0.2.0-preview.7] – 2026-10-05

Test version on the way to 0.2.

### Added

- Activity names the files of every run: uploaded, fetched to the PC, deleted in the cloud, deleted on the PC (in the
  recycle bin) or kept on the PC only - each with a button that shows it in its folder. Same-size changes
  CloudDrive-Sync carries over itself are named, too.
- "Abgleich überprüfen" in the menu of each synchronisation: compares every file on the PC with the one in the cloud
  (names and sizes, checksums where the server has them, on request the content) and lists what is only on the PC,
  only in the cloud or different. It changes nothing and waits for a running synchronisation of the folder.

## [0.2.0-preview.6] – 2026-10-05

Test version on the way to 0.2.

### Added

- IServ: all groups at once ("Gruppen") or the whole account - everything, or ticked groups and folders. Folders that
  take no files of their own get no protection file in the cloud; instead CloudDrive-Sync checks before each run that
  the cloud folder is there and does not suddenly look empty, and stops before anything is deleted (CD-4512).
- Files the server does not take (e.g. in a group to read only) stay on the PC: the rest of the run goes on without a
  rebuild, and the card names the files with "Im Ordner zeigen" and "Erneut versuchen".
- The top folders of IServ carry the names of its web pages: "Eigene Dateien" and "Gruppen".

### Changed

- The assistant explains the IServ folder "Gruppen" and the whole account instead of stopping there (as preview.5 did).

## [0.2.0-preview.5] – 2026-10-05

Test version on the way to 0.2.

### Fixed

- Setting up a synchronisation of a cloud folder the server takes no files in (on IServ "Groups" itself, or a folder
  with the right to read only) ended in "Unerwarteter Fehler". Now the assistant explains it and goes back to the
  choice of the folder; folders it had created on the PC for it are removed again. On IServ it says so already in
  the first step for "Groups" and for the whole account.

## [0.2.0-preview.4] – 2026-10-05

Test version on the way to 0.2.

### Changed

- Page "Über": without the descriptive sentence, with a sharp logo. The program's own pages show the logo from a
  256 px picture (an .ico shows its smallest picture there).

## [0.2.0-preview.3] – 2026-10-05

Test version on the way to 0.2 - the first one that arrives as an update.

### Changed

- The log files are called "clouddrive-sync-<date>.log" (before: "clouddrives-…", the name of CloudDrives); old ones
  expire as before. The start entry names the full version, the update check notes each new version it finds.

## [0.2.0-preview.2] – 2026-10-05

Test version on the way to 0.2.

### Added

- Setup program "CloudDrive-Sync-Setup.exe": installs for the signed-in user without administrator rights, with .NET
  included, and adds entries to the start menu and the desktop. It is uninstalled in the Windows settings ("Apps");
  settings, sign-ins and synchronised files stay.
- Updates from the GitHub project (Einstellungen › Updates): a notice and installation with one click (default),
  automatic installation at a quiet moment, or only when you look. "Testversionen erhalten" brings test versions.
- The installed program records where it is (Windows "App Paths"), so CloudDrives finds it.

### Fixed

- THIRD-PARTY-NOTICES.md is now part of the repository (the allowlist of .gitignore had left it out).

## [0.2.0-preview.1] – 2026-10-05

Test version on the way to 0.2.

### Added

- The settings of a synchronisation on one page - from the gear symbol on its card or "Einstellungen …" on the account
  page: what is synchronised, how often, conflicts, deletion guard. The step-by-step assistant stays for new ones.
- Account page: the synchronised folders of each account with their interval, changeable right there, and
  "Weiteren Ordner synchronisieren …".
- Short explanations of what each interval means (how fast news arrive, how much traffic it causes).
- Page "Über", also from the menu of the notification-area symbol: version, © 2026 Steffen Schwabe, licence, project
  on GitHub and the components used. The licence texts come with the program.

### Changed

- Licences: source code MIT, documentation and graphics CC BY 4.0; THIRD-PARTY-NOTICES.md lists the components used.
- Screen readers reach the texts and buttons of all cards.
- "Änderungen am PC sofort hochladen" now says "wenige Sekunden" - since 0.1.2 changes go up after about 5 seconds.

## [0.1.2] – 2026-10-05

### Fixed

- A file changed on the PC without changing its size (e.g. one letter replaced) is uploaded. IServ and other WebDAV
  servers keep no modification times of their own, so both versions looked equal.
- A changed file another program holds open exclusively no longer stops the synchronisation with "must be rebuilt":
  CloudDrive-Sync waits ("Wartet auf eine geöffnete Datei"), tries again every minute and transfers the file once it is
  free - in both directions. After a run that broke off, the last good state is kept instead of demanding a rebuild.
- Renames that only change upper and lower case ("bericht.docx" to "Bericht.docx") are carried over in both directions
  instead of stopping the synchronisation.
- Two server files whose names differ only in case (impossible in one Windows folder): both stay on the server, and a
  change to one of them never ends up in the other.

### Changed

- Changes on the PC are uploaded about 5 seconds after the last change (before: 30 seconds), and the overview shows at
  once "Änderung am PC erkannt – wird gleich übertragen …".
- After network or server trouble the next try follows after 1, 2, 4 … minutes (at most the interval); a rebuild that
  broke off is finished on its own. A failure that only repeats itself appears once in the activity list.

## [0.1.1] – 2026-10-05

### Changed

- A file deleted on the PC is deleted on the server as well. Version 0.1.0 moved it into a folder
  ".clouddrive-papierkorb" inside the cloud folder (IServ, WebDAV) - visible on the server, also to others in shared
  folders. What you delete yourself is still in the Windows recycle bin; Nextcloud keeps its own.

### Added

- Page "Papierkorb": per account and folder the files the synchronisation deleted or replaced on the PC - restore,
  delete for good, empty, open the folder; also from the menu of a synchronisation or an account
- The recycle bin on the PC can be switched off (Einstellungen › Papierkorb › Aus)
- A recycle bin folder left by version 0.1.0 on the server is shown on the page and can be deleted there

## [0.1.0] – 2026-10-05

### Added

- Desktop program in the Windows 11 style (WPF, Fluent, light and dark like Windows): overview with the state of
  every synchronisation, accounts, activity with conflicts, settings
- Accounts: Nextcloud (sign-in in the browser or with an app password), IServ and other WebDAV servers; the sign-in is
  checked against the server before anything is stored
- Synchronisations: cloud folder or whole account, everything or chosen folders and files, any local folder (with
  hints instead of prohibitions), conflict rule, interval, upload shortly after local changes, deletion guard
- Two-way synchronisation with rclone bisync in a hidden engine (rclone 1.75.1, checksum-pinned download)
- Safety: file-based deletion guard with "restore" or "apply", sentinel files on both sides, conflict copies,
  recycle bins on the PC and - for servers without one - in the cloud folder
- IServ and other servers without own modification times: changes that keep the file size are detected
- Symbol in the notification area with a status dot, Windows notifications, start with Windows
- Opens CloudDrives (the drives program) from its menu when it is installed
- Unit tests and integration tests against a real WebDAV server (`rclone serve webdav`)
