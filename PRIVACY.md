# Datenschutzerklärung / Privacy Policy

## Deutsch

CloudDrive-Sync ist ein Open-Source-Programm, das vollständig **auf deinem Windows-PC** läuft.

- **Keine Datensammlung:** CloudDrive-Sync hat keinen eigenen Server und keine Telemetrie. Es sendet keine Daten an den
  Entwickler.
- **Deine Server:** CloudDrive-Sync verbindet sich mit den Servern, die du selbst einträgst (Nextcloud, IServ, andere
  WebDAV-Speicher), um deine Ordner abzugleichen. Die Dateien fließen direkt zwischen deinem PC und diesen Servern.
- **Anmeldedaten:** Zugangsdaten gibst du nur in CloudDrive-Sync ein. Sie gehen nur an deinen Server und liegen auf deinem
  PC ausschließlich verschlüsselt (in der verschlüsselten rclone-Konfiguration; der Schlüssel steckt in der
  Windows-Anmeldeinformationsverwaltung). Bei Nextcloud entsteht bei der Anmeldung im Browser ein eigenes App-Passwort,
  das du in Nextcloud unter *Einstellungen › Sicherheit* jederzeit widerrufen kannst.
- **Updates:** Wie in den Einstellungen gewählt, sieht CloudDrive-Sync bei GitHub nach neuen Versionen und lädt sie dort
  herunter (github.com). Mit „Nur wenn ich nachsehe“ geschieht das nur, wenn du selbst danach suchst. Dabei werden nur
  die technischen Angaben jeder Webanfrage übertragen (deine IP-Adresse, die Programmversion). Es gilt die
  [Datenschutzerklärung von GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
- **rclone:** Beim ersten Start lädt CloudDrive-Sync das Werkzeug rclone von downloads.rclone.org (ersatzweise von GitHub)
  herunter und prüft seine Prüfsumme. Auch dabei werden nur die technischen Angaben der Webanfrage übertragen.
- **Lokale Dateien:** Einstellungen, Protokolle und der Zustand der Synchronisationen liegen unter
  `%LOCALAPPDATA%\CloudDrive-Sync`, das Programm unter `%LOCALAPPDATA%\CloudDriveSync`. Protokolle enthalten keine
  Passwörter. Beim Deinstallieren bleiben Einstellungen und synchronisierte Ordner erhalten; den Datenordner kannst du
  danach von Hand löschen.

## English

CloudDrive-Sync is open-source software that runs entirely **on your Windows PC**.

- **No data collection:** CloudDrive-Sync has no server of its own and no telemetry. It sends no data to the developer.
- **Your servers:** CloudDrive-Sync connects to the servers you enter yourself (Nextcloud, IServ, other WebDAV storage) to
  keep your folders in step. Files flow directly between your PC and these servers.
- **Sign-in details:** you enter them only in CloudDrive-Sync. They go only to your server and are stored on your PC only in
  encrypted form (in the encrypted rclone configuration; its key lives in the Windows Credential Manager). With Nextcloud,
  the browser sign-in creates an app password of its own, which you can revoke at any time in Nextcloud under
  *Settings › Security*.
- **Updates:** as chosen in the settings, CloudDrive-Sync looks for new versions on GitHub and downloads them from there
  (github.com). With "Nur wenn ich nachsehe" (only when I check) this happens only when you look yourself. Only the
  technical details of any web request are transferred (your IP address, the program version). GitHub's
  [privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) applies.
- **rclone:** at its first start, CloudDrive-Sync downloads the tool rclone from downloads.rclone.org (or else from GitHub)
  and checks its checksum. Here, too, only the technical details of the web request are transferred.
- **Local files:** settings, logs and the state of the synchronisations live in `%LOCALAPPDATA%\CloudDrive-Sync`, the
  program in `%LOCALAPPDATA%\CloudDriveSync`. Logs contain no passwords. Uninstalling keeps settings and synchronised
  folders; you can delete the data folder by hand afterwards.
