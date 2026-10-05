# Changelog

All notable changes to CloudDrive-Sync are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), versions follow [Semantic Versioning](https://semver.org/lang/de/).

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
