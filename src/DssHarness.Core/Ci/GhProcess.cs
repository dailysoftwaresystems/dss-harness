using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Text;
using DssHarness.Core.Waiting;

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

    /// <summary>The variable that sends a <c>gh</c> call that names no host to another host than github.com.</summary>
    public const string HostVariable = "GH_HOST";

    /// <summary>The variable <c>gh</c> reads a token from for github.com and every <c>*.ghe.com</c> host, before its stored login.</summary>
    public const string TokenVariable = "GH_TOKEN";

    /// <summary>The variable <c>gh</c> reads a token from for any other host - a GitHub Enterprise Server's - before its stored login.</summary>
    public const string EnterpriseTokenVariable = "GH_ENTERPRISE_TOKEN";

    /// <summary>
    /// Every variable <c>gh</c> reads a token from, for one host kind or another: <see cref="TokenVariable"/> and its older
    /// spelling for github.com and <c>*.ghe.com</c>, <see cref="EnterpriseTokenVariable"/> and its older spelling for a
    /// GitHub Enterprise Server - where, in a Codespace, <c>GITHUB_TOKEN</c> is read as well.
    /// </summary>
    public static IReadOnlyList<string> TokenVariables { get; } = [TokenVariable, "GITHUB_TOKEN", EnterpriseTokenVariable, "GITHUB_ENTERPRISE_TOKEN"];

    /// <summary>
    /// What <c>gh</c> writes, in any case, where a call never reached the forge: its own words for a name that did not
    /// resolve, and the network's own for an address that did not answer, a connection that dropped or a wait that ran
    /// out. Every other failure is the forge's answer, or gh's own refusal - a credential refused, a login missing, a
    /// thing not there - and never reads as the network not being there.
    /// </summary>
    public static IReadOnlyList<string> UnreachedWords { get; } =
    [
        "error connecting to ", "no such host", "dial tcp", "connection refused", "connection reset", "network is unreachable",
        "no route to host", "i/o timeout", "tls handshake timeout", "client.timeout exceeded", "context deadline exceeded",
        "server misbehaving", "temporary failure in name resolution",
    ];

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly GitLocalVariables _localVariables = localVariables ?? new GitLocalVariables(processRunner);
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>
    /// Whether <paramref name="failed"/>, which a call of this class's raised, says the call never reached the forge: it
    /// did not answer within its limit (<see cref="HarnessExit.Incomplete"/>), or gh said one of
    /// <see cref="UnreachedWords"/>. A forge that answered - 401, 403, 404, a login gh does not have - and gh not
    /// being there are not this.
    /// </summary>
    /// <param name="failed">What a call raised.</param>
    public static bool Unreached(HarnessException failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        return failed.ExitCode == HarnessExit.Incomplete
            || (failed.ExitCode == HarnessExit.CommandFailed && UnreachedWords.Any(words => failed.Message.Contains(words, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The HTTP status the forge answered a call with, as gh says it - <c>(HTTP 404)</c>, <c>HTTP 502:</c> - where
    /// <paramref name="failed"/>, which a call of this class's raised, is a failure gh said one for; null where it is not.
    /// </summary>
    /// <param name="failed">What a call raised.</param>
    public static int? HttpStatus(HarnessException failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        return failed.ExitCode == HarnessExit.CommandFailed && StatusWords.Match(failed.Message) is { Success: true } said
            ? int.Parse(said.Groups["status"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// What a call of this class's that raised <paramref name="failed"/> comes to as one try of a run of them
    /// (<see cref="Retrying"/>), by whether another call can change it: the forge not reached - no answer within the
    /// call's limit, the network's own words (<see cref="Unreached"/>) - a server error (5xx), and a rate limit (429, or
    /// a 403 that says it is one) pass by nature, and are tried again for as long as they last; a refused credential
    /// (401), a thing not there (404) and any other answer of the forge's that refused what was asked (4xx) are final, for
    /// asking again asks the same; any other failure is tried again while it is new, its own words its digest.
    /// </summary>
    /// <typeparam name="T">What a try answers.</typeparam>
    /// <param name="failed">What the call raised.</param>
    public static RetryTry<T> Try<T>(HarnessException failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        var said = failed.Message.TrimEnd('.');

        if (Unreached(failed))
        {
            return new RetryTry<T>.Passing(said, null);
        }

        return HttpStatus(failed) switch
        {
            429 or (>= 500 and <= 599) => new RetryTry<T>.Passing(said, null),
            403 when failed.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) => new RetryTry<T>.Passing(said, null),
            >= 400 and <= 499 => new RetryTry<T>.Final(said),
            _ => new RetryTry<T>.Failed(said, said),
        };
    }

    /// <summary>How gh says the HTTP status the forge answered with: <c>(HTTP 404)</c> after its message, or <c>HTTP 502:</c> before it.</summary>
    private static readonly Regex StatusWords = LinearRegex.Compile(@"\bHTTP (?<status>[1-5][0-9][0-9])\b");

    /// <summary>
    /// The environment a <c>gh</c> call to <paramref name="host"/> is given beyond the machine's own: <see cref="HostVariable"/>
    /// taken away, so the call reaches the host its <c>--hostname</c> names and no other, and for a host beyond github.com
    /// every one of <see cref="TokenVariables"/> taken away but the one gh reads for that host's kind, set to
    /// <paramref name="token"/> - so no token of the machine's reaches a host it was not meant for. github.com keeps the
    /// machine's own <see cref="TokenVariable"/> and login, and never sees an enterprise token.
    /// </summary>
    /// <param name="host">The host the call reaches.</param>
    /// <param name="token">The token for a host beyond github.com, read from the credential its configuration names; ignored for github.com.</param>
    public static Dictionary<string, string?> HostEnvironment(string host, string? token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { [HostVariable] = null };

        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            environment[EnterpriseTokenVariable] = null;
            environment["GITHUB_ENTERPRISE_TOKEN"] = null;

            return environment;
        }

        foreach (var name in TokenVariables)
        {
            environment[name] = null;
        }

        environment[TokenVariableOf(host)] = token;

        return environment;
    }

    /// <summary>
    /// The one variable <c>gh</c> reads a token for <paramref name="host"/> from: <see cref="TokenVariable"/> for
    /// github.com and a <c>*.ghe.com</c> host, <see cref="EnterpriseTokenVariable"/> for any other.
    /// </summary>
    /// <param name="host">The host.</param>
    public static string TokenVariableOf(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)
            ? TokenVariable
            : EnterpriseTokenVariable;
    }

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
    /// <param name="withEnvironment">
    /// Values the call is given beyond the machine's own environment, set after the variables a repository's own
    /// tree would put in are cleared: a host's credential, handed to the call and written nowhere else. Null where
    /// none is given.
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
        string? late = null,
        IReadOnlyDictionary<string, string?>? withEnvironment = null)
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

        // Given beyond the machine's own, after the clearing: a host's credential is the caller's to hand over, never
        // something this tool reads or writes anywhere but here.
        if (withEnvironment is not null)
        {
            foreach (var (name, value) in withEnvironment)
            {
                environment[name] = value;
            }
        }

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
