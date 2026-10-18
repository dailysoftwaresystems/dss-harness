using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Anchors;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration;

/// <summary>What list-orchestrator reports: each orchestrator, and where each of its agents stands.</summary>
public static class OrchestrationReports
{
    /// <summary>What list-orchestrator reports.</summary>
    /// <param name="orchestrators">The orchestrators listed.</param>
    /// <param name="json">Whether to answer with one JSON document rather than lines.</param>
    public static CommandOutcome List(IReadOnlyList<OrchestratorInventory> orchestrators, bool json)
    {
        ArgumentNullException.ThrowIfNull(orchestrators);

        var message = orchestrators.Count == 0 ? "no orchestrators" : ReportText.Counted(orchestrators.Count, "orchestrator");

        return json
            ? CommandOutcome.Ok(message) with { Data = [Document(orchestrators).ToJsonString(ReportJson.Options)], Quiet = true }
            : CommandOutcome.Ok(message, [.. Lines(orchestrators)]);
    }

    /// <summary>
    /// The listing list-orchestrator answers with where it stops before it lists anything - a --directory that does not
    /// exist, a repository not initialised: its own document, listing no orchestrator, after the code it exits with and
    /// the line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string ListStopped(int exitCode, string summary) => ReportJson.Stopped(exitCode, summary, Document([]));

