# Changelog

All notable changes to CloudDrive-Sync are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), versions follow [Semantic Versioning](https://semver.org/lang/de/).

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
