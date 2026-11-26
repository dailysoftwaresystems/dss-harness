using DssHarness.Core.Execution;
using DssHarness.Core.Processes;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Tests;

/// <summary>
/// Windows' list of every process and each process's order, read as the machine reads them - but for the process a test
/// names, whose creation time is read <c>back</c> hundreds of nanoseconds earlier, in the list and from its handle alike,
/// its sequence number as the machine gives it: a clock stepped back between the creation of that process and the
/// creation of the one that made it, which dates the child before its creator, and which no real clock can be made to do
/// on demand.
/// </summary>
/// <param name="stepped">The process whose creation time is read stepped back, by its id.</param>
/// <param name="back">How far back, in a file time's hundreds of nanoseconds.</param>
internal sealed class ClockStepped(int stepped, long back) : ProcessEnds
{
    public override IReadOnlyList<ListedProcess>? Listed()
        => base.Listed() is { } listed
            ? [.. listed.Select(process => process.Id == stepped ? process with { Created = process.Created - back } : process)]
            : null;

    public override CreationOrder Made(int id, SafeProcessHandle handle)
    {
        var made = base.Made(id, handle);

        return id == stepped ? made with { Created = made.Created - back } : made;
    }
}
