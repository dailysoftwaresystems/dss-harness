using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The states an agent's record can be in, in the order it can reach them: live, then closed, then deleted - or live
/// then deleted straight away, where its worktree was gone or never made and there was nothing to close.
/// </summary>
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

    /// <summary>What <see cref="Access"/> holds for an agent that works in a worktree of its own.</summary>
    public const string AccessWrite = "write";

    /// <summary>What <see cref="Access"/> holds for an agent that reads the main tree and has no worktree.</summary>
    public const string AccessRead = "read";

    /// <summary>
    /// Whether the agent works in a worktree of its own (<see cref="AccessWrite"/>, which every record said before
    /// there was another kind) or reads the main tree and has none (<see cref="AccessRead"/>), which the engine makes
    /// for a read-only reason step. Null is write, so the tool writes no <c>access</c> for an agent with a worktree and
    /// an older tool still reads its record.
    /// </summary>
    public string? Access { get; init; }

    /// <summary>
    /// Whether the agent reads the main tree and has no worktree: what <see cref="Access"/> spells, read here as a
    /// fact and written nowhere - a record is what another tool reads, and this is not one of its members.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Reads => Access == AccessRead;

    /// <summary>
    /// Its worktree's address, <c>orchestrator/agent</c>: absent for an agent that reads the main tree, which
    /// <see cref="Problem(string, string)"/> requires of one that does not.
    /// </summary>
    public string? Worktree { get; init; }

    /// <summary>
    /// The worktrees root its worktree was made under, relative to the main checkout, as the configuration named it then:
    /// where its worktree is, whatever the configuration says later. Absent for an agent that reads the main tree, which
    /// <see cref="Problem(string, string)"/> requires of one that does not.
    /// </summary>
    public string? WorktreesRoot { get; init; }

    /// <summary>The commit its worktree was made from; null until the worktree is made.</summary>
    public string? Base { get; init; }

    /// <summary>
    /// The commit a move of its base goes to, written before the move writes anything into its worktree and gone once
    /// <see cref="Base"/> names it: present, a move stopped part way, which only rebase-agent finishes. Null otherwise.
    /// </summary>
    public string? Moving { get; init; }

    /// <summary>
    /// What the move <see cref="Moving"/> names was told - each path it merges or keeps, after its option, as its command
    /// line names them - so the line that finishes it, said by whichever command meets it, is let through the very paths
    /// it was: said bare, it would be refused over them. Null where it was told nothing, or no move stands.
    /// </summary>
    public IReadOnlyList<string>? MovingWith { get; init; }

    /// <summary>
    /// The old names of files renamed in case alone that the move <see cref="Moving"/> names took out of the agent's git
    /// index (<see cref="RebasePlan.RenamedFrom"/>), recorded once the git that checks out what it writes first ends - having
    /// written it all or stopped part way. Only such a name is missing by the move's doing: git leaves the index and the
    /// disk alike after that checkout and after a git rm of the agent's session. Null before any is, where no move stands,
    /// and in a record a build that kept none wrote.
    /// </summary>
    public IReadOnlyList<string>? MovingRemoved { get; init; }

    /// <summary>
    /// The Claude session the agent runs as - a session's id or a subagent's - where it was given: never for an agent that
    /// reads the main tree, which <see cref="Problem(string, string)"/> refuses.
    /// </summary>
    public string? Session { get; init; }

    /// <summary>One of <see cref="AgentStates"/>.</summary>
    public required string State { get; init; }

    /// <summary>
    /// What deleting it recorded before its worktree's removal began: present from its closing on, kept as its history
    /// once it is deleted; absent where it was deleted with no worktree left to close.
    /// </summary>
    public AgentClosing? Closing { get; init; }

    /// <summary>When it was deleted; present exactly once it is.</summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>Whether it was abandoned rather than folded; present exactly once it is closed or deleted.</summary>
    public bool? Abandoned { get; init; }

    /// <summary>
    /// Where its worktree is in the repository at <paramref name="layout"/>: under the worktrees root it was made under,
    /// whatever the configuration names now. Null for an agent that reads the main tree, which has none.
    /// </summary>
    /// <param name="layout">The repository.</param>
    public string? WorktreePath(Repository.HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Reads || WorktreesRoot is not { } root
            ? null
            : WorktreeAddress.Nested(Orchestrator, Name).PathUnder(layout.WorktreesDirectoryUnder(root));
    }

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

        if (Access is not null && Access != AccessRead && Access != AccessWrite)
        {
            return $"its access is '{Access}', and an agent's is '{AccessRead}' or '{AccessWrite}'";
        }

        if (Reads)
        {
            // An agent that reads the main tree holds none of what a worktree is made of, and a record that says any
            // of it is not the record of such an agent.
            if (Worktree is { } worktree)
            {
                return $"it reads the main tree, and records the worktree '{worktree}'";
            }

            if (WorktreesRoot is { } root)
            {
                return $"it reads the main tree, and records the worktrees root '{root}'";
            }

            if (Base is { } from)
            {
                return $"it reads the main tree, and records the base '{from}'";
            }

            if (Moving is { } to)
            {
                return $"it reads the main tree, and records a move of a base to '{to}'";
            }

            if (MovingWith is not null)
            {
                return "it reads the main tree, and records what a move of a base was told";
            }

            if (MovingRemoved is not null)
            {
                return "it reads the main tree, and records what a move of a base removed";
            }

            // Nor does it record a session: deleting it looks for no transcript, so a session it named would be kept by
            // nothing.
            if (Session is { } session)
            {
                return $"it reads the main tree, and records the session '{session}'";
            }
        }
        else
        {
            if (Worktree is null)
            {
                return "it records no worktree, and an agent that does not read the main tree has one of its own";
            }

            if (WorktreesRoot is null)
            {
                return "it records no worktrees root, and an agent that does not read the main tree was made under one";
            }

            if (Worktree != WorktreeAddress.Nested(Orchestrator, Name).Name)
            {
                return $"its worktree is '{Worktree}', and an agent's is '{WorktreeAddress.Nested(Orchestrator, Name).Name}'";
            }
        }

        if ((OrchestrationRules.ModelProblem(Model)
            ?? OrchestrationRules.SessionProblem(Session)
            ?? OrchestrationRules.CommitProblem(Base, "base")
            ?? OrchestrationRules.CommitProblem(Moving, "base's move")
            ?? (WorktreesRoot is { } under ? OrchestrationRules.RelativePathProblem(under, "its worktrees root") : null)) is { } problem)
        {
            return problem;
        }

        if (Moving is not null && (Base is null || Moving == Base))
        {
            return $"it records a move of its base to {Moving}, from {(Base is null ? "no base" : "that very commit")}";
        }

        if (MovingWith is not null && (Moving is null || MovingWith.Count == 0 || MovingWith.Any(string.IsNullOrEmpty)))
        {
            return Moving is null
                ? "it records what a move of its base was told, and no move of it"
                : "it records what the move of its base was told as nothing, or with an empty word";
        }

        if (MovingRemoved is not null && (Moving is null || MovingRemoved.Count == 0 || MovingRemoved.Any(string.IsNullOrEmpty)))
        {
            return Moving is null
                ? "it records what a move of its base removed, and no move of it"
                : "it records what the move of its base removed as nothing, or with an empty name";
        }

        return State switch
        {
            AgentStates.Live when Closing is not null || DeletedAt is not null || Abandoned is not null => "it is live, and records a closing or a deletion",
            AgentStates.Closed when Closing is null || Abandoned is null || DeletedAt is not null => "it is closed, and records no closing, or records a deletion",
            AgentStates.Deleted when DeletedAt is null || Abandoned is null => "it is deleted, and records no deletion",
            AgentStates.Closed or AgentStates.Deleted => Closing?.Problem(),
            AgentStates.Live => null,
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
/// What an agent shares with the main tree, path by path: every path handed to it from the main tree's uncommitted
/// state - when it began, was seeded again or was refreshed - and every path a fold of it wrote or removed since, each
/// with what both trees then held. What a fold subtracts, since such a path is the agent's own work only once the agent
/// changes it, and what it weighs the main tree against, since the main tree held the same.
/// </summary>
public sealed record SeedRecord
{
    /// <summary>When it was seeded.</summary>
    public required DateTimeOffset SeededAt { get; init; }

    /// <summary>Whether it was handed nothing, as asked, rather than finding nothing to hand it; never once it shares a path.</summary>
    public required bool Empty { get; init; }

    /// <summary>Every path both trees held a file at, with the digest of that file.</summary>
    public required Dictionary<string, string> Paths { get; init; }

    /// <summary>
    /// The paths of <see cref="Paths"/> whose file could be run - its execute bit set - where the platform has such a bit;
    /// absent on Windows, which has none. A mode is part of what a file is, and the fold compares it with the content.
    /// </summary>
    public List<string>? Executable { get; init; }

    /// <summary>
    /// The paths both trees were left without: a deletion in the main tree's uncommitted state, made in the agent's worktree
    /// too when it was handed over, or a path a fold of it removed from the main tree. Weighed against that absence as a
    /// path of <see cref="Paths"/> is weighed against its file.
    /// </summary>
    public List<string>? Absent { get; init; }

    /// <summary>
    /// Whether handing the agent the main tree's uncommitted state stopped part way as it was made, so its seed names only
    /// what was handed before it stopped: the agent is not whole (<see cref="AgentService.MakingUnfinished"/>) until a seed
    /// finishes the hand-over, which writes it absent. True or absent, so a whole agent's seed says nothing of it.
    /// </summary>
    public bool? HandOverStopped { get; init; }

    /// <summary>Every path it shares with the main tree: each a fold weighs whatever the agent's status says.</summary>
    [JsonIgnore]
    public IEnumerable<string> Weighed => Paths.Keys.Concat(Absent ?? []);

    /// <summary>What is wrong with it; null when nothing is.</summary>
    public string? Problem()
        => HandOverStopped is false ? "it says its hand-over did not stop, which only its absence says"
            : Empty && HandOverStopped is true ? "it was handed nothing, and says handing it over stopped part way"
            : Empty && (Paths.Count > 0 || Absent is { Count: > 0 }) ? "it was handed nothing, and names paths it shares"
            : Executable?.FirstOrDefault(path => !Paths.ContainsKey(path)) is { } stray ? $"'{stray}' is named executable, and it holds no file there"
            : Absent?.FirstOrDefault(Paths.ContainsKey) is { } both ? $"'{both}' is named both as a file and as absent"
            : Absent?.Count != Absent?.Distinct(StringComparer.Ordinal).Count() ? "it names a path absent twice"
            : Paths.Select(pair => OrchestrationRules.RelativePathProblem(pair.Key, "a path it shares") ?? OrchestrationRules.DigestProblem(pair.Value, pair.Key, allowNone: false))
                .Concat((Absent ?? []).Select(path => OrchestrationRules.RelativePathProblem(path, "a path it shares")))
                .FirstOrDefault(problem => problem is not null);
}

/// <summary>
/// The anchor rows an agent's folds applied, each as it declared it when it was applied: what a later fold weighs its rows
/// against, so a row it has not declared anew since is never applied again over a change the registries took meanwhile.
/// </summary>
public sealed record AppliedRowsRecord
{
    /// <summary>Every row applied, as declared then, one for each id.</summary>
    public required List<Anchors.AnchorRowDeclaration> Rows { get; init; }

    /// <summary>
    /// What is wrong with it; null when nothing is. A row named twice, or declared with a status or a priority that does not
    /// read, is no record of a row applied: every one was a row the registries took.
    /// </summary>
    public string? Problem()
    {
        if (Rows.GroupBy(row => row.Id, Anchors.AnchorIdMatch.Comparer).FirstOrDefault(group => group.Count() > 1) is { } twice)
        {
            return $"it names row '{twice.Key}' {twice.Count()} times";
        }

        return Rows.FirstOrDefault(row => !Anchors.AnchorStatus.TryParse(row.Status, out _) || (row.Priority is { } band && !Anchors.AnchorPriority.TryNormalize(band, out _))) is { } unread
            ? $"it declares row '{unread.Id}' with status '{unread.Status}' and priority '{unread.Priority}', and one of them is not one"
            : null;
    }
}

/// <summary>The rules every orchestration record's values are held to, each spelt once.</summary>
public static partial class OrchestrationRules
{
    /// <summary>What is wrong with an orchestrator's or an agent's name: the worktree name rule, as each names a directory.</summary>
    /// <param name="name">The name.</param>
    public static string? NameProblem(string? name)
        => WorktreeName.ValidateFormat(name).TryGetName(out _, out var error) ? null : error;

    /// <summary>
    /// What is wrong with the first of <paramref name="paths"/> that could name nothing in the tree - each relative to it,
    /// a trailing separator allowed - naming <paramref name="what"/>; null where every one could.
    /// </summary>
    /// <param name="paths">Paths as a command was given them.</param>
    /// <param name="what">What they are, as a refusal names them.</param>
    public static string? PathsProblem(IReadOnlyList<string> paths, string what)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return paths.Select(path => RelativePathProblem(path.TrimEnd('/', '\\'), what)).FirstOrDefault(problem => problem is not null);
    }

    /// <summary>
    /// What is wrong where one path is named with two options that say different things of it, whichever command takes
    /// them - each option given with the paths named with it, and what it does to a path, to follow "a path is" - or null
    /// where none is.
    /// </summary>
    /// <param name="options">Each option, the paths named with it as they were typed, and what it does to a path.</param>
    public static string? NamedTwiceProblem(params (string Option, IReadOnlyList<string> Paths, string Does)[] options)
    {
        ArgumentNullException.ThrowIfNull(options);

        for (var first = 0; first < options.Length; first++)
        {
            for (var second = first + 1; second < options.Length; second++)
            {
                var named = options[second].Paths.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);

                if (options[first].Paths.Select(PathPatterns.Normalize).FirstOrDefault(named.Contains) is { } path)
                {
                    return $"'{path}' is named with both {options[first].Option} and {options[second].Option}: a path is {options[first].Does}, or "
                        + $"{options[second].Does}, never both.";
                }
            }
        }

        return null;
    }

    /// <summary>The letter every agent a run makes is named with, before its number: <c>a1</c>, <c>a2</c> and up.</summary>
    public const string GeneratedAgentPrefix = "a";

    /// <summary>
    /// The longest name a run that may have <paramref name="mostInstances"/> step instances gives an agent it makes: the
    /// letter and the digits of that bound. Each agent is made by one step instance and numbered one past the highest
    /// number given before, from 1, so no run numbers one past its most instances.
    /// </summary>
    /// <param name="mostInstances">The most step instances the run may have (<c>maxInstances</c>).</param>
    public static int LongestGeneratedAgentName(int mostInstances)
        => GeneratedAgentPrefix.Length + Math.Max(mostInstances, 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

    /// <summary>
    /// The name a run gives the next agent it makes: <see cref="GeneratedAgentPrefix"/> and one past the highest number any
    /// of <paramref name="taken"/> carries - the run's agent directories and the names its steps' records hold - so a
    /// name is never given twice, a deleted agent's included. Names of another shape are no number, and count for none,
    /// a number too large to give among them.
    /// </summary>
    /// <param name="taken">Every name the run has given or holds.</param>
    public static string NextAgentName(IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var highest = taken
            .Select(NumberOf)
            .OfType<long>()
            .Where(number => number < long.MaxValue)
            .DefaultIfEmpty(0)
            .Max();

        return GeneratedAgentPrefix + (highest + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The order agents' names go in where a run takes its agents in turn: the names it gives, <see cref="GeneratedAgentPrefix"/>
    /// and a number, by that number - <c>a2</c> before <c>a10</c> - and every other name after them, as ordinal text orders it.
    /// </summary>
    public static IComparer<string> AgentNameOrder { get; } = Comparer<string>.Create((one, other) =>
        (NumberOf(one), NumberOf(other)) switch
        {
            ({ } first, { } second) when first != second => first.CompareTo(second),
            ({ }, null) => -1,
            (null, { }) => 1,
            _ => string.CompareOrdinal(one, other),
        });

    /// <summary>The number a name the run gave carries after <see cref="GeneratedAgentPrefix"/>; null for a name of another shape.</summary>
    private static long? NumberOf(string name)
        => name.Length > GeneratedAgentPrefix.Length
            && name.StartsWith(GeneratedAgentPrefix, StringComparison.Ordinal)
            && name[GeneratedAgentPrefix.Length] != '0'
            && name.Skip(GeneratedAgentPrefix.Length).All(char.IsAsciiDigit)
            && long.TryParse(name.AsSpan(GeneratedAgentPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number
                : null;

    /// <summary>What is wrong with an agent's name beside its orchestrator's: never the same, since the two would share a log and a plans directory.</summary>
    /// <param name="orchestrator">The orchestrator's name.</param>
    /// <param name="agent">The agent's name.</param>
    public static string? AgentNameProblem(string orchestrator, string agent)
        => string.Equals(orchestrator, agent, StringComparison.Ordinal)
            ? $"'{agent}' names the orchestrator itself: an agent is never named as its orchestrator, whose log and plans it would share"
            : null;

    /// <summary>
    /// The agents of <paramref name="orchestrator"/> that count against its limit, by name: every one not deleted - a closed
    /// agent's worktree may still be there - and every one whose worktree git lists, record or not. An agent that reads
    /// the main tree is none of these: the limit is a limit on worktrees, and it has none.
    /// </summary>
    /// <param name="orchestrator">The orchestrator's name.</param>
    /// <param name="agents">Its agents' directories, each with its record where it reads.</param>
    /// <param name="worktrees">The worktrees git lists, by address.</param>
    public static IReadOnlyList<string> OpenAgents(string orchestrator, IEnumerable<AgentEntry> agents, IEnumerable<string> worktrees)
        => [.. agents
            .Where(entry => entry.Record is not { State: AgentStates.Deleted } && entry.Record is not { Access: AgentRecord.AccessRead })
            .Select(entry => entry.Name)
            .Concat(worktrees.Select(worktree => WorktreeAddress.AgentOf(orchestrator, worktree)).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

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

    /// <summary>Whether <paramref name="id"/> is an id git gives an object - a commit's, a blob's: forty or sixty-four lower-case hexadecimal characters.</summary>
    /// <param name="id">The id.</param>
    public static bool IsObjectId(string? id) => id is not null && CommitPattern().IsMatch(id);

    /// <summary>Whether <paramref name="digest"/> is a SHA-256 digest: sixty-four lower-case hexadecimal characters.</summary>
    /// <param name="digest">The digest.</param>
    public static bool IsDigest(string? digest) => digest is not null && DigestPattern().IsMatch(digest);

    /// <summary>What is wrong with a commit's id; null where it is one, or where none is recorded.</summary>
    /// <param name="commit">The id.</param>
    /// <param name="what">What it is, for the reason.</param>
    public static string? CommitProblem(string? commit, string what)
        => commit is null || IsObjectId(commit) ? null : $"its {what}, '{commit}', is not a commit's id";

    /// <summary>What is wrong with a SHA-256 digest; null where it is one, or, where <paramref name="allowNone"/>, where it is null.</summary>
    /// <param name="digest">The digest.</param>
    /// <param name="path">The path it is of, for the reason.</param>
    /// <param name="allowNone">Whether null - no file there - is allowed.</param>
    public static string? DigestProblem(string? digest, string path, bool allowNone)
        => digest is null ? (allowNone ? null : $"'{path}' has no digest")
            : IsDigest(digest) ? null
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
            || PlatformPaths.ClimbsOut(path)
            || path.Split('/', '\\').Any(part => part is "" or ".")
            ? $"{what}, '{path}', is not a path relative to the tree, spelt with forward slashes"
            : null;

    [GeneratedRegex(@"^[\x21-\x7E]{1,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionPattern();

    [GeneratedRegex(@"^[0-9a-f]{40}([0-9a-f]{24})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
