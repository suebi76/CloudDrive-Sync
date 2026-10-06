# Contributing

*Deutsche Fassung: [CONTRIBUTING.md](CONTRIBUTING.md)*

Thank you for your interest in CloudDrive-Sync!

- **Bugs and wishes:** open an [issue](https://github.com/suebi76/CloudDrive-Sync/issues). Helpful are the version
  (Einstellungen › Updates), what you did and the error code `CD-xxxx`.
- **Security vulnerabilities:** please do not report them in public, but confidentially through GitHub (tab *Security*
  → *Report a vulnerability*).

How to set up, build, test and publish the project is in the [developer handbook](docs/DEVELOPMENT.en.md), how it is
built in [docs/ARCHITECTURE.en.md](docs/ARCHITECTURE.en.md).

## Rules for the code

The goal: whoever reads the code for the first time - the author, a school's IT staff or a developer from another
country - understands it without prior knowledge. The rules apply to new and changed code.

### Structure

- **App → Core, never the other way.** Logic belongs in `CloudDriveSync.Core`, not in view models or views. Views hold
  only window behaviour.
- **One class, one job**, and its name says which: `DeleteGuard`, `CaseRenames`, `RunHistory`. No catch-all classes such
  as `Helper`, `Manager` or `Utils`.
- **Files stay manageable:** from about 400 lines on, split - into a class of its own, a partial class for a clearly
  separate job (like `SyncService.PairWorker.cs`) or, for the interface, one page per file (`Views/Pages`).
- One type per file. Small types that belong to just one other type (a result record, an enum) may live in its file.

### Names

- English, written out and telling: `pairFolder`, not `pf`; `PlaceSentinelsAsync`, not `Init`. Asynchronous methods
  end in `Async`.
- Use the terms the code already uses, consistently: *synchronisation* (a folder pair), *run* (one sync), *rebuild*
  (rclone's resync), *sentinel file*, *recycle bin*, *Path1* = cloud, *Path2* = PC.

### Comments

- **English**, in full sentences, for readers without prior knowledge.
- **Every public type** has a `/// <summary>` that says what it is for. Public methods and properties get one when their
  name does not say everything.
- Comments explain the **why** - a quirk of rclone or a server, a decision, a danger - not the what the code shows
  anyway.
- Knowledge about rclone and the servers also belongs in [docs/ARCHITECTURE.en.md](docs/ARCHITECTURE.en.md), so it is not
  only scattered across comments.
- No commented-out code, no `TODO` without an issue.

### User interface texts

- **German**, friendly, without jargon; the user is addressed informally ("du").
- Messages say what happened and what helps - not only that something went wrong.
- Every control without visible text gets `AutomationProperties.Name`, so screen readers can read it out.

### Errors

- Expected errors are a `CdException` with a code from `ErrorCatalog` - with title and fix in German and English.
- An exception is never swallowed silently: either a comment says why that is fine, or it is logged.

### Security and data

- **Secrets** (passwords, app passwords, keys) only in the encrypted rclone configuration or the Windows Credential
  Manager - never in settings, logs, tests or this repository.
- **No real personal data** in code, tests and documentation: use examples such as `example.org` or "Max Mustermann".
- **Never delete what the user did not clearly want.** Safety mechanisms (sentinel file, deletion guard, recycle bin,
  last good state) are only changed together with tests.

### Tests

- Every change needs tests. Anything to do with files, rclone or the server also gets an integration test.
- Test names are sentences describing the behaviour (`Changing_a_file_on_the_PC_without_changing_its_size`).

### Form

- `.editorconfig` applies and `TreatWarningsAsErrors` is on: the build has no warnings.
- C#: file-scoped namespaces, `var` where the type is apparent, nullable checks on.
- Encoding: UTF-8 without BOM (PowerShell scripts with BOM), Windows line ends - `tools/Format-SourceFiles.ps1` takes
  care of it, `SourceFileTests` checks it.

## Submitting changes

- Before: `dotnet build CloudDriveSync.slnx` without warnings, `dotnet test CloudDriveSync.slnx` green (including the
  integration tests, which run only locally).
- The automatic checks on GitHub must be green: build, unit tests and secret scan.
- Commit messages in English: the first line says what changes; below it, why and how.
- Whatever users notice gets an entry in `CHANGELOG.md` (English, in the style of the existing ones).
- Contributions come as a pull request on a branch of their own.
