using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Optimizer.App.ViewModels;
using Wpf.Ui.Controls;

namespace Optimizer.App.Views;

public partial class StoragePage : UserControl
{
    /// <summary>
    /// Content width from which the treemap stands beside the folder list instead of under it: the list needs about
    /// 520 px (fixed columns 304, chevron, icon and margins about 90, the name at least 120) and gets three fifths of
    /// the width, the names being what the list is read by. Below it is only the smallest window.
    /// </summary>
    private const double SideBySideWidth = 1000;

    /// <summary>The page's widest content (StorageWidth in the page resources).</summary>
    private const double ContentMax = 1600;

    private StorageViewModel? _storage;

    public StoragePage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Layout();
        DataContextChanged += (_, _) =>
        {
            if (_storage is not null) _storage.TreeChanged -= OnTreeChanged;
            _storage = (DataContext as MainViewModel)?.Storage;
            if (_storage is not null) _storage.TreeChanged += OnTreeChanged;
        };
    }

    // Entries removed (same tree) or texts in another language: the map lays itself out again.
    private void OnTreeChanged(object? sender, EventArgs e) => Map.Rebuild();

    /// <summary>
    /// Wide: list and map side by side, both as tall as the tab. Narrow: the map under the list at a fixed height, and
    /// the tab scrolls. Either way as wide as the header and the other tabs.
    /// </summary>
    private void Layout()
    {
        var available = ActualWidth - PageWidthConverter.Padding;
        if (available <= 0) return;
        var width = Math.Min(ContentMax, available);
        var wide = width >= SideBySideWidth;
        FoldersContent.Width = width;
        FoldersScroll.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        GapColumn.Width = new GridLength(wide ? 16 : 0);
        ListColumn.Width = new GridLength(wide ? 3 : 1, GridUnitType.Star);
        MapColumn.Width = wide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        ListRow.Height = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        GapRow.Height = new GridLength(wide ? 0 : 16);
        MapRow.Height = wide ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(MapCard, wide ? 0 : 2);
        Grid.SetColumn(MapCard, wide ? 2 : 0);
        ListCard.Height = wide ? double.NaN : 360;
        MapCard.Height = wide ? double.NaN : 480;
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // An entry picked on the map: its row comes into view.
        if (FolderList.SelectedItem is { } row) FolderList.ScrollIntoView(row);
    }

    /// <summary>Left and Right close and open folders as in a tree; Enter shows the folder on the map.</summary>
    private void FolderList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_storage is null || FolderList.SelectedItem is not TreeRow row) return;
        switch (e.Key)
        {
            case Key.Left:
                _storage.CollapseOrSelectParent(row);
                break;
            case Key.Right:
                _storage.ExpandOrSelectChild(row);
                break;
            case Key.Enter:
                _storage.OpenFolder(row.Entry);
                break;
            default:
                return;
        }
        e.Handled = true;
        FocusSelectedRow();
    }

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_storage is not null && FolderList.SelectedItem is TreeRow row && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(FolderList, source) is not null)
            _storage.OpenFolder(row.Entry);
    }

    /// <summary>Keyboard focus follows the selected row (after a collapse or a move to the parent).</summary>
    private void FocusSelectedRow()
    {
        if (FolderList.SelectedItem is not { } row) return;
        FolderList.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() => (FolderList.ItemContainerGenerator.ContainerFromItem(row) as UIElement)?.Focus(),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Map_EntryActivated(object? sender, int folder) => _storage?.OpenFolder(folder);

    /// <summary>The legend lists the groups the map draws (each entry's Tag is its group).</summary>
    private void Map_GroupsShown(object? sender, EventArgs e)
    {
        foreach (var entry in Legend.Children.OfType<FrameworkElement>())
            if (entry.Tag is string name && Enum.TryParse<Optimizer.Core.Storage.FileGroup>(name, out var group))
                entry.Visibility = Map.Shows(group) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is FolderCrumb crumb) _storage?.OpenFolder(crumb.Entry);
    }
}