    private static IEnumerable<string> Lines(IReadOnlyList<OrchestratorInventory> orchestrators)
    {
        foreach (var orchestrator in orchestrators)
        {
            var record = orchestrator.Record;

            yield return $"{record.Name}  model {record.Model}, {orchestrator.Open.Count} of at most {ReportText.Counted(record.Parallel, "agent")} open{Session(record.Session)}";

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
            : $"; kept {ReportText.Counted(agent.Evidence.Count, "evidence set")}, {ReportText.Counted(agent.Transcripts, "transcript file")}";

        var abandoned = record.Abandoned is true ? ", abandoned" : string.Empty;

        return record.State switch
        {
            AgentStates.Live when record.Reads => $"{record.Name}  live, reads the main tree (no worktree){Session(record.Session)}{kept}",
            AgentStates.Live when !agent.WorktreeExists => $"{record.Name}  live, and its worktree is gone from '{agent.WorktreePath}'{kept}",
            AgentStates.Live => $"{record.Name}  live at '{agent.WorktreePath}', base {Base(record.Base)}{Session(record.Session)}{kept}",
            AgentStates.Closed => $"{record.Name}  closed at {Moment(record.Closing!.At)}{abandoned}, and its removal did not finish{(agent.WorktreeExists ? $": its worktree is still at '{agent.WorktreePath}'" : string.Empty)}{kept}",
            _ => $"{record.Name}  deleted at {Moment(record.DeletedAt!.Value)}{abandoned}{kept}",
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
                ["open"] = new JsonArray([.. orchestrator.Open.Select(name => (JsonNode?)name)]),
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

        var node = new JsonObject
        {
            ["name"] = record.Name,
            ["state"] = record.State,
            ["model"] = record.Model,
            ["createdAt"] = Moment(record.CreatedAt),
            ["worktree"] = record.Worktree,
            ["path"] = agent.WorktreePath,
            ["worktreeExists"] = agent.WorktreeExists,
            ["baseCommit"] = record.Base,
            ["session"] = record.Session,
            ["closedAt"] = record.Closing is { } closing ? Moment(closing.At) : null,
            ["deletedAt"] = record.DeletedAt is { } deleted ? Moment(deleted) : null,
            ["abandoned"] = record.Abandoned,
            ["record"] = layout.AgentRecordFile(name),
            ["seed"] = layout.SeedFile(name),
            ["shared"] = layout.SharedDirectory(name),
            ["appliedRows"] = layout.AppliedRowsFile(name),
            ["log"] = layout.LogFile(name),
            ["plans"] = layout.PlansDirectory(name),
            ["work"] = layout.WorkDirectory(name),
            ["evidence"] = evidence,
            ["transcripts"] = agent.Transcripts == 0 ? null : layout.TranscriptsDirectory(name),
        };

        // The kind of agent is named where it is the rarer one, so an agent with a worktree's node is what it always was.
        if (record.Reads)
        {
            node["access"] = record.Access;
        }

        return node;
    }

    /// <summary>What a fold would write, merge, remove and leave out - or did - every path it weighed in exactly one list.</summary>
    /// <param name="plan">A fold with no refusal.</param>
    /// <param name="applied">Whether the fold was written, rather than only measured.</param>
    public static IEnumerable<string> FoldLines(FoldPlan plan, bool applied)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Handed.Count > 0)
        {
            var removed = plan.Handed.Deletions.ToHashSet(StringComparer.Ordinal);

            yield return $"{ReportText.Counted(plan.Handed.Count, "path")} declared settled by hand: the main tree's copy "
                + (applied ? "handed to the agent and recorded as shared:" : "to hand to the agent and record as shared:");

            foreach (var path in plan.Handed.Deletions.Concat(plan.Handed.Files).Order(StringComparer.Ordinal))
            {
                yield return removed.Contains(path) ? $"  {path}   (removed: the main tree holds no file there)" : $"  {path}";
            }

            yield return ReadAgain;
        }

        if (plan.LeftOut.Count > 0)
        {
            // Never silent: a path left out without a word is how an agent's work goes missing while its fold succeeds.
            yield return $"{ReportText.Counted(plan.LeftOut.Count, "path")} declared settled by hand that a fold never reads or writes, left out and not handed - "
                + $"the agent's next fold meets {(plan.LeftOut.Count == 1 ? "it" : "them")} again:";

            foreach (var left in plan.LeftOut)
            {
                yield return $"  {left.Path}   ({left.Why})";
            }
        }

        if (plan.Seeded.Count > 0)
        {
            int Of(IReadOnlyList<string> list) => list.Count(plan.Seeded.Contains);

            // A fold that merges nothing says what it always said.
            var merged = plan.Merged.Count == 0 ? string.Empty : $", {Of([.. plan.Merged.Select(merge => merge.Path)])} merged";

            yield return $"{ReportText.Counted(plan.Seeded.Count, "path")} it shares with the main tree, handed to it or folded before: {Of(plan.Written)} its own, "
                + $"{Of(plan.Inherited)} inherited, {Of(plan.AlreadyIn)} already in the main tree, {Of(plan.Deleted)} deleted, "
                + $"{Of([.. plan.Handed.Deletions, .. plan.Handed.Files, .. plan.LeftOut.Select(left => left.Path)])} settled{merged}";
        }

        yield return $"{ReportText.Counted(plan.Inherited.Count, "inherited path")} left out; {ReportText.Counted(plan.Written.Count, "path")} are its own:";

        foreach (var path in plan.Written)
        {
            // Whether it was shared before, not whether the file is new: a file no agent was handed may be tracked all along.
            yield return plan.Seeded.Contains(path) ? $"  {path}" : $"  {path}   (not handed to it, nor folded before)";
        }

        if (plan.Merged.Count > 0)
        {
            // Never silent, and never taken for more than it is: git merged the text, and nobody has built it.
            yield return $"{ReportText.Counted(plan.Merged.Count, "path")} both trees changed, merged three-way, "
                + (applied ? "written into both trees and recorded as shared" : "to write into both trees and record as shared")
                + " - a clean textual merge proves nothing about the program, so build and test the tree before you commit it:";

            foreach (var line in MergedLines(plan.Merged))
            {
                yield return line;
            }
        }

        if (plan.Deleted.Count > 0)
        {
            yield return $"and {ReportText.Counted(plan.Deleted.Count, "path")} it deleted, removed from the main tree:";

            foreach (var path in plan.Deleted)
            {
                yield return $"  {path}";
            }
        }

        if (plan.AlreadyIn.Count > 0)
        {
            yield return $"and {ReportText.Counted(plan.AlreadyIn.Count, "path")} the main tree already holds as it does, with nothing to write:";

            foreach (var path in plan.AlreadyIn)
            {
                yield return $"  {path}";
            }
        }
    }

