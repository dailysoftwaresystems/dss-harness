using System.Text.RegularExpressions;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Orchestration;

/// <summary>The states an agent's record passes through, in order.</summary>
public static class AgentStates
{
    /// <summary>Working: its worktree is there, and folding it or deleting it are open.</summary>
    public const string Live = "live";

    /// <summary>
    /// Its fold and rows are in the main tree, or it was abandoned, its evidence is kept, and its worktree's removal
    /// began: it is never folded, seeded or given rows again, and deleting it again finishes the removal.
    /// </summary>
    public const string Closed = "closed";

    /// <summary>Gone: its worktree and its copies on hosts are removed, and its directory is kept as its history.</summary>
    public const string Deleted = "deleted";

    /// <summary>Every state, in order.</summary>
    public static IReadOnlyList<string> All { get; } = [Live, Closed, Deleted];
}

/// <summary>An orchestrator's own record: <c>.orchestrators/&lt;name&gt;/agent.json</c>.</summary>
public sealed record OrchestratorRecord
{
    /// <summary>What <see cref="Kind"/> holds for an orchestrator.</summary>
    public const string KindName = "orchestrator";

    /// <summary>How many of its agents may have a worktree at once when nothing says otherwise.</summary>
    public const int DefaultParallel = 4;

    /// <summary>What this record is: always <see cref="KindName"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The orchestrator's name: its directory's.</summary>
    public required string Name { get; init; }

    /// <summary>The model the orchestrating session runs on, as it was given.</summary>
    public required string Model { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The most of its agents that may have a worktree at once.</summary>
    public required int Parallel { get; init; }

    /// <summary>The Claude session the orchestrating session runs as, where it was given.</summary>
    public string? Session { get; init; }

    /// <summary>What is wrong with this record, kept in the directory named <paramref name="directoryName"/>; null when nothing is.</summary>
    /// <param name="directoryName">The name of the directory it is kept in.</param>
    public string? Problem(string directoryName)
        => Kind != KindName ? $"its kind is '{Kind}', not '{KindName}'"
            : Name != directoryName ? $"it names '{Name}', and it is kept in '{directoryName}'"
            : OrchestrationRules.NameProblem(Name)
                ?? OrchestrationRules.ModelProblem(Model)
                ?? OrchestrationRules.ParallelProblem(Parallel)
                ?? OrchestrationRules.SessionProblem(Session);
}

/// <summary>An agent's record: <c>.orchestrators/&lt;orchestrator&gt;/agents/&lt;name&gt;/agent.json</c>.</summary>
public sealed record AgentRecord
{
    /// <summary>What <see cref="Kind"/> holds for an agent.</summary>
    public const string KindName = "agent";

    /// <summary>What this record is: always <see cref="KindName"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The agent's name: its directory's.</summary>
    public required string Name { get; init; }

    /// <summary>Its orchestrator's name.</summary>
    public required string Orchestrator { get; init; }

