using System.Collections.ObjectModel;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudDriveSync.App.ViewModels;

/// <summary>
/// "Only chosen folders and files": the content of a cloud folder as a tree with a tick per entry. Folders load when
/// they are opened, ticks start as they were saved, and <see cref="Collect"/> turns the ticks into the
/// synchronisation's include list (folders end with "/").
/// </summary>
public sealed partial class SelectionTree(CloudDriveSyncHost host) : ObservableObject
{
    private readonly List<string> _saved = [];
    private string _accountId = "";
    private string _basePath = "";

    public ObservableCollection<FolderNode> Roots { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>The server could not be read; the saved ticks then stay as they are.</summary>
    [ObservableProperty] public partial bool LoadFailed { get; set; }

    /// <summary>
    /// Reads the first level of <paramref name="basePath"/>; <paramref name="saved"/> are the ticks so far.
    /// Throws <see cref="CdException"/> when the server cannot be read.
    /// </summary>
    public async Task LoadAsync(string accountId, string basePath, IEnumerable<string> saved)
    {
        _accountId = accountId;
        _basePath = basePath;
        _saved.Clear();
        _saved.AddRange(saved);
        Roots.Clear();
        LoadFailed = false;
        IsLoading = true;
        try
        {
            var entries = await host.Accounts.ListAsync(accountId, basePath, includeFiles: true);
            foreach (var entry in entries) Roots.Add(Node(entry, null));
        }
        catch (CdException)
        {
            LoadFailed = true;
            throw;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The ticked entries, relative to the cloud folder. Parts not loaded yet keep their saved ticks.</summary>
    public List<string> Collect()
    {
        if (LoadFailed) return [.. _saved];
        var result = new List<string>();
        void Walk(IEnumerable<FolderNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder) continue;
                var relative = Relative(node.Path);
                if (node.IsChecked == true) result.Add(node.IsDirectory ? relative + "/" : relative);
                else if (node.IsChecked is null)
                {
                    if (node.IsLoaded) Walk(node.Children);
                    else result.AddRange(_saved.Where(i => i.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        Walk(Roots);
        return result;
    }

    private async Task<IReadOnlyList<FolderNode>> LoadChildrenAsync(FolderNode parent)
    {
        var entries = await host.Accounts.ListAsync(_accountId, parent.Path, includeFiles: true);
        return entries.Select(e => Node(e, parent)).ToList();
    }

    private FolderNode Node(RemoteEntry entry, FolderNode? parent) =>
        new(entry.Name, entry.Path, entry.IsDirectory, entry.Size, parent, entry.IsDirectory ? LoadChildrenAsync : null, InitialCheck(entry, parent));

    /// <summary>Ticks as they were saved; inside a ticked folder everything is ticked.</summary>
    private bool? InitialCheck(RemoteEntry entry, FolderNode? parent)
    {
        if (parent?.IsChecked is bool inherited) return inherited;
        var relative = Relative(entry.Path);
        if (!entry.IsDirectory) return _saved.Contains(relative, StringComparer.OrdinalIgnoreCase);
        if (_saved.Contains(relative + "/", StringComparer.OrdinalIgnoreCase)) return true;
        return _saved.Any(i => i.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)) ? null : false;
    }

    private string Relative(string path) =>
        _basePath.Length == 0 ? path : path.StartsWith(_basePath + "/", StringComparison.Ordinal) ? path[(_basePath.Length + 1)..] : path;
}
