using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Media;

/// <summary>
/// One image of a WIM or ESD. The list form of Get-WimInfo gives only Index and Name; the /Index form adds the rest.
/// <paramref name="DefaultLanguage"/> is the language DISM marks "(Default)".
/// </summary>
public sealed record WimImage(int Index, string Name, string? Edition, string? Architecture, string? Version, IReadOnlyList<string> Languages,
    string? DefaultLanguage = null);

/// <summary>A dism.exe run that ended with a nonzero exit code. The message carries the DISM error code and the end of its output.</summary>
public sealed class DismException(int exitCode, string output) : Exception(Describe(exitCode, output))
{
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;

    private static string Describe(int exitCode, string output)
    {
        var code = DismOutput.ErrorCode(output);
        var tail = output.Length > 1500 ? output[^1500..] : output;
        return $"DISM failed (exit code {exitCode}{(code is { } c ? $", error 0x{c:x8}" : "")}): {tail.Trim()}";
    }
}

/// <summary>Reads the text dism.exe prints (run with /English). The parsers also accept the trimmed, blank-free lines StreamingProcess collects.</summary>
public static class DismOutput
{
    private static readonly Regex KeyValue = new(@"^(?<key>[A-Za-z][A-Za-z ]*?) :[ ]?(?<value>.*)$", RegexOptions.Compiled);
    private static readonly Regex LanguageLine = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]+)*( \(Default\))?$", RegexOptions.Compiled);
    private static readonly Regex ProgressBar = new(@"^\[.*?(?<n>\d{1,3}(?:[.,]\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex ErrorLine = new(@"^Error:\s*(?<n>0x[0-9a-fA-F]+|\d+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(l => l.Trim());

    /// <summary>
    /// The images in a Get-WimInfo answer, one per "Index :" line. Version is "10.0.26300.9711" when both
    /// "Version : 10.0.26300" and "ServicePack Build : 9711" are present (DISM prints them separately).
    /// </summary>
    public static IReadOnlyList<WimImage> ParseWimInfo(string output)
    {
        var images = new List<WimImage>();
        int? index = null;
        string? name = null, edition = null, arch = null, version = null, build = null, defaultLanguage = null;
        var languages = new List<string>();
        var inLanguages = false;

        void AddLanguage(string line)
        {
            var language = StripDefault(line);
            languages.Add(language);
            if (line.EndsWith("(Default)", StringComparison.Ordinal)) defaultLanguage = language;
        }

        void Flush()
        {
            if (index is { } i && name is not null)
            {
                var v = version is not null && build is not null ? $"{version}.{build}" : version;
                images.Add(new WimImage(i, name, edition, arch, v, languages.ToArray(), defaultLanguage));
            }
            index = null; name = edition = arch = version = build = defaultLanguage = null;
            languages = [];
            inLanguages = false;
        }

        foreach (var line in Lines(output))
        {
            if (line.Length == 0) continue;
            var m = KeyValue.Match(line);
            if (inLanguages && !m.Success && LanguageLine.IsMatch(line))
            {
                AddLanguage(line);
                continue;
            }
            inLanguages = false;
            if (!m.Success) continue;
            var key = m.Groups["key"].Value;
            var value = m.Groups["value"].Value.Trim();
            switch (key)
            {
                case "Index":
                    Flush();
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) index = n;
                    break;
                case "Name": name = value; break;
                case "Edition": edition = value; break;
                case "Architecture": arch = value; break;
                case "Version": version = value; break;
                case "ServicePack Build": build = value; break;
                case "Languages":
                    inLanguages = true;
                    if (LanguageLine.IsMatch(value)) AddLanguage(value);
                    break;
            }
        }
        Flush();
        return images;
    }

    private static string StripDefault(string language) => language.Replace(" (Default)", "").Trim();

    /// <summary>The percentage of a progress bar line such as "[=====42.0%     ]", or null for any other line.</summary>
    public static double? Percent(string line)
    {
        var m = ProgressBar.Match(line.Trim());
        if (!m.Success) return null;
        return double.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>The "PackageName :" values of Get-ProvisionedAppxPackages.</summary>
    public static IReadOnlyList<string> ParseProvisioned(string output) => Values(output, "PackageName");

    /// <summary>The "Mount Dir :" values of Get-MountedImageInfo.</summary>
    public static IReadOnlyList<string> ParseMountedDirs(string output) => Values(output, "Mount Dir");

    private static List<string> Values(string output, string key)
    {
        var list = new List<string>();
        foreach (var line in Lines(output))
        {
            var m = KeyValue.Match(line);
            if (m.Success && m.Groups["key"].Value == key && m.Groups["value"].Value.Trim() is { Length: > 0 } v) list.Add(v);
        }
        return list;
    }

    /// <summary>The number of an "Error: 0x800f081f" or "Error: 87" line, or null when there is none.</summary>
    public static int? ErrorCode(string output)
    {
        var m = ErrorLine.Match(output.Replace("\r", ""));
        if (!m.Success) return null;
        var text = m.Groups["n"].Value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h) ? unchecked((int)h) : null;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>
    /// True only when /CheckHealth says "No component store corruption detected." A repairable or corrupted store, an
    /// error and any text this does not know all count as not healthy.
    /// </summary>
    public static bool Healthy(string output) =>
        output.Contains("No component store corruption detected.", StringComparison.Ordinal);
}

/// <summary>Argument strings for dism.exe. Every command starts with /English so the output parsers see English text.</summary>
public static class DismArgs
{
    public static string WimInfo(string image, int? index) =>
        $"/English /Get-WimInfo /WimFile:{Q(image)}" + (index is { } i ? $" /Index:{i}" : "");

    public static string Apply(string image, int index, string target) =>
        $"/English /Apply-Image /ImageFile:{Q(image)} /Index:{index} /ApplyDir:{Q(target)}";

    public static string Export(string source, int index, string target, bool bootable) =>
        $"/English /Export-Image /SourceImageFile:{Q(source)} /SourceIndex:{index} /DestinationImageFile:{Q(target)} /Compress:max" + (bootable ? " /Bootable" : "");

    public static string Mount(string image, int index, string mountDir, bool readOnly = false) =>
        $"/English /Mount-Image /ImageFile:{Q(image)} /Index:{index} /MountDir:{Q(mountDir)}" + (readOnly ? " /ReadOnly" : "");

    public static string Provisioned(string mountDir) =>
        $"/English /Image:{Q(mountDir)} /Get-ProvisionedAppxPackages";

    public static string RemoveProvisioned(string mountDir, string packageName)
    {
        if (packageName.Length == 0 || packageName.Any(c => char.IsWhiteSpace(c) || c == '"'))
            throw new ArgumentException("Not a package name.", nameof(packageName));
        return $"/English /Image:{Q(mountDir)} /Remove-ProvisionedAppxPackage /PackageName:{packageName}";
    }

    public static string CheckHealth(string mountDir) =>
        $"/English /Image:{Q(mountDir)} /Cleanup-Image /CheckHealth";

    public static string Unmount(string mountDir, bool commit) =>
        $"/English /Unmount-Image /MountDir:{Q(mountDir)} " + (commit ? "/Commit" : "/Discard");

    public static string MountedImageInfo() => "/English /Get-MountedImageInfo";

    public static string CleanupMountpoints() => "/English /Cleanup-Mountpoints";

    /// <summary>A quoted path. A quote or a line break inside it could add switches, so those paths are refused.</summary>
    private static string Q(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(c => c == '"' || c == '\r' || c == '\n' || c == '\0'))
            throw new ArgumentException("Not a usable path.", nameof(path));
        // A backslash right before the closing quote would escape it; doubling it keeps one backslash.
        return path.EndsWith('\\') ? $"\"{path}\\\"" : $"\"{path}\"";
    }
}

