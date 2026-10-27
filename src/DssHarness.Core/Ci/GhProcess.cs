using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Core.Ci;

/// <summary>
/// The one way this tool runs the forge's command line, <c>gh</c>, whoever asks: a read of CI, and a pull request's
/// publishing. Each call answers for the repository it stands in, cannot ask anyone anything, and is ended where it has
/// not finished in time: its caller's limit - the step's <c>timeoutMinutes</c>, where the step that makes it says one -
/// else its own, <see cref="Limit"/>.
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
/// window, a server that does not answer - is started so that it cannot ask (<see cref="UnattendedCommands"/>), and
/// ended at its limit: what it was asked was not done, and a caller may ask again (<see cref="HarnessExit.Incomplete"/>).
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

    /// <summary>How long one call may take where its caller gives no limit of its own: five minutes.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The variable that makes the forge's tool answer for a repository somebody else named. Refused rather than cleared:
    /// it is an explicit override, and a call answers for the tree it was pointed at, so silently ignoring it would answer
    /// a different question than the caller asked.
    /// </summary>
    internal const string RepositoryOverrideVariable = "GH_REPO";

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly GitLocalVariables _localVariables = localVariables ?? new GitLocalVariables(processRunner);
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>Runs <c>gh</c> with <paramref name="arguments"/> in <paramref name="repositoryRoot"/> and answers what it printed.</summary>
    /// <param name="repositoryRoot">The repository the call answers for.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="notDone">What a call that did not answer leaves undone, as each refusal ends: "so CI was NOT read (this is not a pass)".</param>
    /// <param name="limit">How long the call may take; <see cref="Limit"/> where null.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <param name="late">
    /// What a call that did not answer in time leaves, where that is not <paramref name="notDone"/> - a write the forge
    /// may have made before it was ended: "so whether a pull request was opened is not known"; null for a read, which did
    /// not read.
    /// </param>
    /// <exception cref="HarnessException">
    /// <c>GH_REPO</c> is set (13); <c>gh</c> is not there (14); it failed (20); or it passed its limit (21). Each says what
    /// it left undone.
    /// </exception>
    public async Task<string> RunAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        string notDone,
        TimeSpan? limit,
        CancellationToken cancellationToken,
        string? late = null)
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

        UnattendedCommands.Apply(environment);

        var budget = limit ?? Limit;
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
                $"{Executable} {string.Join(' ', arguments)} did not answer within {ReportText.Limit(budget)}, {late ?? notDone}. Asked again, "
                + "it may answer: a forge that is slow, or a network that dropped, refused nothing.");
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
}
