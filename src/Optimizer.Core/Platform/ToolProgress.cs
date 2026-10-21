using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimizer.Core.Platform;

/// <summary>
/// How far a long tool is, read from the lines it prints (<see cref="Actions.IProcessRunner.RunAsync"/>): sfc and dism print a
/// percentage, winget upgrade --all numbers the packages it updates.
/// </summary>
public static partial class ToolProgress
{
    /// <summary>
    /// The percentage in a line of sfc ("Verification 45% complete.", German "7 % abgeschlossen") or dism ("[== 9.7% ]",
    /// with a decimal comma in German). Null for a line without one or above 100.
    /// </summary>
    public static double? Percent(string line)
    {
        var m = PercentPattern().Match(line);
        if (!m.Success) return null;
        var value = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return value <= 100 ? value : null;
    }

    /// <summary>
    /// "(2/5) Found Mozilla Firefox ..." from winget upgrade --all: the second of five packages starts, so one is done.
    /// Null for every other line (winget's download bars also show sizes and percentages).
    /// </summary>
    public static (int Done, int Total)? WingetPackage(string line)
    {
        var m = WingetPattern().Match(line);
        if (!m.Success) return null;
        var number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var total = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return number >= 1 && number <= total ? (number - 1, total) : null;
    }

    [GeneratedRegex(@"(?<![\d.,])(\d{1,3}(?:[.,]\d+)?)\s?%")]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"^\s*\((\d{1,4})/(\d{1,4})\)\s")]
    private static partial Regex WingetPattern();
}
