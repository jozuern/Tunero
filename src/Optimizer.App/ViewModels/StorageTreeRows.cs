using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Optimizer.App.Services;
using Optimizer.Core.Storage;

namespace Optimizer.App.ViewModels;

/// <summary>
/// One entry of the folder list: a file or folder of the scanned tree at its depth. Only <see cref="IsExpanded"/> changes,
/// so the list keeps the same row (and its selection) when a folder opens or closes.
/// </summary>
public sealed partial class TreeRow(int entry, int depth, string name, string sizeText, double share, string filesText, string lastChange,
    bool isFolder, bool isExpanded) : ObservableObject
{
    public int Entry { get; } = entry;
    public int Depth { get; } = depth;
    public string Name { get; } = name;
    public string SizeText { get; } = sizeText;
    /// <summary>Share of the parent folder's size, 0 to 1, for the bar in the row.</summary>
    public double Share { get; } = share;
    /// <summary>Number of files below a folder; empty for a file.</summary>
    public string FilesText { get; } = filesText;
    public string LastChange { get; } = lastChange;
    public bool IsFolder { get; } = isFolder;
    /// <summary>A folder with something in it: the row shows a chevron.</summary>
    public bool CanExpand { get; init; }
    /// <summary>
    /// A link (junction, symbolic link, mount point): the scan does not follow it, so it shows no size and "Link" where
    /// the file count goes, not "0 KB" and "0" like an empty folder.
    /// </summary>
    public bool IsLink { get; init; }
    /// <summary>A folder the scan could not look into: "Not readable" where the file count goes, beside what was counted.</summary>
    public bool IsUnreadable { get; init; }
    /// <summary>Indentation of the name, 16 px per level.</summary>
    public double Indent => Depth * 16;

    [ObservableProperty] private bool _isExpanded = isExpanded;

    // What screen readers announce for this item in a list: for a link or a folder that could not be read, that too.
    public override string ToString() => string.Join(", ", new[] { Name, SizeText, IsLink || IsUnreadable ? FilesText : "" }.Where(s => s.Length > 0));
}

/// <summary>
/// The scanned tree as a flat list for a virtualized ListView: the root's children first, and a folder's children right
/// after it once it is expanded. A drive holds millions of entries, so only expanded folders ever become rows.
/// </summary>
public sealed class StorageTreeRows
{
    private readonly RowCollection _rows = [];

    public ObservableCollection<TreeRow> Rows => _rows;

    /// <summary>The tree the rows come from; null before the first <see cref="Reset"/>.</summary>
    public DiskTree? Tree { get; private set; }

    /// <summary>A new tree: only the root's children, nothing expanded.</summary>
    public void Reset(DiskTree tree)
    {
        Tree = tree;
        _rows.ReplaceAll(Build(tree.Root, 0, null));
    }

    /// <summary>No result: no rows.</summary>
    public void Clear()
    {
        Tree = null;
        _rows.ReplaceAll([]);
    }

    /// <summary>
    /// The same tree after entries were removed (or texts in another language): builds the rows again with the same
    /// folders expanded, as far as they still exist.
    /// </summary>
    public void Refresh()
    {
        if (Tree is null) return;
        var expanded = _rows.Where(r => r.IsExpanded).Select(r => r.Entry).ToHashSet();
        _rows.ReplaceAll(Build(Tree.Root, 0, expanded));
    }

    /// <summary>Expands a folder row (its children follow it) or collapses it (everything below it goes).</summary>
    public void Toggle(TreeRow row)
    {
        if (row.IsExpanded) Collapse(row);
        else Expand(row);
    }

    public void Expand(TreeRow row)
    {
        if (Tree is null || row.IsExpanded || !row.CanExpand) return;
        var at = _rows.IndexOf(row);
        if (at < 0) return;
        row.IsExpanded = true;
        _rows.InsertRange(at + 1, Build(row.Entry, row.Depth + 1, null));
    }

    public void Collapse(TreeRow row)
    {
        if (!row.IsExpanded) return;
        var at = _rows.IndexOf(row);
        if (at < 0) return;
        row.IsExpanded = false;
        var end = at + 1;
        while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
        _rows.RemoveRange(at + 1, end - at - 1);
    }

