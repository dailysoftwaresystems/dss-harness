using System.Text.Json;
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

    /// <summary>The owner as a refusal names it.</summary>
    public string Describe()
        => ProcessHolders.Describe(Machine, ProcessId, RunId, TakenUtc) + ProcessHolders.OlderBuildNote(ProcessStamp);
}

/// <summary>What claiming a log directory found.</summary>
/// <param name="Taken">Whether this run now owns it.</param>
/// <param name="Holder">The live run that owns it instead, when one does.</param>
/// <param name="OwnerFile">Where the ownership is recorded.</param>
public sealed record LogClaim(bool Taken, LogOwner? Holder, string OwnerFile)
{
    /// <summary>The run that owns it instead, as a refusal names it, where one does.</summary>
    public string? HeldBy { get; init; }

    /// <summary>
    /// The verdict this forces on the leg, or <see langword="null"/> when the claim succeeded. A run
    /// that cannot own its log path cannot keep the evidence for its own verdict, and a verdict with
    /// no evidence behind it is the thing this tool exists to stop reporting.
    /// </summary>
    public ReachedVerdict? Verdict()
        => Taken
            ? null
            : ReachedVerdict.Of(
                LegVerdict.LogHeld,
                HeldBy is null ? "another run owns this log path" : $"another run owns this log path: {HeldBy}");
}

/// <summary>
/// Records which run owns a log directory, so that two runs cannot write one set of logs.
/// </summary>
/// <remarks>
/// Every run has its own id and every log is scoped to it, which keeps two runs apart as long as
/// both chose their own id. A run told to write somewhere already owned — a rerun pointed at a
/// previous run's directory, or two runs given the same one — is refused as <c>log-held</c> rather
/// than allowed to interleave its output with another run's, since one leg's result read as
/// another's is exactly the failure the ids exist to prevent.
/// </remarks>
public sealed class LogOwnership(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity)
{
    private readonly IProcessIdentity _identity = identity;

    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "logs";

