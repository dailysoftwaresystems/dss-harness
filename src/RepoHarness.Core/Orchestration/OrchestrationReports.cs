using System.Globalization;
using System.Text.Json.Nodes;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Orchestration;

/// <summary>What list-orchestrator reports: each orchestrator, and where each of its agents stands.</summary>
public static class OrchestrationReports
{
    /// <summary>What list-orchestrator reports.</summary>
    /// <param name="orchestrators">The orchestrators listed.</param>
    /// <param name="json">Whether to answer with one JSON document rather than lines.</param>
    public static CommandOutcome List(IReadOnlyList<OrchestratorInventory> orchestrators, bool json)
    {
        ArgumentNullException.ThrowIfNull(orchestrators);

        var message = orchestrators.Count == 0 ? "no orchestrators" : $"{orchestrators.Count} orchestrator(s)";

        return json
            ? CommandOutcome.Ok(message) with { Data = [Document(orchestrators).ToJsonString(ReportJson.Options)], Quiet = true }
            : CommandOutcome.Ok(message, [.. Lines(orchestrators)]);
    }

    private static IEnumerable<string> Lines(IReadOnlyList<OrchestratorInventory> orchestrators)
    {
        foreach (var orchestrator in orchestrators)
        {
            var record = orchestrator.Record;
            var live = orchestrator.Agents.Count(agent => agent.Entry.Record?.State == AgentStates.Live);

            yield return $"{record.Name}  model {record.Model}, at most {record.Parallel} agent(s) at once, {live} live{Session(record.Session)}";

            foreach (var agent in orchestrator.Agents)
            {
                yield return $"  {Line(agent)}";
            }

            foreach (var worktree in orchestrator.WorktreesWithoutAgent)
            {
                yield return $"  worktree {worktree.Name} at '{worktree.Path}' has no agent of this orchestrator's recording it";
            }
        }
    }

    private static string Line(AgentInventory agent)
    {
        if (agent.Entry.Record is not { } record)
        {
            return $"{agent.Entry.Name}  unreadable: {agent.Entry.Problem}";
        }

        var kept = agent.Evidence.Count == 0 && agent.Transcripts == 0
            ? string.Empty
            : $"; kept {agent.Evidence.Count} evidence set(s), {agent.Transcripts} transcript file(s)";

        return record.State switch
        {
            AgentStates.Live when agent.Worktree is null => $"{record.Name}  live, and its worktree is gone from '{agent.WorktreePath}'{kept}",
            AgentStates.Live => $"{record.Name}  live at '{agent.WorktreePath}', base {Base(record.Base)}{Session(record.Session)}{kept}",
            AgentStates.Closed => $"{record.Name}  closed at {Moment(record.Closing!.At)}, and its removal did not finish{(agent.Worktree is null ? string.Empty : $": its worktree is still at '{agent.WorktreePath}'")}{kept}",
            _ => $"{record.Name}  deleted at {Moment(record.DeletedAt!.Value)}{(record.Abandoned is true ? ", abandoned" : string.Empty)}{kept}",
        };
    }

    private static JsonObject Document(IReadOnlyList<OrchestratorInventory> orchestrators)
    {
        var list = new JsonArray();

        foreach (var orchestrator in orchestrators)
        {
            var record = orchestrator.Record;
            var agents = new JsonArray();

            foreach (var agent in orchestrator.Agents)
            {
                agents.Add(AgentNode(orchestrator, agent));
            }

            var worktrees = new JsonArray();

            foreach (var worktree in orchestrator.WorktreesWithoutAgent)
            {
                worktrees.Add(new JsonObject { ["name"] = worktree.Name, ["path"] = worktree.Path });
            }

            list.Add(new JsonObject
            {
                ["name"] = record.Name,
                ["model"] = record.Model,
                ["createdAt"] = Moment(record.CreatedAt),
                ["parallel"] = record.Parallel,
                ["session"] = record.Session,
                ["directory"] = orchestrator.Layout.Directory,
                ["log"] = orchestrator.Layout.LogFile(record.Name),
                ["agents"] = agents,
                ["worktreesWithoutAgent"] = worktrees,
            });
        }

        return new JsonObject { ["orchestrators"] = list };
    }

    private static JsonObject AgentNode(OrchestratorInventory orchestrator, AgentInventory agent)
    {
        var layout = orchestrator.Layout;
        var name = agent.Entry.Name;

        if (agent.Entry.Record is not { } record)
        {
            return new JsonObject { ["name"] = name, ["unreadable"] = agent.Entry.Problem };
        }

        var evidence = new JsonArray();

        foreach (var directory in agent.Evidence)
        {
            evidence.Add(Path.Combine(layout.AgentDirectory(name), directory));
        }

        return new JsonObject
        {
            ["name"] = record.Name,
            ["state"] = record.State,
            ["model"] = record.Model,
            ["createdAt"] = Moment(record.CreatedAt),
            ["worktree"] = record.Worktree,
            ["path"] = agent.WorktreePath,
            ["worktreeExists"] = agent.Worktree is not null,
            ["baseCommit"] = record.Base,
            ["session"] = record.Session,
            ["closedAt"] = record.Closing is { } closing ? Moment(closing.At) : null,
            ["deletedAt"] = record.DeletedAt is { } deleted ? Moment(deleted) : null,
            ["abandoned"] = record.Abandoned ?? record.Closing?.Abandoned,
            ["record"] = layout.AgentRecordFile(name),
            ["log"] = layout.LogFile(name),
            ["plans"] = layout.PlansDirectory(name),
            ["work"] = layout.WorkDirectory(name),
            ["evidence"] = evidence,
            ["transcripts"] = agent.Transcripts == 0 ? null : layout.TranscriptsDirectory(name),
        };
    }

