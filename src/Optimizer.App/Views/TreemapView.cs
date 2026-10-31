using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Storage;

namespace Optimizer.App.Views;

/// <summary>
/// The treemap of one folder of a <see cref="DiskTree"/>: every file a rectangle in proportion to its size, colored by its
/// file group, folders laid out inside their own rectangle (squarified, <see cref="SquarifiedLayout"/>). A drive has
/// millions of files, so it is one element that draws one geometry per group; the layout is cached and built again only
/// when the size, the tree or the folder changes. The shown folder's own entries carry their name and size where the
/// tile has room, in black or white by the tile's luminance (a translucent plate only where the label lies on tiles of
/// several colors), readable on every group color in both themes. Screen readers get one name; the folder list is the accessible way to the same data.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    /// <summary>Smallest side a rectangle is drawn (and a folder laid out) with, in device independent pixels.</summary>
    private const double MinSide = 2;

    /// <summary>A tile of the shown folder gets its name from this size on, its size too from <see cref="TwoLineHeight"/>.</summary>
    private const double LabelMinWidth = 80, LabelMinHeight = 32, TwoLineHeight = 52;

    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(nameof(Tree), typeof(DiskTree), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, (d, _) => ((TreemapView)d).Rebuild()));

    public static readonly DependencyProperty FolderProperty = DependencyProperty.Register(nameof(Folder), typeof(int), typeof(TreemapView),
        new FrameworkPropertyMetadata(0, (d, _) => ((TreemapView)d).Rebuild()));

    public static readonly DependencyProperty SelectedEntryProperty = DependencyProperty.Register(nameof(SelectedEntry), typeof(int), typeof(TreemapView),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TreemapView)d).DrawSelection()));

    /// <summary>The 1 px line around each rectangle (the card stroke).</summary>
    private static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register("OutlineBrush", typeof(Brush), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The frame around the selected entry: the primary text color, which stands out on every group color.</summary>
    private static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register("SelectionBrush", typeof(Brush), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, (d, _) => ((TreemapView)d).DrawSelection()));

    /// <summary>
    /// The labels' text where no single tile color lies under it, and the translucent plate under labels on mixed tiles
    /// (primary text on the page background), so a theme switch redraws them.
    /// </summary>
    private static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register("LabelBrush", typeof(Brush), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty LabelPlateBrushProperty = DependencyProperty.Register("LabelPlateBrush", typeof(Brush), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One brush per <see cref="FileGroup"/>, by resource reference (Treemap*Brush), so a theme switch redraws.</summary>
    private static readonly DependencyProperty[] GroupBrushProperties = Enum.GetNames<FileGroup>()
        .Select(g => DependencyProperty.Register(g + "Brush", typeof(Brush), typeof(TreemapView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender)))
        .ToArray();

    private static readonly int GroupCount = GroupBrushProperties.Length;

    /// <summary>Every rectangle laid out (folders and files), each folder before what is in it.</summary>
    private readonly TreemapCells _cells = new();
    private readonly StreamGeometry?[] _groups = new StreamGeometry?[GroupCount];

    /// <summary>The shown folder's entries with room for a label, with their rectangle.</summary>
    private readonly List<(LayoutRect Rect, int Entry)> _labels = [];
    private readonly DrawingVisual _selection = new();
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse };
    private readonly DispatcherTimer _tipDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _resizeDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _layoutValid;
    private Size _layoutSize;
    private Pen? _outline;
    private int _hover = -1;

    public TreemapView()
    {
        for (var g = 0; g < GroupCount; g++) SetResourceReference(GroupBrushProperties[g], $"Treemap{(FileGroup)g}Brush");
        SetResourceReference(OutlineBrushProperty, "CardStrokeColorDefaultBrush");
        SetResourceReference(SelectionBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LabelBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LabelPlateBrushProperty, "SolidBackgroundFillColorBaseBrush");
        AddVisualChild(_selection);
        _tip.PlacementTarget = this;
        _resizeDelay.Tick += (_, _) =>
        {
            _resizeDelay.Stop();
            Rebuild();
        };
        _tipDelay.Tick += (_, _) =>
        {
            _tipDelay.Stop();
            if (_hover >= 0 && IsMouseOver) _tip.IsOpen = true;
        };
    }

    /// <summary>A DPI change moves the device pixel grid the rectangles snap to.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Rebuild();
    }

    public DiskTree? Tree
    {
        get => (DiskTree?)GetValue(TreeProperty);
        set => SetValue(TreeProperty, value);
    }

    /// <summary>The folder (entry of <see cref="Tree"/>) the map shows.</summary>
    public int Folder
    {
        get => (int)GetValue(FolderProperty);
        set => SetValue(FolderProperty, value);
    }

    /// <summary>The entry framed on the map; a click sets it. -1 for none.</summary>
    public int SelectedEntry
    {
        get => (int)GetValue(SelectedEntryProperty);
        set => SetValue(SelectedEntryProperty, value);
    }

    /// <summary>A double-click: the folder that holds the rectangle under the pointer (never the folder shown).</summary>
    public event EventHandler<int>? EntryActivated;

    /// <summary>The map was laid out again: <see cref="Shows"/> may name other groups (the page's legend follows it).</summary>
    public event EventHandler? GroupsShown;

    /// <summary>The map draws files of this group.</summary>
    public bool Shows(FileGroup group) => _groups[(int)group] is not null;

    /// <summary>Lays the map out again: the tree changed in place (entries removed) or the language changed.</summary>
    public void Rebuild()
    {
        _layoutValid = false;
        _hover = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _selection;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // A drive's map takes up to about 0.1 s to lay out: while the window is being resized the last layout is
        // stretched, and the new one is built once the size has stayed for a moment.
        if (_layoutValid && _cells.Count > 0 && _layoutSize.Width > 0 && _layoutSize.Height > 0)
        {
            _resizeDelay.Stop();
            _resizeDelay.Start();
            InvalidateVisual();
        }
        else Rebuild();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent, not empty: the whole area takes the mouse, also where nothing is drawn.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (!_layoutValid) BuildLayout();
        var stretched = _layoutSize != RenderSize && _layoutSize.Width > 0 && _layoutSize.Height > 0;
        var stretch = stretched ? new ScaleTransform(RenderSize.Width / _layoutSize.Width, RenderSize.Height / _layoutSize.Height) : null;
        _selection.Transform = stretch;
        if (stretch is not null) dc.PushTransform(stretch);
        var outline = OutlinePen();
        for (var g = 0; g < GroupCount; g++)
            if (_groups[g] is { } geometry) dc.DrawGeometry((Brush?)GetValue(GroupBrushProperties[g]), outline, geometry);
        DrawLabels(dc);
        if (stretch is not null) dc.Pop();
    }

    /// <summary>
    /// Name, and size where there is room, at the top left of each labeled tile, cut to the tile's width. On one color the
    /// text is drawn straight on the tile, black or white by the tile's luminance; on tiles of several colors (a folder's
    /// small files) on a translucent plate of the page's background, which keeps the tiles visible under it.
    /// </summary>
    private void DrawLabels(DrawingContext dc)
    {
        if (_labels.Count == 0 || Tree is not { } tree || GetValue(LabelBrushProperty) is not Brush text || GetValue(LabelPlateBrushProperty) is not Brush plate) return;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        FormattedText Line(string s, double width) => new(s, culture, FlowDirection.LeftToRight, typeface, 12, text, pixelsPerDip)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        foreach (var (r, entry) in _labels)
        {
            if (entry >= tree.Count || tree.IsRemoved(entry)) continue;
            var width = r.Width - 16;
            var name = Line(tree.Name(entry), width);
            var size = r.Height >= TwoLineHeight ? Line(CleanupViewModel.Size(tree.Size(entry)), width) : null;
            var textWidth = Math.Max(name.WidthIncludingTrailingWhitespace, size?.WidthIncludingTrailingWhitespace ?? 0);
            var area = new Rect(r.X + 4, r.Y + 4, Math.Min(textWidth + 8, r.Width - 8), name.Height + (size?.Height ?? 0) + 4);
            Brush ink;
            switch (Under(tree, area))
            {
                case Background.Tile when TileColor(tree, area) is { } color:
                    ink = InkOn(color);
                    break;
                case Background.Empty:
                    ink = text;
                    break;
                default:
                    dc.PushOpacity(PlateOpacity);
                    dc.DrawRoundedRectangle(plate, null, area, 2, 2);
                    dc.Pop();
                    ink = text;
                    break;
            }
            name.SetForegroundBrush(ink);
            size?.SetForegroundBrush(ink);
            dc.DrawText(name, new Point(r.X + 8, r.Y + 6));
            if (size is not null) dc.DrawText(size, new Point(r.X + 8, r.Y + 6 + name.Height));
        }
    }

    /// <summary>The plate under a label on tiles of several colors: translucent, so it reads as part of the map.</summary>
    private const double PlateOpacity = 0.75;

    private enum Background { Tile, Empty, Mixed }

    /// <summary>
    /// What lies under a label: tiles of one file group, nothing (the map's own background, where a folder's smallest files
    /// were left out), or a mix. Read at the corners and the middle of the label's area.
    /// </summary>
    private Background Under(DiskTree tree, Rect area)
    {
        FileGroup? group = null;
        var empty = 0;
        Point[] points =
        [
            new(area.Left + 1, area.Top + 1), new(area.Right - 1, area.Top + 1), new(area.Left + 1, area.Bottom - 1),
            new(area.Right - 1, area.Bottom - 1), new(area.Left + area.Width / 2, area.Top + area.Height / 2),
        ];
        foreach (var p in points)
        {
            var hit = _cells.Hit(p.X, p.Y);
            if (hit < 0 || tree.IsFolder(hit))
            {
                empty++;
                continue;
            }
            var g = tree.Group(hit);
            if (group is { } seen && seen != g) return Background.Mixed;
            group = g;
        }
        return empty == points.Length ? Background.Empty : empty > 0 ? Background.Mixed : Background.Tile;
    }

    /// <summary>The color of the tile at the label's top left corner.</summary>
    private Color? TileColor(DiskTree tree, Rect area)
    {
        var hit = _cells.Hit(area.Left + 1, area.Top + 1);
        if (hit < 0 || tree.IsFolder(hit)) return null;
        return (GetValue(GroupBrushProperties[(int)tree.Group(hit)]) as SolidColorBrush)?.Color;
    }

    /// <summary>Black or white, whichever has the higher contrast on the color (WCAG relative luminance).</summary>
    private static Brush InkOn(Color color)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        // Contrast with black (L + 0.05) / 0.05 against white 1.05 / (L + 0.05): equal at L = 0.179.
        return luminance > 0.179 ? Brushes.Black : Brushes.White;
    }

    /// <summary>One device pixel wide, in the card stroke color; made again only when the brush or the DPI changes.</summary>
    private Pen? OutlinePen()
    {
        var brush = (Brush?)GetValue(OutlineBrushProperty);
        var px = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (brush is null) return null;
        if (_outline is { } pen && ReferenceEquals(pen.Brush, brush) && pen.Thickness == px) return pen;
        _outline = new Pen(brush, px);
        _outline.Freeze();
        return _outline;
    }

    private void BuildLayout()
    {
        _layoutValid = true;
        _layoutSize = RenderSize;
        _cells.Clear();
        _labels.Clear();
        Array.Clear(_groups);
        // After the render pass that lays out: the legend's change of layout does not belong in it.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => GroupsShown?.Invoke(this, EventArgs.Empty));
        var tree = Tree;
        var folder = Folder;
        if (tree is null || folder < 0 || folder >= tree.Count || tree.IsRemoved(folder))
        {
            AutomationProperties.SetName(this, "");
            DrawSelection();
            return;
        }
        AutomationProperties.SetName(this, Loc.Instance.Format("Storage_TreemapName", tree.FullPath(folder), CleanupViewModel.Size(tree.Size(folder))));

        var dpi = VisualTreeHelper.GetDpi(this);
        var contexts = new StreamGeometryContext?[GroupCount];
        var weights = new double[256];
        var geometries = new StreamGeometry[GroupCount];
        try
        {
            var area = new LayoutRect(0, 0, RenderSize.Width, RenderSize.Height);
            if (tree.IsFolder(folder)) LayOut(folder, area);
            else Add(folder, area);
        }
        finally
        {
            foreach (var c in contexts) c?.Close();
        }
        for (var g = 0; g < GroupCount; g++)
        {
            if (contexts[g] is null) continue;
            geometries[g].Freeze();
            _groups[g] = geometries[g];
        }
        DrawSelection();

        void LayOut(int parent, LayoutRect area)
        {
            var n = tree.ChildCount(parent);
            if (n == 0 || area.Width < MinSide || area.Height < MinSide) return;
            // Children come largest first: the tail that could not get a 2 x 2 px rectangle is laid out as one block that
            // stays empty, so a folder with 100,000 small files costs what its visible rectangles cost.
            var total = (double)tree.Size(parent);
            if (total <= 0) return;
            var smallest = MinSide * MinSide * total / (area.Width * area.Height);
            var shown = 0;
            while (shown < n && tree.Size(tree.Child(parent, shown)) >= smallest) shown++;
            var count = shown < n ? shown + 1 : shown;
            // One buffer for every folder: the weights are no longer needed once the folder's rectangles are placed.
            if (weights.Length < count) weights = new double[Math.Max(count, weights.Length * 2)];
            double sum = 0;
            for (var k = 0; k < shown; k++) sum += weights[k] = tree.Size(tree.Child(parent, k));
            // The folder's size is the sum of its children: the tail is what is left, without visiting it.
            if (shown < n) weights[shown] = Math.Max(0, total - sum);
            var rects = SquarifiedLayout.Layout(new ArraySegment<double>(weights, 0, count), area);
            for (var k = 0; k < shown; k++)
            {
                var r = rects[k];
                if (r.Width < MinSide || r.Height < MinSide) continue;
                var c = tree.Child(parent, k);
                if (parent == folder && r.Width >= LabelMinWidth && r.Height >= LabelMinHeight) _labels.Add((r, c));
                if (tree.IsFolder(c))
                {
                    var cell = _cells.BeginFolder(r, c);
                    LayOut(c, r);
                    _cells.EndFolder(cell);
                }
                else Add(c, r);
            }
        }

        void Add(int file, LayoutRect r)
        {
            _cells.AddLeaf(r, file);
            var g = (int)tree.Group(file);
            if (contexts[g] is null)
            {
                geometries[g] = new StreamGeometry();
                contexts[g] = geometries[g].Open();
            }
            var box = Snap(r, dpi);
            // Half a device pixel in from the edge: the 1 px outline then covers whole pixels and stays sharp.
            var h = 0.5 / dpi.DpiScaleX;
            var v = 0.5 / dpi.DpiScaleY;
            if (box.Width <= 2 * h || box.Height <= 2 * v) return;
            var ctx = contexts[g]!;
            ctx.BeginFigure(new Point(box.Left + h, box.Top + v), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(box.Right - h, box.Top + v), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(box.Right - h, box.Bottom - v), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(box.Left + h, box.Bottom - v), isStroked: true, isSmoothJoin: false);
        }
    }

    /// <summary>The rectangle with its edges on device pixels, so neighbors share an edge instead of a blurred line.</summary>
    private static Rect Snap(LayoutRect r, DpiScale dpi)
    {
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        var left = Math.Round(r.X * sx) / sx;
        var top = Math.Round(r.Y * sy) / sy;
        var right = Math.Round((r.X + r.Width) * sx) / sx;
        var bottom = Math.Round((r.Y + r.Height) * sy) / sy;
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>A 2 px frame around the selected entry, on its own layer: selecting does not draw the map again.</summary>
    private void DrawSelection()
    {
        using var dc = _selection.RenderOpen();
        var selected = SelectedEntry;
        var tree = Tree;
        var folder = Folder;
        if (selected < 0 || !_layoutValid || tree is null || selected >= tree.Count || folder < 0 || folder >= tree.Count) return;
        // The folders from the shown one down to the entry: the cell is looked for only inside them. A file shown on its
        // own is the map's only cell; an entry outside the shown folder has none.
        var path = new List<int>();
        if (selected == folder) path.Add(folder);
        else
            for (var e = selected; e != folder; e = tree.Parent(e))
            {
                if (e < 0) return;
                path.Add(e);
            }
        path.Reverse();
        var cell = _cells.Find(path);
        if (cell < 0) return;
        // The 2 px line runs inside the rectangle; around it when the rectangle is too small to hold it.
        var box = Snap(_cells.Rect(cell), VisualTreeHelper.GetDpi(this));
        if (box.Width >= 4 && box.Height >= 4) box.Inflate(-1, -1);
        else box.Inflate(1, 1);
        var pen = new Pen((Brush?)GetValue(SelectionBrushProperty), 2);
        pen.Freeze();
        dc.DrawRectangle(null, pen, box);
    }

    /// <summary>The deepest entry under a point; -1 outside every rectangle.</summary>
    private int HitEntry(Point p)
    {
        // Mid-resize the map shows the last layout stretched: back into its coordinates.
        if (_layoutSize != RenderSize && RenderSize.Width > 0 && RenderSize.Height > 0)
            p = new Point(p.X * _layoutSize.Width / RenderSize.Width, p.Y * _layoutSize.Height / RenderSize.Height);
        return _cells.Hit(p.X, p.Y);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Tree is not { } tree) return;
        var hit = HitEntry(e.GetPosition(this));
        if (hit < 0) return;
        _tip.IsOpen = false;
        if (e.ClickCount == 2)
        {
            // The folder the tile lies in (a folder tile too small to show its files: that folder).
            var folder = tree.IsFolder(hit) ? hit : tree.Parent(hit);
            if (folder >= 0 && folder != Folder) EntryActivated?.Invoke(this, folder);
        }
        else SelectedEntry = hit;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitEntry(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        // A new rectangle: the tip closes and opens again at the pointer after a short pause, as tooltips do.
        _tip.IsOpen = false;
        _tipDelay.Stop();
        if (hit < 0 || Tree is not { } tree) return;
        _tip.Content = TipText(tree, hit);
        _tipDelay.Start();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tipDelay.Stop();
        _tip.IsOpen = false;
    }

    /// <summary>Path, size and type ("Video", or "in 1,234 files" for a folder).</summary>
    private static string TipText(DiskTree tree, int entry)
    {
        var size = CleanupViewModel.Size(tree.Size(entry));
        var detail = tree.IsFolder(entry)
            ? tree.Files(entry) == 1 ? Loc.Instance.Format("Storage_TreemapTipFolderOne", size) : Loc.Instance.Format("Storage_TreemapTipFolder", size, tree.Files(entry))
            : Loc.Instance.Format("Storage_TreemapTipFile", size, FileGroupNames.Name(tree.Group(entry)));
        return tree.FullPath(entry) + "\n" + detail;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreemapAutomationPeer(this);

    /// <summary>One image with a name ("Treemap of C:\Users, 180 GB"): its rectangles are not separate elements.</summary>
    private sealed class TreemapAutomationPeer(TreemapView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(TreemapView);
    }
}