    /// <summary>
    /// Why a fold is refused, each path with its reason and what a three-way merge of it gives, the conflicts of each that
    /// conflicts, and how a path is let through: merged where its merge is clean, or reconciled by hand.
    /// </summary>
    /// <param name="plan">A fold with refusals.</param>
    /// <param name="merging">The command that names every path whose merge is clean and was not asked for, quoted; null where there is none.</param>
    public static IEnumerable<string> FoldRefusalLines(FoldPlan plan, string? merging)
    {
        ArgumentNullException.ThrowIfNull(plan);

        yield return $"{ReportText.Counted(plan.Refusals.Count, "problem")}, and nothing was written:";

        foreach (var refusal in plan.Refusals)
        {
            yield return $"  {refusal}";
        }

        foreach (var line in plan.Shown)
        {
            yield return line;
        }

        yield return "A path the main tree changed is usually two agents on one file: by an uncommitted edit while the other's fold is in the "
            + $"tree, by a commit once it was committed, and by both where a committed change was edited again. Where a three-way merge of the two is clean, {FoldAllowances.MergeOption} <path> writes it "
            + "into both trees: it is never taken unasked, and a clean textual merge proves nothing about the program. Where it conflicts, or "
            + "none is offered, merge this agent's change into the main tree by hand, then run again naming the path with --settled <path>, "
            + "which hands the agent the main tree's copy and records it as shared, so the rest can go in and its next fold starts from what "
            + "you reconciled. Neither is a --force: each names one path.";

        if (merging is not null)
        {
            yield return MergingLine(plan.Mergeable.Count, merging);
        }
    }

    /// <summary>
    /// Each merged path with what its merge brought, then what the agent's session must do about them: said under every
    /// list of merged paths, whichever command wrote them into the agent's worktree.
    /// </summary>
    /// <param name="merged">The merged paths, at least one.</param>
    internal static IEnumerable<string> MergedLines(IReadOnlyList<MergedPath> merged) => [.. merged.Select(merge => $"  {merge.Path}   {merge.Listed}"), ReadAgain];

    /// <summary>
    /// What the agent's session must do about paths a command wrote into its worktree from the main tree: said under every
    /// list of them - merged, or settled by hand and handed.
    /// </summary>
    private const string ReadAgain = "the agent's session must read these again before it writes them: a write made from what it held before would fold as its "
        + "change, over what the main tree brought";

    /// <summary>The last of a refusal's lines where paths merge clean that nobody named: the command that takes them, with every option given.</summary>
    /// <param name="count">How many paths merge clean and were not named.</param>
    /// <param name="merging">The command that names them, quoted.</param>
    internal static string MergingLine(int count, string merging) => $"to merge the {ReportText.Counted(count, "path")} that merge clean: {merging}";

