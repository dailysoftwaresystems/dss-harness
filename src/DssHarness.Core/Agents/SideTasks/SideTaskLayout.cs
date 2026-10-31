using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// Where one side-task's records are, derived from the repository's layout: a directory of its own under the
/// repository's <c>.side-tasks/</c>, its owner file beside it, as a run's is beside its own directory
/// (<see cref="OrchestratorLayout.OwnerFile"/>). The one owner of the paths a side-task's records are kept at.
/// </summary>
/// <param name="Root">The repository's <c>.side-tasks/</c> directory.</param>
/// <param name="Name">The side-task's name, which names its directory under it.</param>
public sealed record SideTaskLayout(string Root, string Name)
{
    /// <summary>The file holding the side-task's own record, in its directory.</summary>
    public const string TaskFileName = "task.json";

    /// <summary>The suffix of the owner file beside a side-task's directory, as a run's is beside its own.</summary>
    public const string OwnerSuffix = ".owner.json";

    /// <summary>
    /// The layout of the side-task named <paramref name="name"/> under <paramref name="layout"/>'s repository.
    /// </summary>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="name">The side-task's name.</param>
    public static SideTaskLayout Of(HarnessLayout layout, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new(layout.SideTasksDirectory, name);
    }

    /// <summary>The side-task's own directory, named by its name.</summary>
    public string Directory => System.IO.Path.Combine(Root, Name);

    /// <summary>
    /// The side-task's owner file, beside its directory: the process whose chat holds the side-task open, as a run's
    /// engine is beside the run's own directory.
    /// </summary>
    public string OwnerFile => System.IO.Path.Combine(Root, Name + OwnerSuffix);

    /// <summary>The side-task's own record: what it ran on, its turns, what it used and cost, and how it stands.</summary>
    public string TaskFile => System.IO.Path.Combine(Directory, TaskFileName);

    /// <summary>The side-task's transcript, the conversation as a person reads it: each message, each action, each denial.</summary>
    public string TranscriptFile => System.IO.Path.Combine(Directory, "transcript.md");

    /// <summary>
    /// Where a client handed a conversation stores the side-task's: under its own records, in the same
    /// <c>sessions/&lt;id&gt;/</c> shape every stored conversation is kept in, so the one writer of conversations keeps
    /// its one shape.
    /// </summary>
    public string SessionsDirectory => System.IO.Path.Combine(Directory, "sessions");
}
