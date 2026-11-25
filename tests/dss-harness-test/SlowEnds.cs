using DssHarness.Core.Processes;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Tests;

/// <summary>
/// What a stop ends, ended as the machine ends it - but for the process a test names, or every job where it says so, whose
/// end is held back until the runner first asks whether anything it ended has gone: a process whose end is slow, still
/// ending - still holding what it held - when the stop has made its ends, which no real process can be made to be on
/// demand. A runner that never asks never ends it, and the test ends it itself.
/// </summary>
internal sealed class SlowEnds : ProcessEnds
{
    private readonly Lock _gate = new();
    private readonly List<Action> _held = [];
    private int _slow;
    private bool _jobs;
    private int _heldBack;

    /// <summary>How many ends were held back: a test whose runner held none back tested nothing.</summary>
    public int HeldBack
    {
        get
        {
            lock (_gate)
            {
                return _heldBack;
            }
        }
    }

    /// <summary>Holds back the end of the process <paramref name="id"/> names.</summary>
    /// <param name="id">The process's id.</param>
    public void Slow(int id)
    {
        lock (_gate)
        {
            _slow = id;
        }
    }

    /// <summary>Holds back the end of every job.</summary>
    public void SlowJobs()
    {
        lock (_gate)
        {
            _jobs = true;
        }
    }

    public override void End(KeptProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            if (process.Id == _slow)
            {
                _held.Add(() => base.End(process));
                _heldBack++;
                return;
            }
        }

        base.End(process);
    }

    public override void EndJob(SafeWaitHandle job)
    {
        lock (_gate)
        {
            if (_jobs)
            {
                _held.Add(() => base.EndJob(job));
                _heldBack++;
                return;
            }
        }

        base.EndJob(job);
    }

    public override bool Gone(KeptProcess process)
    {
        List<Action> due;

        lock (_gate)
        {
            due = [.. _held];
            _held.Clear();
        }

        foreach (var end in due)
        {
            end();
        }

        return base.Gone(process);
    }
}