    /// <summary>
    /// What a fold writes, removes and leaves out - or, where <paramref name="plan"/> is null, nothing of it - then the
    /// rows filed in <paramref name="rowsDirectory"/>: the one report of both, every command that folds an agent giving it.
    /// </summary>
    /// <param name="plan">The fold, with no refusal; null where nothing is folded.</param>
    /// <param name="rows">The rows applied, or planned.</param>
    /// <param name="rowsDirectory">Where the agent files its rows.</param>
    /// <param name="applied">Whether the fold was written, rather than only measured.</param>
    public static IEnumerable<string> FoldAndRowLines(FoldPlan? plan, AgentRowsPlan rows, string rowsDirectory, bool applied)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return (plan is null ? [] : FoldLines(plan, applied)).Concat(RowReport(rows, rowsDirectory));
    }

    /// <summary>Why a fold is refused, and its rows' problems where they have any: the one report of a refused measure.</summary>
    /// <param name="plan">The fold measured.</param>
    /// <param name="rows">The rows planned.</param>
    /// <param name="rowsDirectory">Where the agent files its rows.</param>
    /// <param name="merging">The command that names every path whose merge is clean and was not asked for, quoted; null where there is none.</param>
    public static IEnumerable<string> RefusalLines(FoldPlan plan, AgentRowsPlan rows, string rowsDirectory, string? merging)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);

        return (plan.Refusals.Count > 0 ? FoldRefusalLines(plan, merging) : FoldLines(plan, applied: false)).Concat(rows.Batch.Succeeded ? [] : RowReport(rows, rowsDirectory));
    }

    private static IEnumerable<string> RowReport(AgentRowsPlan rows, string rowsDirectory)
    {
        var batch = rows.Batch;

        if (batch.Rows.Count == 0 && batch.Problems.Count == 0 && batch.Failure is null && rows.Unchanged.Count == 0)
        {
            yield break;
        }

        yield return $"rows filed in '{rowsDirectory}':";

        foreach (var line in RowLines(batch))
        {
            yield return line;
        }

        foreach (var id in rows.Unchanged)
        {
            yield return $"  {id}: applied by an earlier fold and not declared anew since, so the registries keep what they hold now";
        }

        // Said once, so the agent's own files do not raise the same cell again: the fold never writes them.
        if (batch.Rows.Any(row => row.Kept.Count > 0))
        {
            yield return "A cell kept is not written into the agent's own file. Before the agent changes that cell again it writes what the registries hold "
                + $"into '{Path.Combine(rowsDirectory, "<ID>", "<cell>" + AgentRows.CellExtension)}' - {Line("read-anchor", "<ID>")} prints the row - or its "
                + $"change, made from the earlier text, is two changes of one cell and is written only with {AnchorBatchRequest.AcceptOverOption}.";
        }
    }

    /// <summary>
    /// What applying an agent's rows does, or did: each row, and where it goes - each prose cell it changes said with what
    /// becomes of its stored text - each cell the registries took since the agent's earlier fold that is kept, each both
    /// changed with the three texts, and each cell that does not keep its stored text shown word by word, what it removes
    /// and what replaces it, with whether that was accepted. A row left untouched is said by the report of the agent's rows.
    /// </summary>
    /// <param name="batch">The rows applied, or planned.</param>
    public static IEnumerable<string> RowLines(AnchorBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        foreach (var row in batch.Rows.Where(row => row.Action != AnchorRowAction.Untouched))
        {
            yield return row.Action switch
            {
                AnchorRowAction.New => $"  {row.Id}: a new row, {row.Change!.StatusAfter}, in the {row.Change.To.Name} registry",
                AnchorRowAction.Changed => $"  {row.Id}: {Changed(row.Change!)}",
                _ when row.Kept.Count > 0 => $"  {row.Id}: the registries hold it as they took it since its earlier fold, and the agent changed none of what they changed: nothing to write",
                _ => $"  {row.Id}: already as it declares, with nothing to write",
            };

            foreach (var kept in row.Kept)
            {
                yield return AnchorCellNames.Text.Contains(kept.Cell, StringComparer.Ordinal)
                    ? $"    its {AnchorCellNames.HeadingOf(kept.Cell)} keeps what the registries took since its earlier fold, which the agent's file does not hold: "
                        + AnchorCellComparison.WordDiff(kept.Agent!, kept.Registries)
                    : kept.Agent is null
                        ? $"    its {AnchorCellNames.HeadingOf(kept.Cell)} keeps what the registries took since its earlier fold: '{kept.Registries}', where the agent's file declares none"
                        : $"    its {AnchorCellNames.HeadingOf(kept.Cell)} keeps what the registries took since its earlier fold: '{kept.Registries}', where the agent's file holds '{kept.Agent}'";
            }

            foreach (var over in row.Over)
            {
                var heading = AnchorCellNames.HeadingOf(over.Cell.Cell);
                var consent = over.Accepted
                    ? $"and the agent's is written over the registries', accepted with {AnchorBatchRequest.AcceptOverOption} {over.Cell}"
                    : $"and the agent's is written only with {AnchorBatchRequest.AcceptOverOption} {over.Cell}";

                if (!over.Cell.IsProse)
                {
                    yield return $"    its {heading} was changed by the registries and by the agent since its earlier fold - then '{over.Earlier}', the registries "
                        + $"'{over.Registries}', the agent '{over.Agent}' - {consent}";
                    continue;
                }

                yield return $"    its {heading} was changed by the registries and by the agent since its earlier fold, {consent}";
                yield return $"      the registries: {AnchorCellComparison.WordDiff(over.Earlier, over.Registries)}";
                yield return $"      the agent:      {AnchorCellComparison.WordDiff(over.Earlier, over.Agent)}";
                yield return $"      written over the registries': {AnchorCellComparison.WordDiff(over.Registries, over.Agent)}";
            }

            foreach (var lost in row.Lost)
            {
                yield return $"    its {lost.Cell.Cell} does not keep its stored text word for word, {(lost.Accepted ? "accepted" : "written only")} with "
                    + $"{AnchorBatchRequest.AcceptLostOption} {lost.Cell}: {lost.Diff}";
            }
        }

        foreach (var problem in batch.Problems)
        {
            yield return $"  refused: {problem}";
        }

        if (batch.Failure is { } failure)
        {
            yield return $"  {failure}";

            if (batch.Restored.Count > 0)
            {
                yield return $"  put back byte for byte: {string.Join(", ", batch.Restored)}";
            }

            if (batch.RestoreFailed.Count > 0)
            {
                yield return $"  NOT put back, and not as they were: {string.Join(", ", batch.RestoreFailed)}: read them before anything else";
            }

            if (batch.Restored.Count == 0 && batch.RestoreFailed.Count == 0)
            {
                yield return "  nothing was left written";
            }
        }
    }

    /// <summary>What is said of an orchestrator that does not exist, with how to make one.</summary>
    /// <param name="name">The orchestrator's name, as it was asked for.</param>
    internal static string NoOrchestrator(string name)
        => $"No orchestrator named '{name}'. Create it with {Line(OrchestratorService.CreateCommand, name, "--model", "<model>")}.";

    /// <summary>The delete-agent command line a message names for an agent, quoted.</summary>
    /// <param name="orchestrator">The agent's orchestrator.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="options">What to run it with.</param>
    internal static string DeleteAgentLine(string orchestrator, string agent, string options = "--apply")
        => Line(AgentService.DeleteCommand, orchestrator, agent, options);

    /// <summary>
    /// <paramref name="argument"/> as a command line takes it: in double quotes where it holds a space, so a path holding one
    /// is still one argument, and as it is otherwise.
    /// </summary>
    /// <param name="argument">One argument, as its command is to receive it.</param>
    internal static string Argument(string argument)
        => argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

    /// <summary>The arguments that name each of <paramref name="paths"/> with <paramref name="option"/>, each path once, as a command line takes them.</summary>
    /// <param name="option">The option, given once for each path.</param>
    /// <param name="paths">The paths it was given.</param>
    internal static IEnumerable<string> PathArguments(string option, IEnumerable<string> paths)
        => paths.Distinct(StringComparer.Ordinal).SelectMany(path => new[] { option, Argument(path) });

    /// <summary>A command line a message names, quoted: the tool, the command, and its arguments as given.</summary>
    /// <param name="command">The command.</param>
    /// <param name="arguments">Its arguments, each already spelt as the line reads.</param>
    internal static string Line(string command, params string[] arguments)
        => $"'{string.Join(' ', [ToolPackage.Command, command, .. arguments.Where(argument => argument.Length > 0)])}'";

    /// <summary>
    /// The outcome of fold-agent, delete-agent, refresh-agent or rebase-agent as one JSON document, the whole of standard
    /// output: its facts serialised member by member - its code, its last line, its lines, with what the code cannot say:
    /// what is wanted of the caller, who holds what its --apply needs, each consent it lacks, why it is refused, what a
    /// fold wrote, and what the plan of a refresh or a move of an agent's base names.
    /// </summary>
    /// <param name="outcome">The outcome as the command answered it, which keeps everything but its document.</param>
    /// <param name="facts">What the answer knows beyond its lines: the document is this, serialised.</param>
    /// <returns>
    /// The same outcome, answering with the document and carrying the facts: its exit code, its cause, its lines and its
    /// failure line are unchanged.
    /// </returns>
    public static CommandOutcome Document(CommandOutcome outcome, AgentVerbFacts facts)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(facts);

        var holders = facts.Holders;
        var heldNow = new JsonArray();

        foreach (var holder in holders?.Holders ?? [])
        {
            // A copy kept in the worktree that a claim of its family's own names the holder of has no entry of the lock: its
            // process, its run and since when are what the claim records, and what only an entry says - the command, the
            // variant and where a leg stands - is null.
            var since = holder.Entry?.Holder.TakenUtc ?? holder.Claim?.TakenUtc;

            heldNow.Add(new JsonObject
            {
                ["holds"] = holder.Copy ? "copy" : "tree",
                ["path"] = holder.Path,
                ["host"] = holder.Host,
                ["variant"] = holder.Entry?.Variant,
                ["processId"] = holder.Entry?.Holder.ProcessId ?? holder.Claim?.ProcessId,
                ["run"] = holder.Entry?.Holder.RunId ?? holder.Claim?.RunId,
                ["since"] = since is { } moment ? Moment(moment) : null,
                ["command"] = holder.Entry?.Holder.Command,
                ["standing"] = holder.Standing switch
                {
                    HolderStanding.NoSlot => "no-slot",
                    HolderStanding.Waiting => "waiting",
                    HolderStanding.HoldsSlot => "holds-slot",
                    HolderStanding.OnHost => "on-host",
                    null => null,
                    var unknown => throw new ArgumentOutOfRangeException(nameof(facts), unknown, "Not where a holder's leg can stand."),
                },
                ["ahead"] = holder.Ahead,
                ["said"] = holder.Said,
            });
        }

        var lines = new JsonArray();

        foreach (var line in facts.Lines)
        {
            lines.Add(line);
        }

        // Null where the lock file could not be read - nobody was named, and heldUnread says why - and an empty array
        // where it was read and nobody held.
        var document = AgentDocument(
            facts.ExitCode,
            facts.Summary,
            facts.Applied,
            facts.Standing,
            holders is { Unread: not null } ? null : heldNow,
            holders?.Unread,
            lines,
            facts.Needs,
            facts.Refusals,
            facts.Folded,
            facts.Planned);

        return outcome with { Data = [document], Quiet = true, Facts = facts };
    }

    /// <summary>
    /// The document fold-agent, delete-agent, refresh-agent or rebase-agent answers with where it stops before it weighs
    /// anything - a --directory that does not exist, a repository not initialised, a usage error, an agent that is not
    /// there: the same document, empty - nothing applied, no request weighed, nobody asked who holds anything, no lines,
    /// nothing needed, no reason typed, nothing folded and no plan - with the code it exits with and the line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary)
        => AgentDocument(exitCode, summary, applied: false, standing: null, heldNow: null, heldUnread: null, [], needs: [], refusals: [], folded: null, planned: null);

    /// <summary>The name a consent's kind has in the document.</summary>
    /// <param name="kind">The kind.</param>
    public static string Named(ConsentKind kind) => kind switch
    {
        ConsentKind.FoldNewRows => "fold-new-rows",
        ConsentKind.FoldLostRows => "fold-lost-rows",
        ConsentKind.FoldOverCells => "fold-over-cells",
        ConsentKind.FoldTwoChanges => "fold-two-changes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a consent a request can lack."),
    };

    /// <summary>The name a refusal's kind has in the document.</summary>
    /// <param name="kind">The kind.</param>
    public static string Named(RefusalKind kind) => kind switch
    {
        RefusalKind.Conflict => "conflict",
        RefusalKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a reason a request is refused for."),
    };

    /// <summary>The one document of the four verbs, member by member, as text.</summary>
    private static string AgentDocument(
        int exitCode,
        string summary,
        bool applied,
        RequestStanding? standing,
        JsonArray? heldNow,
        string? heldUnread,
        JsonArray lines,
        IReadOnlyList<ConsentKind> needs,
        IReadOnlyList<RefusalKind> refusals,
        AgentFolded? folded,
        AgentPlanned? planned)
        => new JsonObject
        {
            ["exitCode"] = exitCode,
            ["summary"] = summary,
            ["applied"] = applied,
            ["request"] = standing switch
            {
                RequestStanding.Sound => "sound",
                RequestStanding.NeedsConsent => "needs-consent",
                RequestStanding.Refused => "refused",
                _ => null,
            },
            ["heldNow"] = heldNow,
            ["heldUnread"] = heldUnread,
            ["lines"] = lines,
            ["needs"] = Texts(needs.Select(Named)),
            ["refusals"] = Texts(refusals.Select(Named)),
            ["folded"] = folded is null
                ? null
                : new JsonObject
                {
                    ["written"] = Texts(folded.Written),
                    ["deleted"] = Texts(folded.Deleted),
                    ["merged"] = Texts(folded.Merged),
                    ["armRegistry"] = folded.ArmRegistry,
                    ["handed"] = new JsonArray([.. folded.Handed.Select(handed => (JsonNode?)new JsonObject { ["path"] = handed.Path, ["digest"] = handed.Digest })]),
                    ["registries"] = Texts(folded.Registries),
                    ["rows"] = Texts(folded.Rows),
                    ["overCells"] = Texts(folded.OverCells),
                },
            ["planned"] = planned is null
                ? null
                : new JsonObject
                {
                    ["taken"] = Texts(planned.Taken),
                    ["kept"] = Texts(planned.Kept),
                    ["handed"] = Texts(planned.Handed),
                    ["merged"] = Texts(planned.Merged),
                    ["armRegistry"] = planned.ArmRegistry,
                },
        }.ToJsonString(ReportJson.Options);

    /// <summary><paramref name="texts"/> as a document's array of strings, in order.</summary>
    private static JsonArray Texts(IEnumerable<string> texts) => new([.. texts.Select(text => (JsonNode?)text)]);

    /// <summary>An agent's base as reports name it, where none may be recorded.</summary>
    /// <param name="commit">The commit, or null where none is recorded.</param>
    internal static string Base(string? commit) => commit is null ? "unrecorded" : ReportText.Commit(commit);

    /// <summary>A moment as orchestration messages and listings spell one: UTC, to the second.</summary>
    /// <param name="moment">The moment.</param>
    internal static string Moment(DateTimeOffset moment) => moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Changed(AnchorChange change)
    {
        var fields = change.Fields.Count == 0 ? "its cells" : string.Join(", ", change.Fields.Select(Described));
        return change.Moved ? $"changes {fields}, and moves to the {change.To.Name} registry" : $"changes {fields}";
    }

    /// <summary>A changed cell, as a row's line names it: a prose cell with what becomes of its stored text.</summary>
    private static string Described(AnchorFieldChange field)
        => !AnchorCellNames.Text.Contains(field.Field, StringComparer.Ordinal)
            ? field.Field
            : AnchorCellComparison.Fate(field.Before, field.After) switch
            {
                AnchorCellFate.Respaced => $"{field.Field} (respaced)",
                AnchorCellFate.Kept => $"{field.Field} (its stored text kept whole)",
                AnchorCellFate.Filled => $"{field.Field} (filled)",
                AnchorCellFate.Lost => $"{field.Field} (its stored text not kept)",
                _ => field.Field,
            };

    private static string Session(string? session) => session is null ? string.Empty : $", session {session}";
}
