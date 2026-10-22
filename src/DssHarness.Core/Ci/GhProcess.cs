using System.Globalization;
using DssHarness.Core.Git;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Core.Ci;

/// <summary>
/// The one way this tool runs the forge's command line, <c>gh</c>, whoever asks: a read of CI, and later a push's and a
/// pull request's publishing. Each call answers for the repository it stands in, cannot ask anyone anything, and is
/// ended where it has not finished in time.
/// </summary>
/// <remarks>
/// <para>
/// The forge's tool finds which repository to answer for by running git, so every variable git reads a repository from
/// is cleared before it starts, the same <see cref="GitLocalVariables"/> the git client clears. Measured: with another
/// repository's <c>GIT_DIR</c> exported, the tool answered for THAT repository even when the branch was given
/// explicitly -- a fix that cleaned only the harness's own git calls would have left this second channel open.
/// </para>
/// <para>
/// A call that could wait for ever on something nobody will give it - a prompt for a password, a credential helper's
/// window, a server that does not answer - is started so that it cannot ask (<c>GH_PROMPT_DISABLED</c>,
/// <c>GCM_INTERACTIVE</c>, <c>GIT_TERMINAL_PROMPT</c>), and ended at its limit: what it was asked was not done, and a
/// caller may ask again (<see cref="HarnessExit.Incomplete"/>).
/// </para>
/// </remarks>
/// <param name="processRunner">Runs the forge's tool.</param>
/// <param name="localVariables">
/// The variables it starts without; asked of git through <paramref name="processRunner"/> where none is given.
/// </param>
/// <param name="environment">Reads this process's environment; the process's own where none is given.</param>
public sealed class GhProcess(IProcessRunner processRunner, GitLocalVariables? localVariables = null, Func<string, string?>? environment = null)
{
    /// <summary>The forge's command line. The one dependency, and there is no fallback to a second.</summary>
    public const string Executable = "gh";

    /// <summary>How long one call may take where its caller names no limit: five minutes.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The variable that makes the forge's tool answer for a repository somebody else named. Refused rather than cleared:
    /// it is an explicit override, and a call answers for the tree it was pointed at, so silently ignoring it would answer
    /// a different question than the caller asked.
    /// </summary>
    internal const string RepositoryOverrideVariable = "GH_REPO";

    /// <summary>What each call is started with so that it cannot ask: the forge's tool, a credential manager, and git.</summary>
    internal static readonly IReadOnlyDictionary<string, string> NeverAsks = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly GitLocalVariables _localVariables = localVariables ?? new GitLocalVariables(processRunner);
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>Runs <c>gh</c> with <paramref name="arguments"/> in <paramref name="repositoryRoot"/> and answers what it printed.</summary>
    /// <param name="repositoryRoot">The repository the call answers for.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="limit">The longest it may take; <see cref="DefaultLimit"/> where null.</param>
    /// <param name="notDone">What a call that did not answer leaves undone, as each refusal ends: "so CI was NOT read (this is not a pass)".</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <exception cref="HarnessException">
    /// <c>GH_REPO</c> is set (13); <c>gh</c> is not there (14); it failed (20); or it passed its limit (21). Each says what
    /// it left undone.
    /// </exception>
    public async Task<string> RunAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        TimeSpan? limit,
        string notDone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(notDone);

        if (_environment(RepositoryOverrideVariable) is { Length: > 0 } named)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{RepositoryOverrideVariable} is set ({named}), so {Executable} would answer for THAT repository, not the tree "
                + $"it was pointed at, {notDone}. Unset it for this call.");
        }

        if (_processRunner.FindExecutable(Executable) is null)
        {
            throw new HarnessException(
                HarnessExit.ToolMissing,
                $"'{Executable}' is not installed or not on PATH, {notDone}: an instrument that could not look must never read as one.");
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        await _localVariables.ClearAsync(environment, cancellationToken).ConfigureAwait(false);

        foreach (var (name, value) in NeverAsks)
        {
            environment[name] = value;
        }

        var budget = limit ?? DefaultLimit;
        var result = await _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = Executable,
                Arguments = arguments,
                WorkingDirectory = repositoryRoot,
                Environment = environment,
                Timeout = budget,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new HarnessException(
                HarnessExit.Incomplete,
                $"{Executable} {string.Join(' ', arguments)} did not answer within {Minutes(budget)}, {notDone}. Asked again, "
                + "it may answer: a forge that is slow or a network that dropped says nothing of the code.");
        }

        if (!result.Succeeded)
        {
            var said = result.StandardError.Trim();

            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Executable} {string.Join(' ', arguments)} failed, {notDone}: "
                + (said.Length > 0 ? said : $"it exited with code {result.ExitCode}."));
        }

        return result.StandardOutput;
    }

    private static string Minutes(TimeSpan budget)
        => budget.TotalMinutes >= 1 && budget.TotalMinutes == Math.Floor(budget.TotalMinutes)
            ? $"{budget.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minute{(budget.TotalMinutes == 1 ? string.Empty : "s")}"
            : $"{budget.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
}
