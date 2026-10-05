using System.Text.RegularExpressions;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Accounts;

/// <summary>
/// Addresses of a WebDAV account: <see cref="Url"/> is the WebDAV address (for Nextcloud known only after the
/// sign-in, as it contains the user ID), <see cref="Server"/> the server itself.
/// </summary>
public sealed record WebDavAddress(string Url, string Server, string HostName, bool Insecure);

public static partial class WebDavAddresses
{
    /// <summary>
    /// Turns what the user typed into addresses: for Nextcloud the server (also from its WebDAV or browser address),
    /// for IServ the WebDAV address of the school server (webdav.&lt;school address&gt;), otherwise the WebDAV address as
    /// typed. Unencrypted http is refused with CD-3015 unless <paramref name="allowInsecure"/> (on this computer it
    /// is always allowed).
    /// </summary>
    public static WebDavAddress Parse(WebDavKind kind, string input, bool allowInsecure = false)
    {
        var text = input.Trim();
        if (text.Length > 0 && !SchemePattern().IsMatch(text)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new CdException("CD-3013", $"not an address: '{input}'");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new CdException("CD-3013", $"not a web address: '{input}'");
        var insecure = uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;
        if (insecure && !allowInsecure) throw new CdException("CD-3015", input);

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath;
        switch (kind)
        {
            case WebDavKind.Nextcloud:
            {
                // Nextcloud may live in a folder; its pages and WebDAV addresses start below that folder. A WebDAV
                // address with the user ID (from the Nextcloud file settings) is taken over as it is.
                var url = "";
                var dav = NextcloudDavPattern().Match(path);
                if (dav.Success) url = $"{origin}{dav.Groups["folder"].Value}/remote.php/dav/files/{dav.Groups["user"].Value}";
                var folder = NextcloudPagePattern().Replace(path, "").TrimEnd('/');
                return new WebDavAddress(url, origin + folder, uri.Host, insecure);
            }
            case WebDavKind.IServ:
            {
                // IServ serves WebDAV on its own sub domain; https://<school>/webdav is the documented alternative.
                string url;
                if (IServPathPattern().IsMatch(path)) url = origin + "/webdav/";
                else if (uri.Host.StartsWith("webdav.", StringComparison.OrdinalIgnoreCase)) url = origin + "/";
                else url = $"{uri.Scheme}://webdav.{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/";
                var webdav = new Uri(url);
                return new WebDavAddress(url, webdav.GetLeftPart(UriPartial.Authority), webdav.Host, insecure);
            }
            default:
                return new WebDavAddress(uri.GetLeftPart(UriPartial.Path), origin, uri.Host, insecure);
        }
    }

    /// <summary>The server address of a Nextcloud WebDAV address (https://host[/folder]/remote.php/dav/files/user).</summary>
    public static string NextcloudServerOf(string url)
    {
        var match = NextcloudServerPattern().Match(url);
        if (match.Success) return match.Groups["server"].Value;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "";
    }

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9+.-]*://")]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^(?<folder>.*?)/remote\.php/dav/files/(?<user>[^/]+)")]
    private static partial Regex NextcloudDavPattern();

    [GeneratedRegex(@"/(remote\.php|index\.php|apps|login|settings|s)(/.*)?$")]
    private static partial Regex NextcloudPagePattern();

    [GeneratedRegex(@"^/webdav(/|$)")]
    private static partial Regex IServPathPattern();

    [GeneratedRegex(@"^(?<server>https?://[^/]+(/.*?)?)/(remote|index)\.php(/|$)")]
    private static partial Regex NextcloudServerPattern();
}
