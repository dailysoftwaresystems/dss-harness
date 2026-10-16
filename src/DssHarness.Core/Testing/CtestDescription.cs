using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Core.Testing;

/// <summary>
/// What a red control asks about the binary it ran, in the worker whose build ran it: everything needed to ask ctest, as
/// the leg's tests start it, what it registers for the binary.
/// </summary>
/// <param name="Program">The binary the control ran, whole, in the worker.</param>
/// <param name="RedCases">The cases its unmutated run reddened.</param>
/// <param name="Settings">The subject's test settings, where it has them.</param>
/// <param name="WorkerOs">The system the worker runs, as the leg names it: which section of the test settings is read.</param>
/// <param name="Paths">The directories the worker's tests run against, their names filled in as a test command's are.</param>
/// <param name="Excludes">The exclusions the <c>test</c> command gives a leg working in a copy: its invocation's remote ones.</param>
/// <param name="Worker">The worker's copy, where the binary is and the runner starts.</param>
/// <param name="HostEnvironment">What the leg's host gives it, over which the invocation's environment is laid.</param>
/// <param name="ProgramDirectories">The host's program directories, put around the runner's PATH.</param>
/// <param name="DefaultCores">The core count the configuration defaults a test to, where its invocation names none.</param>
internal sealed record CtestQuestion(
    string Program,
    IReadOnlyList<string> RedCases,
    TestConfig? Settings,
    string WorkerOs,
    LegPaths? Paths,
    IReadOnlyList<string> Excludes,
    string Worker,
    IReadOnlyDictionary<string, string> HostEnvironment,
    ProgramDirectories ProgramDirectories,
    int? DefaultCores)
{
    /// <summary>The invocation the leg's tests start with, or <see langword="null"/> where the subject has no test settings.</summary>
    public ResolvedTestInvocation? Invocation => Settings is { } settings ? TestInvocationResolver.InvocationFor(settings, WorkerOs) : null;
}

/// <summary>What ctest said of a red control's binary - the match - or why it could not say.</summary>
internal sealed record CtestAsked
{
    private CtestAsked(CtestMatch? match, string? unread)
    {
        Match = match;
        Unread = unread;
    }

    /// <summary>What ctest registered for the binary's red cases; <see langword="null"/> where it could not say.</summary>
    public CtestMatch? Match { get; }

    /// <summary>Why ctest could not say: it would not start, it failed, it ran past the bound, or its answer was no description.</summary>
    public string? Unread { get; }

    /// <summary>Ctest answered: <paramref name="match"/> is what it registered.</summary>
    internal static CtestAsked Said(CtestMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return new(match, null);
    }

    /// <summary>Ctest could not say: <paramref name="unread"/> is why.</summary>
    internal static CtestAsked Refused(string unread) => new(null, unread);
}

/// <summary>
/// Asks ctest, as the leg's tests start it, what it registers for a red control's binary: the command the
/// <c>test</c> command of the same settings makes, with no filter and no label and the exclusions a leg working in a copy
/// is given, <see cref="CtestEntries.ShowOnly"/> appended, started where that command starts, bounded by two minutes. It
/// owns no rule of the match: <see cref="CtestEntries"/> reads the answer.
/// </summary>
internal sealed class CtestDescription(IProcessRunner processes, IFileSystem fileSystem, IHostPlatform platform) : ICtestDescription
{
    /// <summary>How long ctest is given to answer: a description is read, not run.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _processes = processes;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <inheritdoc/>
    public async Task<CtestAsked?> AskAsync(CtestQuestion question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (question.Invocation is not { } invocation || !Ctest.Is(invocation.Runner))
        {
            return null;
        }

        try
        {
            var command = TestInvocationResolver.CommandFor(
                invocation,
                CoreCounts.Resolve(invocation.Cores, null, question.DefaultCores).Value,
                filter: null,
                question.Excludes,
                question.Paths,
                labels: null,
                _fileSystem);
            var start = TestInvocationResolver.StartDirectory(command.WorkingDirectory, question.Worker);
            var answer = await _processes
                .RunAsync(
                    new ProcessRequest
                    {
                        FileName = command.Program,
                        Arguments = [.. command.Arguments, CtestEntries.ShowOnly],
                        WorkingDirectory = start,
                        Environment = TestInvocationResolver.EnvironmentFor(question.HostEnvironment, invocation.Env),
                        ProgramDirectories = question.ProgramDirectories,
                        Timeout = Bound,
                        OutputKept = StreamKept.Whole,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (answer.TimedOut)
            {
                return CtestAsked.Refused($"ctest did not answer within {Bound.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s");
            }

            if (answer.ExitCode != 0)
            {
                return CtestAsked.Refused($"ctest exited {answer.ExitCode}");
            }

            var read = CtestEntries.Read(answer.StandardOutput);

            return read.Tests is { } tests
                ? CtestAsked.Said(CtestEntries.For(tests, question.Program, question.RedCases, start, StringComparer.FromComparison(_platform.PathComparison)))
                : CtestAsked.Refused(read.Why!);
        }
        catch (Exception ex) when (ex is HarnessException or ProgramStartException or IOException or UnauthorizedAccessException)
        {
            return CtestAsked.Refused(ex.Message.TrimEnd('.'));
        }
    }
}

/// <summary>Asks ctest, as the leg's tests start it, what it registers for a red control's cases, in the worker whose build ran the control.</summary>
internal interface ICtestDescription
{
    /// <summary>
    /// What ctest registers for the binary a red control ran - which tests start it, and what of their entries a sweep
    /// does not apply - or why that could not be said; <see langword="null"/> where the leg's tests run through no ctest,
    /// so nothing was asked.
    /// </summary>
    /// <param name="question">What the control asks about the binary it ran.</param>
    /// <param name="cancellationToken">Stops the sweep, which the asking is part of.</param>
    Task<CtestAsked?> AskAsync(CtestQuestion question, CancellationToken cancellationToken = default);
}