    /// <summary>The model the agent's session runs on, as it was given.</summary>
    public required string Model { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Its worktree's address, <c>orchestrator/agent</c>.</summary>
    public required string Worktree { get; init; }

    /// <summary>
    /// The worktrees root its worktree was made under, relative to the main checkout, as the configuration named it then:
    /// where its worktree is, whatever the configuration says later.
    /// </summary>
    public required string WorktreesRoot { get; init; }

    /// <summary>The commit its worktree was made from; null until the worktree is made.</summary>
    public string? Base { get; init; }

    /// <summary>The Claude session the agent runs as - a session's id or a subagent's - where it was given.</summary>
    public string? Session { get; init; }

    /// <summary>One of <see cref="AgentStates"/>.</summary>
    public required string State { get; init; }

    /// <summary>What deleting it recorded before its worktree's removal began; present exactly while it is closed.</summary>
    public AgentClosing? Closing { get; init; }

    /// <summary>When it was deleted; present exactly once it is.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Whether it was abandoned rather than folded; present exactly once it is deleted.</summary>
    public bool? Abandoned { get; init; }

    /// <summary>What is wrong with this record, kept in <paramref name="directoryName"/> under <paramref name="orchestrator"/>; null when nothing is.</summary>
    /// <param name="orchestrator">The orchestrator it is kept under.</param>
    /// <param name="directoryName">The name of the directory it is kept in.</param>
    public string? Problem(string orchestrator, string directoryName)
    {
        if (Kind != KindName)
        {
            return $"its kind is '{Kind}', not '{KindName}'";
        }

        if (Name != directoryName || Orchestrator != orchestrator)
        {
            return $"it names agent '{Name}' of '{Orchestrator}', and it is kept as '{directoryName}' of '{orchestrator}'";
        }

        if (OrchestrationRules.NameProblem(Name) is { } name)
        {
            return name;
        }

        if (OrchestrationRules.AgentNameProblem(Orchestrator, Name) is { } named)
        {
            return named;
        }

        if (Worktree != WorktreeAddress.Nested(Orchestrator, Name).Name)
        {
            return $"its worktree is '{Worktree}', and an agent's is '{WorktreeAddress.Nested(Orchestrator, Name).Name}'";
        }

        if ((OrchestrationRules.ModelProblem(Model)
            ?? OrchestrationRules.SessionProblem(Session)
            ?? OrchestrationRules.CommitProblem(Base, "base")
            ?? OrchestrationRules.RelativePathProblem(WorktreesRoot, "its worktrees root")) is { } problem)
        {
            return problem;
        }

        return State switch
        {
            AgentStates.Live when Closing is not null || DeletedAt is not null || Abandoned is not null => "it is live, and records a closing or a deletion",
            AgentStates.Closed when Closing is null || DeletedAt is not null || Abandoned is not null => "it is closed, and records no closing, or records a deletion",
            AgentStates.Closed => Closing!.Problem(),
            AgentStates.Deleted when DeletedAt is null || Abandoned is null || Closing is not null => "it is deleted, and records no deletion, or still records a closing",
            AgentStates.Live or AgentStates.Deleted => null,
            _ => $"its state is '{State}', and an agent's is one of {string.Join(", ", AgentStates.All)}",
        };
    }
}

/// <summary>
/// What deleting an agent records the moment before its worktree's removal is asked for: from then on it is never folded
/// again, and deleting it again compares its worktree with <see cref="Held"/> rather than with the main tree.
/// </summary>
public sealed record AgentClosing
{
    /// <summary>When it was closed.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Whether it was abandoned rather than folded.</summary>
    public required bool Abandoned { get; init; }

    /// <summary>Where its evidence was kept, relative to the agent's directory; empty when its evidence roots held nothing.</summary>
    public required string Evidence { get; init; }

    /// <summary>Which git worktree it was: <see cref="Worktrees.WorktreeStamp"/>, which a new worktree at the same path does not share.</summary>
    public required string Stamp { get; init; }

    /// <summary>Every path it held that the closing weighed, with its digest, or null where it held no file.</summary>
    public required Dictionary<string, string?> Held { get; init; }

    /// <summary>What is wrong with it; null when nothing is.</summary>
    public string? Problem()
        => (Evidence.Length > 0 ? OrchestrationRules.RelativePathProblem(Evidence, "its evidence") : null)
            ?? (Stamp.Length == 0 ? "it records no stamp of the worktree it closed" : null)
            ?? Held.Select(pair => OrchestrationRules.RelativePathProblem(pair.Key, "a path it held") ?? OrchestrationRules.DigestProblem(pair.Value, pair.Key, allowNone: true))
                .FirstOrDefault(problem => problem is not null);
}

/// <summary>
/// What an agent was handed when it began, or was re-seeded: every path copied into its worktree from the main tree's
/// uncommitted state, with the digest of the agent's copy. What a fold subtracts, since those paths are not the agent's
/// own work until the agent changes them.
/// </summary>
public sealed record SeedRecord
{
    /// <summary>When it was seeded.</summary>
    public required DateTimeOffset SeededAt { get; init; }

    /// <summary>Whether it was handed nothing, as asked, rather than finding nothing to hand it.</summary>
    public required bool Empty { get; init; }

    /// <summary>Every path handed to it, with the digest of its copy.</summary>
    public required Dictionary<string, string> Paths { get; init; }

    /// <summary>
    /// The paths handed to it whose copy could be run - its execute bit set - where the platform has such a bit; absent
    /// on Windows, which has none. A mode is part of what a file is, and the fold compares it with the content.
    /// </summary>
    public List<string>? Executable { get; init; }

