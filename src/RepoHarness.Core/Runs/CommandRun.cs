using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Runs;

/// <summary>
/// The run a command that keeps runs - build, test or run - began before anything could refuse it: begun, and named
/// in the command's first line, by what runs every command, and taken up by the leg run the command asks for. So the
/// run that line names is the one the run's records are kept under, and the one its ledger names however it ended: a
/// run refused before any leg ran keeps no records, and its id is then all there is to cite it by.
/// </summary>
/// <remarks>One for each command, as every service a command runs with is.</remarks>
public sealed class CommandRun
{
    private readonly Lock _gate = new();
    private RunId? _id;

    /// <summary>The run this command began, or <see langword="null"/> where it began none.</summary>
    public RunId? Id
    {
        get
        {
            lock (_gate)
            {
                return _id;
            }
        }
    }

    /// <summary>Begins a run, a new one each time it is asked, and names it as this command's.</summary>
    public RunId Begin()
    {
        var id = RunId.New();

        lock (_gate)
        {
            _id = id;
        }

        return id;
    }
}
