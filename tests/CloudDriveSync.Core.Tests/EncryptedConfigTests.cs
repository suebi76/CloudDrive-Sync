namespace CloudDriveSync.Core.Tests;

public class EncryptedConfigTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cd2-conf-{Guid.NewGuid():N}.conf");

    public void Dispose() => File.Delete(_file);

    [Theory]
    [InlineData("# Encrypted rclone configuration File\n\nRCLONE_ENCRYPT_V0:\nabc", true)]
    [InlineData("RCLONE_ENCRYPT_V0:\nabc", true)]
    [InlineData("", false)]
    [InlineData("[cd-iserv]\ntype = webdav\n", false)]
    [InlineData("# RCLONE_ENCRYPT_V0: only in a comment\n[x]\n", false)]
    public void Recognises_rclones_encrypted_configuration(string content, bool encrypted)
    {
        File.WriteAllText(_file, content.Replace("\n", "\r\n"));
        Assert.Equal(encrypted, Engine.RcloneEngine.IsEncrypted(_file));
    }
}
