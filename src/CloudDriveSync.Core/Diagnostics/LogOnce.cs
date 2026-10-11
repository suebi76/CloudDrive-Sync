namespace CloudDriveSync.Core.Diagnostics;

/// <summary>
/// For a line that would come again unchanged at every try: a folder a program holds is tried every 2 minutes, and its
/// line filled the log - 720 a day. Such a line is logged once per matter; again only when it changes, or when the
/// matter comes back after it was settled.
/// </summary>
internal sealed class LogOnce
{
    private readonly Dictionary<string, string> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Whether <paramref name="line"/> is news for <paramref name="matter"/> (then it is noted as its last line).</summary>
    public bool IsNew(string matter, string line)
    {
        lock (_gate)
        {
            if (_last.TryGetValue(matter, out var before) && before == line) return false;
            _last[matter] = line;
            return true;
        }
    }

    /// <summary>The matter is settled: should it come back, its line is news again.</summary>
    public void Settled(string matter)
    {
        lock (_gate) _last.Remove(matter);
    }
}
