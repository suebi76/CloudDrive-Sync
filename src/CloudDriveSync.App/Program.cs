using CloudDriveSync.App.Infrastructure;
using Velopack;

namespace CloudDriveSync.App;

/// <summary>
/// The start of the program. Velopack (installation and updates) has the first word: while installing, updating or
/// uninstalling, it runs the program briefly with arguments of its own and ends it again.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            // Before the program files go, the entries CloudDrive-Sync made in Windows go, too. Settings and files stay;
            // of synchronisations with files on demand, the files that were only online stay in the cloud.
            .OnBeforeUninstallFastCallback(_ =>
            {
                AppRegistration.Remove();
                AppRegistration.EndFilesOnDemand();
            })
            .Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
