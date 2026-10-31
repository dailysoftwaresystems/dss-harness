using DssHarness.Core.Execution;

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

    /// <summary>The chat's interrupts: the first cuts the agent's turn, a second ends the chat.</summary>
    public SideTaskInterrupts Interrupts { get; internal init; } = new();

    /// <summary>Reads one line of the person's; null where the input ends.</summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => _read(cancellationToken);
}

/// <summary>
/// The interrupts of one side-task's chat: Ctrl+C - and off Windows SIGTERM and a hang-up - are each taken as one
/// interrupt, and never end the process. The first cuts the agent's current turn where it stands, leaving the chat open;
/// a second, whatever the first was doing, ends the chat.
/// </summary>
/// <remarks>
/// The count is the chat's: a turn that was interrupted does not clear it, so the next press - whenever it comes - is
/// the one that ends the chat. Only the chat's end clears it.
/// </remarks>
public sealed class SideTaskInterrupts
{
    private readonly Lock _gate = new();
    private int _pressed;
    private Action<int>? _pressedNow;

    /// <summary>How many interrupts the chat has been given.</summary>
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

    /// <summary>One interrupt: the first cuts the agent's current turn, a second ends the chat.</summary>
    /// <returns>How many interrupts the chat has been given, this one counted.</returns>
    public int Pressed()
    {
        Action<int>? say;
        int count;

        lock (_gate)
        {
            count = ++_pressed;
            say = _pressedNow;
        }

        // Told outside the lock, so a listener that takes its time holds no press of another's.
        say?.Invoke(count);

        return count;
    }

    /// <summary>Wires what a press does while a turn runs: told each press, with the count; unwired where no turn runs.</summary>
    /// <param name="what">What a press does; null to unwire.</param>
    internal void WhileTurning(Action<int>? what)
    {
        lock (_gate)
        {
            _pressedNow = what;
        }
    }
}
