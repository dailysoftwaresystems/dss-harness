using System.Text.Json.Serialization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>The run that owns a log directory.</summary>
/// <param name="Machine">The machine it runs on.</param>
/// <param name="ProcessId">Its process id.</param>
/// <param name="RunId">Its run id.</param>
/// <param name="TakenUtc">When it claimed the directory, for display.</param>
/// <param name="ProcessStamp">
/// What tells that process from another that inherits its id, so a recycled id is not read as a live
/// owner. Holds no clock, so a clock that steps cannot turn a live owner into a dead one. Left out by
/// a build before stamps, and where the platform would not say.
/// </param>
public sealed record LogOwner(
    string Machine,
    int ProcessId,
    string RunId,
    DateTimeOffset TakenUtc,
    string? ProcessStamp = null)
{
    /// <summary>
    /// The wall-clock start time a build before this one recorded here, kept only so such a file is
    /// still readable. Nothing decides anything from it: it is exactly the value that moves when the
    /// clock steps, which is why it stopped being what identifies a process.
    /// </summary>
    /// <remarks>
    /// Declared rather than skipped so that every other unknown member can be refused, as this tool
    /// refuses one everywhere else it reads JSON. Never written back: an entry rewritten by this
    /// build carries a stamp instead.
    /// </remarks>
    [JsonPropertyName("processStartedUtc")]
    public DateTimeOffset? LegacyStartedUtc { get; init; }

    /// <summary>The owner as a refusal names it, said to be another machine's where it is, never naming that machine.</summary>
    /// <param name="identity">This process, which tells this machine from another.</param>
    public string Describe(IProcessIdentity identity)
        => identity.Describe(Machine, ProcessId, RunId, TakenUtc) + ProcessHolders.OlderBuildNote(ProcessStamp);
}

/// <summary>
/// What claiming a directory a run owns found: its log directory, or a mutation worker's copy. What a claim not taken
/// means is its claimer's to say - a run that cannot own its log path is <c>log-held</c>, a sweep that cannot own a
/// worker leaves it for the sweep that does - so the claim itself names no verdict.
/// </summary>
/// <param name="Taken">Whether this run now owns it.</param>
/// <param name="Holder">The live run that owns it instead, when one does.</param>
/// <param name="OwnerFile">Where the ownership is recorded.</param>
public sealed record LogClaim(bool Taken, LogOwner? Holder, string OwnerFile)
{
    /// <summary>The run that owns it instead, as a refusal names it, where one does.</summary>
    public string? HeldBy { get; init; }
}

/// <summary>
/// Records which run owns a log directory, so that two runs cannot write one set of logs.
/// </summary>
/// <remarks>
/// Every run has its own id and every log is scoped to it, which keeps two runs apart as long as
/// both chose their own id. A run told to write somewhere already owned — a rerun pointed at a
/// previous run's directory, or two runs given the same one — is refused as <c>log-held</c> rather
/// than allowed to interleave its output with another run's, since one leg's result read as
/// another's is exactly the failure the ids exist to prevent. Claimed as every directory a run owns
/// is, in the log directory's own terms.
/// </remarks>
public sealed class LogOwnership(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity)
{
    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "logs";

    /// <summary>
    /// The suffix of the file recording the owner. Beside the log directory rather than inside it,
    /// so that wiping a run directory cannot quietly free a directory a live run still owns.
    /// </summary>
    public const string OwnerSuffix = ".owner.json";

    private readonly DirectoryClaims _claims = new(
        fileSystem,
        output,
        identity,
        new ClaimTerms
        {
            CommandName = CommandName,
            OwnerSuffix = OwnerSuffix,
            Directory = directory => $"the log path '{directory}'",
            OwnerFile = "The log owner file",
            Unwritten = "Until it can be, two runs could write one set of logs.",
            Unreadable = "Remove it once no run is using that path.",
            Made = directory => $"The log directory '{directory}'",
            Unmade = "Until it can be, the run has nowhere to keep its records.",
            Beside = "The runs beside",
            Held = directory => $"its records at '{directory}'",
            Abandoned = "so its verdict may never have been reported.",
        });

    /// <summary>Where a log directory's ownership is recorded.</summary>
    /// <param name="logDirectory">The directory a run writes its logs to.</param>
    public static string OwnerFile(string logDirectory) => DirectoryClaims.OwnerFile(logDirectory, OwnerSuffix);

    /// <summary>
    /// Claims <paramref name="logDirectory"/> for <paramref name="runId"/>. An owner whose process
    /// has gone is reclaimed and the reclaim is reported; a live one is refused, and the leg's
    /// verdict is <c>log-held</c>.
    /// </summary>
    /// <param name="logDirectory">The directory this run writes its logs to.</param>
    /// <param name="runId">The run claiming it.</param>
    /// <param name="force">
    /// Whether to take a path a live owner still holds, which <c>--force-lock</c> asks for. The only
    /// way out of an owner that cannot be reclaimed: without it the file has to be deleted by hand.
    /// </param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    public Task<LogClaim> ClaimAsync(
        string logDirectory,
        RunId runId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(runId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_claims.Claim(logDirectory, runId, force));
    }

    /// <summary>
    /// Gives up <paramref name="logDirectory"/>, and only when this run owns it. A release that did
    /// not check would free a directory another run had just claimed.
    /// </summary>
    /// <param name="logDirectory">The directory this run wrote its logs to.</param>
    /// <param name="runId">The run giving it up.</param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    public Task ReleaseAsync(string logDirectory, RunId runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(runId);
        cancellationToken.ThrowIfCancellationRequested();

        // Given up once the run is over, so a failure here is said and never stands in for what the
        // run found: the record names this process, and is reclaimed as a dead owner's is once it
        // has ended.
        _claims.Release(logDirectory, runId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases every log directory beside <paramref name="logDirectory"/> that a run on this machine claimed and never
    /// gave up, its process having ended first, and says each: such a run was most likely killed, or stopped with its
    /// machine, before it finished, and nothing else would ever say so - nothing claims its directory again, every run
    /// having its own.
    /// </summary>
    /// <param name="logDirectory">The directory this run writes its logs to, whose own claim is left alone.</param>
    /// <returns>The runs found abandoned, in the order their owner files sort.</returns>
    /// <remarks>
    /// Only a claim this machine can judge is released: one recorded on another machine stands, as it does beside a run
    /// claiming its own directory. One this build cannot read - a newer build's, a run's still going among them, or one
    /// its machine stopped while it was being written - or cannot reach in the window is left as it is, and said: unsaid,
    /// a run whose machine left its record unreadable would never be said, nor its record removed. No failure expected
    /// here fails the run that found them: a directory that cannot be listed is said, and nothing in it is released.
    /// </remarks>
    public IReadOnlyList<LogOwner> ReleaseAbandoned(string logDirectory) => _claims.ReleaseAbandonedBeside(logDirectory);

    /// <summary>
    /// The run that owns <paramref name="logDirectory"/>, or <see langword="null"/> when none does.
    /// </summary>
    /// <param name="logDirectory">The directory a run writes its logs to.</param>
    public LogOwner? Owner(string logDirectory) => _claims.Owner(logDirectory);
}
