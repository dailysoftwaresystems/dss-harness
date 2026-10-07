using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// How every line a claim on one kind of directory says speaks of it: one set of terms for a run's log directory, one for
/// a mutation worker's copy.
/// </summary>
internal sealed record ClaimTerms
{
    /// <summary>The command the lines report under.</summary>
    public required string CommandName { get; init; }

    /// <summary>The suffix of the file recording the owner, beside the directory it claims.</summary>
    public required string OwnerSuffix { get; init; }

    /// <summary>The directory, as a line taking or reclaiming it names it: <c>the log path '...'</c>.</summary>
    public required Func<string, string> Directory { get; init; }

    /// <summary>The owner file, as a refusal names it before its path: <c>The log owner file</c>.</summary>
    public required string OwnerFile { get; init; }

    /// <summary>What may happen until the owner file can be written, as a refusal to write it ends.</summary>
    public required string Unwritten { get; init; }

    /// <summary>What to do where the owner file cannot be read, after why.</summary>
    public required string Unreadable { get; init; }

    /// <summary>
    /// Where claiming the directory makes it, the directory as a refusal to make it names it: <c>The log directory
    /// '...'</c>; and <see langword="null"/> where claiming makes nothing - a worker's copy, which a sync refuses to make
    /// where a directory it did not make is there already.
    /// </summary>
    public Func<string, string>? Made { get; init; }

    /// <summary>What is lost until a directory claiming makes can be made, as the refusal to make it ends.</summary>
    public string Unmade { get; init; } = string.Empty;

    /// <summary>The other claims beside a directory, as a line that could not list them names them: <c>The runs beside</c>.</summary>
    public required string Beside { get; init; }

    /// <summary>What an abandoned claim held, as its warning names it where it is still there: <c>its records at '...'</c>.</summary>
    public required Func<string, string> Held { get; init; }

    /// <summary>What a claim abandoned may have cost, ending the warning that says it was.</summary>
    public required string Abandoned { get; init; }
}

