# Changelog

All notable changes to CloudDrive-Sync are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), versions follow [Semantic Versioning](https://semver.org/lang/de/).

## [0.3.0-preview.2] – 2026-10-06

Second test version on the way to 0.3, with what the first test showed.

### Fixed

- Folders with files on demand show their status in Explorer (cloud or tick), not only files: after every run
  CloudDrive-Sync marks them in sync, and it tells Windows how the synchronisation is doing.
- A cloud folder the server does not let be read - in Nextcloud for example a share to upload only ("403 Forbidden") -
  no longer ends the whole run. It is left out with everything in it, nothing in it is deleted, and the log names it;
  the rest is synchronised.

### Changed

- Reading the cloud folder is much faster with many folders: folder by folder, eight at once, without rclone's pause of
  10 ms between two requests (measured: 526 folders in 1.2 s instead of 5.4 s). The log names the time of each listing.

## [0.3.0-preview.1] – 2026-10-06

First test version on the way to 0.3: files on demand.

### Added

- Files on demand ("Dateien bei Bedarf") for new synchronisations: all files of the cloud folder appear in Explorer at
  once, but take space on the PC only when they are opened. The assistant asks how the files should come to the PC,
  with files on demand preselected. Where Windows does not allow it - no NTFS, a network or removable drive, the root of
  a drive, a folder inside OneDrive, Dropbox or Nextcloud - it says why, and "Alle Dateien auf diesem PC" (the classic
  way) stays.
- In Explorer, from Windows itself: a cloud for files only online, a tick for files on the PC, an entry in the
  navigation pane for each synchronisation (e.g. "IServ – Eigene Dateien"), and in the context menu "Immer auf diesem
  Gerät behalten" and "Speicherplatz freigeben". Keeping a folder also fetches files that come into it later; freeing
  space never throws away a change that is not uploaded yet.
- Opening a file that is only online fetches it. Should it change in the cloud meanwhile, opening fails cleanly and the
  next run brings the new version - never a mix of both.
- Renaming and moving on the PC become a move on the server: nothing is uploaded again, and shares and versions in
  Nextcloud stay.
- The safety net of classic synchronisations applies, too: the deletion guard, the recycle bin on the PC, conflict
  copies, the protection files, and files the server does not take stay on the PC. Should Windows lose the
  registration of the folder, the next run merges both sides and deletes nothing.
- Uninstalling unregisters the folders with files on demand: fetched files stay as normal files, files that were only
  online leave the PC and stay in the cloud.

### Not yet in this test version

- Existing synchronisations stay classic; switching them to files on demand (and back) follows.
- With files on demand, what is synchronised cannot be changed afterwards yet, and "Abgleich überprüfen" is not offered.
- Freeing space automatically after some days, the space used on the card, the name in Explorer in the settings.

## [0.2.0] – 2026-10-06

The first regular version after 0.1.2. It brings together the test versions 0.2.0-preview.1 to preview.7 and what
followed them.

### Added

- Setup program "CloudDrive-Sync-Setup.exe": installs for the signed-in user without administrator rights, with .NET
  included, and adds entries to the start menu and the desktop. It is uninstalled in the Windows settings ("Apps");
  settings, sign-ins and synchronised files stay.
- Updates from the GitHub project (Einstellungen › Updates): a notice and installation with one click (default),
  automatic installation at a quiet moment, or only when you look. "Testversionen erhalten" brings test versions.
- The settings of a synchronisation on one page - from the gear symbol on its card or "Einstellungen …" on the account
  page: what is synchronised, how often, conflicts, deletion guard. The step-by-step assistant stays for new ones.
- Account page: the synchronised folders of each account with their interval, changeable right there, and
  "Weiteren Ordner synchronisieren …". Short explanations say what each interval means.
- IServ: all groups at once ("Gruppen") or the whole account - everything, or ticked groups and folders. The top
  folders carry the names of IServ's web pages: "Eigene Dateien" and "Gruppen".
- Folders that take no files of their own get no protection file in the cloud; instead CloudDrive-Sync checks before
  each run that the cloud folder is there and does not suddenly look empty, and stops before anything is deleted
  (CD-4512).
- Files the server does not take (e.g. in a group to read only) stay on the PC: the rest of the run goes on without a
  rebuild, and the card names the files with "Im Ordner zeigen" and "Erneut versuchen".
- "Aktivität" names the files of every run: uploaded, fetched to the PC, deleted in the cloud, deleted on the PC (in the
  recycle bin) or kept on the PC only - each with a button that shows it in its folder.
- "Abgleich überprüfen" in the menu of each synchronisation compares every file on the PC with the one in the cloud
  (names and sizes, checksums where the server has them, on request the content). It changes nothing.
- Page "Über", also from the menu of the notification-area symbol: version, © 2026 Steffen Schwabe, licence, project on
  GitHub and the components used. The licence texts come with the program.
- Privacy policy (`PRIVACY.md`): no telemetry; CloudDrive-Sync talks only to the servers you enter, to GitHub for
  updates (as set) and once to rclone.org for rclone. The README has a code signing policy: CloudDrive-Sync applies for
  free code signing by the SignPath Foundation.

### Changed

- Licences: the program (source code and icon) is under the MIT License, the documentation under CC BY 4.0;
  THIRD-PARTY-NOTICES.md lists the components used.
- Screen readers reach the texts and buttons of all cards.
- "Änderungen am PC sofort hochladen" says "wenige Sekunden" - changes go up about 5 seconds after the last change.
- The log files are called "clouddrive-sync-<date>.log" (before: "clouddrives-…", the name of CloudDrives).
- For developers: documentation of the architecture, a developer handbook and code rules in German and English; the
  code is split into smaller files; releases are built on GitHub from the tagged source code, and every push is checked
  there (build, unit tests, secret scan).

### Fixed

- After the first synchronisation of a folder whose files were on both sides already, the next run took the files on
  the PC for changed: it uploaded them all again - possibly over a newer version on the server - or stopped with
  "Ungewöhnlich viele Änderungen". Now and then the same happened to the protection file alone, which the server
  receives a moment after the PC wrote it. CloudDrive-Sync now keeps bisync's record of the PC side true to the files.
- Setting up a synchronisation of a cloud folder the server takes no files in (on IServ "Groups" itself, or a folder
  with the right to read only) ended in "Unerwarteter Fehler". Now it works as described above.
- "CloudDrives öffnen" opens CloudDrives in its own console window with the CloudDrives symbol in the taskbar - before,
  Windows 11 opened it in Windows Terminal first.

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
