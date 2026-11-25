using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// A process runner whose every run's end could not reach or end a process it was to, as the real runner says it: in the
/// run's result (<see cref="ProcessResult.Uncontained"/>) where the run ended of itself, or - where a stop is given - in
/// the stop it throws once it has stopped its caller, the caller's own token cancelled
/// (<see cref="ProcessStoppedException.Uncontained"/>), as the real runner throws it where its caller gave up. What a
/// caller of the runner says of it is then the caller's alone, which a test reads exactly.
/// </summary>
/// <param name="unreached">What the end could not reach or end, as the runner words it.</param>
/// <param name="stopping">The caller's own stop, which the run cancels and is thrown in; none where the run ends of itself.</param>
/// <param name="answer">What the run otherwise answers; it exits 0 saying nothing where none is given.</param>
internal sealed class UnreachingRunner(string unreached, CancellationTokenSource? stopping = null, Func<ProcessRequest, ProcessResult>? answer = null) : IProcessRunner
{
    /// <summary>What a test's runs say they could not reach or end.</summary>
    public const string Unreached = "not every process of the tree of pid 41 could be ended: pid 42 could not be ended: Access is denied";

    /// <summary>Every request run, in order.</summary>
    public List<ProcessRequest> Requests { get; } = [];

    /// <summary>A runner whose runs end of themselves, saying <see cref="Unreached"/> in their result.</summary>
    public static UnreachingRunner InResult(Func<ProcessRequest, ProcessResult>? answer = null) => new(Unreached, answer: answer);

    /// <summary>A runner whose runs its caller stops, saying <see cref="Unreached"/> in the stop.</summary>
    /// <param name="stopping">The caller's own stop.</param>
    public static UnreachingRunner InStop(CancellationTokenSource stopping) => new(Unreached, stopping);

    /// <summary>A runner that says <see cref="Unreached"/> in its caller's stop where one is given, else in its result.</summary>
    /// <param name="stopping">The caller's own stop, or <see langword="null"/>.</param>
    /// <param name="answer">What a run that ends of itself otherwise answers.</param>
    public static UnreachingRunner For(CancellationTokenSource? stopping, Func<ProcessRequest, ProcessResult>? answer = null)
        => stopping is null ? InResult(answer) : InStop(stopping);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (Requests)
        {
            Requests.Add(request);
        }

        request.OnStarted?.Invoke();

        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            throw new ProcessStoppedException(unreached, stopping.Token);
        }

        return (answer?.Invoke(request) ?? new ProcessResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5), TimedOut: false)) with
        {
            Uncontained = unreached,
        };
    }

    public string? FindExecutable(string command) => command;
}

/// <summary>
/// A host's commands as <paramref name="inner"/> answers them - but for those <paramref name="reaches"/> picks, whose end
/// could not reach or end a process it was to: said in the answer, or in the stop each throws once it has stopped its
/// caller where a stop is given, as <see cref="UnreachingRunner"/> says it of a run of its own.
/// </summary>
/// <param name="inner">What answers each command.</param>
/// <param name="stopping">The caller's own stop; none where each command ends of itself.</param>
/// <param name="reaches">Which calls - by a command's line, its program and arguments joined by spaces, or the name of the question asked - are unreached; every call where none is given.</param>
internal sealed class UnreachingHostCommands(IHostCommandRunner inner, CancellationTokenSource? stopping = null, Func<string, bool>? reaches = null) : IHostCommandRunner
{
    public Task<ProcessResult> RunAsync(HostConnection connection, HostCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Unreaching(string.Join(' ', [command.Program, .. command.Arguments]), () => inner.RunAsync(connection, command, cancellationToken));
    }

    public Task<ProcessResult> ProbeShellAsync(HostConnection connection, CancellationToken cancellationToken = default)
        => Unreaching(nameof(ProbeShellAsync), () => inner.ProbeShellAsync(connection, cancellationToken));

    public Task<ProcessResult> ReadSshSettingsAsync(HostConnection connection, CancellationToken cancellationToken = default)
        => Unreaching(nameof(ReadSshSettingsAsync), () => inner.ReadSshSettingsAsync(connection, cancellationToken));

    public Task<ProcessResult> ProbeDefaultWslDistributionAsync(CancellationToken cancellationToken = default)
        => Unreaching(nameof(ProbeDefaultWslDistributionAsync), () => inner.ProbeDefaultWslDistributionAsync(cancellationToken));

