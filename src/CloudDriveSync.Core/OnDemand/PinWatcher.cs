using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// Carries out "Always keep on this device" and "Free up space". Explorer only sets the pin state of the files and
/// folders the user chose (on a folder: of the folder alone); fetching and freeing are the sync provider's job. The
/// watcher notices the changed attributes at once: a pinned folder passes its state on to everything in it, a pinned
/// file without its data is fetched, a freed file gives its space back - but only when it is in sync, so a change not
/// uploaded yet is never thrown away. What happens while CloudDrive-Sync is not running, each run catches up with
/// (<see cref="Executor.ApplyPinStates"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class PinWatcher : IDisposable
{
    private readonly string _root;
    private readonly string _pairId;
    private readonly FileSystemWatcher _watcher;
    private readonly BlockingCollection<string> _queue = new();
    private readonly ConcurrentDictionary<string, PinState> _foldersDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;

    public PinWatcher(string root, string pairId)
    {
        _root = Path.TrimEndingDirectorySeparator(root);
        _pairId = pairId;
        _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.Attributes, InternalBufferSize = 64 * 1024 };
        _watcher.Changed += (_, e) => Enqueue(e.FullPath);
        _watcher.Error += (_, e) => Log.Warn("OnDemand", $"'{_pairId}': watching pin states overflowed ({e.GetException().Message}); the next run catches up.");
        _worker = new Thread(Work) { IsBackground = true, Name = $"pins {pairId}" };
        _worker.Start();
        _watcher.EnableRaisingEvents = true;
    }

    private void Work()
    {
        try
        {
            foreach (var path in _queue.GetConsumingEnumerable(_stop.Token))
            {
                try
                {
                    Handle(path);
                }
                catch (Exception e)
                {
                    // Never ends the watcher: the next run catches up with what failed here.
                    Log.Warn("OnDemand", $"'{_pairId}': pin state of '{Path.GetFileName(path)}' not carried out: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnected: what is still waiting is caught up with by the next run.
        }
    }

    private void Handle(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        if (relative.StartsWith(".clouddrive", StringComparison.OrdinalIgnoreCase) || !Path.Exists(path)) return;
        var info = Placeholders.Read(path);
        if (info is null) return;
        if (Directory.Exists(path))
        {
            if (info.Pin is not (PinState.Pinned or PinState.Unpinned))
            {
                _foldersDone.TryRemove(path, out _);
                return;
            }
            // Passing the state on changes the attributes of every folder below, too: once per state is enough.
            if (_foldersDone.TryGetValue(path, out var done) && done == info.Pin) return;
            _foldersDone[path] = info.Pin;
            foreach (var folder in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)) _foldersDone[folder] = info.Pin;
            Placeholders.SetPinState(path, info.Pin, recurse: true);
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) Enqueue(file);
            return;
        }
        Apply(path, info, _pairId);
    }

    /// <summary>Fetches a pinned file without its data, frees a freed file that is in sync.</summary>
    internal static void Apply(string path, PlaceholderInfo info, string pairId)
    {
        if (info.Pin == PinState.Pinned && !info.IsFullyOnDisk)
        {
            Placeholders.Hydrate(path);
            Log.Info("OnDemand", $"'{pairId}': '{Path.GetFileName(path)}' kept on this device.");
        }
        else if (info.Pin == PinState.Unpinned && info.OnDiskSize > 0 && info.InSync)
        {
            Placeholders.Dehydrate(path);
            Log.Info("OnDemand", $"'{pairId}': space of '{Path.GetFileName(path)}' freed.");
        }
    }

    private void Enqueue(string path)
    {
        try
        {
            _queue.Add(path);
        }
        catch (InvalidOperationException)
        {
            // Stopping: what comes now is caught up with by the next run.
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _queue.CompleteAdding();
        _stop.Cancel();
        // A file being fetched right now may take longer; the thread ends by itself afterwards.
        _worker.Join(TimeSpan.FromSeconds(5));
    }
}
