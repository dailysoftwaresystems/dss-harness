using System.Diagnostics;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// A runner that says one thing and then goes quiet, so a stall is what the bound sees rather
/// than a machine that was busy. Runs <paramref name="life"/> out if nothing stops it, which is
/// how a bound that never fires shows up as a failure rather than as a hang.
/// </summary>
internal sealed class QuietProcessRunner(TimeSpan life, bool speaks = true) : IProcessRunner
{
    /// <summary>Whether the phase was stopped, rather than left to finish on its own.</summary>
    public bool Stopped { get; private set; }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var watch = Stopwatch.StartNew();
        const string Line = "starting";

        request.OnStarted?.Invoke();

        if (speaks)
        {
            request.OnOutputLine?.Invoke(Line);
        }

        try
        {
            await Task.Delay(life, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // What the real runner reports when it stopped the child: the exit code is
            // meaningless and the phase is marked as stopped.
            Stopped = true;
            return new ProcessResult(-1, Said(speaks, Line), string.Empty, watch.Elapsed, TimedOut: true);
        }

        return new ProcessResult(0, Said(speaks, Line), string.Empty, watch.Elapsed, TimedOut: false);
    }

    public string? FindExecutable(string command) => command;

    private static string Said(bool speaks, string line) => speaks ? line + "\n" : string.Empty;
}
