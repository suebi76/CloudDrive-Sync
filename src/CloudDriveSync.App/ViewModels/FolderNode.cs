using System.Collections.ObjectModel;
using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core.Errors;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CloudDriveSync.App.ViewModels;

/// <summary>
/// A folder or file in a cloud tree. Folders load their content when they are opened. In the selection tree a tick
/// on a folder means "everything inside"; a folder with only some ticked entries shows a partial tick.
/// </summary>
public sealed partial class FolderNode : ObservableObject
{
    private readonly Func<FolderNode, Task<IReadOnlyList<FolderNode>>>? _loader;
    private bool _loaded;
    private bool _cascading = true;

    /// <summary>A line that only shows text ("Wird geladen …").</summary>
    private FolderNode(string text)
    {
        Name = text;
        Path = "";
        IsPlaceholder = true;
        _cascading = false;
    }

    public FolderNode(string name, string path, bool isDirectory, long size, FolderNode? parent,
        Func<FolderNode, Task<IReadOnlyList<FolderNode>>>? loader, bool? isChecked = false)
    {
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
        Size = size;
        Parent = parent;
        _loader = loader;
        IsChecked = isChecked;
        if (isDirectory && loader is not null) Children.Add(new FolderNode("Wird geladen …"));
        _cascading = false;
    }

    public string Name { get; }
    /// <summary>Path below the account, "/" separated.</summary>
    public string Path { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public FolderNode? Parent { get; }
    public bool IsPlaceholder { get; }
    public bool IsLoaded => _loaded;
    public string Glyph => IsPlaceholder ? "" : IsDirectory ? Glyphs.Folder : Glyphs.Document;
    public string SizeText => IsDirectory || IsPlaceholder ? "" : Format.Bytes(Size);
    public ObservableCollection<FolderNode> Children { get; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool? IsChecked { get; set; }

    public override string ToString() => Name;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_loaded || _loader is null) return;
        _loaded = true;
        try
        {
            var children = await _loader(this);
            Children.Clear();
            foreach (var child in children) Children.Add(child);
            if (children.Count == 0) Children.Add(new FolderNode(IsDirectory ? "(leer)" : ""));
        }
        catch (CdException e)
        {
            _loaded = false;
            Children.Clear();
            Children.Add(new FolderNode($"Konnte nicht geladen werden: {ErrorCatalog.Get(e.Code).Title}"));
        }
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_cascading) return;
        if (value is bool all)
            foreach (var child in Children.Where(c => !c.IsPlaceholder)) child.SetFromParent(all);
        Parent?.UpdateFromChildren();
    }

    private void SetFromParent(bool value)
    {
        _cascading = true;
        IsChecked = value;
        _cascading = false;
        foreach (var child in Children.Where(c => !c.IsPlaceholder)) child.SetFromParent(value);
    }

    private void UpdateFromChildren()
    {
        var entries = Children.Where(c => !c.IsPlaceholder).ToList();
        if (entries.Count == 0) return;
        bool? state = entries.All(c => c.IsChecked == true) ? true : entries.All(c => c.IsChecked == false) ? false : null;
        _cascading = true;
        IsChecked = state;
        _cascading = false;
        Parent?.UpdateFromChildren();
    }
}
