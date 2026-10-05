using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.App.ViewModels;

/// <summary>
/// The choices for the options of a synchronisation, shared by the setup assistant, the settings page and the account
/// page, so every place offers the same values with the same words.
/// </summary>
public static class SyncChoices
{
    /// <summary>How often the server is asked for news; the description says what the choice costs.</summary>
    public static IReadOnlyList<Choice<int>> Intervals { get; } =
    [
        new(1, "jede Minute", "Am schnellsten aktuell, aber die meisten Anfragen – nur für kleine Ordner."),
        new(5, "alle 5 Minuten", "Empfohlen: zügig aktuell bei wenig Datenverkehr."),
        new(15, "alle 15 Minuten", "Weniger Anfragen; Neues vom Server kommt etwas später."),
        new(30, "alle 30 Minuten", "Für große Ordner, die sich selten ändern."),
        new(60, "jede Stunde", "Spart Datenverkehr, z. B. bei begrenztem Datenvolumen."),
        new(240, "alle 4 Stunden", "Für Archive, die sich kaum ändern."),
    ];

    public const string IntervalExplanation =
        "So oft fragt CloudDrive-Sync den Server nach Neuem. Jede Abfrage lädt die komplette Dateiliste – bei Ordnern mit " +
        "Tausenden Dateien kommt dabei einiges an Datenverkehr zusammen. Änderungen am PC gehen unabhängig davon nach " +
        "wenigen Sekunden hoch.";

    public static IReadOnlyList<Choice<ConflictPolicy>> Conflicts { get; } =
    [
        new(ConflictPolicy.NewerWins, "Die neuere Fassung behält den Namen (empfohlen)", "Die ältere bleibt als Kopie mit „Konflikt“ im Namen erhalten."),
        new(ConflictPolicy.KeepBoth, "Beide Fassungen umbenennen", "Beide tragen danach „Konflikt“ im Namen – du entscheidest selbst."),
        new(ConflictPolicy.CloudWins, "Die Cloud-Fassung behält den Namen", "Die Fassung vom PC bleibt als Kopie erhalten."),
        new(ConflictPolicy.PcWins, "Die PC-Fassung behält den Namen", "Die Fassung aus der Cloud bleibt als Kopie erhalten."),
    ];

    /// <summary>When the deletion guard stops and asks (share of the files that would be deleted).</summary>
    public static IReadOnlyList<Choice<int>> DeleteLimits { get; } =
    [
        new(10, "mehr als 10 % der Dateien gelöscht würden"), new(25, "mehr als 25 % der Dateien gelöscht würden"),
        new(50, "mehr als die Hälfte der Dateien gelöscht würde (empfohlen)"), new(75, "mehr als 75 % der Dateien gelöscht würden"),
        new(100, "nie – Löschungen immer übernehmen"),
    ];

    public static string IntervalTitle(int minutes) => Intervals.FirstOrDefault(c => c.Value == minutes)?.Title ?? $"alle {minutes} Minuten";

    public static string IntervalHint(int minutes) => Intervals.FirstOrDefault(c => c.Value == minutes)?.Description ?? "";

    public static string ConflictHint(ConflictPolicy policy) => Conflicts.FirstOrDefault(c => c.Value == policy)?.Description ?? "";

    /// <summary>The short form for summaries, e.g. "Konflikte: neuere gewinnt".</summary>
    public static string ConflictTitle(ConflictPolicy policy) => policy switch
    {
        ConflictPolicy.KeepBoth => "Konflikte: beide umbenennen",
        ConflictPolicy.CloudWins => "Konflikte: Cloud gewinnt",
        ConflictPolicy.PcWins => "Konflikte: PC gewinnt",
        _ => "Konflikte: neuere gewinnt",
    };
}
