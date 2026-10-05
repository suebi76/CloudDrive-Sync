using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Accounts;

/// <summary>
/// What a WebDAV remote needs. The password stays in memory only until it goes to rclone, which keeps it obscured in
/// the encrypted configuration; it is never logged and never written to the settings.
/// </summary>
public sealed class WebDavCredential
{
    public required string Url { get; init; }
    public required WebDavKind Kind { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }

    /// <summary>rclone knows Nextcloud's extensions (modification times, checksums, uploads in parts).</summary>
    public string Vendor => Kind == WebDavKind.Nextcloud ? "nextcloud" : "other";

    /// <summary>Identity of the cloud account: the user at this WebDAV address.</summary>
    public AccountIdentity Identity
    {
        get
        {
            var uri = new Uri(Url);
            return new AccountIdentity
            {
                Id = $"{User}@{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}".ToLowerInvariant(),
                Name = $"{User} @ {uri.Host}",
            };
        }
    }

    public override string ToString() => $"{User} @ {Url}";
}
