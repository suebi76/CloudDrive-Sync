using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.IntegrationTests;

/// <summary>The synchronisation service as the program uses it: on its own, with states, history and decisions.</summary>
public class ServiceTests
{
    private sealed class StateLog
    {
        private readonly List<SyncPairState> _states = [];
        private readonly List<(Func<SyncPairState, bool> Match, TaskCompletionSource<SyncPairState> Done)> _waits = [];

        public StateLog(SyncService service) => service.StateChanged += (_, state) =>
        {
            lock (_states)
            {
                _states.Add(state);
                foreach (var wait in _waits.Where(w => w.Match(state)).ToList())
                {
                    wait.Done.TrySetResult(state);
                    _waits.Remove(wait);
                }
            }
        };

        public IReadOnlyList<SyncPairState> All
        {
            get
            {
                lock (_states) return _states.ToList();
            }
        }

        public Task<SyncPairState> WaitAsync(Func<SyncPairState, bool> match)
        {
            var done = new TaskCompletionSource<SyncPairState>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_states) _waits.Add((match, done));
            return done.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
    }

    private static bool Finished(SyncPairState state) => state.Status is SyncStatus.Idle or SyncStatus.Error or SyncStatus.NeedsAttention;

    [Fact]
    public async Task The_service_runs_the_first_synchronisation_by_itself_and_keeps_going_on_request()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("Plan.txt", "Stundenplan");
        world.WritePc("Notizen.txt", "lokal");
        await world.AddAccountAsync();
        var log = new StateLog(world.Host.Sync);
        world.Host.Sync.Start();

        var first = log.WaitAsync(s => Finished(s) && s.LastRun is not null);
        await world.AddPairAsync(p => p.OnLocalChange = false);
        var state = await first;

        Assert.Equal(SyncStatus.Idle, state.Status);
        Assert.True(state.FirstSyncDone);
        Assert.Contains(log.All, s => s.Status == SyncStatus.Syncing && s.Activity.StartsWith("Erster Abgleich", StringComparison.Ordinal));
        Assert.Equal("Stundenplan", world.ReadPc("Plan.txt"));
        Assert.Equal("lokal", world.ReadCloud("Notizen.txt"));
        var history = world.Host.Sync.History(world.Pair.Id);
        Assert.True(Assert.Single(history).Success);
        Assert.Equal("Erster Abgleich", history[0].Kind);

