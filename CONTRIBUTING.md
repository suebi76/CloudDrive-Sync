# Mitwirken

*English version: [CONTRIBUTING.en.md](CONTRIBUTING.en.md)*

Danke für dein Interesse an CloudDrive-Sync!

- **Fehler und Wünsche** meldest du als [Issue](https://github.com/suebi76/CloudDrive-Sync/issues). Hilfreich sind die
  Version (Einstellungen › Updates), was du getan hast und der Fehlercode `CD-xxxx`.
- **Sicherheitslücken** meldest du bitte nicht öffentlich, sondern vertraulich über GitHub (Reiter *Security* →
  *Report a vulnerability*).

Wie du das Projekt einrichtest, baust, testest und veröffentlichst, steht im
[Entwickler-Handbuch](docs/DEVELOPMENT.md), wie es aufgebaut ist in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Regeln für den Code

Das Ziel: Wer den Code zum ersten Mal liest – ob Autor, Schul-IT oder Entwickler aus einem anderen Land –, soll ihn ohne
Vorwissen verstehen. Die Regeln gelten für neuen und geänderten Code.

### Aufbau

- **App → Core, nie umgekehrt.** Fachlogik gehört in `CloudDriveSync.Core`, nicht in ViewModels oder Views. Die Views
  enthalten nur Fensterverhalten.
- **Eine Klasse, eine Aufgabe**, und der Name sagt sie: `DeleteGuard`, `CaseRenames`, `RunHistory`. Keine
  Sammelklassen wie `Helper`, `Manager` oder `Utils`.
- **Dateien bleiben überschaubar:** Ab etwa 400 Zeilen wird aufgeteilt – in eine eigene Klasse, eine partielle Klasse
  für eine klar abgegrenzte Aufgabe (wie `SyncService.PairWorker.cs`) oder, bei der Oberfläche, eine Seite pro Datei
  (`Views/Pages`).
- Ein Typ pro Datei. Kleine Typen, die nur zu einem anderen gehören (ein Ergebnis-Record, ein Enum), dürfen in dessen
  Datei stehen.

### Namen

- Englisch, ausgeschrieben und sprechend: `pairFolder` statt `pf`, `PlaceSentinelsAsync` statt `Init`. Asynchrone
  Methoden enden auf `Async`.
- Begriffe einheitlich verwenden, wie sie im Code schon vorkommen: *synchronisation* (eine Ordner-Verbindung),
  *run* (ein Abgleich), *rebuild* (Neuaufbau, rclones resync), *sentinel file* (Wächterdatei), *recycle bin*
  (Papierkorb), *Path1* = Cloud, *Path2* = PC.

### Kommentare

- **Englisch**, in ganzen Sätzen, für Leser ohne Vorwissen.
- **Jeder öffentliche Typ** hat ein `/// <summary>`, das sagt, wofür er da ist. Öffentliche Methoden und Eigenschaften
  bekommen eines, wenn der Name nicht alles sagt.
- Kommentare erklären das **Warum** – eine Eigenheit von rclone oder eines Servers, eine Entscheidung, eine Gefahr –,
  nicht das Was, das ohnehin im Code steht.
- Wissen über rclone und die Server gehört zusätzlich in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), damit es nicht
  nur in Kommentaren verstreut ist.
- Kein auskommentierter Code, keine `TODO` ohne Issue.

### Texte der Oberfläche

- **Deutsch**, freundlich, ohne Fachjargon; der Nutzer wird geduzt.
- Meldungen sagen, was passiert ist und was hilft – nicht nur, dass etwas schiefging.
- Jedes Bedienelement ohne sichtbaren Text bekommt `AutomationProperties.Name`, damit Bildschirmleser es vorlesen.

### Fehler

- Erwartbare Fehler sind eine `CdException` mit einem Code aus `ErrorCatalog` – mit Titel und Lösung auf Deutsch und
  Englisch.
- Eine Ausnahme wird nie stillschweigend geschluckt: Entweder ein Kommentar sagt, warum das in Ordnung ist, oder sie
  wird protokolliert.

### Sicherheit und Daten

- **Geheimnisse** (Passwörter, App-Passwörter, Schlüssel) nur in der verschlüsselten rclone-Konfiguration bzw. der
  Windows-Anmeldeinformationsverwaltung – nie in Einstellungen, Protokollen, Tests oder diesem Repository.
- **Keine echten persönlichen Daten** in Code, Tests und Doku: Beispiele wie `example.org`, „Max Mustermann“.
- **Nichts löschen, was der Nutzer nicht eindeutig wollte.** Schutzmechanismen (Wächterdatei, Löschschutz, Papierkorb,
  letzter guter Zustand) werden nur mit Tests geändert.

### Tests

- Jede Änderung braucht Tests. Was mit Dateien, rclone oder dem Server zu tun hat, bekommt zusätzlich einen
  Integrationstest.
- Testnamen sind Sätze, die das Verhalten beschreiben (`Changing_a_file_on_the_PC_without_changing_its_size`).

### Form

- `.editorconfig` gilt, `TreatWarningsAsErrors` ist an: Der Build hat keine Warnungen.
- C#: dateiweite Namensräume, `var`, wo der Typ offensichtlich ist, Nullable-Prüfungen an.
- Kodierung: UTF-8 ohne BOM (PowerShell-Skripte mit BOM), Windows-Zeilenenden – `tools/Format-SourceFiles.ps1` erledigt
  das, `SourceFileTests` prüft es.

## Änderungen einreichen

- Vorher: `dotnet build CloudDriveSync.slnx` ohne Warnungen, `dotnet test CloudDriveSync.slnx` grün.
- Commit-Nachrichten auf Englisch: Die erste Zeile sagt, was sich ändert; darunter, warum und wie.
- Was Nutzer bemerken, bekommt einen Eintrag in `CHANGELOG.md` (englisch, im Stil der vorhandenen).
- Beiträge kommen als Pull Request auf einem eigenen Branch.