    /// <summary>
    /// The suffix of the file recording the owner. Beside the log directory rather than inside it,
    /// so that wiping a run directory cannot quietly free a directory a live run still owns.
    /// </summary>
    public const string OwnerSuffix = ".owner.json";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>Where a log directory's ownership is recorded.</summary>
    /// <param name="logDirectory">The directory a run writes its logs to.</param>
    public static string OwnerFile(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(logDirectory)) + OwnerSuffix;
    }

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

        var file = OwnerFile(logDirectory);

        // Claimed before anything else a run writes, so a directory this user cannot write - one an
        // earlier run under sudo left to root - is refused here, naming it, rather than escaping as
        // an error that reads as a defect in this tool.
        Written(file, () => _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!));

        var claim = MachineWideFile.Update(file, MachineWideFile.Window, afterwards =>
        {
            if (Read(file) is { } existing && !Mine(existing, runId))
            {
                var held = _identity.Stands(existing.Machine, existing.ProcessId, existing.ProcessStamp);

                if (held && !force)
                {
                    // A holder on another machine cannot be asked whether it is still running, so
                    // it stands. Liveness, never a timeout, is what decides for one on this machine.
                    return new LogClaim(false, existing, file) { HeldBy = existing.Describe() + _identity.ElsewhereNote(existing.Machine) };
                }

                // Said once the owner file is let go, as every line about a machine-wide file is.
                afterwards(held
                    ? () => _output.Warn(CommandName, ProcessHolders.TakenByForce($"the log path '{logDirectory}'", existing.Describe()))
                    : () => _output.Info(CommandName, ProcessHolders.Reclaimed($"the log path '{logDirectory}'", existing.Describe())));
            }

            var owner = new LogOwner(
                _identity.CurrentMachine,
                _identity.CurrentId,
                runId.Value,
                DateTimeOffset.UtcNow,
                _identity.Current);

            Written(file, () => _fileSystem.WriteAllTextAtomic(file, JsonSerializer.Serialize(owner, JsonStateFile.Options) + "\n"));

            // Made as it is claimed: a run names this directory as where its records are, and one
            // whose legs wrote nothing would otherwise have named a directory that did not exist.
            MachineWideFile.Written(
                $"The log directory '{logDirectory}'",
                "Until it can be, the run has nowhere to keep its records.",
                () => _fileSystem.CreateDirectory(logDirectory));

            return new LogClaim(true, owner, file);
        });

        return Task.FromResult(claim);
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

        var file = OwnerFile(logDirectory);

        // Given up once the run is over, so a failure here is said and never stands in for what the
        // run found: the record names this process, and is reclaimed as a dead owner's is once it
        // has ended.
        try
        {
            MachineWideFile.Update<object?>(file, MachineWideFile.Window, () =>
            {
                if (Read(file) is { } existing && Mine(existing, runId))
                {
                    _fileSystem.DeleteFile(file);
                }

                return null;
            });
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            _output.Warn(CommandName, $"'{logDirectory}' could not be given up: {ex.Message} It is reclaimed once this run has ended.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases every log directory beside <paramref name="logDirectory"/> that a run on this machine claimed and never
    /// gave up, its process having ended first, and says each: such a run was killed, or stopped with its machine, before
    /// it finished, and nothing else would ever say so - nothing claims its directory again, every run having its own.
    /// </summary>
    /// <param name="logDirectory">The directory this run writes its logs to, whose own claim is left alone.</param>
    /// <returns>The runs found abandoned, in the order their owner files sort.</returns>
    /// <remarks>
    /// Only a claim this machine can judge is released: one recorded on another machine stands, as it does beside a run
    /// claiming its own directory, and one this build cannot read - a newer build's, a run's still going among them - or
    /// cannot reach in the window is left as it is, for a later run to look at again. Nothing here fails the run that
    /// found them: a directory that cannot be listed is said, and nothing in it is released.
    /// </remarks>
    public IReadOnlyList<LogOwner> ReleaseAbandoned(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        var own = OwnerFile(logDirectory);
        IReadOnlyList<string> files;

        try
        {
            files = [.. _fileSystem.EnumerateFiles(Path.GetDirectoryName(own)!, recursive: false)
                .Where(file => file.EndsWith(OwnerSuffix, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(file), own, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.Warn(
                CommandName,
                $"The runs beside '{logDirectory}' could not be listed: {ex.Message.TrimEnd('.')}. Any of them that was abandoned is said, and released, by a later run.");
            return [];
        }

        var abandoned = new List<LogOwner>();

        foreach (var file in files)
        {
            try
            {
                if (MachineWideFile.Update(file, MachineWideFile.Window, afterwards => Abandoned(file, afterwards)) is { } owner)
                {
                    abandoned.Add(owner);
                }
            }
            catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
            {
                // Unreadable, held past the window by another process, or its mutex could not be opened: see the remarks.
            }
        }

        return abandoned;
    }

    /// <summary>
    /// The owner <paramref name="file"/> records, released and said once the file is let go, where it is a run on this
    /// machine that has ended; <see langword="null"/>, and nothing done, otherwise.
    /// </summary>
    private LogOwner? Abandoned(string file, Action<Action> afterwards)
    {
        // A holder on another machine stands, since nothing here can ask that machine; one on this machine, this run
        // among them, while its process runs.
        if (Read(file) is not { } owner || _identity.Stands(owner.Machine, owner.ProcessId, owner.ProcessStamp))
        {
            return null;
        }

        var directory = file[..^OwnerSuffix.Length];
        var records = _fileSystem.DirectoryExists(directory) ? $"its records at '{directory}'" : $"'{directory}', which is gone";
        var holder = ProcessHolders.Describe(owner.Machine, owner.ProcessId, owner.RunId, owner.TakenUtc);
        var abandoned = $"An earlier run was abandoned: {holder}, is no longer running, and never gave up {records} - "
            + "it was killed, or stopped with its machine, before it finished - so its verdict may never have been reported.";

        try
        {
            _fileSystem.DeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            afterwards(() => _output.Warn(CommandName, $"{abandoned} Its owner file '{file}' could not be removed: {ex.Message}"));
            return owner;
        }

        afterwards(() => _output.Warn(CommandName, $"{abandoned} Its claim is released."));
        return owner;
    }

    /// <summary>Does <paramref name="write"/>, and refuses, naming the owner file, when it could not be done.</summary>
    private static void Written(string file, Action write)
        => MachineWideFile.Written($"The log owner file '{file}'", "Until it can be, two runs could write one set of logs.", write);

    /// <summary>
    /// The run that owns <paramref name="logDirectory"/>, or <see langword="null"/> when none does.
    /// </summary>
    /// <param name="logDirectory">The directory a run writes its logs to.</param>
    public LogOwner? Owner(string logDirectory) => Read(OwnerFile(logDirectory));

    private bool Mine(LogOwner owner, RunId runId)
        => string.Equals(owner.RunId, runId.Value, StringComparison.Ordinal)
            && owner.ProcessId == _identity.CurrentId
            && _identity.IsHere(owner.Machine);

    /// <summary>
    /// The owner <paramref name="file"/> records, by every state file's rules: an owner file that cannot be read says a
    /// run claimed this path and nothing more, so the claim is refused rather than granted - never read as free, the one
    /// reading that lets two runs write one set of logs. The one field an older build wrote is declared, so upgrading
    /// reads its own owner file rather than refusing it.
    /// </summary>
    private LogOwner? Read(string file)
        => MachineWideFile.Read<LogOwner>(
            _fileSystem,
            file,
            ex => $"The log owner file '{file}' could not be read: {ex.Message.TrimEnd('.')}. Remove it once no run is using that path.");
}
