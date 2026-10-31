using Optimizer.App.Services;
using Optimizer.App.ViewModels;

namespace Optimizer.App.Tests;

/// <summary>
/// Pages are read ahead in the background. A load that fails there (WMI cancels a query now and then) says nothing on the
/// page the user looks at, and the page reads again when it opens; a failure on the open page names that page.
/// </summary>
public sealed class PageLoadErrorTests
{
    private sealed class FailingPage(IPageHost owner) : PageViewModel(owner)
    {
        public int Loads { get; private set; }
        public bool Fail { get; set; } = true;

        protected override IReadOnlyList<Page> ShownOn => [Page.Services];

        protected override Task LoadAsync()
        {
            Loads++;
            return Fail ? Task.FromException(new InvalidOperationException("Call cancelled")) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AFailedLoadInTheBackgroundSaysNothingElsewhereAndReadsAgainWhenOpened()
    {
        var host = new FakePageHost { CurrentPage = Page.Cleanup };
        var page = new FailingPage(host);

        await page.EnsureLoadedAsync();
        Assert.Empty(host.Results);

        page.Fail = false;
        host.CurrentPage = Page.Services;
        await page.EnsureLoadedAsync();
        Assert.Equal(2, page.Loads);
        Assert.Empty(host.Results);

        // Loaded now: opening the page again reads nothing.
        await page.EnsureLoadedAsync();
        Assert.Equal(2, page.Loads);
    }

    [Fact]
    public async Task AFailureOnTheOpenPageNamesThePage()
    {
        var host = new FakePageHost { CurrentPage = Page.Services };
        var page = new FailingPage(host);

        await page.EnsureLoadedAsync();

        var (text, severity) = Assert.Single(host.Results);
        Assert.Equal(Wpf.Ui.Controls.InfoBarSeverity.Error, severity);
        Assert.Equal(Loc.Instance.Format("Page_LoadFailed", Loc.Instance["Nav_Services"], "Call cancelled"), text);
    }
}
