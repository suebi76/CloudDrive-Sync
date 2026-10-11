using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;

namespace CloudDriveSync.Core.Tests;

/// <summary>Registering a folder with Windows - with Windows stood in for, so nothing is registered.</summary>
[SupportedOSPlatform("windows10.0.17763")]
public class SyncRootRegistrationTests
{
    private static readonly SyncRootSpec Spec = new("CloudDriveSyncTest!S-1!test", @"C:\Nirgends", "Test", "icon,0", "1", "test");

    [Fact]
    public void A_registration_Windows_refuses_for_a_moment_is_tried_once_more()
    {
        var before = SyncRoots.RegisterCore;
        try
        {
            var calls = 0;
            // "No more data" while another registration changes at the same moment: the second try goes through.
            SyncRoots.RegisterCore = _ =>
            {
                if (++calls == 1) throw new CloudFileException("Windows Runtime (Register): 0x80070103", unchecked((int)0x80070103));
            };
            SyncRoots.Register(Spec);
            Assert.Equal(2, calls);

            // Anything else is no passing trouble: reported at once, not tried again.
            calls = 0;
            SyncRoots.RegisterCore = _ =>
            {
                calls++;
                throw new CloudFileException("Windows Runtime (Register): 0x80070005", unchecked((int)0x80070005));
            };
            Assert.Throws<CloudFileException>(() => SyncRoots.Register(Spec));
            Assert.Equal(1, calls);

            // Refused twice: reported.
            calls = 0;
            SyncRoots.RegisterCore = _ =>
            {
                calls++;
                throw new CloudFileException("Windows Runtime (Register): 0x80070103", unchecked((int)0x80070103));
            };
            Assert.Throws<CloudFileException>(() => SyncRoots.Register(Spec));
            Assert.Equal(2, calls);
        }
        finally
        {
            SyncRoots.RegisterCore = before;
        }
    }
}
