using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <summary>
/// The variables git reads a repository from rather than from <c>-C &lt;directory&gt;</c>: every name
/// <c>git rev-parse --local-env-vars</c> prints. Every git child this tool starts, and every child that
/// runs git itself, starts without them.
/// </summary>
/// <remarks>
/// <para>
/// git owns the list, so git is asked, once, rather than the list being written down here. It was written
/// down, twice, as three names (GIT_DIR, GIT_WORK_TREE and GIT_INDEX_FILE), while git names fifteen, among
/// them GIT_COMMON_DIR, GIT_OBJECT_DIRECTORY and the variables that hold <c>-c</c> settings. Measured: with
/// GIT_COMMON_DIR exported naming another repository, deleting a worktree was refused and left the worktree
/// registered, because git answered for the repository the variable named.
/// </para>
/// <para>
/// The question needs no repository, and a caller's environment does not change its answer: measured with a
/// GIT_DIR naming nothing, a GIT_COMMON_DIR naming nothing, malformed GIT_CONFIG_PARAMETERS and
/// GIT_CONFIG_COUNT, and inside a repository whose configuration git cannot parse. An answer that does not
/// name GIT_DIR is not taken, and nothing is run: a list missing the variable every git hook exports would
/// let a child answer for a repository nobody named. Only an answer is kept; a question that failed is asked
/// again by the next command that needs it.
/// </para>
/// </remarks>
public sealed class GitLocalVariables
{
    /// <summary>What git is asked.</summary>
    internal static readonly IReadOnlyList<string> QueryArguments = ["rev-parse", "--local-env-vars"];

    /// <summary>The variable an answer must name to be taken: the one every git hook runs with.</summary>
    private const string RequiredName = "GIT_DIR";

    private readonly IProcessRunner? _processRunner;
    private readonly SemaphoreSlim _asking = new(1, 1);
    private IReadOnlyList<string>? _names;

    /// <summary>Asks git, through <paramref name="processRunner"/>, the first time the names are needed.</summary>
    /// <param name="processRunner">Runs git.</param>
    public GitLocalVariables(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    private GitLocalVariables(IReadOnlyList<string> names)
    {
        _names = names;
    }

    /// <summary>
    /// Names given rather than asked for: for a caller whose process runner answers every request the same
    /// way, where the question would be answered as if it were the command under test.
    /// </summary>
    /// <param name="names">The names to clear.</param>
    public static GitLocalVariables Fixed(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new GitLocalVariables(names);
    }

    /// <summary>Every name git calls repository-local.</summary>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">
    /// git could not answer, or its answer did not name GIT_DIR (<see cref="HarnessExit.CommandFailed"/>).
    /// </exception>
    public async Task<IReadOnlyList<string>> NamesAsync(CancellationToken cancellationToken = default)
    {
        if (_names is { } known)
        {
            return known;
        }

        await _asking.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return _names ??= await AskAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _asking.Release();
        }
    }

    /// <summary>Marks every repository-local name in <paramref name="environment"/> as removed.</summary>
    /// <param name="environment">A child's environment overrides, where a null value removes the variable.</param>
    /// <param name="cancellationToken">Stops the question, when it has not been answered yet.</param>
    /// <exception cref="HarnessException">As <see cref="NamesAsync"/>.</exception>
    public async Task ClearAsync(IDictionary<string, string?> environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var name in await NamesAsync(cancellationToken).ConfigureAwait(false))
        {
            environment[name] = null;
        }
    }

    private async Task<IReadOnlyList<string>> AskAsync(CancellationToken cancellationToken)
    {
        var result = await _processRunner!
            .RunAsync(new ProcessRequest { FileName = "git", Arguments = QueryArguments }, cancellationToken)
            .ConfigureAwait(false);

        var names = result.StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (!result.Succeeded || !names.Contains(RequiredName, StringComparer.Ordinal))
        {
            var said = result.Succeeded
                ? $"its answer did not name {RequiredName}"
                : result.TimedOut
                    ? "it did not answer in time"
                    : $"it exited {result.ExitCode}{(result.StandardError.Trim() is { Length: > 0 } error ? ": " + error : string.Empty)}";

            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git could not name the variables it reads a repository from ('git {string.Join(' ', QueryArguments)}': {said}), "
                + "so nothing was run: a git command started with one of them set could answer for a repository nobody named.");
        }

        return names;
    }
}
