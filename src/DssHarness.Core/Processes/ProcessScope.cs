namespace DssHarness.Core.Processes;

/// <summary>
/// What the children an operation starts for its work are started under, where the operation opens one: apart from the
/// terminal, and each bound one recorded while it runs - the scope a run's machine step opens around its legs, so that
/// nothing its legs start is in reach of an interruption typed at the engine's terminal, which a safe pause needs, and a
/// killed engine leaves none of its legs' work (<see cref="ProcessRequest.Bound"/>: each phase - a set-up's <c>up</c> line
/// apart, which is left running on purpose - its keep-awake, the ssh or wsl.exe that carries a leg to a host) running
/// unrecorded.
/// </summary>
/// <remarks>
/// <para>
/// The process runner reads it for each child it starts but a contained one, which has a session of its own already
/// (<see cref="Current"/>): on Linux and macOS the child is started in a process group of its own, as
/// <see cref="ProcessRequest.ApartFromTerminal"/> starts one, and a bound one recorded through <see cref="Record"/> once it
/// is placed, as a contained child's session is - nothing there ends either with the process that started it - the record
/// kept while anything is left in its group. On Windows a child starts as it always does, with a console of its own, and
/// a bound one is held in the job the process holds for its whole life, which ends it with the process; nothing is
/// recorded.
/// </para>
/// <para>
/// It flows with the operation that entered it - into every call it makes and every task it starts - and nothing outside
/// that operation sees it: a leg typed by hand runs under none, and starts as it always has.
/// </para>
/// </remarks>
public sealed class ProcessScope
{
    private static readonly AsyncLocal<ProcessScope?> Ambient = new();

    private readonly Func<IContainedSessionRecord> _records;

    /// <summary>Makes a scope whose children are each recorded by a record of their own.</summary>
    /// <param name="records">Makes the record of one child started under the scope.</param>
    public ProcessScope(Func<IContainedSessionRecord> records)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
    }

    /// <summary>The scope the calling operation runs under; <see langword="null"/> where it entered none.</summary>
    public static ProcessScope? Current => Ambient.Value;

    /// <summary>The record of one more child started under the scope.</summary>
    public IContainedSessionRecord Record() => _records();

    /// <summary>Runs what the calling operation does next under <paramref name="scope"/>, until the handle returned is disposed.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>Puts back the scope the operation ran under before.</returns>
    public static IDisposable Enter(ProcessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var before = Ambient.Value;
        Ambient.Value = scope;

        return new Left(before);
    }

    /// <summary>Puts back the scope an operation ran under before it entered another.</summary>
    private sealed class Left(ProcessScope? before) : IDisposable
    {
        public void Dispose() => Ambient.Value = before;
    }
}
