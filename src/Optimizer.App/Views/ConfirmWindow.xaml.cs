using System.Windows;
using Optimizer.App.Services;
using Optimizer.Core.Actions;
using Wpf.Ui.Controls;

namespace Optimizer.App.Views;

/// <summary>Shows every exact change (target, current value, new value) before anything is written.</summary>
public partial class ConfirmWindow : FluentWindow
{
    private readonly bool _needsAck;
    private readonly bool _canApply;

    /// <summary>One line of the table; <see cref="Unchanged"/> when the value already is what the change writes (After then says so).</summary>
    /// <summary>
    /// On a short screen (a laptop at 150 %) the dialog would grow past the taskbar and hide its buttons: the list of
    /// changes scrolls in less room instead, and the dialog stays on the screen.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        var limit = area.Height - 48;
        if (limit <= 0) return;
        if (ActualHeight > limit) ChangeScroll.MaxHeight = Math.Max(96, ChangeScroll.ActualHeight - (ActualHeight - limit));
        UpdateLayout();
        if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight);
    }

    /// <summary>A list of choices (the review before a build) has no "now" values: the new value then takes the room of both columns.</summary>
    private sealed record Row(string Target, string Before, string After, bool Unchanged, bool ListOnly = false)
    {
        public int AfterColumn => ListOnly ? 1 : 2;
        public int AfterSpan => ListOnly ? 2 : 1;

        // What screen readers announce for this item in a list.
        public override string ToString() => $"{Target}: {Before}, {After}";
    }

    public ConfirmWindow(ConfirmRequest request)
    {
        InitializeComponent();
        Loaded += (_, _) => FitToWorkArea();
        TitleText.Text = request.Title;
        Title = request.Title;
        SummaryText.Text = request.Summary;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(request.Summary) ? Visibility.Collapsed : Visibility.Visible;
        BadgeList.ItemsSource = request.Badges;
        BadgeList.Visibility = request.Badges.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DurationNote.Content = request.Duration;
        DurationNote.Visibility = request.Duration is null ? Visibility.Collapsed : Visibility.Visible;
        BlockList.ItemsSource = request.Blocks;
        WarningList.ItemsSource = request.AntiCheatWarning is null ? request.Warnings : request.Warnings.Append(request.AntiCheatWarning).ToList();
        // A value that already is what the change writes says so instead of repeating the number, and goes below the real
        // changes: "0" under Now and New read like a change.
        var rows = request.Changes
            .Select(c => IsUnchanged(c)
                ? new Row(ChangeRunner.ShortTarget(c), Show(c.Before), Loc.Instance["Confirm_AlreadySet"], true)
                : new Row(ChangeRunner.ShortTarget(c), Show(c.Before), Show(c.After), false))
            .OrderBy(r => r.Unchanged)
            .ToList();
        ChangeList.ItemsSource = rows;
        var listOnly = request.Changes.Count > 0 && request.Changes.All(c => c.Before.Length == 0);
        if (listOnly)
        {
            ChangeList.ItemsSource = rows = rows.Select(r => r with { ListOnly = true }).ToList();
            ColumnHeader.Visibility = Visibility.Collapsed;
        }
        NewHeader.Text = Loc.Instance[request.IsUndo ? "Confirm_Restore" : "Confirm_New"];
        ApplyButton.Content = request.Primary ?? Loc.Instance[request.IsUndo ? "Confirm_Undo" : "Confirm_Apply"];
        // Every value already is what the change writes: applying would only save a backup of the same values.
        var nothingToDo = !request.IsUndo && rows.Count > 0 && rows.All(r => r.Unchanged);
        NothingToDo.IsOpen = nothingToDo;
        _canApply = request.Blocks.Count == 0 && !nothingToDo;
        ApplyButton.Visibility = nothingToDo ? Visibility.Collapsed : Visibility.Visible;
        FooterText.Text = request.Blocks.Count > 0 ? Loc.Instance["Confirm_Blocked"]
            : nothingToDo ? ""
            : request.Footer ?? Loc.Instance[request.IsUndo ? "Confirm_UndoKeeps" : "Confirm_Backup"];
        _needsAck = request.AntiCheatWarning is not null;
        // The acknowledgement only matters while the change can be made at all.
        AntiCheatAck.Visibility = _needsAck && _canApply ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = _canApply && !_needsAck;
    }

    /// <summary>
    /// The line writes the value that is already there. Lines without a readable current value (a command, an unknown
    /// value) always count as a change.
    /// </summary>
    private static bool IsUnchanged(ChangeLine c) =>
        c.Before.Length > 0 && c.Before != "?" && string.Equals(c.Before, c.After, StringComparison.Ordinal);

    /// <summary>Placeholders in the current language, also inside combined values ("Key=(not set), Key2=0").</summary>
    private static string Show(string value)
    {
        var lang = Loc.Instance.Language;
        var labels = Optimizer.Core.Docs.Labels.Current;
        return labels.Display(lang, value).Replace("(not set)", labels.Get(lang, "value.notSet"), StringComparison.Ordinal);
    }

    public bool Acknowledged => AntiCheatAck.IsChecked == true;

    private void AntiCheat_Changed(object sender, RoutedEventArgs e) => ApplyButton.IsEnabled = _canApply && (!_needsAck || Acknowledged);

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
