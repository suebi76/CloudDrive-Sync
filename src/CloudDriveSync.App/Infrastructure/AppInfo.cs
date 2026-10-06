using System.Reflection;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>Version, copyright and project address of this program, taken from the project settings (Directory.Build.props).</summary>
internal static class AppInfo
{
    public const string ProjectUrl = "https://github.com/suebi76/CloudDrive-Sync";

    private static readonly Assembly Program = typeof(AppInfo).Assembly;

    /// <summary>For example "0.2.0" - without the commit the build was made from.</summary>
    public static string Version { get; } = (Program.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

    /// <summary>"© 2026 Steffen Schwabe".</summary>
    public static string Copyright { get; } = Program.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
}
