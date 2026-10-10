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
            // Before the program files go, everything else of CloudDrive-Sync goes, too: its entries in Windows, the
            // registrations of files on demand (the files that were only online stay in the cloud), settings, sign-ins,
            // logs and rclone. The synchronised folders and their files stay.
            .OnBeforeUninstallFastCallback(_ =>
            {
                AppRegistration.Remove();
                AppRegistration.EndFilesOnDemand();
                AppRegistration.RemoveData();
            })
            .Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
