using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// A side-task chat's front end: how the person's lines reach the service and how the chat's own lines reach the person.
/// The command line supplies the terminal's reader and its unprefixed writer; a test supplies its own.
/// </summary>
/// <param name="read">Reads one line of the person's, or null where the input ends.</param>
/// <param name="say">Says one line of the chat's to the person: the agent's text as it streams, an action, a denial.</param>
public sealed class SideTaskChat(Func<CancellationToken, Task<string?>> read, Action<string> say)
{
    private readonly Func<CancellationToken, Task<string?>> _read = read ?? throw new ArgumentNullException(nameof(read));

    /// <summary>Says one line of the chat's to the person.</summary>
    public Action<string> Say { get; } = say ?? throw new ArgumentNullException(nameof(say));

    /// <summary>Reads one line of the person's; null where the input ends.</summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => _read(cancellationToken);
}

/// <summary>
/// The interrupts of one side-task, each counted once whoever sent it - the terminal's Ctrl+C, and off Windows SIGTERM
/// and a hang-up (<see cref="SideTaskTerminal"/>), or a front end's own key - and none ever ending the process. In a chat
/// the first cuts the agent's current turn where it stands, leaving the chat open, and a second, whatever the first was
/// doing, ends the chat; in one shot the first cuts the turn, and the side-task ends interrupted. Where no turn runs - the
/// side-task being set up or opened again, its Claude Code or another model made ready - a press stops what is under
/// way: a one shot's first, and a chat's second (<see cref="StopsOn"/>).
/// </summary>
/// <remarks>
/// The count is the side-task's and is never cleared: a turn that was interrupted does not clear it, so the next press -
/// whenever it comes - is the one that ends the chat. What a press does is told inside the count's lock, so once a turn
/// unwires what it does (<see cref="WhileTurning"/>) no press can reach it, and nothing a turn let go of is touched; and
/// presses counted before the side-task's stop is bound are applied once it is, each once.
/// </remarks>
public sealed class SideTaskInterrupts
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pressed;
    private Action<int>? _pressedNow;
    private CancellationTokenSource? _stop;
    private bool _chat;

    /// <summary>How many interrupts the side-task has been given.</summary>
    public int Presses
    {
        get
        {
            lock (_gate)
            {
                return _pressed;
            }
        }
    }

    /// <summary>Completes at the second interrupt: the chat ends, whatever its turn is doing.</summary>
    public Task Ended => _ended.Task;

    /// <summary>One interrupt: the first cuts the agent's current turn, a second ends the chat.</summary>
    /// <returns>How many interrupts the side-task has been given, this one counted.</returns>
    public int Pressed()
    {
        lock (_gate)
        {
            var count = ++_pressed;

            _pressedNow?.Invoke(count);

            if (count >= 2)
            {
                _ended.TrySetResult();
            }

            StopWhereAskedLocked();

            return count;
        }
    }

    /// <summary>
    /// Binds the side-task's stop to its interrupts, for as long as the binding is held: <paramref name="stop"/> is
    /// cancelled at a chat's second interrupt, and at a one shot's first where no turn runs to cut - wherever the
    /// side-task stands. Presses counted before the binding are applied to it at once.
    /// </summary>
    /// <param name="stop">The side-task's stop, which the command's own stop is linked to.</param>
    /// <param name="chat">Whether the side-task is a chat; false for one shot.</param>
    /// <returns>The binding: disposed, no press reaches <paramref name="stop"/> any more.</returns>
    internal IDisposable StopsOn(CancellationTokenSource stop, bool chat)
    {
        ArgumentNullException.ThrowIfNull(stop);

        lock (_gate)
        {
            _stop = stop;
            _chat = chat;
            StopWhereAskedLocked();
        }

        return new Unbinding(this);
    }

    /// <summary>
    /// Wires what a press does while a turn runs: told each press, with the count; unwired where no turn runs. What it
    /// is told must be quick and must not wait - a cut asked, never awaited - for it is told inside the count's lock.
    /// </summary>
    /// <param name="what">What a press does; null to unwire.</param>
    internal void WhileTurning(Action<int>? what)
    {
        lock (_gate)
        {
            _pressedNow = what;
        }
    }

    /// <summary>Cancels the bound stop where the presses ask it: a chat's second, a one shot's first with no turn to cut. Asked, never awaited, inside the lock.</summary>
    private void StopWhereAskedLocked()
    {
        if (_stop is { } stop && (_chat ? _pressed >= 2 : _pressed >= 1 && _pressedNow is null))
        {
            _ = stop.CancelAsync();
        }
    }

    /// <summary>The binding of a stop, which lets it go.</summary>
    private sealed class Unbinding(SideTaskInterrupts interrupts) : IDisposable
    {
        public void Dispose()
        {
            lock (interrupts._gate)
            {
                interrupts._stop = null;
            }
        }
    }
}

/// <summary>
/// The terminal of one side-task: how the interrupts a terminal sends reach it. Ctrl+C - and off Windows SIGTERM and a
/// hang-up - are each taken as one interrupt of the side-task (<see cref="SideTaskInterrupts"/>), through the one
/// registration of the process's signals (<see cref="TerminalSignals"/>), and never end the process: the command line's
/// own handling of them is off for <c>run-side-task</c> (<see cref="PointOfNoReturn.ChatsWithAPerson"/>). Each is said by
/// its place in the count; a terminal that hung up says nothing more, and its lines are dropped where it can no longer
/// take them - what happened is in the side-task's records.
/// </summary>
/// <param name="output">Says what each interrupt does, as a line of <c>run-side-task</c>.</param>
/// <param name="chat">Whether the side-task is a chat; false for one shot.</param>
public sealed class SideTaskTerminal(IHarnessOutput output, bool chat)
{
    private readonly IHarnessOutput _output = output ?? throw new ArgumentNullException(nameof(output));
    private volatile bool _hungUp;

    /// <summary>The side-task's interrupts: the one count of them.</summary>
    public SideTaskInterrupts Interrupts { get; } = new();

    /// <summary>What the first interrupt of a chat is said as.</summary>
    public const string ChatCut = "interrupted: the agent's turn is cut where it stands; a second interrupt ends the chat";

    /// <summary>What the second interrupt of a chat is said as.</summary>
    public const string ChatEnds = "interrupted again: the chat ends";

    /// <summary>What the first interrupt of one shot is said as.</summary>
    public const string OneShotCut = "interrupted: the turn is cut where it stands, and the side-task ends with what it said in its records";

    /// <summary>Takes the process's interrupts for the side-task until disposed.</summary>
    public IDisposable TakeSignals() => TerminalSignals.Take(Interrupted, HungUp);

    /// <summary>One interrupt of the terminal: counted for the side-task, and said by its place in the count.</summary>
    internal void Interrupted()
    {
        var count = Interrupts.Pressed();

        if (_hungUp)
        {
            return;
        }

        var said = (chat, count) switch
        {
            (true, 1) => ChatCut,
            (true, 2) => ChatEnds,
            (false, 1) => OneShotCut,
            _ => null,
        };

        if (said is not null)
        {
            FlowTerminal.Warn(_output, SideTaskService.RunCommand, said);
        }
    }

    /// <summary>The terminal hung up: an interrupt like any other, and the terminal says nothing from now on.</summary>
    internal void HungUp()
    {
        _hungUp = true;
        Interrupted();
    }
}
