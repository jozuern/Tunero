using System.IO;
using Optimizer.Core.Actions;

namespace Optimizer.App.Tests;

/// <summary>
/// The tool runner of the pages without a process: records each streaming run with the environment it would get, reports
/// the lines <see cref="Respond"/> gives and returns its exit code. With <see cref="Hangs"/> a run keeps going until it
/// is stopped (<see cref="OperationCanceledException"/>) or its time limit is up (<see cref="TimeoutException"/>), as
/// with the real runner.
/// </summary>
internal sealed class FakeToolRunner : IProcessRunner
{
    public sealed record ToolRun(string File, string Arguments, TimeSpan? Timeout, RunOptions? Options, IDictionary<string, string?> Environment);

    public List<ToolRun> Runs { get; } = [];

    /// <summary>Exit code and printed lines of a run, by program and arguments.</summary>
    public Func<string, string, (int ExitCode, string[] Lines)> Respond { get; set; } = (_, _) => (0, []);

    /// <summary>The environment before the run's own changes (what a user could have set).</summary>
    public Func<Dictionary<string, string?>> Environment { get; set; } = () => new(StringComparer.OrdinalIgnoreCase);

    public bool Hangs { get; set; }

    /// <summary>How often a hanging run was ended (stopped or timed out).</summary>
    public int Ended { get; private set; }

    private readonly TaskCompletionSource _hanging = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a hanging run has started.</summary>
    public Task Hanging => _hanging.Task;

    public (int ExitCode, string Output) Run(string file, string arguments, TimeSpan? timeout = null) =>
        throw new NotSupportedException($"{Path.GetFileName(file)} {arguments}: only streaming runs in these tests");

    public async Task<int> RunAsync(string file, string arguments, TimeSpan? timeout, CancellationToken ct, IProgress<string>? lines, RunOptions? options = null)
    {
        var env = Environment();
        options?.Environment?.Invoke(env);
        Runs.Add(new ToolRun(file, arguments, timeout, options, env));
        ct.ThrowIfCancellationRequested();
        options?.Started?.Invoke();
        var (code, printed) = Respond(file, arguments);
        foreach (var line in printed) lines?.Report(line);
        if (!Hangs) return code;
        _hanging.TrySetResult();
        try
        {
            await Task.Delay(timeout ?? Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            Ended++;
            throw;
        }
        Ended++;
        throw new TimeoutException($"{Path.GetFileName(file)} {arguments} timed out");
    }
}
