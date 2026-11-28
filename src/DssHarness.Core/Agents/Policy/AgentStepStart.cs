using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// What a step's worktree held untracked as the step's run began - each file git listed there that it neither tracks nor
/// ignores - by which a delete tells the step's own making apart: a path of the worktree git does not track, where this
/// record holds neither it nor anything beneath it, was made since, by the step's own commands, and the step may take it
/// back whatever its <c>writes</c> say (<see cref="AgentPolicy.Delete"/>). Taken once, as the step first starts, and kept in
/// the step's own directory (<see cref="OrchestratorLayout.StepUntrackedFileName"/>), so every later attempt, answer and
/// resume of the step reads the record its first start took - never one taken after its commands ran.
/// </summary>
/// <remarks>
/// A path is held without regard to case on every system: the record only ever keeps a path from being taken back, and a
/// file system that folds case finds the path by any spelling. Git records no directory, so a directory is held where a
/// path it holds is.
/// </remarks>
public sealed class AgentStepStart
{
    /// <summary>What the record is, as a refusal of one that does not read names it.</summary>
    private const string Kind = "the step's record of what its worktree held untracked as it began";

    private readonly HashSet<string> _held;
    private readonly HashSet<string> _holding;

    /// <summary>The record of <paramref name="worktree"/>'s untracked paths, as git named them.</summary>
    /// <param name="worktree">The worktree, absolute.</param>
    /// <param name="untracked">Its untracked files, each relative to it with forward separators.</param>
    internal AgentStepStart(string worktree, IEnumerable<string> untracked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentNullException.ThrowIfNull(untracked);

        Worktree = worktree;
        _held = new HashSet<string>(untracked, StringComparer.OrdinalIgnoreCase);
        _holding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Every directory above a held path, each once: a directory holds a path of the record where it is one of these.
        foreach (var path in _held)
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (!_holding.Add(path[..slash]))
                {
                    break;
                }
            }
        }
    }

    /// <summary>The worktree the record is of, absolute.</summary>
    public string Worktree { get; }

    /// <summary>The untracked files the worktree held, each relative to it with forward separators, in ordinal order.</summary>
    public IReadOnlyList<string> Untracked => [.. _held.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Whether the worktree held <paramref name="relative"/> untracked as the step's run began - the path itself, or for a
    /// directory a path beneath it - in any case.
    /// </summary>
    /// <param name="relative">A path relative to the worktree, with forward separators.</param>
    public bool Holds(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);

        return _held.Contains(relative) || _holding.Contains(relative);
    }

    /// <summary>
    /// The record of what the step's worktree held untracked as its run began, where the step's <c>writes</c> narrow what it
    /// may write in a worktree it writes: read from the step's directory where its first start wrote it, else taken now -
    /// every file git lists untracked there (<see cref="IGitClient.ListWorkingFilesAsync(string, CancellationToken)"/>) -
    /// and written there before the step's agent is started. Null for a step whose writes narrow nothing, and for one that
    /// writes no worktree: no delete of its is narrowed, or none reaches a worktree.
    /// </summary>
    /// <param name="fileSystem">Reads and writes the record.</param>
    /// <param name="git">Lists the worktree's untracked files.</param>
    /// <param name="ground">The step's ground, whose writes and writable roots say whether a record is needed.</param>
    /// <param name="worktree">The step's agent's worktree; null for an agent with none.</param>
    /// <param name="stepDirectory">The step's own directory, which keeps the record.</param>
    /// <param name="comparison">How the platform compares names, the worktree among the writable roots.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">
    /// The record kept does not read as one, or could not be read (<see cref="HarnessExit.Refused"/>,
    /// <see cref="HarnessExit.CommandFailed"/>); or git could not list the worktree's files: nothing was written.
    /// </exception>
    public static async Task<AgentStepStart?> OfAsync(
        IFileSystem fileSystem,
        IGitClient git,
        AgentPolicyGround ground,
        string? worktree,
        string stepDirectory,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDirectory);

        if (ground.Writes is null || worktree is not { Length: > 0 } tree || !ground.WritableRoots.Any(root => PathContainment.AreSame(root, tree, comparison)))
        {
            return null;
        }

        var file = Path.Combine(stepDirectory, OrchestratorLayout.StepUntrackedFileName);

        if (Read(fileSystem, file) is { } kept)
        {
            return new AgentStepStart(tree, kept);
        }

        var untracked = (await git.ListWorkingFilesAsync(tree, cancellationToken).ConfigureAwait(false))
            .Where(listed => listed.Untracked)
            .Select(listed => listed.Name.Text)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        JsonStateFile.WriteLines(fileSystem, file, lines =>
        {
            lines.Write(new Head(untracked.Count));

            foreach (var path in untracked)
            {
                lines.Write(path);
            }
        });

        return new AgentStepStart(tree, untracked);
    }

    /// <summary>The paths the step's directory keeps, where its first start wrote them; null where it wrote none.</summary>
    private static IReadOnlyList<string>? Read(IFileSystem fileSystem, string file)
    {
        try
        {
            if (!fileSystem.FileExists(file))
            {
                return null;
            }

            return JsonStateFile.ReadLines(fileSystem, file, lines =>
            {
                var head = lines.Next<Head>("its head");

                if (head.Paths < 0)
                {
                    throw new JsonException("its head counts fewer than no paths");
                }

                var paths = new List<string>(head.Paths);

                for (var at = 0; at < head.Paths; at++)
                {
                    paths.Add(lines.Next<string>("a path"));
                }

                lines.End();

                return paths;
            });
        }
        catch (JsonException ex)
        {
            throw new HarnessException(HarnessExit.Refused, $"'{file}' is {Kind}, and does not read as one: {ex.Message.TrimEnd('.')}. Nothing was changed: a record is never guessed at.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"'{file}', {Kind}, could not be read: {ex.Message.TrimEnd('.')}. Nothing was changed.");
        }
    }

    /// <summary>The record's first line: how many paths follow it, one to a line.</summary>
    /// <param name="Paths">How many paths follow.</param>
    private sealed record Head(int Paths);
}
