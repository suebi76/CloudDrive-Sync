using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Accounts;

/// <summary>An entry of a cloud folder.</summary>
public sealed record RemoteEntry(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset? Modified);

/// <summary>Storage use of an account (values the server does not report are null).</summary>
public sealed record Quota(long? Used, long? Total, long? Free);

/// <summary>
/// Cloud accounts: adding (with a check that the server accepts the sign-in), signing in again, removing, and
/// looking into their folders. Each account is an rclone remote "cd-&lt;id&gt;" in the encrypted configuration.
/// </summary>
public sealed partial class AccountService
{
    private readonly SettingsStore _settings;
    private readonly RcloneEngine _engine;

    public AccountService(SettingsStore settings, RcloneEngine engine)
    {
        _settings = settings;
        _engine = engine;
    }

    public static string RemoteName(string accountId) => $"cd-{accountId}";

    private static string SignInRemoteName(string accountId) => $"cd-signin-{accountId}";

    public IReadOnlyList<AccountSettings> Accounts => _settings.Current.Accounts;

    public AccountSettings? Find(string accountId) => _settings.Current.Accounts.FirstOrDefault(a => a.Id == accountId);

    /// <summary>Adds a WebDAV account; refuses the same cloud account a second time (CD-3010).</summary>
    public async Task<AccountSettings> AddWebDavAsync(string label, WebDavCredential credential, CancellationToken cancellationToken = default)
    {
        var identity = credential.Identity;
        var duplicate = _settings.Current.Accounts.FirstOrDefault(a => a.Identity?.Id == identity.Id);
        if (duplicate is not null) throw new CdException("CD-3010", $"{identity.Name} = {duplicate.Label}");

        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var id = UniqueId(label, _settings.Current.Accounts.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var remote = RemoteName(id);
        await RemoveRemoteIfPresentAsync(rc, remote);
        await CreateWebDavRemoteAsync(rc, remote, credential, cancellationToken);

        var account = new AccountSettings
        {
            Id = id,
            Provider = "webdav",
            Kind = credential.Kind,
            Label = string.IsNullOrWhiteSpace(label) ? identity.Name : label.Trim(),
            Identity = identity,
            Added = DateTimeOffset.Now,
        };
        _settings.Update(s => s.Accounts.Add(account));
        Log.Info("Accounts", $"Account '{id}' ({credential.Kind}) added.");
        return account;
    }

    /// <summary>
    /// Signs an account in again, e.g. after a password change. The new sign-in is checked on a temporary remote
    /// first; the account only changes when it works and belongs to the same cloud account (otherwise CD-3009).
    /// </summary>
    public async Task ReloginAsync(string accountId, WebDavCredential credential, CancellationToken cancellationToken = default)
    {
        var account = Find(accountId) ?? throw new CdException("CD-9000", $"unknown account '{accountId}'");
        if (account.Identity is { } expected && expected.Id != credential.Identity.Id)
            throw new CdException("CD-3009", $"{credential.Identity.Name} instead of {expected.Name}");

        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var signIn = SignInRemoteName(accountId);
        var remote = RemoteName(accountId);
        await RemoveRemoteIfPresentAsync(rc, signIn);
        await CreateWebDavRemoteAsync(rc, signIn, credential, cancellationToken);
        try
        {
            // The password is taken over obscured, as rclone stored it on the temporary remote.
            var fresh = await rc.CallAsync("config/get", new JsonObject { ["name"] = signIn }, cancellationToken: cancellationToken);
            var values = new JsonObject();
            foreach (var key in new[] { "url", "vendor", "user", "pass" })
                values[key] = fresh[key]?.GetValue<string>() ?? "";
            var opt = new JsonObject { ["nonInteractive"] = true, ["noObscure"] = true };
            if ((await RemoteNamesAsync(rc)).Contains(remote))
                await rc.CallAsync("config/update", new JsonObject { ["name"] = remote, ["parameters"] = values, ["opt"] = opt }, cancellationToken: cancellationToken);
            else
                await rc.CallAsync("config/create", new JsonObject { ["name"] = remote, ["type"] = "webdav", ["parameters"] = values, ["opt"] = opt }, cancellationToken: cancellationToken);
        }
        finally
        {
            await RemoveRemoteIfPresentAsync(rc, signIn);
        }
        // Drop connections that still use the old password.
        await rc.CallAsync("fscache/clear", cancellationToken: cancellationToken);
        _settings.Update(s =>
        {
            var entry = s.Accounts.First(a => a.Id == accountId);
            entry.Identity = credential.Identity;
        });
        Log.Info("Accounts", $"Account '{accountId}' signed in again.");
    }

    /// <summary>WebDAV address and user name of an account (for signing in again); the password stays in rclone.</summary>
    public async Task<(string Url, string User)> GetSignInAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        if (!(await RemoteNamesAsync(rc)).Contains(RemoteName(accountId))) return ("", "");
        var config = await rc.CallAsync("config/get", new JsonObject { ["name"] = RemoteName(accountId) }, cancellationToken: cancellationToken);
        return (config["url"]?.GetValue<string>() ?? "", config["user"]?.GetValue<string>() ?? "");
    }

