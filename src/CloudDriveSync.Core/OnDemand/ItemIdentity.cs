using System.Globalization;
using System.Text;
using CloudDriveSync.Core.CloudFiles;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// What CloudDrive-Sync stores in every placeholder: the item's ID in the <see cref="ItemStore"/> and its path in the
/// cloud when the placeholder was made ("cds1|42|Ordner/Datei.txt"). Windows hands it back with every request for data
/// and keeps it when the file is renamed or moved on the PC - that is how a moved file is recognised without uploading
/// it again. The path helps only when the store has to be rebuilt; it is left out when it would not fit into the 4 KB
/// Windows allows.
/// </summary>
public static class ItemIdentity
{
    private const string Prefix = "cds1|";

    public static byte[] Encode(long id, string cloudPath)
    {
        var bytes = Encoding.UTF8.GetBytes($"{Prefix}{id.ToString(CultureInfo.InvariantCulture)}|{cloudPath}");
        return bytes.Length <= Placeholders.MaxIdentityLength ? bytes : Encoding.UTF8.GetBytes($"{Prefix}{id.ToString(CultureInfo.InvariantCulture)}|");
    }

    /// <summary>ID and path from an identity, or null when it is not one of CloudDrive-Sync's.</summary>
    public static (long Id, string Path)? Decode(ReadOnlySpan<byte> identity)
    {
        var text = Encoding.UTF8.GetString(identity);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var separator = text.IndexOf('|', Prefix.Length);
        if (separator < 0 || !long.TryParse(text.AsSpan(Prefix.Length, separator - Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        return (id, text[(separator + 1)..]);
    }
}
