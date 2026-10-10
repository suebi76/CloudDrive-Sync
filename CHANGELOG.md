# Changelog

All notable changes to CloudDrive-Sync are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), versions follow [Semantic Versioning](https://semver.org/lang/de/).

## [0.3.0-preview.7] – 2026-10-10

Seventh test version on the way to 0.3: setting up is checked before anything is written, and clearing up never stops.

### Changed

- Two synchronisations can no longer share a folder on this PC - the same folder, one inside the other, or one around
  the other (`CD-4506`). Two synchronisations writing the same files could undo each other's work.
- Before a synchronisation is set up in a folder that exists already, CloudDrive-Sync looks through it (names and marks
  only, no content): damaged or orphaned cloud placeholders or an unreadable folder in it stop the setup before
  anything is written (`CD-4513`).
- The suggested folder for a new synchronisation sits next to the others ("<Konto> - <Ordner>") and never takes a
  folder that exists already.
- A setup that is cancelled or fails leaves nothing behind: no sentinel file, no folders it created, no entry in the
  settings.

### Fixed

- Every registration of CloudDrive-Sync with Windows that no synchronisation uses any more is cleared, too - not only
  those on the list of folders to clear up. A damaged list is set aside (never thrown away) instead of stopping ending,
  setting up or uninstalling.
- After a registration ended, a folder Windows cannot read counts as not clear: the registration comes back and the
  folder is cleared, instead of being left behind.
- The list of folders to clear up is written so that a crash can never leave half of it.

## [0.3.0-preview.6] – 2026-10-10

Sixth test version on the way to 0.3: nothing of a synchronisation or of CloudDrive-Sync itself is left behind.

### Fixed

- A synchronisation with files on demand could still leave a folder behind that could not be deleted, when Windows did
  not clear every placeholder as its registration ended. The registration with Windows now ends only once nothing of it
  is left in the folder: while a program holds a file there, the registration stays - every placeholder stays valid and
  deletable - and CloudDrive-Sync clears the folder as soon as the program lets go (every two minutes, and at every
  start). Should Windows still leave placeholders after the end, the same registration comes back at once, clears them,
  and ends again. The window for ending says when a program still holds something.

### Changed

- Uninstalling removes everything of CloudDrive-Sync from the PC: its entries in Windows, the registrations of files on
  demand (each ended only with its folder clear, as above), the settings, the encrypted sign-ins and their key in
  Windows' credential manager, the logs, rclone and the sentinel files in the synchronised folders. The synchronised
  folders and every file in them stay; in the cloud nothing changes. Only when a program held files in a folder with
  files on demand does a note of it stay, so a new installation can finish clearing it.

## [0.3.0-preview.5] – 2026-10-07

Fifth test version on the way to 0.3, with what the fourth showed.

### Added

- Ending a synchronisation or removing an account first asks what stays on this PC. In the cloud nothing changes.
  - "Heruntergeladene Dateien behalten" (the default): with files on demand, fetched files stay as normal files and
    files only online leave the PC; a classic folder stays as it is.
  - "Alles herunterladen und behalten" (files on demand): every file only online comes onto the PC first - when the
    drive has room for it.
  - "Vom PC löschen": a last run uploads everything first; only when it succeeded does the folder go into Windows'
    recycle bin with everything in it. Files that exist only on this PC (such as Office's lock files) are named and go
    only when confirmed; a file too large for the recycle bin is deleted only after Windows asks. Should the upload
    fail, the synchronisation stays.
  The window shows what it is doing and closes by itself when it is done.

### Fixed

- After ending a synchronisation with files on demand, its folder could not be deleted - Explorer did nothing, or
  Windows called the files damaged. Files a program held at that moment (Explorer, the search index) stayed behind as
  placeholders of a registration that no longer existed. Now every placeholder becomes a normal file or leaves the PC
  before the registration ends, CloudDrive-Sync waits until the folder is clear, and whatever still stays is noted and
  cleared at the next start, with the registration it belongs to. A folder with such remains is never taken for a new
  synchronisation.
- Each synchronisation with files on demand gets a registration of its own every time it is set up or switched, so the
  remains of an earlier one never mix with a new one.
- The last step of the assistant no longer waits minutes for a large cloud folder, such as a whole Nextcloud account,
  before starting is possible. It counts with the fast listing the synchronisation uses itself, shows how far it got
  ("bisher 1.234 Ordner, 12.345 Dateien, 23 GB"), and "Synchronisation starten" works at any time. A folder the server
  does not let be read no longer stops the count ("Größe konnte nicht ermittelt werden"); it is left out and mentioned.

### Changed

- While a synchronisation with files on demand reads a large cloud folder, its card says how far it got ("Liest die
  Cloud: 1.234 Ordner, 12.345 Dateien …") instead of only "Wird synchronisiert …".
- A classic synchronisation's card counts what it has read while it compares both sides ("Liest Cloud und PC:
  12.345 Einträge …"), so the first run of a large folder no longer looks stuck.

## [0.3.0-preview.4] – 2026-10-07

Fourth test version on the way to 0.3.

### Added

- Switching an existing synchronisation, in its settings under "So kommen die Dateien auf diesen PC":
  - to files on demand without any transfer: after a last normal run the files on the PC become files on demand where
    they lie. Only files neither side changed since then count as the same; any other file is kept in both versions.
    Should the switch break off, everything stays as it was.
  - back to "Alle Dateien auf diesem PC": changes go up first, then every file comes onto the PC - when the drive has
    room for it - and the first classic run merges both sides.
- Classic synchronisations get an entry in Explorer's navigation pane, too. The name of the entry can be chosen in the
  settings of a synchronisation (default "<Konto> – <Ordner>").
- After a crash, CloudDrive-Sync starts again by itself, in the background.

### Changed

- With files on demand, what is synchronised can be changed again: from a folder no longer selected, files that were
  only online leave the PC (they stay in the cloud), fetched files stay as normal files.
- "Abgleich überprüfen" works with files on demand, too, and fetches nothing for it: files only online are compared by
  name and size.
- Files on demand are no longer offered for a folder with a folder of another cloud program inside it (for example the
  user folder with OneDrive in it).

## [0.3.0-preview.3] – 2026-10-07

Third test version on the way to 0.3.

### Added

- "Speicherplatz automatisch freigeben" (Einstellungen): files on demand that were not opened for 7, 14, 30 or 60 days
  give their space on the PC back and stay in the cloud; opening one brings it back. The default is "Nie". Files kept
  with "Immer auf diesem Gerät beibehalten" and changes not uploaded yet always stay, and a file fetched just now counts
  as used.
- The card of a synchronisation with files on demand says so ("Dateien bei Bedarf") and shows how much of the cloud
  folder lies on the PC, e.g. "1,2 GB von 18 GB auf diesem PC".

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
