using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Platform;

/// <summary>
/// Runs a long tool (winget, sfc, dism) without a window and reports every output line while it runs.
/// Cancellation kills the process tree. Output is logged once at the end (truncated).
/// </summary>
public static class StreamingProcess
{
    /// <param name="started">Called right after the process has started (for example to release a file held open until then).</param>
    public static async Task<int> RunAsync(string file, string arguments, IProgress<string>? lines, CancellationToken ct = default, Encoding? encoding = null,
        Action? started = null)
    {
        var psi = ProcessLauncher.Info(file, arguments, capture: encoding ?? Encoding.UTF8);
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var all = new List<string>();
        void OnLine(string? line)
        {
            if (line is null) return;
            // sfc and dism print progress with carriage returns; keep the last segment of each line.
            var text = line.Split('\r').LastOrDefault(s => s.Trim().Length > 0)?.Trim() ?? "";
            if (text.Length == 0) return;
            lock (all) OutputLines.Add(all, text, int.MaxValue);
            lines?.Report(text);
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (_, _) => exited.TrySetResult();
        if (!p.Start()) throw new InvalidOperationException($"Cannot start {file}");
        try
        {
            started?.Invoke();
        }
        catch
        {
            // Nobody would read its output or wait for it: the tool does not keep running unobserved.
            ProcessHardening.KillTree(p);
            throw;
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            // The exit itself, not the end of the output: WaitForExitAsync also waits until the output pipes close, and a
            // program the tool started (an installer launching the updated app) can keep them open for as long as it runs.
            await exited.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            ProcessHardening.KillTree(p);
            throw;
        }
        try
        {
            using var rest = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rest.CancelAfter(OutputGrace);
            await p.WaitForExitAsync(rest.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn("command", $"{Path.GetFileName(file)} {arguments}: output still open after the tool ended; using what was read");
        }
        string output;
        lock (all) output = string.Join(Environment.NewLine, all);
        Log.Info("command", $"{Path.GetFileName(file)} {arguments}", new { exitCode = p.ExitCode, output = output.Length > 4000 ? output[^4000..] : output });
        return p.ExitCode;
    }

    /// <summary>How long the rest of the output is awaited after the tool ended.</summary>
    internal static TimeSpan OutputGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>sfc.exe writes UTF-16 to a redirected pipe.</summary>
    public static readonly Encoding Utf16 = Encoding.Unicode;

    /// <summary>
    /// The OEM code page of the system (850 on German Windows), which console tools such as dism, ipconfig, chkdsk,
    /// netsh, net, reagentc and Windows PowerShell write to a redirected pipe. Read as UTF-8, umlauts come out as
    /// replacement characters. UTF-8 when the code page is not available.
    /// </summary>
    public static readonly Encoding Oem = OemEncoding();

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetOEMCP();
}
