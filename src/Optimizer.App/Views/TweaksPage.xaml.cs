using System.Windows;
using System.Windows.Controls;

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
}
