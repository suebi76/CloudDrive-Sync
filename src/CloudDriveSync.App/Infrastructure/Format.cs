using System.Globalization;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>Numbers, sizes and times the way people read them (German).</summary>
internal static class Format
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Bytes(long bytes)
    {
        string[] units = ["Bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value.ToString("N0", German)} {units[0]}" : $"{value.ToString(value < 10 ? "N1" : "N0", German)} {units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Number(long value) => value.ToString("N0", German);

    public static string Count(long value, string one, string many) => $"{Number(value)} {(value == 1 ? one : many)}";

    /// <summary>"gerade eben", "vor 5 Min.", "heute, 14:05", "gestern, 08:12", "03.10.2026, 17:40".</summary>
    public static string Ago(DateTimeOffset? time)
    {
        if (time is null) return "noch nie";
        var local = time.Value.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age < TimeSpan.FromMinutes(1)) return "gerade eben";
        if (age < TimeSpan.FromMinutes(60)) return $"vor {(int)age.TotalMinutes} Min.";
        if (local.Date == DateTime.Today) return $"heute, {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"gestern, {local:HH:mm}";
        return local.ToString("dd.MM.yyyy, HH:mm", German);
    }

    public static string Time(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        if (local.Date == DateTime.Today) return local.ToString("HH:mm", German);
        if (local.Date == DateTime.Today.AddDays(-1)) return $"gestern, {local:HH:mm}";
        return local.ToString("dd.MM., HH:mm", German);
    }

    public static string Duration(TimeSpan duration) =>
        duration < TimeSpan.FromSeconds(1) ? "< 1 s"
        : duration < TimeSpan.FromMinutes(1) ? $"{(int)duration.TotalSeconds} s"
        : duration < TimeSpan.FromHours(1) ? $"{(int)duration.TotalMinutes} min {duration.Seconds} s"
        : $"{(int)duration.TotalHours} h {duration.Minutes} min";
}
