using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Storage;
using Optimizer.Core.Tools;
using Wpf.Ui.Controls;

namespace Optimizer.App.ViewModels;

/// <summary>
/// The Storage page: a drive (or folder) read into a <see cref="DiskTree"/> on request only, shown in four tabs. Opening
/// the page reads the drive list and nothing else; Analyze and Find duplicates run under the progress banner with Stop.
/// Only the open tab builds its rows. The command bar acts on the selection of the open tab: Open in Explorer, Copy
/// path, and Move to Recycle Bin for files and folders.
/// </summary>
/// <param name="scanner">Reads a root into a tree; tests pass their own. Default: <see cref="VolumeScanners.ScanAsync"/>.</param>
/// <param name="recycle">
/// Moves items to the Recycle Bin and returns the paths that were not moved; tests pass a recorder, so no test reaches the
/// Recycle Bin. Default: <see cref="StorageAnalyzer.RecycleItems"/> with the protected roots and kept folders of the
/// last analysis, which checks every item again at the moment of the move and always refuses AppData.
/// </param>
/// <param name="appData">Refuse every AppData folder in the selection check; off only in tests, whose temp folder lies there.</param>
public sealed partial class StorageViewModel(IPageHost owner, AppServices services, IDialogs dialogs,
    Func<string, IProgress<ScanProgress>?, CancellationToken, Task<DiskTree>>? scanner = null,
    Func<IReadOnlyList<(string Path, bool Folder)>, IReadOnlyList<string>>? recycle = null, bool appData = true) : PageViewModel(owner)
{
    protected override IReadOnlyList<Page> ShownOn => [Page.Storage];

    public const int TabFolders = 0, TabTypes = 1, TabLargest = 2, TabDuplicates = 3;

    private readonly Func<string, IProgress<ScanProgress>?, CancellationToken, Task<DiskTree>> _scan =
        scanner ?? ((root, progress, ct) => VolumeScanners.ScanAsync(root, services.Elevation.IsElevated, progress, ct));

    /// <summary>
    /// What the last analysis may not offer: protected roots (Windows, programs, game libraries of that PC scan) and kept
    /// folders (drive roots, profiles, Desktop and the like), read once per analysis and normalized once for the
    /// per-row check (<see cref="RecycleRules.CheckNormalized"/>). <see cref="Above"/>: a cloud folder or another reparse
    /// point at or above an analyzed folder, which holds every entry (<see cref="RecycleRules.CheckAbove"/>).
    /// </summary>
    private sealed record RecycleScope(IReadOnlyList<string> Protected, IReadOnlyCollection<string> Kept, string[] ProtectedNormalized,
        string[] KeptNormalized, RecycleBlock? Above);

    /// <summary>
    /// The path checks of the current tree, by path and kind. They read the disk (long names, folder attributes), so a
    /// file list runs them on a worker thread before its rows are added (<see cref="RowsBuilt"/>), and the lists built
    /// again after a removal or a language switch find them here. Touched on the UI thread only; the move checks every
    /// item again anyway.
    /// </summary>
    private readonly Dictionary<(string Path, bool Folder), RecycleBlock?> _blocks = new();

    /// <summary>
    /// The file rows being built (their path checks run on a worker thread first); complete when none are. Rows built
    /// again from checks already made are added at once. Tests and the --storage-scan switch wait for it.
    /// </summary>
    public Task RowsBuilt { get; private set; } = Task.CompletedTask;

    /// <summary>Raised whenever the file lists are dropped: rows still being built for them are not added.</summary>
    private int _rowsVersion;

    /// <summary>Null before the first analysis: then nothing is offered.</summary>
    private RecycleScope? _rules;

    /// <summary>A selected entry as the command bar acts on it; <see cref="Block"/> is why it may not be recycled.</summary>
    private sealed record Pick(int Entry, string Path, bool Folder, long Bytes, string? Block);

    private IReadOnlyList<Pick> _picks = [];

    // The tests pass no services.
    private ElevationInfo? Elevation => services?.Elevation;

    public ObservableCollection<DriveRow> Drives { get; } = [];
    public ObservableCollection<StorageFileRow> LargestFiles { get; } = [];
    public ObservableCollection<StorageFileRow> Duplicates { get; } = [];

    /// <summary>The File types tab: groups by size, and the largest 100 files of the selected extension.</summary>
    public ObservableCollection<TypeGroupRow> TypeGroups { get; } = [];
    public ObservableCollection<StorageFileRow> TypeFiles { get; } = [];

    /// <summary>The Folders tab: the tree as rows (only expanded folders), the folder the treemap shows and its path.</summary>
    public StorageTreeRows Folders { get; } = new();
    public ObservableCollection<FolderCrumb> Crumbs { get; } = [];

    /// <summary>The folder (entry of <see cref="Tree"/>) the treemap shows.</summary>
    [ObservableProperty] private int _folder;

    /// <summary>The entry selected in the folder list or on the treemap; -1 for none. Both follow each other.</summary>
    [ObservableProperty] private int _selectedEntry = -1;

    [ObservableProperty] private TreeRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyHint))]
    private DriveRow? _selectedDrive;

    /// <summary>Before an analysis: "Select Analyze to read C:\." for the drive picked.</summary>
    public string EmptyHint => Loc.Instance.Format("Storage_EmptyHint", SelectedDrive?.Root ?? "");

    /// <summary>The extension whose files <see cref="TypeFiles"/> lists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeFilesTitle))]
    private TypeRow? _selectedType;

    /// <summary>"Largest files: .mp4" above the list of the selected extension; null without one.</summary>
    public string? TypeFilesTitle => SelectedType is { } t ? Loc.Instance.Format("Storage_TypeFiles", t.Name) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(NoResult), nameof(CanFindDuplicates))]
    [NotifyCanExecuteChangedFor(nameof(FindDuplicatesCommand))]
    private DiskTree? _tree;

    /// <summary>"120 GB in 84,000 files, read folder by folder in 12.3 s"; null without a result.</summary>
    [ObservableProperty] private string? _resultText;

    /// <summary>The open tab: <see cref="TabFolders"/>, <see cref="TabTypes"/>, <see cref="TabLargest"/>, <see cref="TabDuplicates"/>.</summary>
    [ObservableProperty] private int _tab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isFindingDuplicates;

    /// <summary>Find duplicates ran on the current tree (the tab shows its list, or that there are none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoDuplicates))]
    private bool _duplicatesSearched;

    /// <summary>"Duplicates: 1.2 GB can be freed" after Find duplicates.</summary>
    [ObservableProperty] private string? _duplicatesText;

    /// <summary>How many entries the open tab has selected (the folder list one, the file lists every ticked row).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanRecycle), nameof(CanOpenInExplorer), nameof(CanCopyPath), nameof(RecycleText))]
    [NotifyCanExecuteChangedFor(nameof(RecycleCommand), nameof(OpenInExplorerCommand), nameof(CopyPathCommand))]
    private int _selectionCount;

    /// <summary>The tree entries of the selection (-1 for a duplicate the tree does not know).</summary>
    [ObservableProperty] private IReadOnlyList<int> _selectedEntries = [];

    /// <summary>Why the selection may not go to the Recycle Bin: the reason of the first refused entry, or every copy of a duplicate group; null when it may.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecycle))]
    [NotifyCanExecuteChangedFor(nameof(RecycleCommand))]
    private string? _selectionBlock;

    public bool HasSelection => SelectionCount > 0;
    public bool HasResult => Tree is not null;
    public bool NoResult => Tree is null;
    public bool IsBusy => IsAnalyzing || IsFindingDuplicates;

    /// <summary>"Move to Recycle Bin (3)" with a selection.</summary>
    public string RecycleText => SelectionCount > 0 ? Loc.Instance.Format("Storage_RecycleCount", SelectionCount) : Loc.Instance["Storage_Recycle"];

    // Analyze, Find duplicates and Recycle exclude each other: the tree is not thread safe, and a recycle removes entries
    // from it while Find duplicates would read it on a worker thread.
    public bool CanAnalyze => !IsBusy && !IsWorking;
    public bool CanFindDuplicates => Tree is not null && !IsBusy && !IsWorking;
    public bool CanRecycle => SelectionCount > 0 && SelectionBlock is null && !IsBusy && !IsWorking;
    /// <summary>File Explorer selects one item: with more, it would be a guess which.</summary>
    public bool CanOpenInExplorer => SelectionCount == 1;
    public bool CanCopyPath => SelectionCount > 0;

    partial void OnIsAnalyzingChanged(bool value) => BusyChanged();
    partial void OnIsFindingDuplicatesChanged(bool value) => BusyChanged();
    protected override void WorkingChanged() => BusyChanged();

    private void BusyChanged()
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanFindDuplicates));
        OnPropertyChanged(nameof(CanRecycle));
        AnalyzeCommand.NotifyCanExecuteChanged();
        FindDuplicatesCommand.NotifyCanExecuteChanged();
        RecycleCommand.NotifyCanExecuteChanged();
    }
    public bool NoDuplicates => DuplicatesSearched && Duplicates.Count == 0;

    /// <summary>After a scan, Close result, a removal and a language switch: views drawn from the tree (the treemap) build again.</summary>
    public event EventHandler? TreeChanged;

    private CancellationTokenSource? _cancel;
    private bool _largestFilled;
    private bool _foldersFilled;
    private bool _typesFilled;
    // Kept over a rebuild of the same tree (a removal, a language switch): what was open stays open.
    private readonly HashSet<FileGroup> _openGroups = [];
    private string? _openType;
    private IReadOnlyList<DuplicateGroup> _duplicateGroups = [];
    private Dictionary<string, int> _duplicateEntries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Only the drive list (used and free space): nothing on a drive is read before Analyze.</summary>
    protected override async Task LoadAsync()
    {
        // Asking a drive whether it is ready can take seconds (a sleeping disk): off the UI thread.
        var drives = await Task.Run(ReadDrives);
        var selected = SelectedDrive?.Root;
        // The combo box sets its selection to null while its rows are replaced: that is no other drive.
        _refreshingDrives = true;
        try
        {
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
            SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root, selected, StringComparison.OrdinalIgnoreCase)) ?? Drives.FirstOrDefault();
        }
        finally
        {
            _refreshingDrives = false;
        }
        if (selected is not null && !string.Equals(SelectedDrive?.Root, selected, StringComparison.OrdinalIgnoreCase)) SetTree(null);
        // A language switch (which reads the drive list again) builds the texts drawn from the tree in the new language.
        if (_textLanguage != Lang)
        {
            _textLanguage = Lang;
            Relabel();
        }
    }

    private bool _refreshingDrives;
    private string? _textLanguage;

    private void Relabel()
    {
        OnPropertyChanged(nameof(EmptyHint));
        if (Tree is { } tree) ResultText = Summary(tree);
        if (DuplicatesSearched) ShowDuplicates();
        _rowsVersion++;
        LargestFiles.Clear();
        _largestFilled = false;
        _foldersFilled = false;
        ResetTypes(keep: true);
        FillTab();
        UpdateSelection();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static List<DriveRow> ReadDrives()
    {
        var rows = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try
            {
                if (d.IsReady) rows.Add(new DriveRow(d.RootDirectory.FullName, d.TotalSize, d.TotalFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return rows;
    }

    /// <summary>Another drive: the result of the last one no longer belongs to the picker (a language switch keeps it).</summary>
    partial void OnSelectedDriveChanged(DriveRow? oldValue, DriveRow? newValue)
    {
        if (!_refreshingDrives && Tree is not null && !string.Equals(oldValue?.Root, newValue?.Root, StringComparison.OrdinalIgnoreCase)) SetTree(null);
    }

    // The command bar follows the open tab's selection.
    partial void OnTabChanged(int value)
    {
        FillTab();
        UpdateSelection();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => SelectedDrive is { } drive ? AnalyzeRootAsync(drive.Root, isDrive: true) : Task.CompletedTask;

    /// <summary>Analyzes a folder instead of a drive (the --storage-scan developer switch and tests).</summary>
    public Task AnalyzeFolderAsync(string folder) => AnalyzeRootAsync(folder, isDrive: false);

    /// <param name="isDrive">The drive of the picker: a result that comes when the picker shows another one (the drive list was read again meanwhile) is dropped.</param>
    private async Task AnalyzeRootAsync(string root, bool isDrive)
    {
        // One analysis at a time; not while duplicates are hashed from the tree it would replace or files are recycled.
        if (!CanAnalyze) return;
        // Game folders come from the PC scan; without it they could be offered for deletion.
        if (Owner.Profile?.Software is not { } software)
        {
            Owner.ShowResult(Loc.Instance["Storage_WaitForScan"], InfoBarSeverity.Warning);
            return;
        }
        _cancel = new CancellationTokenSource();
        IsAnalyzing = true;
        try
        {
            // Read for every analysis: a scan since the page opened may have found a new library, a profile may be new.
            var session = services;
            var rules = await Task.Run(() =>
            {
                var protectedRoots = StorageAnalyzer.ProtectedRoots(software.GameLibraryPaths.Concat(software.OtherGameFolders));
                // The signed-in user's folders too, not only those of the account the app runs as (the tests pass no services).
                var kept = RecycleRules.KeptFolders(session?.SessionKnownFolders());
                return new RecycleScope(protectedRoots, kept, RecycleRules.NormalizeAll(protectedRoots), RecycleRules.NormalizeAll(kept),
                    RecycleRules.CheckAbove(root));
            });
            // Only reads: no change gate, but the banner (with Stop) like every long run. The tree before stays until the
            // new one is complete, so a stopped analysis leaves the page as it was.
            using var work = Owner.Runner.Work.Begin(Loc.Instance["Work_StorageScan"], _cancel);
            var progress = new Progress<ScanProgress>(p => work.Report(p.Percent, Loc.Instance.Format("Storage_Progress", p.Files, CleanupViewModel.Size(p.Bytes))));
            var tree = await _scan(root, progress, _cancel.Token);
            if (isDrive && !string.Equals(SelectedDrive?.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("storage", "analysis dropped, another drive is selected");
                return;
            }
            _rules = rules;
            Log.Info("storage", "analysis done", new { reader = tree.Reader.ToString(), files = tree.Files(tree.Root), lost = tree.Lost, ms = (long)tree.Elapsed.TotalMilliseconds });
            SetTree(tree);
        }
        catch (OperationCanceledException)
        {
            Owner.ShowResult(Loc.Instance["Storage_Stopped"]);
        }
        catch (Exception ex)
        {
            // The page stays usable with the result before (a root that vanished, a reader that failed).
            Log.Error("storage", "analysis failed", ex);
            Owner.ShowResult(Loc.Instance.Format("Result_Error", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            IsAnalyzing = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    [RelayCommand]
    private void CloseResult() => SetTree(null);

    /// <summary>A new tree (or none): every list drawn from the old one goes, and the open tab builds its rows again.</summary>
    private void SetTree(DiskTree? tree)
    {
        _blocks.Clear();
        Tree = tree;
        ResultText = tree is null ? null : Summary(tree);
        _duplicateGroups = [];
        _duplicateEntries = new(StringComparer.OrdinalIgnoreCase);
        Duplicates.Clear();
        DuplicatesSearched = false;
        DuplicatesText = null;
        RefreshFromTree(keepTypes: false);
    }

    /// <summary>The tree changed (new, closed, entries removed): lists are built again when their tab is open.</summary>
    private void RefreshFromTree(bool keepTypes = true)
    {
        _rowsVersion++;
        LargestFiles.Clear();
        _largestFilled = false;
        _foldersFilled = false;
        ResetTypes(keepTypes);
        if (Tree is null)
        {
            Folders.Clear();
            Crumbs.Clear();
            SelectedEntry = -1;
        }
        FillTab();
        UpdateSelection();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The result line; entries the reader could not see (<see cref="DiskTree.Lost"/>) are counted in it.</summary>
    private static string Summary(DiskTree tree)
    {
        var files = tree.Files(tree.Root);
        var text = Loc.Instance.Format(files == 1 ? "Storage_ResultOneFile" : "Storage_Result", CleanupViewModel.Size(tree.Size(tree.Root)), files,
            Loc.Instance[tree.Reader == ScanReader.FileTable ? "Storage_ReaderFileTable" : "Storage_ReaderFolders"], tree.Elapsed.TotalSeconds);
        return tree.Lost switch
        {
            0 => text,
            1 => Loc.Instance.Format("Storage_ResultLostOne", text),
            var lost => Loc.Instance.Format("Storage_ResultLost", text, lost),
        };
    }

    private void FillTab()
    {
        if (Tab == TabFolders && !_foldersFilled && Tree is { } shown)
        {
            _foldersFilled = true;
            // The same tree after a removal or a language switch keeps its expanded folders; a new one starts at the root.
            if (ReferenceEquals(Folders.Tree, shown))
            {
                Folders.Refresh();
                if (shown.IsRemoved(Folder)) Folder = shown.Root;
                if (SelectedEntry >= 0 && shown.IsRemoved(SelectedEntry)) SelectedEntry = -1;
                SelectedRow = SelectedEntry >= 0 ? Folders.Reveal(SelectedEntry) : null;
            }
            else
            {
                Folders.Reset(shown);
                SelectedEntry = -1;
                SelectedRow = null;
                Folder = shown.Root;
            }
            UpdateCrumbs();
        }
        if (Tab == TabTypes && !_typesFilled && Tree is { } typed) FillTypes(typed);
        if (Tab == TabLargest && !_largestFilled && Tree is { } tree)
        {
            _largestFilled = true;
            Track(FillFilesAsync(LargestFiles, tree, tree.LargestFiles(100), () => true));
        }
    }

    private void Track(Task fill) => RowsBuilt = RowsBuilt.IsCompleted ? fill : Task.WhenAll(RowsBuilt, fill);

    /// <summary>
    /// Adds the rows of these entries once their path checks are made, off the UI thread; nothing when the list was
    /// dropped meanwhile or <paramref name="wanted"/> says the rows are no longer asked for (another extension).
    /// </summary>
    private async Task FillFilesAsync(ObservableCollection<StorageFileRow> into, DiskTree tree, IReadOnlyList<int> entries, Func<bool> wanted)
    {
        var version = _rowsVersion;
        var paths = entries.Select(tree.FullPath).ToList();
        try
        {
            await CheckPathsAsync(paths);
        }
        catch (Exception ex)
        {
            // The rows come anyway: their checks are made on the UI thread then.
            Log.Error("storage", "path checks failed", ex);
        }
        if (version != _rowsVersion || !ReferenceEquals(tree, Tree) || !wanted()) return;
        for (var k = 0; k < entries.Count; k++) into.Add(FileRow(tree, entries[k], paths[k]));
        UpdateSelection();
    }

    /// <summary>Makes the path checks of these files that are not made yet, on a worker thread; completes at once when all are.</summary>
    private async Task CheckPathsAsync(IEnumerable<string> files)
    {
        if (_rules is not { } rules) return;
        var missing = files.Select(p => (Path: p, Folder: false)).Where(k => !_blocks.ContainsKey(k)).Distinct().ToList();
        if (missing.Count == 0) return;
        var found = await Task.Run(() => missing.Select(k => (Key: k, Block: PathBlock(k.Path, k.Folder, rules))).ToList());
        // Another analysis meanwhile: these belong to its predecessor.
        if (!ReferenceEquals(rules, _rules)) return;
        foreach (var (key, block) in found) _blocks.TryAdd(key, block);
    }

    /// <summary>The path rules for one item (reads the disk); a path that cannot be normalized counts as protected. Thread safe.</summary>
    private RecycleBlock? PathBlock(string path, bool folder, RecycleScope rules)
    {
        try
        {
            return RecycleRules.CheckNormalized(path, folder, systemFile: false, rules.ProtectedNormalized, rules.KeptNormalized, appData);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return RecycleBlock.Protected;
        }
    }

    /// <summary>A file of the tree as a list row: its last change, and why Recycle is not offered for it.</summary>
    private StorageFileRow FileRow(DiskTree tree, int i, string path)
    {
        var system = (tree.Flags(i) & EntryFlags.System) != 0;
        return new StorageFileRow(path, tree.Size(i),
            tree.LastWriteUtc(i).ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture),
            BlockText(path, folder: false, system, tree, i), i, UpdateSelection);
    }

    /// <summary>
    /// Why a file or folder is not offered for the Recycle Bin, in the app language; null when it is. Uses the lists the
    /// analysis normalized once; a path that cannot be normalized, or no analysis yet, counts as protected. With its tree
    /// entry, also what the tree knows (<see cref="RecycleRules.CheckEntry"/>): a link, or an item that is or lies in a
    /// cloud folder or another reparse point, which the move would refuse.
    /// </summary>
    private string? BlockText(string path, bool folder, bool system, DiskTree? tree = null, int entry = -1)
    {
        RecycleBlock? block;
        if (_rules is not { } rules) block = RecycleBlock.Protected;
        else
        {
            if (system) block = RecycleBlock.SystemFile;
            // The one entry selected in the tree, or a check the worker could not make: here, once.
            else if (!_blocks.TryGetValue((path, folder), out block)) _blocks[(path, folder)] = block = PathBlock(path, folder, rules);
            block ??= rules.Above;
        }
        if (block is null && tree is not null && entry >= 0 && entry < tree.Count) block = RecycleRules.CheckEntry(tree, entry);
        return block switch
        {
            null => null,
            RecycleBlock.ContainsProtected => Loc.Instance["Storage_BlockContainsProtected"],
            RecycleBlock.KeptFolder => Loc.Instance["Storage_BlockKept"],
            RecycleBlock.SystemFile => Loc.Instance["Storage_BlockSystem"],
            RecycleBlock.SystemFolder => Loc.Instance["Storage_BlockSystemFolder"],
            RecycleBlock.WindowsSetup => Loc.Instance["Storage_BlockWindowsSetup"],
            RecycleBlock.NameMismatch => Loc.Instance["Storage_BlockName"],
            RecycleBlock.Link => Loc.Instance["Storage_BlockLink"],
            RecycleBlock.CloudFolder => Loc.Instance["Storage_BlockCloud"],
            RecycleBlock.OtherReparse => Loc.Instance["Storage_BlockReparse"],
            _ => Loc.Instance["Storage_BlockProtected"],
        };
    }

    /// <summary>Drops the File types rows; with <paramref name="keep"/> the open groups and the selected extension are remembered for the next fill.</summary>
    private void ResetTypes(bool keep)
    {
        if (!keep)
        {
            _openGroups.Clear();
            _openType = null;
        }
        else if (_typesFilled)
        {
            _openGroups.Clear();
            foreach (var g in TypeGroups.Where(g => g.IsExpanded)) _openGroups.Add(g.Group);
            _openType = SelectedType?.Extension;
        }
        _typesFilled = false;
        SelectedType = null;
        TypeGroups.Clear();
        TypeFiles.Clear();
    }

    /// <summary>The groups with their totals (one pass over the tree); extension rows are built when a group opens.</summary>
    private void FillTypes(DiskTree tree)
    {
        _typesFilled = true;
        var totals = tree.TypeTotals();
        var all = (double)totals.Sum(t => t.Bytes);
        foreach (var g in totals.GroupBy(t => t.Group).Select(g => (Group: g.Key, Rows: g.ToList(), Bytes: g.Sum(t => t.Bytes))).OrderByDescending(g => g.Bytes))
            TypeGroups.Add(new TypeGroupRow(g.Group, g.Bytes, g.Rows.Sum(t => t.Count), all > 0 ? g.Bytes / all : 0, g.Rows, all, row => SelectedType = row));
        foreach (var row in TypeGroups.Where(r => _openGroups.Contains(r.Group))) row.IsExpanded = true;
        if (_openType is { } ext)
            SelectedType = TypeGroups.Where(g => g.IsExpanded).Select(g => g.Show(ext)).FirstOrDefault(t => t is not null);
        _openGroups.Clear();
        _openType = null;
    }

    /// <summary>Counts the File types fills; only the newest one adds its rows.</summary>
    private int _typeFill;

    /// <summary>The largest 100 files of the chosen extension, built for this one only.</summary>
    partial void OnSelectedTypeChanged(TypeRow? oldValue, TypeRow? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        // Each group's selection list shows only its own pick.
        foreach (var g in TypeGroups) g.Selected = newValue is not null && g.Extensions.Contains(newValue) ? newValue : null;
        TypeFiles.Clear();
        // Each pick is its own fill: picking an extension again while its earlier fill still runs must not let that one
        // add its rows too (every file would show twice). A cleared selection drops a pending fill as well.
        var fill = ++_typeFill;
        if (newValue is not null && Tree is { } tree)
            Track(FillFilesAsync(TypeFiles, tree, tree.LargestFilesOfType(newValue.Extension, 100), () => fill == _typeFill && ReferenceEquals(SelectedType, newValue)));
        UpdateSelection();
    }

    /// <summary>Shows a folder on the treemap; a file shows the folder it is in.</summary>
    public void OpenFolder(int entry)
    {
        if (Tree is not { } tree || entry < 0 || entry >= tree.Count || tree.IsRemoved(entry)) return;
        Folder = tree.IsFolder(entry) ? entry : tree.Parent(entry);
    }

    partial void OnFolderChanged(int value) => UpdateCrumbs();

    /// <summary>The path from the analyzed root to the shown folder, for the breadcrumb above the treemap.</summary>
    private void UpdateCrumbs()
    {
        Crumbs.Clear();
        if (Tree is not { } tree || Folder < 0 || Folder >= tree.Count) return;
        var chain = new List<int>();
        for (var e = Folder; e >= 0; e = tree.Parent(e)) chain.Add(e);
        for (var k = chain.Count - 1; k >= 0; k--)
            Crumbs.Add(new FolderCrumb(chain[k], chain[k] == tree.Root ? tree.RootPath : tree.Name(chain[k])));
    }

    // The list and the map select the same entry: a row selects its entry, and an entry picked on the map expands the
    // folders above it and selects its row.
    partial void OnSelectedRowChanged(TreeRow? value)
    {
        if (value is not null && value.Entry != SelectedEntry) SelectedEntry = value.Entry;
    }

    partial void OnSelectedEntryChanged(int value)
    {
        UpdateSelection();
        if (SelectedRow?.Entry == value) return;
        SelectedRow = value >= 0 ? Folders.Reveal(value) : null;
    }

    [RelayCommand]
    private void ToggleRow(TreeRow? row)
    {
        if (row is not null) Folders.Toggle(row);
    }

    /// <summary>Left in the list: an open folder closes, anything else selects the folder it is in.</summary>
    public void CollapseOrSelectParent(TreeRow row)
    {
        if (row.IsExpanded) Folders.Collapse(row);
        else if (Folders.ParentOf(row) is { } parent) SelectedRow = parent;
    }

    /// <summary>Right in the list: a closed folder opens, an open one selects its first entry.</summary>
    public void ExpandOrSelectChild(TreeRow row)
    {
        if (!row.CanExpand) return;
        if (!row.IsExpanded) Folders.Expand(row);
        else if (Folders.Rows.IndexOf(row) is var at and >= 0 && at + 1 < Folders.Rows.Count) SelectedRow = Folders.Rows[at + 1];
    }

    /// <summary>
    /// Hashes files of equal size from the tree (at least 1 MB, not system, not cloud-only, not protected) on a worker
    /// thread under the banner with Stop. Nothing is hashed before this is pressed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindDuplicates))]
    private async Task FindDuplicatesAsync()
    {
        if (!CanFindDuplicates || Tree is not { } tree || _rules is not { } rules) return;
        var protectedRoots = rules.Protected;
        using var stop = new CancellationTokenSource();
        IsFindingDuplicates = true;
        try
        {
            using var work = Owner.Runner.Work.Begin(Loc.Instance["Work_FindDuplicates"], stop);
            var entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var groups = await Task.Run(() => StorageAnalyzer.FindDuplicates(StorageAnalyzer.DuplicateCandidates(tree, protectedRoots, entries: entries, ct: stop.Token), stop.Token));
            Log.Info("storage", "duplicates found", new { groups = groups.Count, files = groups.Sum(g => g.Paths.Count) });
            // A new analysis or Close result meanwhile: these belong to the old tree.
            if (!ReferenceEquals(tree, Tree)) return;
            var shown = groups.Take(50).ToList();
            await CheckPathsAsync(shown.SelectMany(g => g.Paths));
            if (!ReferenceEquals(tree, Tree)) return;
            _duplicateGroups = shown;
            _duplicateEntries = entries;
            DuplicatesSearched = true;
            ShowDuplicates();
        }
        catch (OperationCanceledException)
        {
            Owner.ShowResult(Loc.Instance["Storage_Stopped"]);
        }
        catch (Exception ex)
        {
            // As for the analysis: the page stays usable with the tree, without a duplicate list.
            Log.Error("storage", "finding duplicates failed", ex);
            Owner.ShowResult(Loc.Instance.Format("Result_Error", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            IsFindingDuplicates = false;
        }
    }

    /// <summary>The grouped list: every copy can be selected, and the selection may not hold every copy of a group.</summary>
    private void ShowDuplicates()
    {
        Duplicates.Clear();
        foreach (var g in _duplicateGroups)
            foreach (var (p, i) in g.Paths.Select((p, i) => (p, i)))
            {
                var entry = _duplicateEntries.GetValueOrDefault(p, -1);
                var tree = entry >= 0 ? Tree : null;
                var system = tree is not null && (tree.Flags(entry) & EntryFlags.System) != 0;
                Duplicates.Add(new StorageFileRow(p, g.BytesEach, i == 0 ? Loc.Instance.Format("Storage_DuplicateOf", g.Paths.Count) : "",
                    BlockText(p, folder: false, system, tree, entry), entry, UpdateSelection));
            }
        DuplicatesText = _duplicateGroups.Count == 0 ? null
            : Loc.Instance.Format("Storage_DuplicatesFound", CleanupViewModel.Size(_duplicateGroups.Sum(d => d.Reclaimable)));
        OnPropertyChanged(nameof(NoDuplicates));
        UpdateSelection();
    }

    /// <summary>
    /// The open tab's selection for the command bar: the entry selected in the folder list or on the treemap, or the
    /// ticked rows of a file list. Recycle is off when one of them may not go, or when every copy of a duplicate group is
    /// in it (one copy always stays).
    /// </summary>
    private void UpdateSelection()
    {
        _picks = Picks();
        SelectedEntries = _picks.Select(p => p.Entry).ToList();
        SelectionBlock = _picks.FirstOrDefault(p => p.Block is not null)?.Block
            ?? (StorageAnalyzer.WholeGroupsSelected(_picks.Select(p => p.Path).ToList(), _duplicateGroups) is [var whole, ..]
                ? Loc.Instance.Format("Storage_KeepOneCopy", whole.Paths[0]) : null);
        SelectionCount = _picks.Count;
        // The count is the same after a language switch: the text is not.
        OnPropertyChanged(nameof(RecycleText));
    }

    private List<Pick> Picks()
    {
        if (Tree is not { } tree) return [];
        if (Tab == TabFolders)
        {
            // The analyzed root has no row and is never offered: it is what the page shows.
            var i = SelectedEntry;
            if (i <= tree.Root || i >= tree.Count || tree.IsRemoved(i)) return [];
            var path = tree.FullPath(i);
            var folder = tree.IsFolder(i);
            var flags = tree.Flags(i);
            var system = !folder && (flags & EntryFlags.System) != 0;
            // The move refuses a link, anything in a cloud folder or another reparse point, and a folder it cannot look
            // into (it must hold no link): the bar says so first.
            var block = BlockText(path, folder, system, tree, i)
                ?? ((flags & EntryFlags.Unreadable) != 0 ? Loc.Instance["Storage_BlockUnreadable"] : null);
            return [new Pick(i, path, folder, tree.Size(i), block)];
        }
        var rows = Tab switch
        {
            TabTypes => TypeFiles,
            TabLargest => LargestFiles,
            TabDuplicates => Duplicates,
            _ => [],
        };
        return rows.Where(r => r.Selected).DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(r => new Pick(r.Entry, r.Path, false, r.Bytes, r.BlockText)).ToList();
    }

    /// <summary>
    /// Selects the item in File Explorer, started as the signed-in user, never elevated (<see cref="DeElevatedLauncher"/>).
    /// Only a full path from the tree that still exists is handed over, quoted.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenInExplorer))]
    private void OpenInExplorer()
    {
        if (_picks is not [var pick]) return;
        if (!Path.IsPathFullyQualified(pick.Path) || !(pick.Folder ? Directory.Exists(pick.Path) : File.Exists(pick.Path)))
        {
            Owner.ShowResult(Loc.Instance.Format("Storage_Missing", pick.Path), InfoBarSeverity.Warning);
            return;
        }
        Owner.ShowInExplorer(pick.Path, Elevation, missingText: Loc.Instance.Format("Storage_Missing", pick.Path),
            failedText: Loc.Instance.Format("Storage_OpenFailed", pick.Path));
    }

    /// <summary>The selected paths, one per line.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyPath))]
    private void CopyPath()
    {
        if (_picks.Count == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _picks.Select(p => p.Path)));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is held by another program; nothing to do.
        }
    }

    /// <summary>
    /// Moves the selected files and folders to the Recycle Bin after a confirmation that lists them (the first ten) with
    /// their total size. The selection was checked when it changed (<see cref="SelectionBlock"/>), and the default
    /// <c>recycle</c> checks every item again at the moment of the move. The ones moved leave the tree, and every tab
    /// builds its rows again; the ones not moved stay and are named in the result bar.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRecycle))]
    private async Task RecycleAsync()
    {
        if (!CanRecycle || Tree is not { } tree || _rules is not { } rules) return;
        var picks = _picks;
        if (picks.Count == 0 || picks.Any(p => p.Block is not null)) return;
        var paths = picks.Select(p => p.Path).ToList();
        // Checked again here: the duplicate groups can change while the bar shows the selection.
        if (StorageAnalyzer.WholeGroupsSelected(paths, _duplicateGroups) is [var whole, ..])
        {
            Owner.ShowResult(Loc.Instance.Format("Storage_KeepOneCopy", whole.Paths[0]), InfoBarSeverity.Warning);
            return;
        }
        var text = Loc.Instance.Format("Storage_RecycleText", picks.Count, CleanupViewModel.Size(picks.Sum(p => p.Bytes)), string.Join("\n", paths.Take(10)));
        if (picks.Count > 10) text += "\n" + Loc.Instance.Format("Storage_RecycleMore", picks.Count - 10);
        // The Recycle Bin belongs to the account this process runs as. With a separate admin account (or Administrator
        // protection) that is not the signed-in user's bin: say so before deleting.
        if (Elevation is { UserMismatch: true } e) text += "\n\n" + Loc.Instance.Format("Storage_RecycleOtherAccount", e.ProcessUser);
        if (!dialogs.Ask(Loc.Instance["Storage_RecycleTitle"], text, Loc.Instance["Storage_Recycle"])) return;

        var move = recycle ?? (items => StorageAnalyzer.RecycleItems(items, rules.Protected, rules.Kept));
        IReadOnlyList<string> failed = paths;
        var ran = await RunWorkAsync("recycle", Loc.Instance["Work_Recycle"], async work =>
        {
            var steps = new Progress<int>(done => work.Step(done, picks.Count, paths[done]));
            failed = await Task.Run(() => RecycleEach(picks, move, steps));
        });
        // Not run (another change was running): nothing was moved.
        if (!ran) return;
        var failedSet = failed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = picks.Where(p => !failedSet.Contains(p.Path)).ToList();
        Log.Info("storage", "recycle done", new { moved = gone.Count, failed = failed.Count, folders = gone.Count(p => p.Folder) });
        // A new analysis cannot start meanwhile (IsWorking), but the tree is checked anyway before entries leave it.
        if (gone.Count > 0 && ReferenceEquals(tree, Tree))
        {
            foreach (var p in gone.Where(p => p.Entry >= 0)) tree.Remove(p.Entry);
            var goneSet = gone.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A copy leaves its duplicate group when it was moved itself or with a folder above it.
            bool Gone(string path) => goneSet.Contains(path) || (_duplicateEntries.TryGetValue(path, out var entry) && entry >= 0 && tree.IsRemoved(entry));
            _duplicateGroups = _duplicateGroups.Select(g => g with { Paths = g.Paths.Where(p => !Gone(p)).ToList() }).Where(g => g.Paths.Count > 1).ToList();
            ResultText = Summary(tree);
            if (DuplicatesSearched) ShowDuplicates();
            RefreshFromTree();
        }
        var result = Loc.Instance.Format("Storage_Recycled", gone.Count, failed.Count);
        if (failed.Count > 0)
            result += "\n" + Loc.Instance.Format("Storage_RecycleFailed", string.Join("\n", failed.Take(10)))
                + (failed.Count > 10 ? "\n" + Loc.Instance.Format("Storage_RecycleMore", failed.Count - 10) : "");
        Owner.ShowResult(result, failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    /// <summary>One item at a time, so the banner counts them; an item that throws counts as not moved and the rest go on.</summary>
    private static List<string> RecycleEach(IReadOnlyList<Pick> picks, Func<IReadOnlyList<(string Path, bool Folder)>, IReadOnlyList<string>> move, IProgress<int> progress)
    {
        var failed = new List<string>();
        for (var k = 0; k < picks.Count; k++)
        {
            progress.Report(k);
            try
            {
                failed.AddRange(move([(picks[k].Path, picks[k].Folder)]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Error("storage", "an item was not moved to the Recycle Bin", ex);
                failed.Add(picks[k].Path);
            }
        }
        return failed;
    }
}
