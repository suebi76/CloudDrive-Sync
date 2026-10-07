using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudDriveSync.Core.Errors;

/// <summary>One entry of the error catalog: what happened and what helps.</summary>
public sealed record ErrorEntry(string Code, string Title, string Fix, bool NeedsSignIn = false);

/// <summary>
/// The error catalog: every code with a title and a fix in German and English, and the patterns that recognise a
/// code in the text of an error (rclone's answers, server messages).
/// </summary>
public static class ErrorCatalog
{
    private sealed record Texts(string TitleDe, string FixDe, string TitleEn, string FixEn, bool NeedsSignIn = false);

    private static readonly Dictionary<string, Texts> Entries = new()
    {
        ["CD-1004"] = new("Download fehlgeschlagen", "Prüfe die Internetverbindung und versuche es erneut.",
            "Download failed", "Check the internet connection and try again."),
        ["CD-1006"] = new("Prüfsumme stimmt nicht", "Die heruntergeladene Datei ist beschädigt oder verändert und wurde verworfen. Versuche es später erneut.",
            "Checksum mismatch", "The downloaded file is damaged or altered and was discarded. Try again later."),
        ["CD-2001"] = new("Einstellungen beschädigt", "CloudDrive-Sync hat die Einstellungen aus der Sicherung wiederhergestellt oder neu angelegt.",
            "Settings damaged", "CloudDrive-Sync restored the settings from the backup or created new ones."),
        ["CD-2002"] = new("Konfiguration konnte nicht verschlüsselt werden", "Prüfe, ob der Datenordner beschreibbar ist, und starte CloudDrive-Sync neu.",
            "The configuration could not be encrypted", "Check that the data folder is writable and restart CloudDrive-Sync."),
        ["CD-2003"] = new("Schlüssel der Konfiguration fehlt", "Der Schlüssel in der Windows-Anmeldeinformationsverwaltung fehlt. Melde die Konten neu an.",
            "Key of the configuration missing", "The key in the Windows Credential Manager is missing. Sign the accounts in again."),
        ["CD-3004"] = new("Anmeldung abgebrochen", "Die Anmeldung wurde abgebrochen. Du kannst es jederzeit erneut versuchen.",
            "Sign-in cancelled", "The sign-in was cancelled. You can try again at any time."),
        ["CD-3005"] = new("Zeitüberschreitung bei der Anmeldung", "Die Anmeldung im Browser wurde nicht rechtzeitig abgeschlossen. Bitte erneut versuchen.",
            "Sign-in timed out", "The sign-in in the browser was not completed in time. Please try again."),
        ["CD-3006"] = new("Cloud-Speicher ist voll", "Gib Speicher frei oder erweitere dein Speicher-Abo beim Anbieter.",
            "Cloud storage is full", "Free up space or upgrade your storage plan at the provider."),
        ["CD-3008"] = new("Anmeldung fehlgeschlagen", "Prüfe die Internetverbindung und versuche die Anmeldung erneut.",
            "Sign-in failed", "Check the internet connection and try signing in again."),
        ["CD-3009"] = new("Mit einem anderen Konto angemeldet", "Melde dich mit dem Konto an, das zu diesem Eintrag gehört. Die bisherige Anmeldung bleibt unverändert.",
            "Signed in with a different account", "Sign in with the account that belongs to this entry. The previous sign-in stays unchanged."),
        ["CD-3010"] = new("Dieses Konto ist bereits eingerichtet", "Weitere Ordner dieses Kontos fügst du als zusätzliche Synchronisation hinzu.",
            "This account is already set up", "Add more folders of this account as additional synchronisations."),
        ["CD-3012"] = new("Benutzername oder Passwort abgelehnt", "Der Server hat die Anmeldung abgelehnt. Prüfe Benutzername und Passwort – bei IServ dein IServ-Passwort. Wurde das Passwort geändert, melde das Konto neu an.",
            "User name or password refused", "The server refused the sign-in. Check the user name and the password - for IServ your IServ password. If the password was changed, sign the account in again.", NeedsSignIn: true),
        ["CD-3013"] = new("WebDAV-Adresse nicht gefunden", "Unter dieser Adresse antwortet kein WebDAV-Server. Bei IServ ist es webdav.<Adresse deiner Schule>, bei Nextcloud die Adresse, unter der du sie im Browser öffnest. Adressen müssen mit https beginnen.",
            "WebDAV address not found", "No WebDAV server answers at this address. For IServ it is webdav.<address of your school>, for Nextcloud the address you open it with in the browser. Addresses must start with https."),
        ["CD-3014"] = new("Nextcloud-Anmeldung nicht möglich", "Die Anmeldung im Browser hat nicht geklappt. Versuche es erneut. Lässt deine Nextcloud sie für Programme nicht zu, fragt CloudDrive-Sync nach einem App-Passwort (Nextcloud → Einstellungen → Sicherheit).",
            "Nextcloud sign-in not possible", "The sign-in in the browser did not work. Try again. If your Nextcloud does not allow it for programs, CloudDrive-Sync asks for an app password (Nextcloud > Settings > Security)."),
        ["CD-3015"] = new("Unverschlüsselte Verbindung", "Die Adresse beginnt mit http:// – Passwort und Dateien würden lesbar übertragen. Nutze https:// oder bestätige ausdrücklich, dass du das willst.",
            "Unencrypted connection", "The address starts with http:// - password and files would travel readable. Use https:// or confirm explicitly that you want this."),
        ["CD-4501"] = new("Lokaler Ordner nicht verfügbar", "Der Ordner oder sein Datenträger ist nicht erreichbar. Schließe den Datenträger an – solange wird nichts synchronisiert und nichts gelöscht.",
            "Local folder not available", "The folder or its drive cannot be reached. Connect the drive - until then nothing is synchronised and nothing is deleted."),
        ["CD-4502"] = new("Zu viele Löschungen – Synchronisation angehalten", "Es würde mehr gelöscht als erlaubt. Entscheide, ob die Löschungen übernommen oder die Dateien wiederhergestellt werden.",
            "Too many deletions - synchronisation stopped", "More would be deleted than allowed. Decide whether to apply the deletions or to restore the files."),
        ["CD-4503"] = new("Wächterdatei fehlt", "Die Datei .clouddrive-sync fehlt auf einer Seite – der Ordner wurde verschoben oder ist nicht erreichbar. Zur Sicherheit wurde nichts geändert.",
            "Sentinel file missing", "The file .clouddrive-sync is missing on one side - the folder was moved or cannot be reached. To be safe, nothing was changed."),
        ["CD-4504"] = new("Abgleich muss neu aufgebaut werden", "Nach einem schweren Fehler muss der Abgleich neu aufgebaut werden. Dabei wird nichts gelöscht; abweichende Dateien behält CloudDrive-Sync als Kopie.",
            "The synchronisation must be rebuilt", "After a serious error the synchronisation must be rebuilt. Nothing is deleted; differing files are kept as copies."),
        ["CD-4505"] = new("Nicht genug Speicherplatz", "Für die Auswahl reicht der freie Platz auf dem Datenträger nicht. Wähle weniger Ordner oder einen anderen Speicherort.",
            "Not enough disk space", "The free space on the drive is not enough for the selection. Choose fewer folders or another location."),
        ["CD-4507"] = new("Konflikte gefunden", "Dateien wurden auf beiden Seiten geändert. Beide Fassungen sind erhalten; entscheide in der Konfliktliste, welche bleibt.",
            "Conflicts found", "Files were changed on both sides. Both versions are kept; decide in the list of conflicts which one stays."),
        ["CD-4508"] = new("Synchronisation läuft bereits", "Ein anderer Abgleich dieses Ordners läuft noch. Er wird abgewartet.",
            "Synchronisation already running", "Another synchronisation of this folder is still running. It is waited for."),
        ["CD-4509"] = new("Ungewöhnlich viele Änderungen – Synchronisation angehalten", "Auf einer Seite sind alle Dateien verändert, z. B. nach einer Zeitumstellung oder einem Kopiervorgang. Entscheide, ob die Änderungen übernommen werden oder der Abgleich neu aufgebaut wird (dabei wird nichts gelöscht).",
            "Unusually many changes - synchronisation stopped", "All files changed on one side, e.g. after a time change or a copy. Decide whether to apply the changes or to rebuild the synchronisation (nothing is deleted)."),
        ["CD-4510"] = new("Datei ist in einem anderen Programm geöffnet", "Eine geänderte Datei ist gerade in einem anderen Programm geöffnet und gesperrt, z. B. in Word oder Excel. CloudDrive-Sync versucht es jede Minute erneut und überträgt sie, sobald sie frei ist. Es geht nichts verloren.",
            "File is open in another program", "A changed file is open and locked in another program right now, e.g. in Word or Excel. CloudDrive-Sync tries again every minute and transfers it once it is free. Nothing is lost."),
        ["CD-4511"] = new("Nicht alles konnte hochgeladen werden", "In manchen Ordnern darfst du nur lesen – zum Beispiel in Gruppen, in denen nur Lehrkräfte schreiben. Deine Änderungen dort bleiben auf diesem PC erhalten; alles andere wird weiter abgeglichen. Eigene Dateien legst du am besten in einen Ordner, in dem du schreiben darfst.",
            "Not everything could be uploaded", "In some folders you may only read - for example in groups where only teachers write. Your changes there stay on this PC; everything else keeps being synchronised. Put files of your own into a folder you may write to."),
        ["CD-4512"] = new("Cloud-Ordner nicht erreichbar – Synchronisation angehalten", "Der Ordner auf dem Server fehlt oder erscheint plötzlich leer – zum Beispiel, weil er umbenannt wurde oder du nicht mehr Mitglied der Gruppe bist. Zur Sicherheit wurde nichts verändert. Prüfe den Ordner im Browser und versuche es dann erneut.",
            "Cloud folder not reachable - synchronisation stopped", "The folder on the server is missing or suddenly looks empty - for example because it was renamed or you are no longer a member of the group. To be safe, nothing was changed. Check the folder in the browser, then try again."),
        ["CD-4601"] = new("Dateien bei Bedarf sind in diesem Ordner nicht möglich", "Windows bietet „Dateien bei Bedarf“ nur auf NTFS-Laufwerken dieses PCs und nicht in Ordnern, die schon ein anderes Cloud-Programm nutzt (z. B. OneDrive). Wähle einen anderen Ordner oder „Alle Dateien auf diesem PC“.",
            "Files on demand are not possible in this folder", "Windows offers files on demand only on NTFS drives of this PC and not in folders another cloud program uses already (e.g. OneDrive). Choose another folder or \"All files on this PC\"."),
        ["CD-4602"] = new("Windows hat den Ordner nicht angenommen", "Der Ordner konnte nicht für „Dateien bei Bedarf“ angemeldet werden. Starte CloudDrive-Sync neu; hilft das nicht, erstelle ein Support-Paket. Deine Dateien sind davon nicht betroffen.",
            "Windows did not accept the folder", "The folder could not be registered for files on demand. Restart CloudDrive-Sync; if that does not help, create a support bundle. Your files are not affected."),
        ["CD-4603"] = new("Verbindung zum Ordner unterbrochen", "Dateien, die nur online liegen, lassen sich gerade nicht öffnen. CloudDrive-Sync verbindet den Ordner erneut; Dateien auf diesem PC funktionieren weiter.",
            "Connection to the folder lost", "Files that are online only cannot be opened right now. CloudDrive-Sync connects the folder again; files on this PC keep working."),
        ["CD-4604"] = new("Datei konnte nicht geladen werden", "Die Datei liegt nur online und konnte nicht vom Server geholt werden – vielleicht wurde sie dort gerade geändert. Prüfe die Internetverbindung und öffne sie gleich noch einmal.",
            "File could not be fetched", "The file is online only and could not be fetched from the server - maybe it was changed there just now. Check the internet connection and open it again in a moment."),
        ["CD-4605"] = new("Platzhalter konnte nicht geändert werden", "Eine Datei konnte im Ordner nicht angelegt oder aktualisiert werden, zum Beispiel weil ein Programm sie gerade benutzt. CloudDrive-Sync versucht es beim nächsten Abgleich erneut; es geht nichts verloren.",
            "Placeholder could not be changed", "A file could not be created or updated in the folder, for example because a program is using it right now. CloudDrive-Sync tries again at the next synchronisation; nothing is lost."),
        ["CD-4606"] = new("Nicht genug Platz für alle Dateien", "Für „Alle Dateien auf diesem PC“ müssen alle Dateien heruntergeladen werden, dafür ist auf dem Laufwerk zu wenig frei. Schaffe Platz oder bleib bei „Dateien bei Bedarf“.",
            "Not enough space for all files", "\"All files on this PC\" needs every file downloaded, and the drive has too little free space for that. Make room or stay with files on demand."),
        ["CD-4608"] = new("Noch nicht alles in der Cloud", "Bevor Dateien vom PC entfernt werden, muss alles hochgeladen sein – beim letzten Abgleich ist aber etwas schiefgegangen. Die Synchronisation bleibt bestehen; sieh dir den Hinweis auf ihrer Karte an und versuche es danach noch einmal.",
            "Not everything in the cloud yet", "Before files leave the PC, everything must be uploaded - but something went wrong in the last run. The synchronisation stays; look at the notice on its card and try again afterwards."),
        ["CD-4607"] = new("Umstellen nicht möglich", "Vor dem Umstellen müssen beide Seiten abgeglichen sein, und dabei ist etwas schiefgegangen. Die Synchronisation bleibt, wie sie war; sieh dir den Hinweis auf ihrer Karte an und versuche es danach noch einmal.",
            "Switching not possible", "Both sides must be in step before switching, and something went wrong there. The synchronisation stays as it was; look at the notice on its card and try again afterwards."),
        ["CD-5001"] = new("Keine Verbindung zum Server", "Prüfe die Internetverbindung. CloudDrive-Sync versucht es automatisch erneut.",
            "No connection to the server", "Check the internet connection. CloudDrive-Sync tries again automatically."),
        ["CD-5002"] = new("Die Engine startet nicht", "rclone konnte nicht gestartet werden. Starte CloudDrive-Sync neu; hilft das nicht, erstelle ein Support-Paket.",
            "The engine does not start", "rclone could not be started. Restart CloudDrive-Sync; if that does not help, create a support bundle."),
        ["CD-5003"] = new("Die Engine antwortet nicht", "CloudDrive-Sync startet die Engine neu.",
            "The engine does not answer", "CloudDrive-Sync restarts the engine."),
        ["CD-9000"] = new("Unerwarteter Fehler", "Versuche es erneut. Hilft das nicht, erstelle ein Support-Paket.",
            "Unexpected error", "Try again. If that does not help, create a support bundle."),
    };

