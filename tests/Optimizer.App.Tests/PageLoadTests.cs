using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The load loop every page shares (<see cref="PageViewModel"/>): one load at a time, a reload or relabel asked for during
/// a load follows it once, a change turns the next relabel into a load, a page that needs the scan waits for it, and a
/// failing load is shown and ends. A test page counts its loads and relabels and holds them on gates.
/// </summary>
public sealed class PageLoadTests
{
    private sealed class TestPage(IPageHost host, bool usesProfile = false) : PageViewModel(host)
    {
        private int _running;

        public int Loads;
        public int Relabels;
        public int Updates;
        public int MaxRunning;
        public Exception? Fail;

        /// <summary>The next load or relabel waits for the first gate in the queue.</summary>
        public Queue<TaskCompletionSource> Gates { get; } = [];

        protected override bool UsesProfile => usesProfile;

        // FakePageHost shows Storage: a failure of this page is on the open page.
        protected override IReadOnlyList<Page> ShownOn => [Page.Storage];

        public TaskCompletionSource Hold()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Gates) Gates.Enqueue(gate);
            return gate;
        }

        protected override Task LoadAsync()
        {
            Interlocked.Increment(ref Loads);
            return Run();
        }

        protected override Task RelabelAsync()
        {
            Interlocked.Increment(ref Relabels);
            return Run();
        }

        protected override void ProfileUpdated() => Interlocked.Increment(ref Updates);

        private async Task Run()
        {
            var now = Interlocked.Increment(ref _running);
            if (now > MaxRunning) MaxRunning = now;
            try
            {
                if (Fail is { } fail) throw fail;
                TaskCompletionSource? gate;
                lock (Gates) Gates.TryDequeue(out gate);
                if (gate is not null) await gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    [Fact]
    public async Task AReloadDuringALoadRunsOnceAfterIt()
    {
        var page = new TestPage(new FakePageHost());
        var first = page.Hold();
        var load = page.EnsureLoadedAsync();
        // Refresh twice while the first load runs: both join one load after it.
        var reload1 = page.ReloadAsync();
        var reload2 = page.ReloadAsync();
        Assert.Equal(1, page.Loads);

        first.SetResult();
        await Task.WhenAll(load, reload1, reload2).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, page.Loads);
        Assert.Equal(1, page.MaxRunning);
        Assert.False(page.IsLoading);
    }

    [Fact]
    public async Task ARelabelDuringALoadFollowsIt()
    {
        var page = new TestPage(new FakePageHost());
        var first = page.Hold();
        var load = page.EnsureLoadedAsync();
        // The rows of the running load may be built in the old language.
        page.OnLanguageChanged();

        first.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((1, 1), (page.Loads, page.Relabels));
        Assert.Equal(1, page.MaxRunning);
    }

    [Fact]
    public async Task AReloadReplacesAPendingRelabel()
    {
        var page = new TestPage(new FakePageHost());
        await page.EnsureLoadedAsync();
        var relabel = page.Hold();
        page.OnLanguageChanged();
        Assert.Equal(1, page.Relabels);
        // A second language switch and a Refresh while the relabel runs: what follows is one full load, no second relabel.
        page.OnLanguageChanged();
        var reload = page.ReloadAsync();

        relabel.SetResult();
        await reload.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((2, 1), (page.Loads, page.Relabels));
    }

    [Fact]
    public async Task AChangeTurnsTheNextRelabelIntoALoad()
    {
        var host = new FakePageHost();
        var page = new TestPage(host);
        await page.EnsureLoadedAsync();

        // A change anywhere: what the page read may no longer be true, so the language switch reads again.
        host.Runner.NotifyChanged(null);
        page.OnLanguageChanged();
        await page.Loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((2, 0), (page.Loads, page.Relabels));

        // The load read everything again: the next language switch only relabels.
        page.OnLanguageChanged();
        await page.Loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((2, 1), (page.Loads, page.Relabels));
    }

    [Fact]
    public async Task APageOpenedBeforeTheFirstScanWaitsForIt()
    {
        var scan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakePageHost { Profile = null, ScanInProgress = scan.Task };
        var page = new TestPage(host, usesProfile: true);
        var load = page.EnsureLoadedAsync();
        Assert.True(page.IsLoading);
        Assert.Equal(0, page.Loads);

        // The scan brings its result while the page waits: the waiting load reads it, no second load starts.
        host.Profile = new FakePageHost().Profile;
        page.OnProfileChanged();
        scan.SetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, page.Loads);
        Assert.Equal(0, page.Updates);
        Assert.False(page.IsLoading);
    }

    [Fact]
    public async Task APageLoadedBeforeTheScanReloadsAfterIt()
    {
        // The first scan failed (no result, nothing running): the page loads without one.
        var host = new FakePageHost { Profile = null };
        var page = new TestPage(host, usesProfile: true);
        await page.EnsureLoadedAsync();
        Assert.Equal(1, page.Loads);

        host.Profile = new FakePageHost().Profile;
        page.OnProfileChanged();
        await page.Loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, page.Loads);
        Assert.Equal(0, page.Updates);
    }

    [Fact]
    public async Task APageLoadedWithAScanOnlyUpdates()
    {
        var host = new FakePageHost();
        var page = new TestPage(host, usesProfile: true);
        await page.EnsureLoadedAsync();

        page.OnProfileChanged();
        await page.Loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((1, 1), (page.Loads, page.Updates));
    }

    [Fact]
    public async Task AFailingLoadIsShownAndEnds()
    {
        var host = new FakePageHost();
        var page = new TestPage(host) { Fail = new InvalidOperationException("list unreadable") };
        await page.EnsureLoadedAsync();

        var (text, severity) = Assert.Single(host.Results);
        Assert.Equal(Loc.Instance.Format("Page_LoadFailed", Loc.Instance["Nav_Storage"], "list unreadable"), text);
        Assert.Equal(InfoBarSeverity.Error, severity);
        Assert.False(page.IsLoading);

        // Refresh works again afterwards.
        page.Fail = null;
        await page.ReloadAsync();
        Assert.Equal(2, page.Loads);
        Assert.Single(host.Results);
    }

    [Fact]
    public async Task AnUnopenedPageIgnoresLanguageAndScan()
    {
        var page = new TestPage(new FakePageHost(), usesProfile: true);
        page.OnLanguageChanged();
        page.OnProfileChanged();
        await page.Loading;
        Assert.Equal((0, 0, 0), (page.Loads, page.Relabels, page.Updates));
    }
}
