using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optimizer.App.Views;

public partial class TweaksPage : UserControl
{
    /// <summary>Below this the search box, the view and the impact filter do not fit on one line (German, details pane open).</summary>
    private const double OneLineWidth = 720;

    public TweaksPage() => InitializeComponent();

    private void FilterRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = FilterRow.ActualWidth < OneLineWidth;
        Grid.SetRow(Filters, narrow ? 1 : 0);
        Grid.SetColumn(Filters, narrow ? 0 : 1);
        Grid.SetColumnSpan(SearchBox, narrow ? 2 : 1);
        Filters.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
        SearchBox.Margin = new Thickness(0, 0, narrow ? 0 : 16, 0);
    }

    /// <summary>
    /// The counts keep the line beside the buttons while they need at most two lines there; otherwise (German, details
    /// pane open) the buttons go under them, left aligned, so the counts do not break into a narrow column.
    /// </summary>
    private void ListRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var text = new FormattedText(ListSummary.Text ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(ListSummary.FontFamily, ListSummary.FontStyle, ListSummary.FontWeight, ListSummary.FontStretch), ListSummary.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        // The width the counts get beside the buttons (16 px gap); a little room is lost where a line breaks between words.
        var beside = ListRow.ActualWidth - ListActions.DesiredSize.Width - 16;
        var below = text > 1.8 * beside;
        if (below == (DockPanel.GetDock(ListActions) == Dock.Bottom)) return;
        DockPanel.SetDock(ListActions, below ? Dock.Bottom : Dock.Right);
        ListActions.HorizontalAlignment = below ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ListActions.Margin = below ? new Thickness(0, 8, 0, 0) : new Thickness(16, 0, 0, 0);
    }
}
