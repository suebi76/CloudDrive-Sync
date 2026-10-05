using System.Diagnostics;
using System.IO;
using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>Opens folders, files and web pages with Windows.</summary>
internal static class Shell
{
    public static void OpenFolder(string folder)
    {
        if (Directory.Exists(folder)) Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
    }

    public static void OpenFile(string file)
    {
        if (File.Exists(file)) Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    /// <summary>Opens the folder of a file in Explorer with the file selected.</summary>
    public static void ShowInFolder(string file)
    {
        if (File.Exists(file)) Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\""));
        else OpenFolder(Path.GetDirectoryName(file) ?? "");
    }

    /// <summary>Only http(s) addresses are opened - never anything Windows would run.</summary>
    public static void OpenWebPage(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static void Start(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Shell", $"Could not open '{start.FileName}': {e.Message}");
        }
    }
}