/// <summary>What the install media build needs from dism.exe. Every failure surfaces as a <see cref="DismException"/>.</summary>
public interface IDism
{
    Task<string> GetWimInfoAsync(string image, int? index, CancellationToken ct);
    Task ApplyAsync(string image, int index, string target, IProgress<double>? p, CancellationToken ct);
    Task ExportAsync(string source, int index, string target, bool bootable, IProgress<double>? p, CancellationToken ct);
    /// <summary>Mounts the image; a read-only mount can only be unmounted without commit.</summary>
    Task MountAsync(string image, int index, string mountDir, bool readOnly, IProgress<double>? p, CancellationToken ct);
    Task<IReadOnlyList<string>> ProvisionedAsync(string mountDir, CancellationToken ct);
    Task RemoveProvisionedAsync(string mountDir, string packageName, CancellationToken ct);
    /// <summary>Runs /CheckHealth on the mounted image and returns the output (see <see cref="DismOutput.Healthy"/>).</summary>
    Task<string> CheckHealthAsync(string mountDir, CancellationToken ct);
    Task UnmountAsync(string mountDir, bool commit, IProgress<double>? p, CancellationToken ct);
    Task<IReadOnlyList<string>> MountedDirsAsync(CancellationToken ct);
    Task CleanupMountpointsAsync(CancellationToken ct);
}

