using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Core.Hosts;

/// <summary>
/// What a machine's listing of what is installed says of one program: that it could not be read,
/// that the program is not in it, or the release it is installed at. The three are never one:
/// a program the listing reads as absent is a fact, and a listing nobody could read is none.
/// </summary>
public sealed record ListedRelease
{
    private ListedRelease(bool readable, string? release)
    {
        Readable = readable;
        Release = release;
    }

    /// <summary>A listing that failed, or said nothing the reader recognises: what is installed is not known.</summary>
    public static ListedRelease Unreadable { get; } = new(readable: false, release: null);

    /// <summary>A listing that was read, and does not hold the program.</summary>
    public static ListedRelease Absent { get; } = new(readable: true, release: null);

    /// <summary>Whether the listing could be read, whatever it said of the program.</summary>
    public bool Readable { get; }

    /// <summary>The release the listing says is installed, or <see langword="null"/> where it holds none or could not be read.</summary>
    public string? Release { get; }

    /// <summary>A listing that was read: the program installed at <paramref name="release"/>, or absent where that is <see langword="null"/>.</summary>
    /// <param name="release">The release the listing names, or <see langword="null"/> where it names none.</param>
    public static ListedRelease Read(string? release) => release is null ? Absent : new(readable: true, release);
}

/// <summary>What running a program's change left installed, as the machine's own listing says.</summary>
/// <param name="Ran">How the change itself ended.</param>
/// <param name="Listed">The listing read since the change ran.</param>
/// <param name="Now">
/// What that listing says is installed: <see cref="ListedRelease.Unreadable"/> where the listing
/// failed or said nothing the reader recognises, <see cref="ListedRelease.Absent"/> where it was read
/// and holds no such program, and the release it names otherwise.
/// </param>
public sealed record ProgramChange(ProcessResult Ran, ProcessResult Listed, ListedRelease Now);

/// <summary>
/// The parts of an installed program's update that are no program's own: which of the machine's
/// processes are the program's, which of them started the one asking, the machine-wide lock on the
/// place the program is kept, and the change itself - run under that lock, never stopped once begun,
/// then read back, so an update is said done only where the machine's own listing says the release
/// it was moved to.
/// </summary>
/// <remarks>
/// <para>
/// The change is run with no token to stop it and the reading of what it left the same way: an
/// update cut off part way leaves a machine with no program, or half of one, and a command that
/// then says nothing of which. Each request carries its own bound - one that carries none is
/// refused before anything runs - so a machine that never answers ends the change rather than
/// holding it for good.
/// </para>
/// <para>
/// Two updates of one place at once make one change: the change is run only under the place's lock
/// (<see cref="TryHold"/>), and an updater that finds it held changes nothing. Where neither is a
/// process of the program by its name - two copies of a tool run beside the installed one, each as
/// the program that runs it - neither sees the other running, and their two changes would write the
/// same files together. The second is not made to wait: it read what is installed before the first
/// changed it, and would make the change again over a reading the first had made untrue.
/// </para>
/// <para>
/// The words of each refusal stay with the program's own updater, which knows what the program is
/// called and what a person can do about it; this holds the rules that hold whichever the program.
/// </para>
/// </remarks>
public sealed class ProgramUpdate(IProcessRunner processRunner, IHostPlatform platform)
{
    /// <summary>The kind of subject the lock on a place programs are kept in is named for, beside the place itself.</summary>
    public const string LockKind = "update";

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// Every process of the program but the one asking, by the name of its program, in the order of
    /// their ids.
    /// </summary>
    /// <param name="table">The machine's processes, as it was read.</param>
    /// <param name="isProgram">Whether one process is the program to be replaced.</param>
    /// <param name="exceptId">The process asking, which is never one of them.</param>
    public IReadOnlyList<SampledProcess> Running(ProcessTableReading table, Func<SampledProcess, bool> isProgram, int exceptId)
        => [.. table.Processes.Where(one => one.Id != exceptId && isProgram(one)).OrderBy(one => one.Id)];

    /// <summary>
    /// Where <paramref name="table"/> was read by names alone - the platform's own source failed, so what each process
    /// runs, what it was asked and which started which are all missing from it -, what an update that needs
    /// <paramref name="needed"/> of it says: that it could not be read, and why the table was read so, as the opening of
    /// a refusal's sentence, before its "so"; null where the table was read whole.
    /// </summary>
    /// <param name="table">The machine's processes, as it was read.</param>
    /// <param name="needed">What the update decides by that a reading by names alone lacks, as a noun phrase.</param>
    /// <remarks>
    /// The one rule every program's updater refuses by: an update that would decide on what the reading lacks never
    /// passes over it, since nobody looked and nothing was found are different facts - a wait from inside a process
    /// that started this one, or a session told by what it runs and never by its name.
    /// </remarks>
    public static string? Unread(ProcessTableReading table, string needed)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(needed);

