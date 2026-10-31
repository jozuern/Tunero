using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Optimizer.App.Services;
using Optimizer.Core.Storage;

namespace Optimizer.App.ViewModels;

/// <summary>A fixed drive in the picker: its root and how full it is (read from DriveInfo only).</summary>
public sealed record DriveRow(string Root, long Total, long Free)
{
    /// <summary>Used share of the drive, 0 to 1, for the bar under its name.</summary>
    public double UsedShare => Total > 0 ? Math.Clamp((double)(Total - Free) / Total, 0, 1) : 0;

    /// <summary>"120 GB free of 931 GB", in the language the row was built in.</summary>
    public string SpaceText { get; } = Loc.Instance.Format("Storage_DriveSpace", CleanupViewModel.Size(Free), CleanupViewModel.Size(Total));

    // What screen readers announce for this item in a combo box.
    public override string ToString() => $"{Root}, {SpaceText}";
}

/// <summary>
/// A file in the Largest files, File types or Duplicates list; <see cref="Entry"/> is its index in the scanned tree.
/// Every row can be selected (Open in Explorer, Copy path); <see cref="BlockText"/> says why Recycle is not offered.
/// </summary>
public sealed partial class StorageFileRow(string path, long bytes, string meta, string? blockText, int entry, Action? selectionChanged = null) : ObservableObject
{
    partial void OnSelectedChanged(bool value) => selectionChanged?.Invoke();

    // What screen readers announce for this item in a list.
    public override string ToString() => Path;

    public string Path { get; } = path;
    public long Bytes { get; } = bytes;
    public string SizeText { get; } = CleanupViewModel.Size(bytes);
    public string Meta { get; } = meta;
    /// <summary>Why the file may not go to the Recycle Bin ("Protected: ..."); null when it may.</summary>
    public string? BlockText { get; } = blockText;
    public int Entry { get; } = entry;

    [ObservableProperty] private bool _selected;
}

/// <summary>One folder of the breadcrumb above the treemap: the analyzed root, then each folder down to the shown one.</summary>
public sealed record FolderCrumb(int Entry, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The name of a file group in the app language (legend, treemap tooltip, File types tab).</summary>
public static class FileGroupNames
{
    public static string Name(FileGroup group) => Loc.Instance[group switch
    {
        FileGroup.Video => "Storage_GroupVideo",
        FileGroup.Audio => "Storage_GroupAudio",
        FileGroup.Images => "Storage_GroupImages",
        FileGroup.Archives => "Storage_GroupArchives",
        FileGroup.Programs => "Storage_GroupPrograms",
        FileGroup.Documents => "Storage_GroupDocuments",
        FileGroup.System => "Storage_GroupSystem",
        _ => "Storage_GroupOther",
    }];
}

/// <summary>One extension of the File types tab (".mp4"); an empty <see cref="Extension"/> is the files without one.</summary>
public sealed partial class TypeRow(TypeTotal total, double share) : ObservableObject
{
    /// <summary>Lower case, no dot; the key <see cref="DiskTree.LargestFilesOfType"/> takes.</summary>
    public string Extension { get; } = total.Extension;
    public string Name { get; } = total.Extension.Length == 0 ? Loc.Instance["Storage_NoExtension"] : "." + total.Extension;
    public long Bytes { get; } = total.Bytes;
    public int Count { get; } = total.Count;
    public string SizeText { get; } = CleanupViewModel.Size(total.Bytes);
    public string CountText { get; } = total.Count.ToString("N0", CultureInfo.CurrentCulture);
    /// <summary>Share of everything the analysis read, 0 to 1, for the bar in the row.</summary>
    public double Share { get; } = share;

    /// <summary>The extension whose largest files are listed.</summary>
    [ObservableProperty] private bool _isSelected;

    // What screen readers announce for this item in a list.
    public override string ToString() => $"{Name}, {SizeText}";
}

/// <summary>
/// One file group of the File types tab with its totals. <see cref="Extensions"/> stays empty until the group is opened:
/// a drive can hold thousands of extensions, and only the ones the user looks at become rows.
/// </summary>
public sealed partial class TypeGroupRow(FileGroup group, long bytes, int count, double share, IReadOnlyList<TypeTotal> totals, double all,
    Action<TypeRow?>? picked = null) : ObservableObject
{
    /// <summary>Most extension rows one group shows (the rest is a count): a group can hold thousands.</summary>
    public const int MaxExtensions = 200;

    public FileGroup Group { get; } = group;
    public string Name { get; } = FileGroupNames.Name(group);
    public long Bytes { get; } = bytes;
    public int Count { get; } = count;
    public string SizeText { get; } = CleanupViewModel.Size(bytes);
    public string CountText { get; } = count.ToString("N0", CultureInfo.CurrentCulture);
    /// <summary>Share of everything the analysis read, 0 to 1, for the bar in the row.</summary>
    public double Share { get; } = share;

    /// <summary>The resource key of the group's treemap color, so the tab and the map legend agree.</summary>
    public string BrushKey => $"Treemap{Group}Brush";

    public ObservableCollection<TypeRow> Extensions { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    /// <summary>The extension picked in this group's list (the page's selection list binds to it); the view model clears it when another group's is picked.</summary>
    [ObservableProperty] private TypeRow? _selected;

    /// <summary>"12 more types" under the list when the group has more extensions than it shows; null otherwise.</summary>
    [ObservableProperty] private string? _moreText;

    partial void OnSelectedChanged(TypeRow? value)
    {
        if (value is not null) picked?.Invoke(value);
    }

    /// <summary>
    /// The row of an extension of this open group, added below the others when it ranks past <see cref="MaxExtensions"/>
    /// (the one picked before a rebuild, which a removal can move down); null when the group does not have it.
    /// </summary>
    public TypeRow? Show(string extension)
    {
        if (Extensions.FirstOrDefault(t => t.Extension == extension) is { } shown) return shown;
        if (!IsExpanded || totals.FirstOrDefault(t => t.Extension == extension) is not { } total) return null;
        var row = new TypeRow(total, all > 0 ? total.Bytes / all : 0);
        Extensions.Add(row);
        var hidden = totals.Count - Extensions.Count;
        MoreText = hidden > 0 ? MoreTypes(hidden) : null;
        return row;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        // Closing the group of the picked extension leaves nothing picked: its file list would hang in the air.
        if (!value && Selected is not null) picked?.Invoke(null);
        Extensions.Clear();
        MoreText = null;
        if (!value) return;
        foreach (var t in totals.Take(MaxExtensions)) Extensions.Add(new TypeRow(t, all > 0 ? t.Bytes / all : 0));
        if (totals.Count > MaxExtensions) MoreText = MoreTypes(totals.Count - MaxExtensions);
    }

    /// <summary>"3 more types", in the singular for one.</summary>
    private static string MoreTypes(int hidden) => hidden == 1 ? Loc.Instance["Storage_MoreTypesOne"] : Loc.Instance.Format("Storage_MoreTypes", hidden);

    // What screen readers announce for this item in a list.
    public override string ToString() => $"{Name}, {SizeText}";
}