/// <summary>
/// Records which run owns a directory, in a file beside it, so two runs never work in one: claimed by a run whose process
/// stands, reclaimed - and said - from one whose process has ended, and given up when the run is done. One implementation
/// for each kind of directory a run claims, spoken of in that kind's own <see cref="ClaimTerms"/>.
/// </summary>
/// <remarks>
/// Beside the directory rather than inside it, so wiping the directory cannot quietly free one a live run still owns.
/// A holder on another machine cannot be asked whether it still runs, so it stands; liveness, never a timeout, decides
/// for one on this machine.
/// </remarks>
internal sealed class DirectoryClaims(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, ClaimTerms terms)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly IProcessIdentity _identity = identity;
    private readonly ClaimTerms _terms = terms;

    /// <summary>Where <paramref name="directory"/>'s ownership is recorded.</summary>
    /// <param name="directory">The directory claimed.</param>
    public string OwnerFile(string directory) => OwnerFile(directory, _terms.OwnerSuffix);

    /// <summary>Where the ownership of <paramref name="directory"/> is recorded, by a file ending in <paramref name="suffix"/> beside it.</summary>
    /// <param name="directory">The directory claimed.</param>
    /// <param name="suffix">What its owner file adds to its path.</param>
    public static string OwnerFile(string directory, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + suffix;
    }

    /// <summary>
    /// Claims <paramref name="directory"/> for <paramref name="runId"/>: an owner whose process has gone is reclaimed and
    /// said, and a live one refused, unless <paramref name="force"/> takes it.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="runId">The run claiming it.</param>
    /// <param name="force">Whether to take one a live owner still holds.</param>
    public LogClaim Claim(string directory, RunId runId, bool force)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(runId);

        var file = OwnerFile(directory);

        // Claimed before anything else a run writes, so a directory this user cannot write - one an earlier run under
        // sudo left to root - is refused here, naming it, rather than escaping as an error that reads as a defect in
        // this tool.
        Written(file, () => _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!));

        return MachineWideFile.Update(file, MachineWideFile.Window, afterwards =>
        {
            if (Read(file) is { } existing && !Mine(existing, runId))
            {
                var held = _identity.Stands(existing.Machine, existing.ProcessId, existing.ProcessStamp);

                if (held && !force)
                {
                    // A holder on another machine cannot be asked whether it is still running, so it stands. Liveness,
                    // never a timeout, is what decides for one on this machine.
                    return new LogClaim(false, existing, file) { HeldBy = existing.Describe() + _identity.ElsewhereNote(existing.Machine) };
                }

                // Said once the owner file is let go, as every line about a machine-wide file is.
                afterwards(held
                    ? () => _output.Warn(_terms.CommandName, ProcessHolders.TakenByForce(_terms.Directory(directory), existing.Describe()))
                    : () => _output.Info(_terms.CommandName, ProcessHolders.Reclaimed(_terms.Directory(directory), existing.Describe())));
            }

            var owner = new LogOwner(
                _identity.CurrentMachine,
                _identity.CurrentId,
                runId.Value,
                DateTimeOffset.UtcNow,
                _identity.Current);

            Written(file, () => _fileSystem.WriteAllTextAtomic(file, JsonSerializer.Serialize(owner, JsonStateFile.Options) + "\n"));

            // Made as it is claimed, where claiming makes it: a run names its log directory as where its records are, and
            // one whose legs wrote nothing would otherwise have named a directory that did not exist.
            if (_terms.Made is { } made)
            {
                MachineWideFile.Written(made(directory), _terms.Unmade, () => _fileSystem.CreateDirectory(directory));
            }

            return new LogClaim(true, owner, file);
        });
    }

    /// <summary>
    /// Gives up <paramref name="directory"/>, and only when <paramref name="runId"/> of this process owns it: a release that
    /// did not check would free a directory another run had just claimed. A failure is said, never raised: the record names
    /// this process, and is reclaimed as a dead owner's is once it has ended.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="runId">The run giving it up.</param>
    public void Release(string directory, RunId runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(runId);

        var file = OwnerFile(directory);

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
            _output.Warn(_terms.CommandName, $"'{directory}' could not be given up: {ex.Message} It is reclaimed once this run has ended.");
        }
    }

    /// <summary>
    /// Releases every directory beside <paramref name="directory"/> that a run on this machine claimed and never gave up,
    /// its process having ended first, and says each; <paramref name="directory"/>'s own claim is left alone.
    /// </summary>
    /// <param name="directory">The directory whose neighbours are looked at.</param>
    /// <returns>The runs found abandoned, in the order their owner files sort.</returns>
    /// <remarks>
    /// Only a claim this machine can judge is released: one recorded on another machine stands. One this build cannot
    /// read, or cannot reach in the window, is left as it is, and said. No failure expected here fails the run that found
    /// them: a directory that cannot be listed is said, and nothing in it is released.
    /// </remarks>
    public IReadOnlyList<LogOwner> ReleaseAbandonedBeside(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var own = OwnerFile(directory);
        IReadOnlyList<string> files;

        try
        {
            files = [.. _fileSystem.EnumerateFiles(Path.GetDirectoryName(own)!, recursive: false)
                .Where(file => file.EndsWith(_terms.OwnerSuffix, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFullPath(file), own, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.Warn(
                _terms.CommandName,
                $"{_terms.Beside} '{directory}' could not be listed: {ex.Message.TrimEnd('.')}. Any of them that was abandoned is said, and released, by a later run.");
            return [];
        }

        return [.. files.Select(ReleaseIfAbandoned).OfType<LogOwner>()];
    }

    /// <summary>
    /// Releases <paramref name="directory"/>'s own claim where a run on this machine made it and its process has ended, and
    /// says so; <see langword="null"/>, and nothing done, where no such run holds it.
    /// </summary>
    /// <param name="directory">The directory.</param>
    public LogOwner? ReleaseAbandoned(string directory) => ReleaseIfAbandoned(OwnerFile(directory));

    /// <summary>The run that owns <paramref name="directory"/>, or <see langword="null"/> when none does.</summary>
    /// <param name="directory">The directory.</param>
    public LogOwner? Owner(string directory) => Read(OwnerFile(directory));

    /// <summary>Whether <paramref name="owner"/>'s process still stands, as a claim it holds is judged.</summary>
    /// <param name="owner">The owner.</param>
    public bool Stands(LogOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return _identity.Stands(owner.Machine, owner.ProcessId, owner.ProcessStamp);
    }

    /// <summary>
    /// The owner <paramref name="file"/> records, released and said where it is a run on this machine that has ended;
    /// <see langword="null"/> otherwise, and where it could not be judged, which is said.
    /// </summary>
    private LogOwner? ReleaseIfAbandoned(string file)
    {
        try
        {
            return MachineWideFile.Update(file, MachineWideFile.Window, afterwards => Abandoned(file, afterwards));
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            // Unreadable, held past the window by another process, or its mutex could not be opened. Said once the file
            // is let go, as everything said here is.
            _output.Warn(
                _terms.CommandName,
                $"Whether the run that claimed '{file[..^_terms.OwnerSuffix.Length]}' was abandoned could not be judged, so its claim is left as it is: {ex.Message}");
            return null;
        }
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

        var directory = file[..^_terms.OwnerSuffix.Length];
        var held = _fileSystem.DirectoryExists(directory) ? _terms.Held(directory) : $"'{directory}', which is gone";
        var holder = ProcessHolders.Describe(owner.Machine, owner.ProcessId, owner.RunId, owner.TakenUtc);
        var abandoned = $"An earlier run was abandoned: {holder}, is no longer running, and never gave up {held} - "
            + "most likely it was killed, or stopped with its machine, before it finished, unless it said as it ended that its "
            + $"claim could not be given up - {_terms.Abandoned}";

        try
        {
            _fileSystem.DeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            afterwards(() => _output.Warn(_terms.CommandName, $"{abandoned} Its owner file '{file}' could not be removed: {ex.Message}"));
            return owner;
        }

        afterwards(() => _output.Warn(_terms.CommandName, $"{abandoned} Its claim is released."));
        return owner;
    }

    /// <summary>Does <paramref name="write"/>, and refuses, naming the owner file, when it could not be done.</summary>
    private void Written(string file, Action write) => MachineWideFile.Written($"{_terms.OwnerFile} '{file}'", _terms.Unwritten, write);

    private bool Mine(LogOwner owner, RunId runId)
        => string.Equals(owner.RunId, runId.Value, StringComparison.Ordinal)
            && owner.ProcessId == _identity.CurrentId
            && _identity.IsHere(owner.Machine);

    /// <summary>
    /// The owner <paramref name="file"/> records, by every state file's rules: an owner file that cannot be read says a
    /// run claimed this directory and nothing more, so the claim is refused rather than granted - never read as free, the
    /// one reading that lets two runs work in one directory. The one field an older build wrote is declared, so upgrading
    /// reads its own owner file rather than refusing it.
    /// </summary>
    private LogOwner? Read(string file)
        => MachineWideFile.Read<LogOwner>(
            _fileSystem,
            file,
            ex => $"{_terms.OwnerFile} '{file}' could not be read: {ex.Message.TrimEnd('.')}. {_terms.Unreadable}");
}