    public Task<ProcessResult> ListRunningWslDistributionsAsync(CancellationToken cancellationToken = default)
        => Unreaching(nameof(ListRunningWslDistributionsAsync), () => inner.ListRunningWslDistributionsAsync(cancellationToken));

    private async Task<ProcessResult> Unreaching(string call, Func<Task<ProcessResult>> answer)
    {
        if (reaches is not null && !reaches(call))
        {
            return await answer().ConfigureAwait(false);
        }

        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            throw new ProcessStoppedException(UnreachingRunner.Unreached, stopping.Token);
        }

        return await answer().ConfigureAwait(false) with { Uncontained = UnreachingRunner.Unreached };
    }
}

/// <summary>Where a test that is not about what a stop could not reach or end has it said: nowhere.</summary>
internal static class UnreachedSaid
{
    /// <summary>Says nothing.</summary>
    public static readonly Action<string> Nowhere = static _ => { };

    /// <summary>
    /// A caller run against what could not reach or end a process - in its result, or in its caller's stop - and what it
    /// then said of that on the harness's error stream, each line as written: the lines that name it, and nothing else of
    /// what the caller said. What the caller then raised is no part of it - a stop goes on as it came, and a caller handed
    /// an answer made for this may refuse it - but is given beside, to say what happened where the lines differ.
    /// </summary>
    /// <param name="factory">The harness whose error stream is read.</param>
    /// <param name="stopped">Whether the caller's own stop ends the run, the stop thrown in place of a result.</param>
    /// <param name="act">The caller's run, given the stop to cancel and the token it holds.</param>
    public static Task<(string[] Said, Exception? Raised)> ByAsync(HarnessFactory factory, bool stopped, Func<CancellationTokenSource?, CancellationToken, Task> act)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return ByAsync(() => factory.StandardError.ToString(), stopped, act);
    }

    /// <summary>As <see cref="ByAsync(HarnessFactory, bool, Func{CancellationTokenSource?, CancellationToken, Task})"/>, what was said read from <paramref name="written"/>.</summary>
    /// <param name="written">What the caller wrote, read once it has run.</param>
    /// <param name="stopped">Whether the caller's own stop ends the run.</param>
    /// <param name="act">The caller's run.</param>
    public static async Task<(string[] Said, Exception? Raised)> ByAsync(Func<string> written, bool stopped, Func<CancellationTokenSource?, CancellationToken, Task> act)
    {
        ArgumentNullException.ThrowIfNull(written);

        var raised = await RunAsync(stopped, act);

        return (Lines(written()), raised);
    }

    /// <summary>
    /// What a caller handed its caller's own way of saying it said, each line as it was handed: a caller with no output
    /// of its own, run as <see cref="ByAsync(HarnessFactory, bool, Func{CancellationTokenSource?, CancellationToken, Task})"/> runs one.
    /// </summary>
    /// <param name="stopped">Whether the caller's own stop ends the run.</param>
    /// <param name="act">The caller's run, given the stop, the token it holds and where its lines are said.</param>
    public static async Task<(string[] Said, Exception? Raised)> HandedAsync(bool stopped, Func<CancellationTokenSource?, CancellationToken, Action<string>, Task> act)
    {
        ArgumentNullException.ThrowIfNull(act);

        var said = new List<string>();
        var raised = await RunAsync(stopped, (stop, token) => act(stop, token, line =>
        {
            lock (said)
            {
                said.Add(line);
            }
        }));

        lock (said)
        {
            return ([.. said], raised);
        }
    }

    /// <summary>The line a caller hands on, naming <paramref name="what"/> ran.</summary>
    public static string Handed(string what) => $"{what}: {UnreachingRunner.Unreached}";

    /// <summary>Runs <paramref name="act"/> with or without its caller's stop, bounded, and answers what it raised.</summary>
    private static async Task<Exception?> RunAsync(bool stopped, Func<CancellationTokenSource?, CancellationToken, Task> act)
    {
        ArgumentNullException.ThrowIfNull(act);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            await act(stopped ? stop : null, stop.Token).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return ex;
        }
    }

    /// <summary>The lines of <paramref name="written"/> that name what could not be reached or ended.</summary>
    public static string[] Lines(string written)
        => [.. written.ReplaceLineEndings("\n").Split('\n').Where(line => line.Contains(UnreachingRunner.Unreached, StringComparison.Ordinal))];

    /// <summary>The warning a caller says under <paramref name="command"/>, naming <paramref name="what"/> ran.</summary>
    public static string Warned(string command, string what) => $"{command}: WARN - {what}: {UnreachingRunner.Unreached}";
}