        return table.Degraded is { } why ? $"{needed} could not be read - {why.TrimEnd('.')} -" : null;
    }

    /// <summary>
    /// The nearest of <paramref name="running"/> that started the process <paramref name="currentId"/>,
    /// through whatever it started in between, or <see langword="null"/>.
    /// </summary>
    /// <param name="table">The machine's processes, as it was read.</param>
    /// <param name="running">The program's running processes.</param>
    /// <param name="currentId">The process asking.</param>
    public SampledProcess? StartedThis(ProcessTableReading table, IReadOnlyList<SampledProcess> running, int currentId)
    {
        var parents = table.Processes.GroupBy(one => one.Id).ToDictionary(group => group.Key, group => group.First().ParentId);
        var seen = new HashSet<int>();

        for (var at = parents.GetValueOrDefault(currentId); at is { } id && seen.Add(id); at = parents.GetValueOrDefault(id))
        {
            if (running.FirstOrDefault(one => one.Id == id) is { } parent)
            {
                return parent;
            }
        }

        return null;
    }

    /// <summary>
    /// The code a change that ran and did not succeed exits with, by the one rule every program's updater shares:
    /// <see cref="HarnessExit.Incomplete"/> where it was begun and not finished - ended by its own bound, or the registry
    /// or the download it fetches from not reached, which <paramref name="unreached"/> says from the program's own words,
    /// read by its updater, which knows them -, since running it again can finish it; otherwise
    /// <paramref name="refused"/>, the updater's own code for a change its program refused.
    /// </summary>
    /// <param name="ran">How the change ended.</param>
    /// <param name="unreached">Whether the program said it reached neither its registry nor its download.</param>
    /// <param name="refused">The updater's code for a change its program refused: what its caller reads that as.</param>
    /// <exception cref="ArgumentException"><paramref name="ran"/> succeeded, and a change that did has no failure's code.</exception>
    public static int FailedExit(ProcessResult ran, bool unreached, int refused)
    {
        ArgumentNullException.ThrowIfNull(ran);

        if (ran.Succeeded)
        {
            throw new ArgumentException("A change that succeeded has no failure's code.", nameof(ran));
        }

        return ran.TimedOut || unreached ? HarnessExit.Incomplete : refused;
    }

    /// <summary>
    /// The machine-wide lock on <paramref name="place"/>, taken where no other update of it holds it; or
    /// <see langword="null"/> at once where one does, and nothing may be changed there.
    /// </summary>
    /// <param name="place">The directory the program is kept in, which the change writes.</param>
    /// <returns>The hold, which the change is run under and which lets the lock go when it is disposed.</returns>
    /// <exception cref="HarnessException">The lock could not be opened, as <see cref="MachineWideMutex.Open(string, string)"/> says.</exception>
    /// <remarks>
    /// The lock is named for the place, however its path is spelled (<see cref="LockName"/>): two
    /// programs kept in one place share files a change writes, and one program kept in two places -
    /// each user's own, or a directory of its own - is two changes that touch nothing of each other's.
    /// A lock whose holder ended holding it - a process killed part way through its change - is taken
    /// by the next update, which reads what that change left as it reads anything installed.
    /// </remarks>
    public MachineWideHold? TryHold(string place)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(place);

        return MachineWideMutex.TryHold(LockName(place), $"the programs kept in '{place}'");
    }

    /// <summary>The name of the machine-wide lock on <paramref name="place"/>, however its path is spelled.</summary>
    /// <param name="place">The directory programs are kept in.</param>
    public string LockName(string place)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(place);

        return MachineWideMutex.NameFor(LockKind, [place], _platform.PathComparison == StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The change itself, under <paramref name="held"/>: <paramref name="change"/> run never stopped,
    /// then what is installed read back the same way through <paramref name="listSince"/> and
    /// <paramref name="readRelease"/>.
    /// </summary>
    /// <param name="held">The lock on the place the change writes, from <see cref="TryHold"/>, held for as long as this runs.</param>
    /// <param name="change">The request that changes what is installed; its own bound is its budget.</param>
    /// <param name="listSince">The request that lists what is installed since the change; its own bound is its budget.</param>
    /// <param name="readRelease">
    /// What a listing that ran says of the program: the release it names, that the program is absent
    /// from it, or that it is not something the reader can read.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="held"/> has been let go, so it guards nothing; or <paramref name="change"/> or
    /// <paramref name="listSince"/> carries no bound: neither can be stopped once begun, so one with none
    /// would hold the change for good on a machine that never answers.
    /// </exception>
    public async Task<ProgramChange> ChangeAsync(
        MachineWideHold held,
        ProcessRequest change,
        ProcessRequest listSince,
        Func<string, ListedRelease> readRelease)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(listSince);
        ArgumentNullException.ThrowIfNull(readRelease);

        if (held.Released)
        {
            throw new ArgumentException("A change is run under the lock on the place it writes, and this one has been let go.", nameof(held));
        }

        if (change.Timeout is null)
        {
            throw new ArgumentException("A change is run never stopped, so it carries a bound of its own.", nameof(change));
        }

        if (listSince.Timeout is null)
        {
            throw new ArgumentException("A listing since a change is run never stopped, so it carries a bound of its own.", nameof(listSince));
        }

        var ran = await _processRunner.RunAsync(change, CancellationToken.None).ConfigureAwait(false);
        var listed = await _processRunner.RunAsync(listSince, CancellationToken.None).ConfigureAwait(false);

        return new ProgramChange(
            ran,
            listed,
            listed.Succeeded ? readRelease(listed.StandardOutput) : ListedRelease.Unreadable);
    }
}
