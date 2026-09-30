using RepoHarness.Core.Configuration;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// A repository run through init, with everything committed and one orchestrator, <c>o1</c>, created: what every
/// orchestration test starts from.
/// </summary>
internal sealed class OrchestrationKit
{
    /// <summary>The orchestrator every test works with.</summary>
    public const string Orchestrator = "o1";

    /// <summary>
    /// The worktrees root init writes, an evidence root, and a path budget loose enough for a temporary directory's long
    /// name.
    /// </summary>
    public static readonly WorktreeSettings Settings = new()
    {
        Root = WorktreeSettings.SeededRoot,
        PathBudgetReserve = 5,
        PathBudgetMargin = 2,
        EvidenceRoots = ["evidence"],
    };

    /// <summary><see cref="Settings"/>, with another worktrees root or other evidence roots.</summary>
    public static WorktreeSettings SettingsWith(string? root = null, List<string>? evidenceRoots = null)
        => new()
        {
            Root = root ?? Settings.Root,
            PathBudgetReserve = Settings.PathBudgetReserve,
            PathBudgetMargin = Settings.PathBudgetMargin,
            EvidenceRoots = evidenceRoots ?? Settings.EvidenceRoots,
        };

    private OrchestrationKit(HarnessFactory harness, string main)
    {
        Harness = harness;
        Main = main;
    }

    public HarnessFactory Harness { get; }

    /// <summary>The main checkout.</summary>
    public string Main { get; }

    /// <summary>Where the orchestrator keeps what it and its agents hold.</summary>
    public OrchestratorLayout Layout => OrchestratorLayout.Of(new HarnessLayout(Main, Main), Orchestrator);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A repository holding <c>a.txt</c>, <c>b.txt</c> and <c>docs/x.md</c>, run through init with
    /// <paramref name="settings"/>, ignoring the evidence root, all of it committed, and orchestrator <c>o1</c> created.
    /// </summary>
    public static async Task<OrchestrationKit> PrepareAsync(TempDirectory temp, int? parallel = null, WorktreeSettings? settings = null)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { Worktrees = settings ?? Settings });
        temp.WriteFile("a.txt", "one\n");
        temp.WriteFile("b.txt", "two\n");
        temp.WriteFile(Path.Combine("docs", "x.md"), "x\n");

        // An evidence root holds what git ignores: a measurement is not source.
        File.AppendAllText(temp.Combine(".gitignore"), "/evidence/\n");
        await harness.CommitAllAsync(temp.Path, "files", Token);

        var created = await harness.OrchestratorService.CreateAsync(temp.Path, Orchestrator, "model-a", parallel, session: null, Token);
        Assert.True(created.Succeeded, Describe(created));

        return new OrchestrationKit(harness, temp.Path);
    }

    /// <summary>The directory the orchestrator's agents' worktrees are made in, under the worktrees root.</summary>
    public string Worktrees => WorktreeAddress.Plain(Orchestrator).PathUnder(Path.Combine(Main, Settings.Root));

    /// <summary>Where agent <paramref name="agent"/>'s worktree is.</summary>
    public string Worktree(string agent) => WorktreeAddress.Nested(Orchestrator, agent).PathUnder(Path.Combine(Main, Settings.Root));

    /// <summary>Creates agent <paramref name="agent"/>, failing the test where it is not created, and returns its worktree.</summary>
    public async Task<string> CreateAgentAsync(string agent, bool empty = false, string? session = null)
    {
        var created = await Harness.AgentService.CreateAsync(Main, Orchestrator, agent, "model-b", empty, session, Token);
        Assert.True(created.Succeeded, Describe(created));
        return Worktree(agent);
    }

    public Task<CommandOutcome> FoldAsync(string agent, bool apply, params string[] settled)
        => FoldAsync(agent, apply, new FoldAllowances { Settled = settled });

    public Task<CommandOutcome> FoldAsync(string agent, bool apply, FoldAllowances allowances)
        => Harness.AgentService.FoldAsync(Main, Orchestrator, agent, allowances, apply, Token);

    public Task<CommandOutcome> DeleteAsync(string agent, bool apply, bool discard = false, params string[] settled)
        => DeleteAsync(agent, apply, new FoldAllowances { Settled = settled }, discard);

    public Task<CommandOutcome> DeleteAsync(string agent, bool apply, FoldAllowances allowances, bool discard = false)
        => Harness.AgentService.DeleteAsync(Main, Orchestrator, agent, allowances, apply, discard, Token);

    /// <summary>A fold let make the rows <paramref name="ids"/>, as --new names them.</summary>
    public static FoldAllowances Making(params string[] ids) => new() { New = ids };

    /// <summary>The agent's record.</summary>
    public AgentRecord Record(string agent) => Harness.OrchestrationStore.ReadAgent(Layout, agent)!;

    /// <summary>Writes <paramref name="text"/> to <paramref name="relative"/> under <paramref name="root"/>, making its directory.</summary>
    public static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

    /// <summary>Files a row for <paramref name="agent"/>, one file for each cell given.</summary>
    public void FileRow(string agent, string id, IReadOnlyDictionary<string, string> cells)
        => WriteRow(Layout.RowsDirectory(agent), id, [.. cells.Select(cell => (cell.Key, cell.Value))]);

    /// <summary>Files a row in <paramref name="rowsDirectory"/>: a directory named <paramref name="id"/>, holding a file for each cell given.</summary>
    public static void WriteRow(string rowsDirectory, string id, params (string Cell, string Text)[] cells)
    {
        foreach (var (cell, text) in cells)
        {
            Write(Path.Combine(rowsDirectory, id), cell + AgentRows.CellExtension, text);
        }
    }

    /// <summary>An outcome's message and every detail, for a failed assertion to show.</summary>
    public static string Describe(CommandOutcome outcome)
        => string.Join(Environment.NewLine, [$"{outcome.ExitCode}: {outcome.Message}", .. outcome.Details ?? []]);

    /// <summary>Runs git in <paramref name="directory"/>, failing the test when git fails.</summary>
    public Task GitAsync(string directory, params string[] arguments) => Harness.RunGitAsync(directory, arguments, Token);
}