        world.WritePc("Neu.txt", "neu");
        var second = log.WaitAsync(s => Finished(s) && s.LastRun > state.LastRun);
        world.Host.Sync.RunNow(world.Pair.Id);
        Assert.Equal(SyncStatus.Idle, (await second).Status);
        Assert.Equal("neu", world.ReadCloud("Neu.txt"));
        Assert.Equal("Abgleich", world.Host.Sync.History(world.Pair.Id)[0].Kind);
    }

    [Fact]
    public async Task Synchronise_now_does_not_wait_for_the_pause_after_a_restart()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("A.txt", "a");
        await world.AddAccountAsync();
        var log = new StateLog(world.Host.Sync);
        world.Host.Sync.Start();
        var first = log.WaitAsync(s => Finished(s) && s.LastRun is not null);
        await world.AddPairAsync(p => p.OnLocalChange = false);
        var ready = await first;

        // After a restart CloudDrive-Sync waits 15 s before the first run - unless the user asks for it.
        await world.RestartAsync();
        world.WriteCloud("B.txt", "b");
        var restarted = new StateLog(world.Host.Sync);
        world.Host.Sync.Start();
        var asked = DateTime.UtcNow;
        var done = restarted.WaitAsync(s => s.Status == SyncStatus.Idle && s.LastRun > ready.LastRun);
        world.Host.Sync.RunNow(world.Pair.Id);
        await done;

        Assert.True(DateTime.UtcNow - asked < TimeSpan.FromSeconds(10), $"took {DateTime.UtcNow - asked}");
        Assert.Equal("b", world.ReadPc("B.txt"));
    }

    [Fact]
    public async Task A_decision_holds_automatic_runs_back_until_the_user_answers()
    {
        await using var world = await SyncWorld.CreateAsync();
        for (var i = 1; i <= 4; i++) world.WriteCloud($"Datei {i}.txt", $"{i}");
        await world.AddAccountAsync();
        var notices = new List<SyncNotice>();
        world.Host.Sync.Notice += (_, notice) => { lock (notices) notices.Add(notice); };
        bool HasNotice()
        {
            lock (notices) return notices.Any(n => n.Kind == SyncNoticeKind.NeedsAttention);
        }
        var log = new StateLog(world.Host.Sync);
        world.Host.Sync.Start();
        var first = log.WaitAsync(s => Finished(s) && s.LastRun is not null);
        await world.AddPairAsync(p => p.OnLocalChange = false);
        var ready = await first;

        for (var i = 1; i <= 3; i++) File.Delete(world.Pc($"Datei {i}.txt"));
        var stop = log.WaitAsync(s => Finished(s) && s.LastRun > ready.LastRun);
        world.Host.Sync.RunNow(world.Pair.Id);
        var stopped = await stop;

        Assert.Equal(SyncStatus.NeedsAttention, stopped.Status);
        Assert.Equal(SyncDecision.Deletions, stopped.Decision);
        Assert.Equal("CD-4502", stopped.ErrorCode);
        // The notice follows right after the state.
        for (var i = 0; i < 50 && !HasNotice(); i++) await Task.Delay(100);
        Assert.True(HasNotice());

        // Automatic runs (interval, "synchronise all") wait for the decision.
        world.Host.Sync.RunAll();
        await Task.Delay(1500);
        Assert.Equal(SyncStatus.NeedsAttention, world.Host.Sync.GetState(world.Pair.Id)!.Status);

        // The decision survives a restart of the service.
        var saved = PersistedSyncState.Load(Path.Combine(world.Host.Paths.SyncPairDir(world.Pair.Id), "state.json"));
        Assert.Equal(SyncDecision.Deletions, saved.Decision);

        var restore = log.WaitAsync(s => Finished(s) && s.LastRun > stopped.LastRun);
        world.Host.Sync.ResolveDeletions(world.Pair.Id, apply: false);
        var restored = await restore;
        Assert.Equal(SyncStatus.Idle, restored.Status);
        Assert.Equal(SyncDecision.None, restored.Decision);
        Assert.Equal(4, world.PcFiles().Count);
        Assert.Equal("Neuaufbau", world.Host.Sync.History(world.Pair.Id)[0].Kind);
    }

    [Fact]
    public async Task A_changed_selection_rebuilds_by_itself_and_removing_keeps_all_files()
    {
        await using var world = await SyncWorld.CreateAsync();
        world.WriteCloud("A.txt", "a");
        world.WriteCloud("B.bak", "b");
        await world.AddAccountAsync();
        var log = new StateLog(world.Host.Sync);
        world.Host.Sync.Start();
        var first = log.WaitAsync(s => Finished(s) && s.LastRun is not null);
        await world.AddPairAsync(p => p.OnLocalChange = false);
        var ready = await first;

        var rebuild = log.WaitAsync(s => Finished(s) && s.LastRun > ready.LastRun);
        world.Host.Sync.Update(world.Pair.Id, p => p.Selection.Exclude.Add("*.bak"));
        var rebuilt = await rebuild;
        Assert.Equal(SyncStatus.Idle, rebuilt.Status);
        Assert.Equal("Neuaufbau", world.Host.Sync.History(world.Pair.Id)[0].Kind);

        world.Host.Sync.SetPaused(world.Pair.Id, true);
        Assert.Equal(SyncStatus.Paused, world.Host.Sync.GetState(world.Pair.Id)!.Status);
        Assert.True(world.Host.Settings.Current.Syncs[0].Paused);

        await world.Host.Sync.RemoveAsync(world.Pair.Id);
        Assert.Empty(world.Host.Settings.Current.Syncs);
        Assert.Equal(["A.txt", "B.bak"], world.PcFiles());
        Assert.Equal(["A.txt", "B.bak"], world.CloudFiles());
        Assert.False(File.Exists(world.Pc(SyncFilters.SentinelFile)));
        Assert.False(File.Exists(world.Cloud(SyncFilters.SentinelFile)));
        Assert.False(Directory.Exists(world.Host.Paths.SyncPairDir(world.Pair.Id)));
    }
}