    // First match wins: the more specific patterns come first.
    private static readonly (string Code, Regex Pattern)[] Patterns =
    [
        ("CD-4502", new Regex(@"(?i)too many deletes", RegexOptions.Compiled)),
        ("CD-4509", new Regex(@"(?i)Safety abort: all files were changed", RegexOptions.Compiled)),
        ("CD-4503", new Regex(@"(?i)check file check failed", RegexOptions.Compiled)),
        ("CD-4508", new Regex(@"(?i)prior lock file found", RegexOptions.Compiled)),
        ("CD-4504", new Regex(@"(?i)must run --resync|cannot find prior path1 or path2 listings|out of sync, run --resync", RegexOptions.Compiled)),
        ("CD-4510", new Regex(@"(?i)being used by another process|cannot access the file because", RegexOptions.Compiled)),
        ("CD-3012", new Regex(@"(?im):\s*401 Unauthorized\s*$|NotAuthenticated|Username or password was incorrect", RegexOptions.Compiled)),
        ("CD-3006", new Regex(@"(?i)507 Insufficient Storage|quota (?:limit )?(?:reached|exceeded)|insufficient storage", RegexOptions.Compiled)),
        // IServ answers a write into a folder without the right to write with "Failed to write file … 500".
        ("CD-4511", new Regex(@"(?i)Failed to write file|403 Forbidden|read[- ]only file system", RegexOptions.Compiled)),
        ("CD-5001", new Regex(@"(?i)no such host|dial tcp|i/o timeout|TLS handshake timeout|network is unreachable|No connection could be made|connection reset", RegexOptions.Compiled)),
    ];

    public static ErrorEntry Get(string code)
    {
        if (!Entries.TryGetValue(code, out var texts)) texts = Entries["CD-9000"];
        var german = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "en";
        return german
            ? new ErrorEntry(code, texts.TitleDe, texts.FixDe, texts.NeedsSignIn)
            : new ErrorEntry(code, texts.TitleEn, texts.FixEn, texts.NeedsSignIn);
    }

    public static IReadOnlyCollection<string> Codes => Entries.Keys;

    /// <summary>The code an error text stands for, or <paramref name="fallback"/>.</summary>
    public static string Classify(string? text, string fallback = "CD-9000")
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        foreach (var (code, pattern) in Patterns)
            if (pattern.IsMatch(text)) return code;
        return fallback;
    }
}