/// <summary>The real dism.exe from System32. Not covered by unit tests (it changes disks and images); the VM test drives it.</summary>
public sealed class DismTool : IDism
{
    private static string Exe => Path.Combine(Environment.SystemDirectory, "dism.exe");

    public Task<string> GetWimInfoAsync(string image, int? index, CancellationToken ct) =>
        RunAsync(DismArgs.WimInfo(image, index), null, ct);

    public async Task ApplyAsync(string image, int index, string target, IProgress<double>? p, CancellationToken ct) =>
        await RunAsync(DismArgs.Apply(image, index, target), p, ct);

    public async Task ExportAsync(string source, int index, string target, bool bootable, IProgress<double>? p, CancellationToken ct) =>
        await RunAsync(DismArgs.Export(source, index, target, bootable), p, ct);

    public async Task MountAsync(string image, int index, string mountDir, bool readOnly, IProgress<double>? p, CancellationToken ct) =>
        await RunAsync(DismArgs.Mount(image, index, mountDir, readOnly), p, ct);

    public async Task<IReadOnlyList<string>> ProvisionedAsync(string mountDir, CancellationToken ct) =>
        DismOutput.ParseProvisioned(await RunAsync(DismArgs.Provisioned(mountDir), null, ct));

    public async Task RemoveProvisionedAsync(string mountDir, string packageName, CancellationToken ct) =>
        await RunAsync(DismArgs.RemoveProvisioned(mountDir, packageName), null, ct);

    public Task<string> CheckHealthAsync(string mountDir, CancellationToken ct) =>
        RunAsync(DismArgs.CheckHealth(mountDir), null, ct);

    public async Task UnmountAsync(string mountDir, bool commit, IProgress<double>? p, CancellationToken ct) =>
        await RunAsync(DismArgs.Unmount(mountDir, commit), p, ct);

    public async Task<IReadOnlyList<string>> MountedDirsAsync(CancellationToken ct) =>
        DismOutput.ParseMountedDirs(await RunAsync(DismArgs.MountedImageInfo(), null, ct));

    public async Task CleanupMountpointsAsync(CancellationToken ct) =>
        await RunAsync(DismArgs.CleanupMountpoints(), null, ct);

    private static async Task<string> RunAsync(string arguments, IProgress<double>? percent, CancellationToken ct)
    {
        var text = new StringBuilder();
        var lines = new LineProgress(line =>
        {
            lock (text) text.AppendLine(line);
            if (percent is not null && DismOutput.Percent(line) is { } p) percent.Report(p);
        });
        var code = await StreamingProcess.RunAsync(Exe, arguments, lines, ct, StreamingProcess.Oem);
        string output;
        lock (text) output = text.ToString();
        if (code != 0) throw new DismException(code, output);
        return output;
    }

    private sealed class LineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

/// <summary>
/// The fixed header at the start of a .wim file: "MSWIM\0\0\0", the header size 208 at offset 8, the image count at 44 and
/// the boot index at 120 (the image Windows Boot Manager starts from a boot.wim; 0 for none). DISM does not show the
/// boot index: its "WIM Bootable" line said No for both images of the 26H2 media's boot.wim, whose header names image 2,
/// and stayed No after /Export-Image /Bootable (checked in PCO-Test 2026-10-20). The test fixtures are those real headers.
/// </summary>
public static class WimHeader
{
    private const int Size = 208;

    public sealed record Info(int ImageCount, int BootIndex);

    /// <summary>The header at the start of <paramref name="stream"/>, or null when it is not a WIM header.</summary>
    public static Info? Read(Stream stream)
    {
        var header = new byte[Size];
        var read = 0;
        int n;
        while (read < Size && (n = stream.Read(header, read, Size - read)) > 0) read += n;
        if (read < Size || !header.AsSpan(0, 8).SequenceEqual("MSWIM\0\0\0"u8) || BitConverter.ToUInt32(header, 8) != Size) return null;
        var count = BitConverter.ToUInt32(header, 44);
        var boot = BitConverter.ToUInt32(header, 120);
        return count > int.MaxValue || boot > count ? null : new Info((int)count, (int)boot);
    }

    /// <summary>The header of the file at <paramref name="path"/>, or null when it is missing or not a WIM file.</summary>
    public static Info? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