    /// <summary>The row of the folder that holds <paramref name="row"/>; null at the top level.</summary>
    public TreeRow? ParentOf(TreeRow row)
    {
        for (var i = _rows.IndexOf(row) - 1; i >= 0; i--)
            if (_rows[i].Depth < row.Depth) return _rows[i];
        return null;
    }

    /// <summary>Expands every folder above an entry and returns its row; null for the root, which has no row.</summary>
    public TreeRow? Reveal(int entry)
    {
        if (Tree is not { } tree || entry == tree.Root || entry < 0 || entry >= tree.Count) return null;
        var chain = new List<int>();
        for (var e = entry; e != tree.Root && e >= 0; e = tree.Parent(e)) chain.Add(e);
        chain.Reverse();
        TreeRow? row = null;
        foreach (var e in chain)
        {
            if (row is not null) Expand(row);
            row = Find(e, row);
            if (row is null) return null;   // removed meanwhile
        }
        return row;
    }

    /// <summary>The row of an entry among the rows after <paramref name="parent"/> (all rows when null).</summary>
    private TreeRow? Find(int entry, TreeRow? parent)
    {
        var start = parent is null ? 0 : _rows.IndexOf(parent) + 1;
        for (var i = start; i < _rows.Count; i++)
        {
            if (parent is not null && _rows[i].Depth <= parent.Depth) break;
            if (_rows[i].Entry == entry) return _rows[i];
        }
        return null;
    }

    /// <summary>The rows of a folder's children (largest first, as the tree keeps them) and of every expanded folder below.</summary>
    private List<TreeRow> Build(int folder, int depth, HashSet<int>? expanded)
    {
        var tree = Tree!;
        var list = new List<TreeRow>();
        Add(folder, depth);
        return list;

        void Add(int parent, int level)
        {
            var parentSize = tree.Size(parent);
            for (var k = 0; k < tree.ChildCount(parent); k++)
            {
                var c = tree.Child(parent, k);
                var open = expanded?.Contains(c) == true && tree.ChildCount(c) > 0;
                list.Add(Row(tree, c, level, parentSize, open));
                if (open) Add(c, level + 1);
            }
        }
    }

    private static TreeRow Row(DiskTree tree, int i, int depth, long parentSize, bool expanded)
    {
        var folder = tree.IsFolder(i);
        var size = tree.Size(i);
        var flags = tree.Flags(i);
        // A link is not followed, so its size says nothing; a folder that could not be read keeps what was counted.
        var link = (flags & EntryFlags.ReparsePoint) != 0;
        var unreadable = !link && folder && (flags & EntryFlags.Unreadable) != 0;
        return new TreeRow(i, depth, tree.Name(i), link ? "" : CleanupViewModel.Size(size), parentSize > 0 ? (double)size / parentSize : 0,
            link ? Loc.Instance["Storage_LinkRow"]
            : unreadable ? Loc.Instance["Storage_UnreadableRow"]
            : folder ? tree.Files(i).ToString("N0", CultureInfo.CurrentCulture) : "",
            tree.LastWriteUtc(i) == default ? "" : tree.LastWriteUtc(i).ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
            folder, expanded)
        { CanExpand = tree.ChildCount(i) > 0, IsLink = link, IsUnreadable = unreadable };
    }

    /// <summary>
    /// Inserts and removes many rows with one Reset notification: expanding a folder with 20,000 files one Insert at a
    /// time made the list handle 20,000 events. Small changes stay single events, so the list keeps its scroll position.
    /// </summary>
    private sealed class RowCollection : ObservableCollection<TreeRow>
    {
        private const int Many = 64;

        public void ReplaceAll(List<TreeRow> rows)
        {
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
            Changed();
        }

        public void InsertRange(int index, List<TreeRow> rows)
        {
            if (rows.Count <= Many)
            {
                for (var k = 0; k < rows.Count; k++) InsertItem(index + k, rows[k]);
                return;
            }
            ((List<TreeRow>)Items).InsertRange(index, rows);
            Changed();
        }

        public void RemoveRange(int index, int count)
        {
            if (count <= Many)
            {
                for (var k = 0; k < count; k++) RemoveItem(index);
                return;
            }
            ((List<TreeRow>)Items).RemoveRange(index, count);
            Changed();
        }

        private void Changed()
        {
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