    /// <summary>What is wrong with it; null when nothing is.</summary>
    public string? Problem()
        => Empty && Paths.Count > 0 ? "it was handed nothing, and names paths it was handed"
            : Executable?.FirstOrDefault(path => !Paths.ContainsKey(path)) is { } stray ? $"'{stray}' is named executable, and was not handed to it"
            : Paths.Select(pair => OrchestrationRules.RelativePathProblem(pair.Key, "a path it was handed") ?? OrchestrationRules.DigestProblem(pair.Value, pair.Key, allowNone: false))
                .FirstOrDefault(problem => problem is not null);
}

/// <summary>The rules every orchestration record's values are held to, each spelt once.</summary>
public static partial class OrchestrationRules
{
    /// <summary>What is wrong with an orchestrator's or an agent's name: the worktree name rule, as each names a directory.</summary>
    /// <param name="name">The name.</param>
    public static string? NameProblem(string? name)
        => WorktreeName.ValidateFormat(name).TryGetName(out _, out var error) ? null : error;

    /// <summary>What is wrong with an agent's name beside its orchestrator's: never the same, since the two would share a log and a plans directory.</summary>
    /// <param name="orchestrator">The orchestrator's name.</param>
    /// <param name="agent">The agent's name.</param>
    public static string? AgentNameProblem(string orchestrator, string agent)
        => string.Equals(orchestrator, agent, StringComparison.Ordinal)
            ? $"'{agent}' names the orchestrator itself: an agent is never named as its orchestrator, whose log and plans it would share"
            : null;

    /// <summary>What is wrong with the most agents an orchestrator allows with a worktree at once: at least one.</summary>
    /// <param name="parallel">The number, or null where none is given.</param>
    public static string? ParallelProblem(int? parallel)
        => parallel is < 1 ? $"parallel is {parallel}: at least one agent must be allowed at once" : null;

    /// <summary>What is wrong with a model's name: printable, with no space, at most 200 characters, as model ids are.</summary>
    /// <param name="model">The model.</param>
    public static string? ModelProblem(string? model)
        => model is not null && ModelPattern().IsMatch(model)
            ? null
            : $"'{model}' is not a model's name: printable characters with no space, at most 200";

    /// <summary>
    /// What is wrong with a Claude session's id - a session's, or a subagent's: letters, digits, hyphens and
    /// underscores, at most 128, never beginning with a hyphen or an underscore. It names a transcript's file, so it
    /// can never name a path.
    /// </summary>
    /// <param name="session">The id, or null where none is recorded.</param>
    public static string? SessionProblem(string? session)
        => session is null || SessionPattern().IsMatch(session)
            ? null
            : $"'{session}' is not a Claude session's id: letters, digits, hyphens and underscores, at most 128, beginning with a letter or a digit";

    /// <summary>What is wrong with a commit's id; null where it is one, or where none is recorded.</summary>
    /// <param name="commit">The id.</param>
    /// <param name="what">What it is, for the reason.</param>
    public static string? CommitProblem(string? commit, string what)
        => commit is null || CommitPattern().IsMatch(commit) ? null : $"its {what}, '{commit}', is not a commit's id";

    /// <summary>What is wrong with a SHA-256 digest; null where it is one, or, where <paramref name="allowNone"/>, where it is null.</summary>
    /// <param name="digest">The digest.</param>
    /// <param name="path">The path it is of, for the reason.</param>
    /// <param name="allowNone">Whether null - no file there - is allowed.</param>
    public static string? DigestProblem(string? digest, string path, bool allowNone)
        => digest is null ? (allowNone ? null : $"'{path}' has no digest")
            : DigestPattern().IsMatch(digest) ? null
            : $"'{path}' has '{digest}', which is not a SHA-256 digest";

    /// <summary>
    /// What is wrong with a path relative to a tree, as records keep one - a name git gave, spelt with forward slashes:
    /// never rooted on any platform (<see cref="PlatformPaths.IsRootedOnAnyPlatform"/>), and no empty, <c>.</c> or
    /// <c>..</c> part, whichever slash parts it. Anything else a name on Linux may hold, a colon or a backslash among it,
    /// is kept. Asked of the path as given, before anything tidies it.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="what">What it is, for the reason.</param>
    public static string? RelativePathProblem(string path, string what)
        => string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal) || PlatformPaths.IsRootedOnAnyPlatform(path)
            || path.Split('/', '\\').Any(part => part is "" or "." or "..")
            ? $"{what}, '{path}', is not a path relative to the tree, spelt with forward slashes"
            : null;

    [GeneratedRegex(@"^[\x21-\x7E]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionPattern();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
