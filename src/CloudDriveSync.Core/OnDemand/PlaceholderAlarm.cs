using System.Globalization;
using System.Reflection;
using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// The safety catch for new placeholders. Should Windows ever write one broken although CloudDrive-Sync hands them over
/// as it must (see <c>Placeholders.Create</c>), no more are made in this synchronisation: every further one could stay
/// behind for good, removable only in Safe Mode. Everything else goes on; nothing is deleted. The file
/// sync\&lt;id&gt;\placeholders-stopped.txt names the version that stopped - a later version, which knows more, tries again.
/// </summary>
internal static class PlaceholderAlarm
{
    public static string FileOf(string pairFolder) => Path.Combine(pairFolder, "placeholders-stopped.txt");

    /// <summary>This version of CloudDrive-Sync, as the alarm notes it.</summary>
    public static string Version { get; } =
        typeof(PlaceholderAlarm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>Whether this version stopped making placeholders in this synchronisation.</summary>
    public static bool IsRaised(string pairFolder)
    {
        try
        {
            var file = FileOf(pairFolder);
            return File.Exists(file) && File.ReadLines(file).FirstOrDefault() == Version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable: rather stopped than more broken ones.
            Log.Warn("OnDemand", $"Placeholder alarm not read: {e.Message}");
            return true;
        }
    }

    /// <summary>Stops making placeholders in this synchronisation, for this version (which ones broke is in the log).</summary>
    public static void Raise(string pairFolder, int broken) =>
        File.WriteAllLines(FileOf(pairFolder), [Version, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), broken.ToString(CultureInfo.InvariantCulture)]);
}