    /// <summary>Removes an account and its stored sign-in. Its synchronisations must be removed before.</summary>
    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        await RemoveRemoteIfPresentAsync(rc, RemoteName(accountId));
        await RemoveRemoteIfPresentAsync(rc, SignInRemoteName(accountId));
        _settings.Update(s => s.Accounts.RemoveAll(a => a.Id == accountId));
        Log.Info("Accounts", $"Account '{accountId}' removed.");
    }

    /// <summary>Folders (and optionally files) directly inside a cloud folder, folders first.</summary>
    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string accountId, string path, bool includeFiles, CancellationToken cancellationToken = default)
    {
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var opt = new JsonObject { ["recurse"] = false, ["noMimeType"] = true };
        if (!includeFiles) opt["dirsOnly"] = true;
        var result = await rc.CallAsync("operations/list",
            new JsonObject { ["fs"] = RemoteName(accountId) + ":", ["remote"] = path.Trim('/'), ["opt"] = opt }, TimeSpan.FromSeconds(60), cancellationToken);
        var entries = new List<RemoteEntry>();
        foreach (var item in result["list"] as JsonArray ?? [])
        {
            if (item is not JsonObject entry) continue;
            DateTimeOffset? modified = DateTimeOffset.TryParse(entry["ModTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
            entries.Add(new RemoteEntry(
                entry["Name"]?.GetValue<string>() ?? "",
                entry["Path"]?.GetValue<string>() ?? "",
                entry["IsDir"]?.GetValue<bool>() == true,
                entry["Size"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? bytes : 0,
                modified));
        }
        return entries
            .Where(e => !e.Name.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Size and number of files of a cloud folder (all levels).</summary>
    public async Task<(long Bytes, long Count)> GetSizeAsync(string accountId, string path, CancellationToken cancellationToken = default)
    {
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var result = await rc.CallAsync("operations/size", new JsonObject { ["fs"] = $"{RemoteName(accountId)}:{path.Trim('/')}" }, TimeSpan.FromMinutes(5), cancellationToken);
        return (Number(result, "bytes") ?? 0, Number(result, "count") ?? 0);
    }

    public async Task<Quota> GetQuotaAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var rc = await _engine.EnsureRunningAsync(cancellationToken);
        var result = await rc.CallAsync("operations/about", new JsonObject { ["fs"] = RemoteName(accountId) + ":" }, TimeSpan.FromSeconds(20), cancellationToken);
        return new Quota(Number(result, "used"), Number(result, "total"), Number(result, "free"));
    }

    /// <summary>Creates a WebDAV remote and checks with one request that the server accepts the sign-in.</summary>
    internal static async Task CreateWebDavRemoteAsync(RcClient rc, string remote, WebDavCredential credential, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["url"] = credential.Url,
            ["vendor"] = credential.Vendor,
            ["user"] = credential.User,
            ["pass"] = credential.Password,
        };
        // rclone obscures the password before it stores it in the encrypted configuration.
        await rc.CallAsync("config/create", new JsonObject
        {
            ["name"] = remote,
            ["type"] = "webdav",
            ["parameters"] = parameters,
            ["opt"] = new JsonObject { ["nonInteractive"] = true, ["obscure"] = true },
        }, cancellationToken: cancellationToken);
        CdException? failure = null;
        try
        {
            await rc.CallAsync("operations/about", new JsonObject { ["fs"] = remote + ":" }, TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (CdException e)
        {
            failure = e;
        }
        if (failure is not null && !UnauthorizedPattern().IsMatch(failure.Detail ?? "") && !NotFoundPattern().IsMatch(failure.Detail ?? ""))
        {
            // Some servers do not report their storage; a look into the top folder proves the sign-in as well.
            try
            {
                await rc.CallAsync("operations/list", new JsonObject
                {
                    ["fs"] = remote + ":",
                    ["remote"] = "",
                    ["opt"] = new JsonObject { ["dirsOnly"] = true, ["noModTime"] = true, ["noMimeType"] = true },
                }, TimeSpan.FromSeconds(30), cancellationToken);
                Log.Info("Accounts", $"{new Uri(credential.Url).Host} reports no storage use ({failure.Code}); the folder listing works.");
                failure = null;
            }
            catch (CdException e)
            {
                failure = e;
            }
        }
        if (failure is not null)
        {
            await RemoveRemoteIfPresentAsync(rc, remote);
            var detail = failure.Detail ?? "";
            Log.Warn("Accounts", $"Sign-in at {new Uri(credential.Url).Host} refused: {failure.Code} {detail}");
            if (UnauthorizedPattern().IsMatch(detail)) throw new CdException("CD-3012", detail);
            if (NotFoundPattern().IsMatch(detail)) throw new CdException("CD-3013", detail);
            throw failure;
        }
        Log.Info("Accounts", $"Signed in at {new Uri(credential.Url).Host}.");
    }

    internal static async Task<HashSet<string>> RemoteNamesAsync(RcClient rc)
    {
        var result = await rc.CallAsync("config/listremotes");
        return (result["remotes"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static async Task RemoveRemoteIfPresentAsync(RcClient rc, string name)
    {
        try
        {
            if ((await RemoteNamesAsync(rc)).Contains(name)) await rc.CallAsync("config/delete", new JsonObject { ["name"] = name });
        }
        catch (CdException e)
        {
            Log.Warn("Accounts", $"Could not remove remote '{name}': {e.Detail}");
        }
    }

    /// <summary>A short, readable, unique ID from the label ("IServ Schule" → "iserv-schule", then "-2" …).</summary>
    internal static string UniqueId(string label, ISet<string> existing)
    {
        var text = label.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var builder = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(character);
            else if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var baseId = builder.ToString().Trim('-');
        if (baseId.Length == 0) baseId = "konto";
        if (baseId.Length > 32) baseId = baseId[..32].TrimEnd('-');
        var id = baseId;
        for (var number = 2; existing.Contains(id); number++) id = $"{baseId}-{number}";
        return id;
    }

    private static long? Number(JsonObject result, string name) =>
        result[name] is JsonValue value && value.TryGetValue<double>(out var number) ? (long)number : null;

    [GeneratedRegex("401 Unauthorized|NotAuthenticated")]
    private static partial Regex UnauthorizedPattern();

    [GeneratedRegex(@"404 Not Found|405 Method Not Allowed|\b30[1278]\b")]
    private static partial Regex NotFoundPattern();
}
