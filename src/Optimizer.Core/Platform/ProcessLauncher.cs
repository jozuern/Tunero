using System.Diagnostics;
using System.Text;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Platform;

/// <summary>
/// How this app starts another program, in one place: a plain tool name resolves to System32
/// (<see cref="ProcessHardening.ResolveSystemTool"/>), there is no shell, and the environment has none of the variables
/// a user could set to load code into a tool that runs elevated (<see cref="ProcessHardening.Apply"/>). Programs that
/// run as the signed-in user start through <see cref="DeElevatedLauncher"/>; this app restarting itself keeps its own
/// environment rules (Program.cs, the update).
/// </summary>
public static class ProcessLauncher
{
    /// <summary>
    /// Start info for <paramref name="file"/>, hardened and without a shell. <paramref name="capture"/> redirects both
    /// outputs in that encoding (console tools write in the OEM code page: <see cref="ToolEncoding.Oem"/>).
    /// </summary>
    public static ProcessStartInfo Info(string file, string? arguments = null, bool hidden = true, Encoding? capture = null)
    {
        var psi = new ProcessStartInfo(ProcessHardening.ResolveSystemTool(file), arguments ?? "") { UseShellExecute = false, CreateNoWindow = hidden };
        if (capture is not null)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = capture;
            psi.StandardErrorEncoding = capture;
        }
        ProcessHardening.Apply(psi);
        return psi;
    }

    /// <summary>Start info with each argument quoted by .NET (paths and queries with spaces or quotes in them).</summary>
    public static ProcessStartInfo Info(string file, IEnumerable<string> arguments, bool hidden = true, Encoding? capture = null)
    {
        var psi = Info(file, (string?)null, hidden, capture);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return psi;
    }

    public static Process Start(ProcessStartInfo psi) =>
        Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(psi.FileName)} did not start");

    /// <summary>Starts a program and lets it run on its own (a window of its own, a script that outlives this app).</summary>
    public static void StartDetached(ProcessStartInfo psi) => Start(psi).Dispose();

    /// <summary>
    /// Runs a console tool to its end and returns its exit code and output (stdout, then stderr), logged as a command.
    /// After <paramref name="timeout"/> the tool and what it started are ended and a <see cref="TimeoutException"/> follows.
    /// </summary>
    public static (int ExitCode, string Output) Run(ProcessStartInfo psi, TimeSpan timeout)
    {
        var what = $"{Path.GetFileName(psi.FileName)} {(psi.ArgumentList.Count > 0 ? string.Join(' ', psi.ArgumentList) : psi.Arguments)}".TrimEnd();
        var watch = Stopwatch.StartNew();
        using var p = Start(psi);
        var stdout = psi.RedirectStandardOutput ? p.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var stderr = psi.RedirectStandardError ? p.StandardError.ReadToEndAsync() : Task.FromResult("");
        if (!p.WaitForExit(timeout))
        {
            ProcessHardening.KillTree(p);
            throw new TimeoutException($"{what} timed out");
        }
        // A program the tool started can keep the output pipes open after the tool ended: do not wait for it forever.
        if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(10)))
            Log.Warn("command", $"{what}: output still open after the tool ended; using what was read");
        var output = (stdout.IsCompletedSuccessfully ? stdout.Result : "") + (stderr.IsCompletedSuccessfully ? stderr.Result : "");
        Log.Info("command", what, new { exitCode = p.ExitCode, ms = watch.ElapsedMilliseconds, output = output.Length > 2000 ? output[..2000] : output });
        return (p.ExitCode, output);
    }

    /// <summary>
    /// Runs a long tool (winget, sfc, dism, PresentMon) and reports every output line while it runs; returns its exit
    /// code. <paramref name="psi"/> must capture the output (<see cref="Info(string, string?, bool, Encoding?)"/> with an
    /// encoding). Cancelling <paramref name="ct"/> ends the process tree and throws <see cref="OperationCanceledException"/>
    /// once the tool has exited; after <paramref name="timeout"/> (null: no limit) the same happens with a
    /// <see cref="TimeoutException"/>. The output is logged once at the end (truncated).
    /// </summary>
    /// <param name="started">Called right after the process has started (for example to release a file held open until then).</param>
    public static async Task<int> RunStreamingAsync(ProcessStartInfo psi, IProgress<string>? lines, TimeSpan? timeout, CancellationToken ct, Action? started = null)
    {
        if (!psi.RedirectStandardOutput || !psi.RedirectStandardError) throw new ArgumentException("the output must be captured", nameof(psi));
        var what = $"{Path.GetFileName(psi.FileName)} {(psi.ArgumentList.Count > 0 ? string.Join(' ', psi.ArgumentList) : psi.Arguments)}".TrimEnd();
        ct.ThrowIfCancellationRequested();
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
        if (!p.Start()) throw new InvalidOperationException($"Cannot start {psi.FileName}");
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
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) limit.CancelAfter(t);
        try
        {
            // The exit itself, not the end of the output: WaitForExitAsync also waits until the output pipes close, and a
            // program the tool started (an installer launching the updated app) can keep them open for as long as it runs.
            await exited.Task.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(p, exited.Task);
            var stopped = ct.IsCancellationRequested;
            Log.Warn("command", $"{what}: {(stopped ? "stopped" : $"timed out after {timeout?.TotalSeconds:0} s")}, process tree ended");
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{what} timed out");
        }
        try
        {
            using var rest = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rest.CancelAfter(OutputGrace);
            await p.WaitForExitAsync(rest.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn("command", $"{what}: output still open after the tool ended; using what was read");
        }
        string output;
        lock (all) output = string.Join(Environment.NewLine, all);
        Log.Info("command", what, new { exitCode = p.ExitCode, output = output.Length > 4000 ? output[^4000..] : output });
        return p.ExitCode;
    }

    /// <summary>How long a stopped tool may take to end before the stop returns anyway.</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ends the process tree and waits until the tool itself has exited (at most <see cref="KillWait"/>): killing only
    /// starts the termination, and a caller cleaning up right after a Stop (discarding a DISM mount, deleting its files)
    /// must not race a tool that still runs.
    /// </summary>
    internal static async Task KillAndWaitAsync(Process process, Task exited)
    {
        ProcessHardening.KillTree(process);
        try
        {
            await exited.WaitAsync(KillWait, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            Log.Warn("command", $"{process.StartInfo.FileName} had not ended {KillWait.TotalSeconds:0} s after it was stopped");
        }
    }

    /// <summary>How long the rest of the output is awaited after the tool ended.</summary>
    internal static TimeSpan OutputGrace { get; set; } = TimeSpan.FromSeconds(10);
}
