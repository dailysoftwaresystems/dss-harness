using System.ComponentModel;
using System.Diagnostics;
using DssHarness.Core.Execution;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Tests;

/// <summary>
/// What a stop ends, ended as the machine ends it - but for the process a test names, or every job or group where it says
/// so, whose end is held back until the runner first asks whether anything it ended has gone: a process whose end is slow,
/// still ending - still holding what it held - when the stop has made its ends, which no real process can be made to be on
/// demand. A runner that never asks never ends it, and the test ends it itself. At that first ask, before any end held
/// back is made, it can have a test's act done: a process the stop kept making another while it is being ended, once the
/// stop has last looked for what it holds.
/// </summary>
internal sealed class SlowEnds : ProcessEnds
{
    /// <summary>What tells a process listed in a job from a later one given its id: its start.</summary>
    private static readonly ProcessIdentity Identity = new(new HostPlatform());

    private readonly Lock _gate = new();
    private readonly List<Action> _held = [];
    private int _slow;
    private bool _jobs;
    private bool _jobsByTheirListing;
    private bool _groups;
    private int _heldBack;
    private Action<KeptProcess>? _atFirstAsk;

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

    /// <summary>
    /// Holds back the end of every job, and makes it, then, by ending each process the job held as its end was asked, each
    /// by its own id and alone: a job's end as TerminateJobObject - as asynchronous as any end - makes it where a member
    /// makes another while it runs, which it then misses.
    /// </summary>
    public void SlowJobsByTheirListing()
    {
        lock (_gate)
        {
            _jobsByTheirListing = true;
        }
    }

    /// <summary>Holds back the end of every group.</summary>
    public void SlowGroups()
    {
        lock (_gate)
        {
            _groups = true;
        }
    }

    /// <summary>
    /// Has <paramref name="act"/> done at the first ask whether anything ended has gone - given the process that ask is
    /// about - before any end held back is made.
    /// </summary>
    /// <param name="act">The act: a process the stop kept making another, or what a test reads of one still standing.</param>
    public void AtFirstAsk(Action<KeptProcess> act)
    {
        ArgumentNullException.ThrowIfNull(act);

        lock (_gate)
        {
            _atFirstAsk = act;
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
            if (_jobsByTheirListing)
            {
                var listed = Containment.Members(job).ConvertAll(id => (Id: id, Stamp: Identity.StampOf(id)));

                _held.Add(() => EndEach(listed));
                _heldBack++;
                return;
            }

            if (_jobs)
            {
                _held.Add(() => base.EndJob(job));
                _heldBack++;
                return;
            }
        }

        base.EndJob(job);
    }

    public override void EndGroup(int group)
    {
        lock (_gate)
        {
            if (_groups)
            {
                _held.Add(() => base.EndGroup(group));
                _heldBack++;
                return;
            }
        }

        base.EndGroup(group);
    }

    public override bool Gone(KeptProcess process)
    {
        Action<KeptProcess>? act;

        lock (_gate)
        {
            act = _atFirstAsk;
            _atFirstAsk = null;
        }

        act?.Invoke(process);

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

    /// <summary>
    /// Ends each process <paramref name="listed"/> names, by its own id and alone - never its tree - and only where the id
    /// is still the very process listed, as its start says: held open as it is asked, an id passes to no other.
    /// </summary>
    private static void EndEach(List<(int Id, string? Stamp)> listed)
    {
        foreach (var (id, stamp) in listed)
        {
            try
            {
                using var member = Process.GetProcessById(id);
                _ = member.SafeHandle;

                if (stamp is { Length: > 0 } && Identity.IsSame(id, stamp))
                {
                    member.Kill();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Gone already, or going.
            }
        }
    }
}
