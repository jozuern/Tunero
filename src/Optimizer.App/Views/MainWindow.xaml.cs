using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Wpf.Ui.Controls;

namespace Optimizer.App.Views;

/// <summary>Main window: Fluent title bar, NavigationView rail, pages, details pane.</summary>
public partial class MainWindow : FluentWindow, IServiceProvider
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<Type, FrameworkElement> _pages = [];

    private bool _syncing;

    /// <summary>Closing waits for a stopped install media build to clean up.</summary>
    private bool _closingAfterBuild;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Nav.SetServiceProvider(this);
        Nav.Navigated += (_, e) =>
        {
            if (_syncing || e.Page is null) return;
            if (PageRegistry.ForView(e.Page.GetType()) is not { } page) return;
            _syncing = true;
            vm.CurrentPage = page.Id;
            _syncing = false;
        };
        SourceInitialized += (_, _) => FitToWorkArea();
        Loaded += (_, _) => Nav.Navigate(typeof(OverviewPage));
        // The title bar lays out its parts after the window: center the search box once that is done.
        Loaded += (_, _) => Dispatcher.BeginInvoke(CenterSearchBox, System.Windows.Threading.DispatcherPriority.Loaded);
        Loaded += (_, _) => SetTitleBarIcon(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX);
        Loaded += (_, _) => NameIconButtons();
        DpiChanged += (_, e) => SetTitleBarIcon(e.NewDpi.DpiScaleX);
        vm.PropertyChanged += OnViewModelChanged;
        SizeChanged += (_, _) =>
        {
            FitPanes();
            CenterSearchBox();
        };
        // Closing ends the process, and with it the threads of a change in progress: a batch, an undo, a winget run or
        // an app removal would stop halfway. The window stays open until the change has finished.
        Closing += async (_, e) =>
        {
            // An install media build can stop safely: asked first, it stops and cleans up (unmount, work folder), then the
            // window closes.
            if (vm.InstallMedia.IsBuilding)
            {
                e.Cancel = true;
                if (_closingAfterBuild) return;
                if (MessageWindow.Show(this, Loc.Instance["Media_CloseTitle"], Loc.Instance["Media_CloseText"], Loc.Instance["Confirm_Cancel"], Loc.Instance["Media_CloseStop"]) != 1)
                    return;
                _closingAfterBuild = true;
                try
                {
                    await vm.InstallMedia.StopAndWaitAsync();
                }
                finally
                {
                    _closingAfterBuild = false;
                }
                if (!ChangeGate.Instance.Busy) Close();
                return;
            }
            if (!ChangeGate.Instance.Busy) return;
            e.Cancel = true;
            vm.ShowResult(Loc.Instance["Close_WhileBusy"], InfoBarSeverity.Warning);
        };
        // The headings and lines are all shown in the first layout, so their room is known before the first decision.
        Loaded += (_, _) =>
        {
            _paneLaidOut = true;
            ShowGroupHeaders();
        };
        Nav.PaneOpened += (_, _) => ShowGroupHeaders();
        Nav.PaneClosed += (_, _) => ShowGroupHeaders();
        // The room also changes without a new window size: the footer under the pages grows while a scan runs in the rail
        // (its ring). Decided only on window sizes, the headings stayed lines after the start-up scan had ended.
        PaneFooterPanel.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged) ShowGroupHeaders();
        };
        PreviewKeyDown += (_, e) =>
        {
            // The setup screen covers the pages: no search and no scan under it (Install would then wait for the scan).
            if (vm.SetupShown && e.Key is System.Windows.Input.Key.F5 or System.Windows.Input.Key.F) return;
            if (e.Key == System.Windows.Input.Key.Escape && !vm.SearchResultsOpen) vm.SelectedItem = null;
            // Ctrl+F searches the whole app; F5 reads the page or the PC again.
            else if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F5)
            {
                vm.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// The pane button and the window buttons come from WPF-UI's templates with an icon only, so a screen reader said
    /// "button": they get names in the app language (bound, so a language switch renames them).
    /// </summary>
    private void NameIconButtons()
    {
        var names = new Dictionary<string, string>
        {
            ["PART_ToggleButton"] = "Nav_Toggle",
            ["TitleBarMinimizeButton"] = "Window_Minimize",
            ["TitleBarMaximizeButton"] = "Window_Maximize",
            ["TitleBarCloseButton"] = "Common_Close",
        };
        var pending = new Stack<DependencyObject>([this]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element is FrameworkElement fe && (names.TryGetValue(fe.Name, out var key) || names.TryGetValue(System.Windows.Automation.AutomationProperties.GetAutomationId(fe), out key)))
                fe.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,
                    new System.Windows.Data.Binding($"[{key}]") { Source = Loc.Instance, Mode = System.Windows.Data.BindingMode.OneWay });
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) pending.Push(VisualTreeHelper.GetChild(element, i));
        }
    }

    private void SearchBox_SizeChanged(object sender, SizeChangedEventArgs e) => CenterSearchBox();

    /// <summary>
    /// The title bar puts its center content right after the title: the box is moved to the middle of the window, as in
    /// Windows 11 apps (never closer than 24 px to the title).
    /// </summary>
    private void CenterSearchBox()
    {
        if (!SearchBox.IsLoaded || ActualWidth <= 0) return;
        var left = SearchBox.TranslatePoint(new Point(0, 0), this).X - SearchBox.Margin.Left;
        var wanted = (ActualWidth - SearchBox.ActualWidth) / 2 - left;
        SearchBox.Margin = new Thickness(Math.Max(24, wanted), 6, 0, 6);
    }

    /// <summary>A new result message is announced by screen readers (the text is a live region).</summary>
    private void AnnounceResult()
    {
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(ResultMessage) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    /// <summary>In the search box: Down moves into the results, Enter opens the first one, Escape clears the search.</summary>
    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Down when _vm.SearchResults.Count > 0:
                _vm.SearchResultsOpen = true;
                SearchList.SelectedIndex = 0;
                Dispatcher.BeginInvoke(() => (SearchList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(),
                    System.Windows.Threading.DispatcherPriority.Input);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter when _vm.SearchResults.Count > 0:
                OpenResult(_vm.SearchResults[0]);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Escape:
                _vm.GlobalSearch = "";
                _vm.SearchResultsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SearchList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && SearchList.SelectedItem is SearchResultItem item)
        {
            OpenResult(item);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            _vm.SearchResultsOpen = false;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void SearchList_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is SearchResultItem item) OpenResult(item);
    }

    private void OpenResult(SearchResultItem item)
    {
        _vm.OpenSearchResultCommand.Execute(item);
        SearchList.SelectedItem = null;
    }

    /// <summary>Pages are created once and share the main view model.</summary>
    public object? GetService(Type serviceType)
    {
        if (PageRegistry.ForView(serviceType) is null) return null;
        if (!_pages.TryGetValue(serviceType, out var page))
        {
            page = (FrameworkElement)Activator.CreateInstance(serviceType)!;
            page.DataContext = _vm;
            _pages[serviceType] = page;
        }
        return page;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new text in the open bar, or the bar opening (ShowResult sets the text first, while a bar that closed by itself
        // is still closed): either way the result is read out once.
        if (e.PropertyName is nameof(MainViewModel.ResultText) or nameof(MainViewModel.ResultOpen) && _vm.ResultOpen) Dispatcher.BeginInvoke(AnnounceResult);
        if (e.PropertyName == nameof(MainViewModel.SelectedItem))
        {
            RenderDetails();
            FitPanes();
        }
        if (e.PropertyName == nameof(MainViewModel.CurrentPage) && !_syncing && PageRegistry.Of(_vm.CurrentPage).View is var type)
        {
            _syncing = true;
            Nav.Navigate(type);
            _syncing = false;
        }
    }

    /// <summary>
    /// The window's size and minimum fit the screen's work area (without the taskbar): a 1080p laptop at 150 % has about
    /// 1280 x 672 DIP, less than the default size and the minimum height, so the footer and the result bar would sit
    /// behind the taskbar. Where even the smaller window would not fit, it opens maximized. The screen is the one the
    /// window opens on, which need not be the primary one.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = ScreenWorkArea.Of(this);
        if (area.Width <= 0 || area.Height <= 0) return;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        if (Width > area.Width) Width = area.Width;
        if (Height > area.Height) Height = area.Height;
        if (area.Width < 1040 || area.Height < 720) WindowState = WindowState.Maximized;
    }

    /// <summary>Window width below which the open pane and the details pane together leave the page too narrow.</summary>
    private const double NarrowWidth = 1320;

    private bool _paneCollapsedForDetails;

    /// <summary>
    /// In a narrow window the open details pane takes the room of the navigation pane, which folds to the rail (as
    /// NavigationView does in WinUI); it opens again when the details close or the window gets wide enough.
    /// </summary>
    private void FitPanes()
    {
        FitHeight();
        // The details pane never leaves the page less room than it needs for a readable list; the width it was dragged
        // to comes back when the window is wide enough again.
        DetailsPane.Width = Math.Clamp(_detailsWidth, DetailsMin, DetailsMax);
        var crowded = _vm.SelectedItem is not null && ActualWidth > 0 && ActualWidth < NarrowWidth;
        if (crowded && Nav.IsPaneOpen)
        {
            Nav.IsPaneOpen = false;
            _paneCollapsedForDetails = true;
        }
        else if (!crowded && _paneCollapsedForDetails)
        {
            // The details no longer need the room. A window that got too short for the open pane meanwhile keeps the
            // rail, and the pane opens when it is tall enough again (FitHeight).
            _paneCollapsedForDetails = false;
            if (ActualHeight >= RailHeight) Nav.IsPaneOpen = true;
            else _paneCollapsedForHeight = true;
            ShowGroupHeaders();
        }
    }

    /// <summary>
    /// Until the pane has been laid out once: below this height the open pane cannot show the group headings and every
    /// page. After that the room is measured (<see cref="ShowGroupHeaders"/>).
    /// </summary>
    private const double HeadingsHeight = 940;

    /// <summary>
    /// Below this height even the pane without headings would cut off pages: it folds to the rail, as in WinUI. 830 since
    /// the sixteenth page (Windows install media): at 790 the last page slid under the footer.
    /// </summary>
    private const double RailHeight = 830;

    private bool _paneCollapsedForHeight;

    /// <summary>
    /// The pane does not scroll: on a short window (a 768 px screen) its last pages would slide under the footer. First the
    /// group headings give way to lines, then the pane folds to the rail; both come back when the window is tall enough
    /// again.
    /// </summary>
    private void FitHeight()
    {
        if (ActualHeight <= 0) return;
        var tooShort = ActualHeight < RailHeight;
        if (tooShort && Nav.IsPaneOpen)
        {
            Nav.IsPaneOpen = false;
            _paneCollapsedForHeight = true;
        }
        else if (!tooShort && _paneCollapsedForHeight)
        {
            _paneCollapsedForHeight = false;
            // Details in a narrow window still need the room: FitPanes opens the pane when they close.
            if (!_paneCollapsedForDetails) Nav.IsPaneOpen = true;
        }
        // Below this even the rail cut off its last page under the footer (1920 x 1080 at 150 %, VM): the items take a
        // compact height. 32 rather than the 36 of Windows Settings: with sixteen pages, 34 left the last one 37 px above
        // the footer at 720 px, and a maximized window at 150 % (672 px) moves the footer up 48 px more.
        var compact = ActualHeight < CompactHeight;
        if (compact != _compactNav)
        {
            _compactNav = compact;
            foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>().Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>()))
                item.Height = compact ? CompactItemHeight : double.NaN;
        }
        ShowGroupHeaders();
    }

    /// <summary>
    /// Window height below which the pages in the rail get the compact item height: wherever the pane folds to the rail
    /// for lack of height. At 750 a 760 px window still put the last page (Manual checks) under the footer.
    /// </summary>
    private const double CompactHeight = RailHeight;

    private const double CompactItemHeight = 32;

    private bool _compactNav;

    /// <summary>
    /// Room the three headings (in the open pane) and the three lines take, read from the layout while they are shown:
    /// all are in the first layout, before <see cref="ShowGroupHeaders"/> first decides.
    /// </summary>
    private double _headingsRoom, _linesRoom;

    /// <summary>Set once the window is loaded: the pane has been laid out with every heading and line.</summary>
    private bool _paneLaidOut;

    /// <summary>Space kept between the last page and the footer, so the two never touch.</summary>
    private const double FooterGap = 8;

    /// <summary>
    /// The groups stay visible whenever there is room: the headings in the open pane when they fit above the footer,
    /// otherwise a thin line between the groups (also in the rail), and neither when even the lines would push the last
    /// page under the footer. The room is measured, not guessed from the window height: a fixed 940 px limit hid the
    /// headings in a 1440 x 912 window that had nearly enough room for them. The scan status in the pane footer takes the
    /// line of the last scan, so a scan does not change the room in the open pane.
    /// </summary>
    private void ShowGroupHeaders()
    {
        FrameworkElement[] headings = [HeaderOptimize, HeaderCleanUp, HeaderMaintain];
        FrameworkElement[] lines = [LineOptimize, LineCleanUp, LineMaintain];
        if (Nav.IsPaneOpen) Learn(headings, ref _headingsRoom);
        Learn(lines, ref _linesRoom);
        if (!_paneLaidOut) return;
        var free = FreePaneHeight(headings, lines);
        var showHeadings = Nav.IsPaneOpen && (free is { } f && _headingsRoom > 0 ? f >= _headingsRoom : ActualHeight >= HeadingsHeight);
        var showLines = !showHeadings && free is { } g && _linesRoom > 0 && g >= _linesRoom;
        foreach (var h in headings) h.Visibility = showHeadings ? Visibility.Visible : Visibility.Collapsed;
        foreach (var l in lines) l.Visibility = showLines ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double Room(FrameworkElement[] elements) =>
        elements.Where(e => e.IsVisible).Sum(e => e.ActualHeight + e.Margin.Top + e.Margin.Bottom);

    private static void Learn(FrameworkElement[] elements, ref double room)
    {
        if (elements.All(e => e.IsVisible && e.ActualHeight > 0)) room = Room(elements);
    }

    /// <summary>
    /// The height between the last page and the first footer item as it would be without headings and lines, less the
    /// gap; null before the pane is laid out.
    /// </summary>
    private double? FreePaneHeight(FrameworkElement[] headings, FrameworkElement[] lines)
    {
        if (Nav.MenuItems.OfType<NavigationViewItem>().LastOrDefault() is not { IsVisible: true } last
            || Nav.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault() is not { IsVisible: true } footer)
            return null;
        Nav.UpdateLayout();
        if (last.ActualHeight <= 0 || footer.ActualHeight <= 0) return null;
        var bottom = last.TranslatePoint(new Point(0, last.ActualHeight), Nav).Y;
        var top = footer.TranslatePoint(new Point(0, 0), Nav).Y;
        return top - bottom + Room(headings) + Room(lines) - FooterGap;
    }

    private const double DetailsMin = 340;

    /// <summary>The width the details pane is given (460 until its edge is dragged); narrow windows show less of it.</summary>
    private double _detailsWidth = 460;

    /// <summary>
    /// Widest details pane: at most 45 % of the window, and the page keeps at least 640 px (the rail and a list whose
    /// rows still have room for their text next to the switch; with 520 px a row's summary ran over six lines).
    /// </summary>
    private double DetailsMax => Math.Max(DetailsMin, Math.Min(ActualWidth - 640, ActualWidth * 0.45));

    /// <summary>Dragging the left edge of the details pane: to the left makes it wider.</summary>
    private void DetailsResize_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        _detailsWidth = Math.Clamp(DetailsPane.Width - e.HorizontalChange, DetailsMin, DetailsMax);
        DetailsPane.Width = _detailsWidth;
    }

    /// <summary>
    /// Renders the selected item's explanation (re-run after theme changes so brushes match). The document is built in
    /// the background; the title shows at once, and a result for an item that is no longer selected is dropped.
    /// </summary>
    public void RenderDetails() => RenderDetailsAsync().Forget("inspector");

    private async Task RenderDetailsAsync()
    {
        if (_vm.SelectedItem is not { } item)
        {
            DetailsViewer.Document = null;
            return;
        }
        var pending = item.MarkdownAsync();
        if (!pending.IsCompleted) DetailsViewer.Document = null;
        var markdown = await pending;
        if (ReferenceEquals(_vm.SelectedItem, item)) Show(markdown);
    }

    private void Show(string markdown)
    {
        // The pane header shows the title (the row's, with its subject); the page's own heading would repeat it.
        markdown = LeadingTitle().Replace(markdown, "", 1);
        Brush B(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        DetailsViewer.Document = MarkdownFlow.Render(markdown,
            B("TextFillColorPrimaryBrush", Brushes.White),
            B("TextFillColorSecondaryBrush", Brushes.Gray),
            B("SubtleFillColorSecondaryBrush", Brushes.DimGray),
            B("DividerStrokeColorDefaultBrush", Brushes.DimGray),
            url => _vm.OpenLinkCommand.Execute(url),
            B("AccentTextFillColorPrimaryBrush", Brushes.DodgerBlue));
    }

    [GeneratedRegex(@"\A\s*#[ \t][^\r\n]*(\r?\n)?")]
    private static partial Regex LeadingTitle();

    /// <summary>
    /// Title bar icon: the hand-tuned bitmap for the current scaling (16, 24 or 32 px for a 16 px slot), never a
    /// scaled-down large icon, which would look blurry at small sizes.
    /// </summary>
    private void SetTitleBarIcon(double scale)
    {
        var size = scale >= 1.75 ? 32 : scale >= 1.25 ? 24 : 16;
        // The window's markup already shows the 16 px one: at 100 % nothing is decoded a second time.
        if (size == _titleIconSize) return;
        _titleIconSize = size;
        var image = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/icon-{size}.png"));
        image.Freeze();
        TitleIcon.Source = image;
    }

    /// <summary>The size of the bitmap the title bar shows (MainWindow.xaml starts with icon-16.png).</summary>
    private int _titleIconSize = 16;
}
