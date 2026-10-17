namespace DssHarness.Core.Platform;

/// <summary>
/// The one reader of this process's current directory.
/// </summary>
/// <remarks>
/// Three rules read it: a command acts on the current directory where it is given no <c>--directory</c>, a worktree
/// deletion refuses to run from inside the worktree it removes, and an agent's record names the directory a verb was
/// typed in. Each reads the directory through this seam rather than the process's own, so a front end that hosts
/// commands without a current directory of their process - a graphical one, an orchestrator run - says what "here" is
/// once for all of them. The reader is asked again each time and never caches: the host agent changes the process's
/// current directory between the commands it serves, and every one of them must see the change.
/// </remarks>
public interface IProcessContext
{
    /// <summary>The process's current directory, read as it stands when this is asked.</summary>
    string CurrentDirectory { get; }
}

/// <summary>Reads the current directory of the process it runs in, as it stands when it is asked.</summary>
public sealed class ProcessContext : IProcessContext
{
    /// <inheritdoc/>
    public string CurrentDirectory => Environment.CurrentDirectory;
}
