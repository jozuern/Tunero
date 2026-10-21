using System.Globalization;
using System.Security.Cryptography;
using Optimizer.Core.Actions;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Tools;

public sealed record FrameStats(int Frames, double Seconds, double AverageFps, double OnePercentLowFps, double P99FrameTimeMs, string? PresentMode);

public enum Comparison { NoMeasurableDifference, Better, Worse, NotEnoughRuns }

public sealed record BenchmarkComparison(Comparison Result, double BeforeMean, double AfterMean, double ChangePercent, (double Min, double Max) BeforeRange, (double Min, double Max) AfterRange);

/// <summary>
/// Frame time capture with Intel PresentMon 2.6.0 (MIT), embedded and extracted to the app's data folder with a SHA-256
/// check. Benchmark protocol: at least three runs per setting; a change counts only when the ranges of the
/// average FPS do not overlap, otherwise the result is "no measurable difference".
/// </summary>
public static class PresentMon
{
    public const string Version = "2.6.0";
    public const string Sha256 = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF";
    public const int MinRuns = 3;

    /// <summary>Extracts the embedded exe if missing or modified. Returns its path.</summary>
    public static string Extract(string toolsFolder)
    {
        Directory.CreateDirectory(toolsFolder);
        var path = Path.Combine(toolsFolder, $"PresentMon-{Version}-x64.exe");
        if (File.Exists(path) && HashOf(path) == Sha256) return path;
        using (var resource = typeof(PresentMon).Assembly.GetManifestResourceStream("Tools.PresentMon.exe")
                              ?? throw new FileNotFoundException("PresentMon resource missing"))
        using (var file = File.Create(path))
        {
            resource.CopyTo(file);
        }
        if (HashOf(path) != Sha256)
        {
            File.Delete(path);
            throw new InvalidDataException("PresentMon hash mismatch");
        }
        return path;
    }

    private static string HashOf(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Only plain executable names (game.exe) are passed to PresentMon.</summary>
    public static bool IsSafeProcessName(string name) =>
        name.Length is > 4 and < 128 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' or '(' or ')');

    public static string Arguments(string processName, string csv, int seconds) =>
        $"--process_name \"{processName}\" --output_file \"{csv}\" --timed {seconds} --terminate_after_timed --stop_existing_session --no_console_stats --v1_metrics";

    /// <summary>
    /// Captures one run of the running game. Needs administrator rights (ETW). PresentMon ends itself after
    /// <paramref name="seconds"/>; one that is still running <see cref="ToolTimeouts.CaptureGrace"/> later is ended
    /// (<see cref="TimeoutException"/>).
    /// </summary>
    public static async Task<FrameStats?> CaptureAsync(IProcessRunner processes, string exe, string processName, int seconds, string outputFolder, IProgress<string>? lines, CancellationToken ct)
    {
        if (!IsSafeProcessName(processName)) throw new ArgumentException($"invalid process name {processName}");
        Directory.CreateDirectory(outputFolder);
        var csv = Path.Combine(outputFolder, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(processName)}.csv");
        // Held open without write or delete sharing from the hash check until the process has started, so the checked
        // file is the one that runs. The pinned hash already proves it is Intel's unmodified build; no signature check is needed on top.
        var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        int code;
        try
        {
            if (Convert.ToHexString(SHA256.HashData(hold)) != Sha256) throw new InvalidDataException("PresentMon hash mismatch");
            code = await processes.RunAsync(exe, Arguments(processName, csv, seconds), TimeSpan.FromSeconds(seconds) + ToolTimeouts.CaptureGrace, ct, lines,
                new RunOptions { Started = hold.Dispose });
        }
        finally
        {
            hold.Dispose();
        }
        if (!File.Exists(csv))
        {
            Log.Warn("presentmon", $"no output (exit {code})");
            return null;
        }
        try
        {
            return Parse(File.ReadLines(csv));
        }
        finally
        {
            // Only the stats are kept; a CSV per capture would pile up in the data folder.
            try
            {
                File.Delete(csv);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("presentmon", $"capture file not deleted: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stats from a PresentMon CSV (MsBetweenPresents column). Average FPS = frames / time; 1 % low = the FPS of the
    /// 99th percentile frame time (the frame time that 1 % of frames are slower than).
    /// </summary>
    public static FrameStats? Parse(IEnumerable<string> csvLines)
    {
        using var e = csvLines.GetEnumerator();
        if (!e.MoveNext()) return null;
        var header = e.Current.Split(',');
        // The --v1_metrics header of PresentMon 2.x spells it "msBetweenPresents", the v2 header "MsBetweenPresents".
        var msIndex = Array.FindIndex(header, h => h.Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
        var modeIndex = Array.FindIndex(header, h => h.Trim().Equals("PresentMode", StringComparison.OrdinalIgnoreCase));
        if (msIndex < 0) return null;
        var times = new List<double>();
        var modes = new Dictionary<string, int>();
        while (e.MoveNext())
        {
            var cols = e.Current.Split(',');
            if (cols.Length <= msIndex) continue;
            if (double.TryParse(cols[msIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms is > 0 and < 5000) times.Add(ms);
            if (modeIndex >= 0 && cols.Length > modeIndex) modes[cols[modeIndex]] = modes.GetValueOrDefault(cols[modeIndex]) + 1;
        }
        if (times.Count < 30) return null;
        var total = times.Sum();
        var sorted = times.Order().ToList();
        var p99 = Percentile(sorted, 0.99);
        return new FrameStats(times.Count, total / 1000, times.Count / (total / 1000), 1000 / p99, p99, modes.Count == 0 ? null : modes.MaxBy(kv => kv.Value).Key);
    }

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Compares average FPS of two sets of runs. Overlapping ranges = no measurable difference.</summary>
    public static BenchmarkComparison Compare(IReadOnlyList<FrameStats> before, IReadOnlyList<FrameStats> after)
    {
        if (before.Count < MinRuns || after.Count < MinRuns)
            return new BenchmarkComparison(Comparison.NotEnoughRuns, Mean(before), Mean(after), 0, Range(before), Range(after));
        var b = Range(before);
        var a = Range(after);
        var mb = Mean(before);
        var ma = Mean(after);
        var change = mb == 0 ? 0 : (ma - mb) / mb * 100;
        var overlap = a.Min <= b.Max && b.Min <= a.Max;
        var result = overlap ? Comparison.NoMeasurableDifference : ma > mb ? Comparison.Better : Comparison.Worse;
        return new BenchmarkComparison(result, mb, ma, change, b, a);
    }

    private static double Mean(IReadOnlyList<FrameStats> runs) => runs.Count == 0 ? 0 : runs.Average(r => r.AverageFps);
    private static (double Min, double Max) Range(IReadOnlyList<FrameStats> runs) => runs.Count == 0 ? (0, 0) : (runs.Min(r => r.AverageFps), runs.Max(r => r.AverageFps));
}
