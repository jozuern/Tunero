using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Cleanup;

namespace Optimizer.App.Tests;

/// <summary>
/// The Cleanup page without the window. The rows get their sizes from fake scans, so no folder is read, measured or
/// cleaned: nothing here calls the cleanup engine.
/// </summary>
public sealed class CleanupViewModelTests
{
    private static CleanupRow Row(string id, long bytes)
    {
        var category = new CleanupCategory(id, [@"C:\nowhere\" + id]);
        var row = new CleanupRow(category, "en");
        row.SetScan(new CleanupScan(category, bytes, bytes > 0 ? 3 : 0, []));
        return row;
    }

    /// <summary>
    /// Clean selected is off while nothing with a size is ticked (also right after a cleanup, when the ticked categories
    /// are empty): pressed, it would do nothing at all. A tick on a category with something in it turns it on.
    /// </summary>
    [Fact]
    public void CleanSelectedIsOffWhileNothingWithASizeIsTicked()
    {
        var cleanup = new CleanupViewModel(new FakePageHost(), null!, new FakeDialogs());
        Assert.False(cleanup.CleanCommand.CanExecute(null));

        var full = Row("cleanup.windowsTemp", 4096);
        var empty = Row("cleanup.errorReports", 0);
        cleanup.Rows.Add(full);
        cleanup.Rows.Add(empty);
        var raised = 0;
        cleanup.CleanCommand.CanExecuteChanged += (_, _) => raised++;

        // As the check box does: the tick changes, then the command runs.
        cleanup.SelectionChangedCommand.Execute(null);
        Assert.True(raised > 0);
        Assert.True(cleanup.CleanCommand.CanExecute(null));
        Assert.Equal(Loc.Instance.Format("Cleanup_Total", CleanupViewModel.Size(4096)), cleanup.TotalText);

        full.Checked = false;
        raised = 0;
        cleanup.SelectionChangedCommand.Execute(null);
        Assert.True(raised > 0);
        // Only the empty category is still ticked: nothing to clean.
        Assert.True(empty.Selected);
        Assert.False(cleanup.CleanCommand.CanExecute(null));
        Assert.Equal(Loc.Instance.Format("Cleanup_Total", CleanupViewModel.Size(0)), cleanup.TotalText);

        full.Checked = true;
        cleanup.SelectionChangedCommand.Execute(null);
        Assert.True(cleanup.CleanCommand.CanExecute(null));
    }
}
