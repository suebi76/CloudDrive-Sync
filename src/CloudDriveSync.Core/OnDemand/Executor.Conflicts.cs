using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.OnDemand;

internal sealed partial class Executor
{
    /// <summary>
    /// A file changed on both sides (or different on both sides without a common past). Both versions are kept, as in
    /// classic synchronisations: the version that wins keeps the name, the other one is kept beside it as
    /// "Name.Konflikt-PC1.ext" or "Name.Konflikt-Cloud1.ext" - for "keep both" both are renamed. The cloud's version is
    /// renamed on the server (nothing is downloaded), the PC's version is uploaded under its new name. Where the server
    /// has a checksum and the contents are the same after all, nothing needs to be kept twice.
    /// </summary>
    private async Task ResolveAsync(Conflict conflict)
    {
        var local = conflict.Local;
        var cloud = conflict.Cloud;
        var full = LocalFull(local.Path);
        if (!local.OnDisk)
        {
            Log.Warn("OnDemand", $"'{_pair.Id}': conflict at '{conflict.Path}' left for later: the PC's version is not on the PC.");
            return;
        }
        if (cloud.Hash is not null && LocalHashMatches(full, cloud.Hash))
        {
            var version = Placeholders.ReadVersion(full);
            var id = _store.Upsert(new SyncItem(conflict.Item?.Id ?? 0, conflict.Path, false, cloud.Size, cloud.Ticks, cloud.Hash, version.Size, version.LastWriteTicks));
            FinishUpload(local, id, version);
            return;
        }

        var pcKeepsName = PcKeepsName(local, cloud);
        Log.Info("OnDemand", $"'{_pair.Id}': conflict at '{conflict.Path}' - {(pcKeepsName switch { true => "the PC's version keeps the name", false => "the cloud's version keeps the name", null => "both versions are renamed" })}.");
        if (pcKeepsName != true)
        {
            // The PC's version goes beside it: a normal file under its new name, uploaded as a new file.
            var name = ConflictName(conflict.Path, "PC");
            if (local.Placeholder is not null) Placeholders.Revert(full);
            File.Move(full, LocalFull(name));
            await UploadAsync(new LocalEntry(name, false, local.Size, local.Ticks, null), null);
        }
        if (pcKeepsName != false)
        {
            // The cloud's version goes beside it: renamed on the server, it appears on the PC as a placeholder.
            var name = ConflictName(conflict.Path, "Cloud");
            await _rc.CallAsync("operations/movefile", new JsonObject
            {
                ["srcFs"] = _cloudFs,
                ["srcRemote"] = conflict.Path,
                ["dstFs"] = _cloudFs,
                ["dstRemote"] = name,
            }, TimeSpan.FromMinutes(5), _cancel);
            await CreatePlaceholdersAsync([cloud with { Path = name }]);
        }
        if (pcKeepsName == true)
            await UploadAsync(local, conflict.Item);
        else if (pcKeepsName == false)
            await CreatePlaceholdersAsync([cloud]);
        else if (conflict.Item is not null)
            _store.Remove(conflict.Item.Id);
    }

    /// <summary>
    /// Whether the PC's version keeps the name (true), the cloud's (false) or neither (null: both are renamed). "The
    /// newer one" needs times of the server's own: on servers without them (IServ, most WebDAV servers) both are kept.
    /// </summary>
    private bool? PcKeepsName(LocalEntry local, CloudEntry cloud) => _pair.Conflicts switch
    {
        ConflictPolicy.PcWins => true,
        ConflictPolicy.CloudWins => false,
        ConflictPolicy.NewerWins when _account.Kind == WebDavKind.Nextcloud => local.Ticks >= cloud.Ticks,
        _ => null,
    };

    /// <summary>"Ordner/Plan.Konflikt-PC1.txt" - the first number free on both sides and in this run.</summary>
    private string ConflictName(string path, string side)
    {
        var folder = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        var name = path[folder.Length..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var number = 1; ; number++)
        {
            var candidate = $"{folder}{stem}.Konflikt-{side}{number}{extension}";
            if (_takenNames.Contains(candidate) || Path.Exists(LocalFull(candidate))) continue;
            _takenNames.Add(candidate);
            return candidate;
        }
    }

    /// <summary>Whether a file on the PC has the checksum the server reported ("sha1:…" or "md5:…").</summary>
    private static bool LocalHashMatches(string file, string hash)
    {
        var separator = hash.IndexOf(':');
        var type = hash[..separator];
        if (type is not ("sha1" or "md5")) return false;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Only compared with the server's checksum; nothing depends on these hashes for security.
        var bytes = type == "sha1" ? SHA1.HashData(stream) : MD5.HashData(stream);
        return Convert.ToHexString(bytes).Equals(hash[(separator + 1)..], StringComparison.OrdinalIgnoreCase);
    }
}
