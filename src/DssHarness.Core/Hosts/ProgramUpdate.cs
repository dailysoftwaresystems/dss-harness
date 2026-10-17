using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Hosts;

/// <summary>What running a program's change left installed, as the machine's own listing says.</summary>
/// <param name="Ran">How the change itself ended.</param>
/// <param name="Listed">The listing read since the change ran.</param>
/// <param name="ReleaseNow">
/// The release the listing says is installed, or <see langword="null"/> where the listing failed or
/// said nothing this reader recognises.
/// </param>
public sealed record ProgramChange(ProcessResult Ran, ProcessResult Listed, string? ReleaseNow);

/// <summary>
/// The parts of an installed program's update that are no program's own: which of the machine's
/// processes are the program's, which of them started the one asking, and the change itself - run
/// never stopped once begun, then read back, so an update is said done only where the machine's own
/// listing says the release it was moved to.
/// </summary>
/// <remarks>
/// <para>
/// The change is run with no token to stop it and the reading of what it left the same way: an
/// update cut off part way leaves a machine with no program, or half of one, and a command that
/// then says nothing of which. Each request carries its own bound, so a machine that never answers
/// ends the change rather than holding it for good.
/// </para>
/// <para>
/// The words of each refusal stay with the program's own updater, which knows what the program is
/// called and what a person can do about it; this holds the rules that hold whichever the program.
/// </para>
/// </remarks>
public sealed class ProgramUpdate(IProcessRunner processRunner)
{
    private readonly IProcessRunner _processRunner = processRunner;

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
    /// The change itself: <paramref name="change"/> run never stopped, then what is installed read
    /// back the same way through <paramref name="listSince"/> and <paramref name="readRelease"/>.
    /// </summary>
    /// <param name="change">The request that changes what is installed; its own bound is its budget.</param>
    /// <param name="listSince">The request that lists what is installed since the change.</param>
    /// <param name="readRelease">The release a listing says is installed, or <see langword="null"/> where it says none.</param>
    public async Task<ProgramChange> ChangeAsync(
        ProcessRequest change,
        ProcessRequest listSince,
        Func<string, string?> readRelease)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(listSince);
        ArgumentNullException.ThrowIfNull(readRelease);

        var ran = await _processRunner.RunAsync(change, CancellationToken.None).ConfigureAwait(false);
        var listed = await _processRunner.RunAsync(listSince, CancellationToken.None).ConfigureAwait(false);

        return new ProgramChange(
            ran,
            listed,
            listed.Succeeded ? readRelease(listed.StandardOutput) : null);
    }
}
