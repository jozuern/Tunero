using Optimizer.Core.Platform;

namespace Optimizer.App.Services;

/// <summary>
/// The output box of a tool run (winget, Chocolatey, SFC, DISM): the lines it printed so far, at most <c>max</c> of them,
/// with a repeated progress line ("45 %", "46 %") merged into one (<see cref="OutputLines.Add"/>). The output boxes of
/// Install &amp; update and System repair use this one buffer, so none can lose a line to the threading below.
/// </summary>
public sealed class LiveOutput
{
    private readonly List<string> _lines = [];
    private readonly Action<string> _show;
    private readonly int _max;
    private bool _ended;

    /// <param name="show">Gets the whole text after each change (the page's output property).</param>
    /// <param name="max">The most lines kept; older ones go.</param>
    /// <param name="onLine">Also called for each printed line, after it was added (a progress banner reads its step from it).</param>
    public LiveOutput(Action<string> show, int max = 200, Action<string>? onLine = null)
    {
        _show = show;
        _max = max;
        // Progress<T> posts to the context it was made on: the window's thread in the app. Without one (tests) each
        // report runs on the thread pool, so lines can arrive at the same time and after the run has ended: Add locks.
        Progress = new Progress<string>(line =>
        {
            if (Append([line])) onLine?.Invoke(line);
        });
    }

    /// <summary>What the tool run reports its printed lines to.</summary>
    public IProgress<string> Progress { get; }

    /// <summary>Adds one line and shows the text.</summary>
    public void Add(string line) => Append([line]);

    /// <summary>Adds lines in one go (the result lines after a run) and shows the text once.</summary>
    public void AddRange(IEnumerable<string> lines) => Append(lines);

    /// <summary>
    /// Adds the last line (the run was stopped): a line the tool printed before it was ended can arrive after it and is
    /// dropped, as is anything added later.
    /// </summary>
    public void End(string line)
    {
        lock (_lines)
        {
            Append([line]);
            _ended = true;
        }
    }

    /// <summary>Adds and shows; false once the output has ended, when nothing is added.</summary>
    private bool Append(IEnumerable<string> lines)
    {
        lock (_lines)
        {
            if (_ended) return false;
            foreach (var line in lines) OutputLines.Add(_lines, line, _max);
            _show(string.Join("\n", _lines));
            return true;
        }
    }
}
