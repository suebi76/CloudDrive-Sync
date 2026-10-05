namespace CloudDriveSync.App.Infrastructure;

/// <summary>
/// Only one CloudDrive-Sync per user and data folder. A second start asks the first one to show its window and ends.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
        _registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public event EventHandler? ShowRequested;

    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}-instance", out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}-show"));
    }

    public static void ShowRunning(string name)
    {
        if (EventWaitHandle.TryOpenExisting($@"Local\{name}-show", out var show))
        {
            using (show) show.Set();
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _show.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released on another thread already; the handle goes away anyway.
        }
        _mutex.Dispose();
    }
}