    /// <summary>What a fold would write, remove and leave out, every path it weighed in exactly one list.</summary>
    /// <param name="plan">A fold with no refusal.</param>
    public static IEnumerable<string> FoldLines(FoldPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Settled.Count > 0)
        {
            // Never silent: a path left out without a word is how an agent's work goes missing while its fold succeeds.
            yield return $"{plan.Settled.Count} path(s) declared settled by hand, neither compared nor written:";

            foreach (var path in plan.Settled)
            {
                yield return $"  {path}";
            }
        }

        if (plan.Seeded.Count > 0)
        {
            int Of(IReadOnlyList<string> list) => list.Count(plan.Seeded.Contains);

            yield return $"{plan.Seeded.Count} path(s) handed to it: {Of(plan.Written)} its own, {Of(plan.Inherited)} inherited, "
                + $"{Of(plan.AlreadyIn)} already in the main tree, {Of(plan.Deleted)} deleted, {Of(plan.Settled)} settled";
        }

        yield return $"{plan.Inherited.Count} inherited path(s) left out; {plan.Written.Count} path(s) are its own:";

        foreach (var path in plan.Written)
        {
            // Whether it was handed over, not whether the file is new: a file no agent was handed may be tracked all along.
            yield return plan.Seeded.Contains(path) ? $"  {path}" : $"  {path}   (not handed to it)";
        }

        if (plan.Deleted.Count > 0)
        {
            yield return $"and {plan.Deleted.Count} path(s) it deleted, removed from the main tree:";

            foreach (var path in plan.Deleted)
            {
                yield return $"  {path}";
            }
        }

        if (plan.AlreadyIn.Count > 0)
        {
            yield return $"and {plan.AlreadyIn.Count} path(s) the main tree already holds as it does, with nothing to write:";

            foreach (var path in plan.AlreadyIn)
            {
                yield return $"  {path}";
            }
        }
    }

    /// <summary>Why a fold is refused, each path with its reason, and how a path reconciled by hand is let through.</summary>
    /// <param name="plan">A fold with refusals.</param>
    public static IEnumerable<string> FoldRefusalLines(FoldPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        yield return $"{plan.Refusals.Count} problem(s), and nothing was written:";

        foreach (var refusal in plan.Refusals)
        {
            yield return $"  {refusal}";
        }

        yield return "A path the main tree changed is usually two agents on one file: by an uncommitted edit while the other's fold is in the "
            + "tree, by a commit once it was committed. Merge this agent's change into the main tree by hand, then run again naming each "
            + "path you reconciled with --settled <path>, which leaves just those paths out so the rest can go in. Run again without it, "
            + "and the fold refuses the same way: merging by hand makes the main tree differ more from the agent's baseline, not less. "
            + "--settled says you reconciled the path yourself; it is not a --force, and nothing is written for it.";
    }

    /// <summary>What applying an agent's rows does, or did: each row, and where it goes.</summary>
    /// <param name="batch">The rows applied, or planned.</param>
    public static IEnumerable<string> RowLines(AnchorBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        foreach (var row in batch.Rows)
        {
            yield return row.Action switch
            {
                AnchorRowAction.New => $"  {row.Id}: a new row, {row.Change.StatusAfter}, in the {row.Change.To.Name} registry",
                AnchorRowAction.Changed => $"  {row.Id}: {Changed(row.Change)}",
                _ => $"  {row.Id}: already as it declares, with nothing to write",
            };
        }

        foreach (var problem in batch.Problems)
        {
            yield return $"  refused: {problem}";
        }

        if (batch.Failure is { } failure)
        {
            yield return $"  {failure}";
            yield return batch.Restored.Count > 0 ? $"  put back byte for byte: {string.Join(", ", batch.Restored)}" : "  nothing was left written";

            if (batch.RestoreFailed.Count > 0)
            {
                yield return $"  NOT put back, and not as they were: {string.Join(", ", batch.RestoreFailed)}: read them before anything else";
            }
        }
    }

    /// <summary>What is said of an orchestrator that does not exist, with how to make one.</summary>
    /// <param name="name">The orchestrator's name, as it was asked for.</param>
    internal static string NoOrchestrator(string name)
        => $"No orchestrator named '{name}'. Create it with '{ToolPackage.Command} {OrchestratorService.CreateCommand} {name} --model <model>'.";

    /// <summary>The delete-agent command line a message names for an agent, quoted.</summary>
    /// <param name="orchestrator">The agent's orchestrator.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="options">What to run it with.</param>
    internal static string DeleteAgentLine(string orchestrator, string agent, string options = "--apply")
        => $"'{ToolPackage.Command} {AgentService.DeleteCommand} {orchestrator} {agent} {options}'";

    /// <summary>An agent's base as reports name it, where none may be recorded.</summary>
    /// <param name="commit">The commit, or null where none is recorded.</param>
    internal static string Base(string? commit) => commit is null ? "unrecorded" : ReportText.Commit(commit);

    /// <summary>A moment as orchestration messages and listings spell one: UTC, to the second.</summary>
    /// <param name="moment">The moment.</param>
    internal static string Moment(DateTimeOffset moment) => moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Changed(AnchorChange change)
    {
        var fields = change.Fields.Count == 0 ? "its cells" : string.Join(", ", change.Fields.Select(field => field.Field));
        return change.Moved ? $"changes {fields}, and moves to the {change.To.Name} registry" : $"changes {fields}";
    }

    private static string Session(string? session) => session is null ? string.Empty : $", session {session}";
}
