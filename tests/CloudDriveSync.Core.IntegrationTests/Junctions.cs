using System.Diagnostics;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>Junctions (links to a folder) as a user makes them - no rights needed, unlike symbolic links.</summary>
internal static class Junctions
{
    public static async Task CreateAsync(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        await mklink.WaitForExitAsync();
        Assert.Equal(0, mklink.ExitCode);
    }
}
