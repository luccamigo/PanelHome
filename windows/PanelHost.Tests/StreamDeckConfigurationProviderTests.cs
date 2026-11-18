using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;

namespace PanelHost.Tests;

/// <summary>
/// The reload loop that keeps PanelHost in step with the Control Center.
/// </summary>
[TestClass]
public sealed class StreamDeckConfigurationProviderTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task The_first_load_publishes_the_active_profile()
    {
        var store = new FakeStreamDeckStore(TwoProfiles(activeProfileId: "profile-b"));
        await using var provider = CreateProvider(store);

        await provider.StartAsync(CancellationToken.None);

        var snapshot = provider.Snapshot;
        Assert.AreEqual("profile-b", snapshot.ProfileId);
        Assert.AreEqual("Mídia", snapshot.ProfileName);
        Assert.AreEqual(1, snapshot.PageCount);
        Assert.AreNotEqual(StreamDeckLayoutRevision.None, snapshot.Revision);
    }

    [TestMethod]
    public async Task A_saved_change_is_reloaded_and_published_once()
    {
        var store = new FakeStreamDeckStore(SingleProfile());
        var changes = new ManualChangeSource();
        await using var provider = CreateProvider(store, changes);
        await provider.StartAsync(CancellationToken.None);
        var first = provider.Snapshot.Revision;
        var published = new List<string>();
        provider.SnapshotChanged += (_, args) => published.Add(args.Snapshot.Revision);

        store.Configuration = SingleProfile(label: "Renomeado");
        changes.Raise();
        await WaitForAsync(() => published.Count == 1);

        Assert.AreEqual(1, published.Count);
        Assert.AreNotEqual(first, published[0]);
        Assert.AreEqual(published[0], provider.Snapshot.Revision);
        Assert.AreEqual(
            "Renomeado",
            provider.Snapshot.Layout!.Pages[0].Buttons[0].Label);
    }

    [TestMethod]
    public async Task A_burst_of_filesystem_events_produces_one_reload()
    {
        // An atomic replace surfaces as several events; reading on each one
        // would read the file mid-swap and reload for nothing.
        var store = new FakeStreamDeckStore(SingleProfile());
        var changes = new ManualChangeSource();
        var debounce = new ControllableDelay();
        await using var provider = CreateProvider(store, changes, debounce.Delay);
        await provider.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, store.LoadCount);

        store.Configuration = SingleProfile(label: "Depois");
        changes.Raise();
        await debounce.Entered.WaitAsync(TestTimeout);
        changes.Raise();
        changes.Raise();
        debounce.Release();
        await WaitForAsync(
            () => provider.Snapshot.Layout!.Pages[0].Buttons[0].Label == "Depois");

        // Three events, one read: the coalescing is the point.
        Assert.AreEqual(2, store.LoadCount);
    }

    [TestMethod]
    public async Task A_file_caught_mid_replace_is_retried_rather_than_adopted()
    {
        var store = new FakeStreamDeckStore(SingleProfile());
        var changes = new ManualChangeSource();
        await using var provider = CreateProvider(store, changes);
        await provider.StartAsync(CancellationToken.None);

        // The store reports the swap window as unreadable, then succeeds.
        store.Configuration = SingleProfile(label: "Depois");
        store.FailNextLoads(StreamDeckConfigurationSource.Unreadable, times: 2);
        changes.Raise();
        await WaitForAsync(
            () => provider.Snapshot.Layout!.Pages[0].Buttons[0].Label == "Depois");

        Assert.AreEqual(3, store.LoadCount - 1, "the read is retried, not abandoned");
    }

    [TestMethod]
    public async Task A_configuration_that_stays_invalid_keeps_the_last_good_layout()
    {
        var store = new FakeStreamDeckStore(SingleProfile());
        var changes = new ManualChangeSource();
        await using var provider = CreateProvider(store, changes);
        await provider.StartAsync(CancellationToken.None);
        var good = provider.Snapshot;
        var published = 0;
        provider.SnapshotChanged += (_, _) => published++;

        store.FailNextLoads(
            StreamDeckConfigurationSource.RecoveredFromInvalidFile,
            times: int.MaxValue);
        changes.Raise();
        await WaitForAsync(() => store.LoadCount >= 5);

        // A broken document must never blank the panel that is working.
        Assert.AreSame(good, provider.Snapshot);
        Assert.AreEqual(0, published);
    }

    [TestMethod]
    public async Task An_identical_save_does_not_republish_the_layout()
    {
        var store = new FakeStreamDeckStore(SingleProfile());
        var changes = new ManualChangeSource();
        await using var provider = CreateProvider(store, changes);
        await provider.StartAsync(CancellationToken.None);
        var published = 0;
        provider.SnapshotChanged += (_, _) => published++;

        changes.Raise();
        await WaitForAsync(() => store.LoadCount >= 2);

        Assert.AreEqual(0, published);
    }

    [TestMethod]
    public async Task Changing_the_active_profile_changes_the_published_layout()
    {
        var store = new FakeStreamDeckStore(TwoProfiles(activeProfileId: "profile-a"));
        var changes = new ManualChangeSource();
        await using var provider = CreateProvider(store, changes);
        await provider.StartAsync(CancellationToken.None);
        var before = provider.Snapshot.Revision;

        store.Configuration = TwoProfiles(activeProfileId: "profile-b");
        changes.Raise();
        await WaitForAsync(() => provider.Snapshot.ProfileId == "profile-b");

        Assert.AreNotEqual(before, provider.Snapshot.Revision);
        Assert.AreEqual("Mídia", provider.Snapshot.ProfileName);
    }

    [TestMethod]
    public async Task A_real_atomic_replace_on_disk_is_picked_up_without_a_restart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PanelHost.Tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "streamdeck.json");
        try
        {
            var store = new JsonStreamDeckConfigurationStore(
                path,
                quarantineInvalidFile: false);
            await store.SaveAsync(SingleProfile(), CancellationToken.None);

            await using var provider = new StreamDeckConfigurationProvider(
                store,
                new StreamDeckConfigurationFileWatcher(path),
                options: new StreamDeckReloadOptions
                {
                    Debounce = TimeSpan.FromMilliseconds(50),
                    RetryDelay = TimeSpan.FromMilliseconds(20),
                });
            await provider.StartAsync(CancellationToken.None);
            Assert.AreEqual("Play", provider.Snapshot.Layout!.Pages[0].Buttons[0].Label);

            // The store writes beside the file and moves it over, which is what
            // the watcher has to survive.
            await store.SaveAsync(
                SingleProfile(label: "Ao vivo"),
                CancellationToken.None);
            await WaitForAsync(
                () => provider.Snapshot.Layout!.Pages[0].Buttons[0].Label == "Ao vivo",
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static StreamDeckConfigurationProvider CreateProvider(
        FakeStreamDeckStore store,
        IStreamDeckConfigurationChangeSource? changes = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            store,
            changes,
            options: new StreamDeckReloadOptions
            {
                Debounce = TimeSpan.Zero,
                RetryDelay = TimeSpan.Zero,
            },
            delay: delay ?? ((_, _) => Task.CompletedTask));

    internal static StreamDeckConfiguration SingleProfile(string label = "Play") => new()
    {
        ActiveProfileId = "profile-a",
        Profiles =
        [
            new StreamDeckProfile
            {
                Id = "profile-a",
                Name = "Principal",
                Pages = [Page("page-a", "Principal", "btn-a", label)],
            },
        ],
    };

    internal static StreamDeckConfiguration TwoProfiles(string activeProfileId) => new()
    {
        ActiveProfileId = activeProfileId,
        Profiles =
        [
            new StreamDeckProfile
            {
                Id = "profile-a",
                Name = "Principal",
                Pages = [Page("page-a", "Principal", "btn-a", "Play")],
            },
            new StreamDeckProfile
            {
                Id = "profile-b",
                Name = "Mídia",
                Pages = [Page("page-b", "Mídia", "btn-b", "Mute")],
            },
        ],
    };

    private static StreamDeckPage Page(
        string pageId,
        string name,
        string buttonId,
        string label) => new()
        {
            Id = pageId,
            Name = name,
            Rows = 2,
            Columns = 2,
            Buttons =
            [
                new StreamDeckButton
                {
                    Id = buttonId,
                    Row = 0,
                    Column = 0,
                    Label = label,
                    Action = new MediaCommandAction { Command = MediaCommand.PlayPause },
                },
            ],
        };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The expected state was not reached in time.");
    }

    private sealed class ManualChangeSource : IStreamDeckConfigurationChangeSource
    {
        public event EventHandler? Changed;

        public void Start()
        {
        }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    /// <summary>Lets a test hold the loop inside the debounce window.</summary>
    private sealed class ControllableDelay
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult(true);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            _ = duration;
            _entered.TrySetResult(true);
            return _release.Task.WaitAsync(cancellationToken);
        }
    }
}

internal sealed class FakeStreamDeckStore : IStreamDeckConfigurationStore
{
    private int _failures;
    private StreamDeckConfigurationSource _failureSource;

    public FakeStreamDeckStore(StreamDeckConfiguration configuration)
    {
        Configuration = configuration;
    }

    public StreamDeckConfiguration Configuration { get; set; }

    public int LoadCount { get; private set; }

    public string Location => @"C:\Fake\streamdeck.json";

    public void FailNextLoads(StreamDeckConfigurationSource source, int times)
    {
        _failureSource = source;
        _failures = times;
    }

    public Task<StreamDeckConfigurationLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        LoadCount++;
        if (_failures > 0)
        {
            _failures--;
            return Task.FromResult(new StreamDeckConfigurationLoadResult(
                StreamDeckDefaults.CreateConfiguration(),
                _failureSource,
                "test failure"));
        }

        return Task.FromResult(new StreamDeckConfigurationLoadResult(
            Configuration,
            StreamDeckConfigurationSource.Loaded));
    }

    public Task SaveAsync(
        StreamDeckConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Configuration = configuration;
        return Task.CompletedTask;
    }
}
