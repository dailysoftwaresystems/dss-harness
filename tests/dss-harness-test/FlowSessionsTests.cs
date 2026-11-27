using System.Text.Json.Nodes;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// One unfinished run at a time: the repository's one section holds the scan for unfinished runs, the r&lt;N&gt;
/// allocation and the run record's creation (a name that is taken refused, naming its record and the way on with that
/// one), with the run's first status written there; the test is made again at the start of every session, leaving out
/// the run itself; and the path budget at a run's start counts each copy and worker its work keeps inside its trees.
/// </summary>
public sealed class FlowSessionsTests
{
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    private static WorkflowDefinition Definition()
        => Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                dependsOn: [one]
            """);

    /// <summary>The run's owner, released as the session that made the run releases it at its end.</summary>
    private static void Released(HarnessFactory harness, OrchestratorLayout run)
        => new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Release(run);

    /// <summary>
    /// The ground a start in <paramref name="layout"/> decides its file inputs on, as the one builder of an agent's ground
    /// builds it under a configuration that restricts nothing, on a machine whose Claude Code directories and credential
    /// places <paramref name="claudeDirectories"/> and <paramref name="credentialPlaces"/> name - none unless a test says.
    /// </summary>
    internal static DssHarness.Core.Agents.Policy.AgentPolicyGround InputGround(
        HarnessLayout layout,
        HarnessConfig? config = null,
        IReadOnlyList<string>? claudeDirectories = null,
        IReadOnlyList<DssHarness.Core.Agents.Isolation.HiddenPlace>? credentialPlaces = null)
        => DssHarness.Core.Agents.Policy.AgentStepGround.OfFileInputs(config ?? new HarnessConfig(), layout.MainCheckoutRoot, claudeDirectories ?? [], credentialPlaces ?? []);

    /// <summary>
    /// A run starts with one more than the highest r&lt;N&gt;, its record written, its first status - every step of the
    /// definition NotStarted, the run Running - its owner: this process, claimed before the section is left - and its
    /// temp, artifacts and inputs directories, which hold its launches' temporary directories, its artifacts and the copies
    /// of the files its own file inputs named for its life.
    /// </summary>
    [Fact]
    public async Task ARunStartsWithTheNextName_ItsRecordItsFirstStatusAndItsOwner()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);

        var (run, context) = await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);

        Assert.Equal("r1", run.Name);
        Assert.Equal("r1", context.Run);
        Assert.Equal(Environment.ProcessId, harness.OrchestrationStore.ReadOwner(run)!.ProcessId);
        Assert.True(harness.FileSystem.DirectoryExists(run.TempDirectory), $"{run.TempDirectory} is where the run's launches keep their temporary directories");
        Assert.True(harness.FileSystem.DirectoryExists(run.ArtifactsDirectory), $"{run.ArtifactsDirectory} is where the run keeps its artifacts");
        Assert.True(harness.FileSystem.DirectoryExists(run.InputsDirectory), $"{run.InputsDirectory} is where the run keeps its file inputs' copies");

        var record = harness.OrchestrationStore.ReadOrchestrator(run);

        Assert.NotNull(record);
        Assert.Equal("flow-engine", record.Model);
        Assert.Equal(2, record.Parallel);

        var status = harness.OrchestrationStore.ReadWorkflowStatus(run);

        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.Equal(["one", "two"], status.Steps.Keys.Order(StringComparer.Ordinal));
        Assert.All(status.Steps.Values, step => Assert.Equal("NotStarted", step.Status));

        // The next run takes one more than the highest name there, whatever lies between.
        Released(harness, run);
        Directory.CreateDirectory(Path.Combine(layout.OrchestratorsDirectory, "r7"));
        Directory.Delete(run.Directory, recursive: true);

        Assert.Equal("r8", (await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token)).Run.Name);
    }

    /// <summary>A <c>--run</c> name that is taken is refused, naming its record and the way on with that one, by the one sentence create-orchestrator says it with.</summary>
    [Fact]
    public async Task ARunNameThatIsTakenIsRefused_NamingItsRecordAndTheWayOn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);

        var (mine, _) = await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "mine", cancellationToken: Token);
        Released(harness, mine);

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "mine", cancellationToken: Token));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Equal(OrchestrationReports.RunExists(mine), raised.Message);
        Assert.Equal($"run 'mine' exists - '{mine.RecordFile}' is its record; --run names a new run, and resume-orchestrator mine goes on with that one. Nothing was changed.", raised.Message);
    }

    /// <summary>A <c>--run</c> name whose directory is there holding no run's record is refused: a run is never made over what something else left there.</summary>
    [Fact]
    public async Task ARunNameWhoseDirectoryHoldsNoRecordIsRefused()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var left = OrchestratorLayout.Of(layout, "left");

        Directory.CreateDirectory(left.Directory);
        File.WriteAllText(left.DefinitionFile, "{}");

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "left", cancellationToken: Token));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Equal(
            $"'{left.Directory}' is there and holds no run's record ('{left.RecordFile}'); --run names a new run, and a run is never made over what something else left there. "
            + "Nothing was changed: remove it, or name the run otherwise.",
            raised.Message);
        Assert.Equal("{}", File.ReadAllText(left.DefinitionFile));
    }

    /// <summary>A start that its refusal check refuses - its path budget - writes nothing: the name is chosen, and nothing of a run is made by it.</summary>
    [Fact]
    public async Task AStartItsCheckRefuses_WritesNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), refusal: name => $"run '{name}' is too deep", cancellationToken: Token));

        Assert.Equal("run 'r1' is too deep", raised.Message);
        Assert.False(Directory.Exists(OrchestratorLayout.Of(layout, "r1").Directory));
        Assert.False(File.Exists(OrchestratorLayout.Of(layout, "r1").OwnerFile));
    }

    /// <summary>A workflow whose run takes a plan as a file, a spec as a file it may leave empty, and a request in words.</summary>
    private static WorkflowDefinition WithFiles()
        => Parse("""
            name: flow
            inputs:
              - name: plan
                kind: file
              - name: spec
                kind: file
                default: ''
              - name: request
            steps:
              - name: one
                type: orient
            """);

    /// <summary>
    /// A run's own file input is held at the start to a file inside the repository - one nothing ever committed, in a
    /// directory that is no repository at all - and its bytes are copied into the run's inputs, under the input's name and
    /// the file's own, before the run's record is written: the context keeps the values as they were given, and what the
    /// run's steps read for the input is the copy's full path. A file input left empty copies nothing and reads empty.
    /// </summary>
    [Fact]
    public async Task ARunsFileInput_IsCopiedIntoTheRunAsItStarts_ItsValueKeptAsGiven_AndItsStepsReadTheCopy()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var definition = WithFiles();
        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "notes/plan.md", ["request"] = "build it" };

        temp.WriteFile(Path.Combine("notes", "plan.md"), "the plan as it was given\n");

        var (run, context) = await sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), inputs: given, cancellationToken: Token);
        var copy = Path.Combine(run.Directory, HarnessLayout.OrchestratorInputsDirectoryName, "plan", "plan.md");

        Assert.Equal(Path.Combine(run.Directory, "inputs"), run.InputsDirectory);
        Assert.Equal(copy, run.InputCopy("plan", "notes/plan.md"));
        Assert.Equal([copy], Directory.GetFiles(run.InputsDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal("the plan as it was given\n", File.ReadAllText(copy));
        Assert.Equal(given, context.Inputs);
        Assert.True(context.InputCopies);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = copy, ["spec"] = string.Empty, ["request"] = "build it" },
            FlowInputs.AsRead(definition, context.Inputs, run, context.InputCopies));
    }

    /// <summary>
    /// A run's own file input that names no file inside the repository an agent may read refuses the start, naming the
    /// input and the path and saying that no run was started, and nothing of the run is made - its directory, its record,
    /// its owner: a path that is not there, one that is a directory, an absolute one outside the repository, one that
    /// climbs out of it by '..', and one in a credential slot of the tool, which no agent of a run reads and so no copy may
    /// hand one - each refused as an agent's read of it is, in the deciders' own words.
    /// </summary>
    [Fact]
    public async Task AFileInputThatNamesNoFileInsideTheRepository_RefusesTheStart_NothingOfTheRunMade()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var key = Path.Combine(temp.Path, ".harness-config", "runner", ".secrets", "key");

        temp.WriteFile(Path.Combine("notes", "kept.md"), "a file beside\n");
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".secrets", "key"), "the secret\n");
        var absolute = outside.WriteFile("plan.md", "a plan outside\n");

        foreach (var (value, why) in new[]
        {
            ("notes/plan.md", $"and '{Path.Combine(temp.Path, "notes", "plan.md")}' is not there"),
            ("notes", $"and '{Path.Combine(temp.Path, "notes")}' is a directory"),
            (absolute, ReadRefused($"'{absolute}' is outside the roots of this agent: {temp.Path}")),
            ("../plan.md", ReadRefused($"'{Path.Combine(Path.GetDirectoryName(temp.Path)!, "plan.md")}' is outside the roots of this agent: {temp.Path}")),
            (".harness-config/runner/.secrets/key", ReadRefused($"'{key}' is a credential slot of this tool: no file tool of an agent reads or writes one")),
        })
        {
            await RefusedAsync(sessions, layout, value, why);
        }

        Assert.Equal("a plan outside\n", File.ReadAllText(absolute));
    }

    /// <summary>
    /// A file input whose path leads out of the repository through a link inside it - a directory on its way that is a
    /// link to a place outside, or one into a credential slot of the tool - refuses the start as an agent's read of it is
    /// refused, by where it leads, and nothing of the run is made: the link is followed to decide, never to copy what lies
    /// beyond it, for every agent of the run would read the copy.
    /// </summary>
    [Fact]
    public async Task AFileInputWhoseDirectoryOnItsWayIsALinkLeadingOut_RefusesTheStart_ByWhereItLeads()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var secrets = Path.Combine(temp.Path, ".harness-config", "runner", ".secrets");

        outside.WriteFile("plan.md", "a plan outside\n");
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".secrets", "key"), "the secret\n");
        Directory.CreateDirectory(temp.Combine("notes"));
        TestLinks.DirectoryLink(temp.Combine("notes", "away"), outside.Path);
        TestLinks.DirectoryLink(temp.Combine("notes", "keys"), secrets);

        await RefusedAsync(
            sessions,
            layout,
            "notes/away/plan.md",
            ReadRefused($"'{temp.Combine("notes", "away", "plan.md")}' leads to '{Path.Combine(outside.Path, "plan.md")}', outside the roots of this agent: {temp.Path}"));
        await RefusedAsync(
            sessions,
            layout,
            "notes/keys/key",
            ReadRefused($"'{Path.Combine(secrets, "key")}' is a credential slot of this tool: no file tool of an agent reads or writes one"));
    }

    /// <summary>
    /// A file input that is itself a link inside the repository to a file outside it refuses the start by where it leads,
    /// nothing of the run made - and the file it leads to is never copied, for every agent of the run would read the copy.
    /// Where this machine does not let this user make a file link the test is skipped: a directory's link on the way, which
    /// needs no privilege, is held by its own test.
    /// </summary>
    [Fact]
    public async Task AFileInputThatIsALinkLeadingOut_RefusesTheStart_ByWhereItLeads()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var secret = outside.WriteFile("id_rsa", "the secret outside\n");
        var plan = temp.Combine("notes", "plan.md");

        Directory.CreateDirectory(temp.Combine("notes"));
        TestLinks.OrSkip(() => File.CreateSymbolicLink(plan, secret));

        await RefusedAsync(sessions, layout, "notes/plan.md", ReadRefused($"'{plan}' leads to '{secret}', outside the roots of this agent: {temp.Path}"));
    }

    /// <summary>
    /// A file input that reaches its file inside the repository through a link that stays inside it - a directory on its
    /// way that is a link to another of the repository's - is copied as any other, the bytes of the file it leads to; and
    /// one given by its absolute path inside the repository is copied too, its value kept as given and its copy named by the
    /// file's own name.
    /// </summary>
    [Fact]
    public async Task AFileInputThroughALinkThatStaysInside_OrGivenAbsoluteInsideTheRepository_IsCopied()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var definition = WithFiles();
        var spec = temp.WriteFile(Path.Combine("docs", "spec.md"), "the spec, given whole\n");
        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "notes/plan.md", ["spec"] = spec };

        temp.WriteFile(Path.Combine("real", "plan.md"), "the plan, through a link\n");
        TestLinks.DirectoryLink(temp.Combine("notes"), temp.Combine("real"));

        var (run, context) = await sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), inputs: given, cancellationToken: Token);
        var plan = run.InputCopy("plan", "notes/plan.md");
        var copied = run.InputCopy("spec", spec);

        Assert.Equal(Path.Combine(run.InputsDirectory, "spec", "spec.md"), copied);
        Assert.Equal([plan, copied], Directory.GetFiles(run.InputsDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal(["the plan, through a link\n", "the spec, given whole\n"], [File.ReadAllText(plan), File.ReadAllText(copied)]);
        Assert.Equal(given, context.Inputs);
    }

    /// <summary>
    /// A file input whose directory on its way is made a link out of the repository after its path was decided and before
    /// it is opened - swapped in between the check and the act - keeps nothing of where the link leads: the file is read
    /// through itself as it was opened, where it stands decided again before a byte passes, so the start is refused naming
    /// where the open file stood, nothing of the run is made and the file outside is left as it was.
    /// </summary>
    [Fact]
    public async Task AFileInputWhoseWayIsMadeALinkAtItsOpen_RefusesTheStart_WhereTheOpenFileStands()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var notes = temp.Combine("notes");
        var sessions = new FlowSessions(harness.OrchestrationStore, new LinkedAtOpen(harness.FileSystem, notes, outside.Path), harness.Identity, harness.GitClient, harness.Platform);
        var opened = outside.WriteFile("plan.md", "the plan outside\n");
        var plan = temp.WriteFile(Path.Combine("notes", "plan.md"), "the plan as it was decided\n");

        await RefusedAsync(
            sessions,
            layout,
            "notes/plan.md",
            ReadRefused($"'{plan}' was opened as '{opened}', where a link put on its way after it was decided leads; nothing was read: '{opened}' is where the open file stands, outside the roots of this agent: {temp.Path}"));
        Assert.Equal("the plan outside\n", File.ReadAllText(opened));
    }

    /// <summary>
    /// A file input whose copy cannot be made as the run starts - its bytes first read there, a full disk refusing the
    /// write - refuses the start, naming the input and why and saying no run was started, and takes back what the start had
    /// made of the run: no directory is left holding no run's record, so a start of that name made after it is made.
    /// </summary>
    [Fact]
    public async Task AFileInputWhoseCopyCannotBeMade_RefusesTheStart_AndTakesBackWhatItMade()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var run = OrchestratorLayout.Of(layout, "r1");
        var sessions = new FlowSessions(harness.OrchestrationStore, new FullUnder(harness.FileSystem, run.InputsDirectory), harness.Identity, harness.GitClient, harness.Platform);
        var given = new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "notes/plan.md" };

        temp.WriteFile(Path.Combine("notes", "plan.md"), "the plan as it was given\n");

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, WithFiles(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), inputs: given, cancellationToken: Token));

        Assert.Equal(
            (HarnessExit.Refused, $"input 'plan' is 'notes/plan.md', and the copy the run keeps of it could not be made: {FullUnder.Said}; what the start had made of run 'r1' was removed. No run was started."),
            (raised.ExitCode, raised.Message));
        Assert.False(Directory.Exists(run.Directory), "the start left a directory of the run it did not make");
        Assert.False(File.Exists(run.OwnerFile));

        // Nothing stands in the way of the next start of that name.
        var (made, _) = await new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform)
            .CreateAsync(layout, WithFiles(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "r1", inputs: given, cancellationToken: Token);

        Assert.Equal("the plan as it was given\n", File.ReadAllText(made.InputCopy("plan", "notes/plan.md")));
    }

    /// <summary>
    /// A start whose file input's copy cannot be made, and whose directory then cannot be taken back, says so in its
    /// refusal - the directory it left and why, and what to do - never leaving it unsaid.
    /// </summary>
    [Fact]
    public async Task AFileInputWhoseCopyCannotBeMade_WhereWhatTheStartMadeCannotBeRemoved_SaysWhatItLeft()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var run = OrchestratorLayout.Of(layout, "r1");
        var sessions = new FlowSessions(harness.OrchestrationStore, new FullUnder(harness.FileSystem, run.InputsDirectory, removes: false), harness.Identity, harness.GitClient, harness.Platform);

        temp.WriteFile(Path.Combine("notes", "plan.md"), "the plan as it was given\n");

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(
            layout,
            WithFiles(),
            () => ChildSweep.None,
            FlowService.RunCommand,
            InputGround(layout),
            inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "notes/plan.md" },
            cancellationToken: Token));

        Assert.Equal(
            (HarnessExit.Refused, $"input 'plan' is 'notes/plan.md', and the copy the run keeps of it could not be made: {FullUnder.Said}; what the start had made of run 'r1', '{run.Directory}', could not be removed ({FullUnder.Held}): remove it, for a run of that name is refused while it stands. No run was started."),
            (raised.ExitCode, raised.Message));
        Assert.True(Directory.Exists(run.Directory));
        Assert.Null(harness.OrchestrationStore.ReadOrchestrator(run));
    }

    /// <summary>
    /// A start whose run's places cannot all be made - its temp directory refused - takes back what it had made of the run
    /// and is refused saying which run's places and why, and that no run was started: no directory is left holding no
    /// run's record.
    /// </summary>
    [Fact]
    public async Task AStartWhoseRunsPlacesCannotBeMade_IsRefused_AndTakesBackWhatItMade()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var run = OrchestratorLayout.Of(layout, "r1");
        var sessions = new FlowSessions(harness.OrchestrationStore, new UnmadeDirectory(harness.FileSystem, run.TempDirectory), harness.Identity, harness.GitClient, harness.Platform);

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token));

        Assert.Equal(
            (HarnessExit.Refused, $"the places of run 'r1' could not be made: {UnmadeDirectory.Said}; what the start had made of run 'r1' was removed. No run was started."),
            (raised.ExitCode, raised.Message));
        Assert.False(Directory.Exists(run.Directory), "the start left a directory of the run it did not make");
        Assert.False(File.Exists(run.OwnerFile));
    }

    /// <summary>A file system that refuses to make the directory <paramref name="refused"/>, as one denied access does.</summary>
    private sealed class UnmadeDirectory(DssHarness.Core.FileSystem.IFileSystem inner, string refused) : PassThroughFileSystem(inner)
    {
        /// <summary>What the refusal says.</summary>
        public const string Said = "Access to the path is denied";

        public override void CreateDirectory(string path)
        {
            if (string.Equals(path, refused, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"{Said}.");
            }

            base.CreateDirectory(path);
        }
    }

    /// <summary>
    /// A file system whose every whole write beneath <paramref name="under"/> fails as a full disk's does, and whose removal
    /// of a directory fails as one held open does where <paramref name="removes"/> is false.
    /// </summary>
    private sealed class FullUnder(DssHarness.Core.FileSystem.IFileSystem inner, string under, bool removes = true) : PassThroughFileSystem(inner)
    {
        /// <summary>What the full disk says.</summary>
        public const string Said = "There is not enough space on the disk";

        /// <summary>What a directory held open says.</summary>
        public const string Held = "the directory is held open by another process";

        public override void WriteAtomic(string path, Action<Stream> write)
        {
            if (path.StartsWith(under + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new IOException($"{Said}.");
            }

            base.WriteAtomic(path, write);
        }

        public override void DeleteDirectory(string path)
        {
            if (!removes)
            {
                throw new IOException($"{Held}.");
            }

            base.DeleteDirectory(path);
        }
    }

    /// <summary>The refusal's clause for a file input an agent's read of which is refused for <paramref name="reason"/>.</summary>
    private static string ReadRefused(string reason) => $"which is refused as an agent's read of it would be: {reason}";

    /// <summary>
    /// Starts a run whose input <c>plan</c> is <paramref name="value"/> and holds it refused (13) for <paramref name="why"/>,
    /// naming the input, the path and the repository's root and saying that no run was started, with nothing of the run made;
    /// decided on <paramref name="ground"/>, or the ground of a configuration that restricts nothing where none is given.
    /// </summary>
    private static async Task RefusedAsync(FlowSessions sessions, HarnessLayout layout, string value, string why, DssHarness.Core.Agents.Policy.AgentPolicyGround? ground = null)
    {
        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(
            layout,
            WithFiles(),
            () => ChildSweep.None,
            FlowService.RunCommand,
            ground ?? InputGround(layout),
            inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = value },
            cancellationToken: Token));

        Assert.Equal(
            (HarnessExit.Refused, $"input 'plan' is '{value}', {why}; a file input names a file inside the repository that an agent may read, by its path from the repository's root ('{layout.MainCheckoutRoot}') or an absolute one inside it, and the run keeps a copy of it from its start. No run was started."),
            (raised.ExitCode, raised.Message));
        Assert.False(Directory.Exists(OrchestratorLayout.Of(layout, "r1").Directory), $"'{value}' made nothing of the run");
        Assert.False(File.Exists(OrchestratorLayout.Of(layout, "r1").OwnerFile));
    }

    /// <summary>A session is refused while another run of the repository is unfinished over a live owner, and the refusal names the run and the ways on with it.</summary>
    [Fact]
    public async Task ASessionIsRefusedWhileAnotherRuns_OverALiveOwner()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);

        // The run's engine holds it: its owner was claimed with it.
        _ = await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "second", cancellationToken: Token));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Contains("run 'r1' of this repository is unfinished (running; owned by a live engine)", raised.Message, StringComparison.Ordinal);
        Assert.Contains("resume-orchestrator r1 or answer-orchestrator r1 goes on with it, delete-orchestrator r1 ends it", raised.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A paused run whose agents still hold worktrees whose work is folded is unfinished; one whose agents hold none is not -
    /// an agent that only reads has no work in the tree, its own tree there or not.
    /// </summary>
    [Fact]
    public async Task APausedRunIsUnfinishedOnlyWhereItsAgentsHoldWorktrees()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var definition = Definition();

        var (other, context) = await sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);

        // Its session has ended: nothing holds it but its records.
        Released(harness, other);

        // The run pauses with an agent still open: its state file says Paused, and its agent holds a worktree.
        using (var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, other, context))
        {
            var coordinator = TestAdvice.Coordinator(definition, new Runner(), new Facts(), records: records);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);

            harness.OrchestrationStore.WriteAgent(
                other,
                new AgentRecord
                {
                    Kind = AgentRecord.KindName,
                    Name = "a1",
                    Orchestrator = other.Name,
                    Model = "m",
                    CreatedAt = DateTimeOffset.UtcNow,
                    State = AgentStates.Live,
                    Worktree = "r1/a1",
                    WorktreesRoot = ".harness-config/worktrees",
                });
        }

        // The run's own status says Paused - written by hand as a pause would have, for the test is of the scan, not of the pause.
        var flow = harness.OrchestrationStore.ReadFlow(other)!;
        harness.OrchestrationStore.WriteFlow(other, flow with { Status = "Paused" });

        var held = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "second", cancellationToken: Token));

        Assert.Contains("paused; 1 agent with worktree still open", held.Message, StringComparison.Ordinal);

        // The agent is one that only reads, its tree of its own still there: nothing in it is ever folded, so the run holds
        // nothing in the tree, and a second run may start.
        var agent = harness.OrchestrationStore.ReadAgent(other, "a1")!;
        harness.OrchestrationStore.WriteAgent(other, agent with { Access = AgentRecord.AccessRead, Tree = AgentRecord.TreeRepository });

        var second = await sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "second", cancellationToken: Token);

        Assert.Equal("second", second.Run.Name);

        // One an older tool wrote, with no tree, holds nothing either.
        harness.OrchestrationStore.WriteAgent(other, agent with { Access = AgentRecord.AccessRead, Worktree = null, WorktreesRoot = null });
        Released(harness, second.Run);

        var third = await sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "third", cancellationToken: Token);

        Assert.Equal("third", third.Run.Name);
    }

    /// <summary>
    /// The test is made at the start of every session - a resume, an answer alike - and leaves out the run the session
    /// is for; the session's owner is claimed in the same section, and a stop asked of an engine that ended before it
    /// took it is taken back, so it reaches no later session.
    /// </summary>
    [Fact]
    public async Task TheTestIsMadeAtTheStartOfEverySession_LeavingOutTheRunItself()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);

        var (first, _) = await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);
        Released(harness, first);
        var (second, _) = await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);
        Released(harness, second);

        // While the first runs, a session of the second is refused.
        _ = new FlowRunOwner(harness.OrchestrationStore, harness.Identity).Claim(first);

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.ClaimAsync(layout, second, () => ChildSweep.None, FlowService.ResumeCommand, Token));

        Assert.Contains($"run '{first.Name}' of this repository is unfinished", raised.Message, StringComparison.Ordinal);
        Assert.Null(harness.OrchestrationStore.ReadOwner(second));

        // Once its engine has left, a session of the first is not refused - the test leaves out the run it is for - and
        // a stop asked of the engine that left reaches this session no more.
        Released(harness, first);
        harness.OrchestrationStore.WriteStopFile(first, new FlowStopFile(1, Stop: new FlowStopStop(DateTimeOffset.UtcNow)));

        Assert.Null(await sessions.ClaimAsync(layout, first, () => ChildSweep.None, FlowService.ResumeCommand, Token));
        Assert.Equal(Environment.ProcessId, harness.OrchestrationStore.ReadOwner(first)!.ProcessId);
        Assert.Null(harness.OrchestrationStore.ReadStopFile(first));
    }

    /// <summary>
    /// A session's claim marks the run's state as its engine's start before it writes the owner, once no live owner holds
    /// the run: what the session before left the state saying of its engine - the stop it ended with, the pause it was
    /// left under - stands as written, the run's record of where it stood, and a reader that finds the owner live reads its
    /// engine as starting - never stopping, nor recording what a pause left - so a question still owing its recommendation
    /// is named with the start's own step, its answer taken as the start seeks it. A claim a live owner refuses marks
    /// nothing, and a claim let go before its session wrote anything takes its mark back, leaving the state as it found it.
    /// </summary>
    [Fact]
    public async Task ASessionsClaim_MarksTheRunStartingBeforeItsOwner_AndItsReleaseTakesTheMarkBack()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var files = new WriteOrder(harness.FileSystem);
        var store = new OrchestrationStore(files, harness.Output);
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(store, files, harness.Identity, harness.GitClient, harness.Platform);
        var definition = Definition();
        var (run, context) = await sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);
        var owner = new FlowRunOwner(store, harness.Identity);
        owner.Release(run);

        using (var records = new FlowRunRecords(files, store, run, context))
        {
            _ = await TestAdvice.Coordinator(definition, new Runner(), new Facts(), records: records).RunAsync(Token);
        }

        // The state as a session that stopped once its pause was complete leaves it.
        var at = DateTimeOffset.UnixEpoch;
        var left = store.ReadFlow(run)! with { Status = "Paused", Pause = new FlowPauseRecord("command", true, at, [], at), StoppingSince = at };
        store.WriteFlow(run, left);
        var before = File.ReadAllText(run.FlowFile);

        // A live owner refuses the claim, and nothing is marked.
        _ = owner.Claim(run);
        files.Written.Clear();

        _ = await Assert.ThrowsAsync<HarnessException>(() => sessions.ClaimAsync(layout, run, () => ChildSweep.None, FlowService.ResumeCommand, Token));
        Assert.Empty(files.Written);

        owner.Release(run);
        Assert.Equal(before, File.ReadAllText(run.FlowFile));
        files.Written.Clear();

        Assert.Null(await sessions.ClaimAsync(layout, run, () => ChildSweep.None, FlowService.ResumeCommand, Token));

        var marked = store.ReadFlow(run)!;
        var engine = OrchestratorService.EngineOf(store, owner, run, marked);
        var deferred = new FlowQuestionEntry(
            "q1",
            "open",
            "pause",
            at,
            [],
            [new FlowQuestionOption("stay", "it stays"), new FlowQuestionOption("go", "it goes")],
            [],
            Recommendation: new FlowRecommendation("deferred", Why: "the run was stopped once its pause was complete: the recommendation is sought when the run is resumed with this question still open"));

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(left with { Starting = true }, DssHarness.Core.FileSystem.JsonStateFile.Options),
            System.Text.Json.JsonSerializer.Serialize(marked, DssHarness.Core.FileSystem.JsonStateFile.Options));
        Assert.Equal([Path.GetFullPath(run.FlowFile), Path.GetFullPath(run.OwnerFile)], files.Written);
        Assert.Equal((FlowEngineState.Starting, $"its engine (process {Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}) is starting"), (engine.State, engine.Said));
        Assert.Equal(FlowNextStep.AnswerWhileAdvised, FlowAnswerRules.NextStepOf(deferred, engine));

        // The session never wrote: its release takes the mark back, and the state is as the claim found it.
        owner.Release(run);

        Assert.Equal(before, File.ReadAllText(run.FlowFile));
        Assert.Null(store.ReadOwner(run));
    }

    /// <summary>A file system that keeps, in order, every file written whole.</summary>
    private sealed class WriteOrder(DssHarness.Core.FileSystem.IFileSystem inner) : PassThroughFileSystem(inner)
    {
        /// <summary>Every file written whole, by its full path, in the order written.</summary>
        public List<string> Written { get; } = [];

        public override void WriteAtomic(string path, Action<Stream> write)
        {
            base.WriteAtomic(path, write);
            Written.Add(Path.GetFullPath(path));
        }
    }

    /// <summary>
    /// What another run holds that cannot be told - an agent whose record does not read - refuses a session as work in
    /// the tree does, by a sentence that says so: it is never read as nothing.
    /// </summary>
    [Fact]
    public async Task AnotherRunsUnreadableAgent_RefusesBySayingSo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var definition = Definition();

        var (other, context) = await sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);
        Released(harness, other);

        using (var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, other, context))
        {
            _ = await TestAdvice.Coordinator(definition, new Runner(), new Facts(), records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        harness.OrchestrationStore.WriteFlow(other, harness.OrchestrationStore.ReadFlow(other)! with { Status = "Paused" });
        Directory.CreateDirectory(other.AgentDirectory("a1"));
        File.WriteAllText(other.AgentRecordFile("a1"), "not a record");

        var raised = await Assert.ThrowsAsync<HarnessException>(() => sessions.CreateAsync(layout, definition, () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "second", cancellationToken: Token));

        Assert.StartsWith("run 'r1' of this repository is unfinished (paused, and whether its agent 'a1' holds a worktree cannot be told: ", raised.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two starts at once on two threads - the section is a machine-wide one, which lets the same thread in twice - make
    /// one run: the other is refused, naming it.
    /// </summary>
    [Fact]
    public void TwoStartsAtOnce_OneIsRefusedNamingTheOther()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var results = AtOnce(
            async () => (await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "left", cancellationToken: Token)).Run.Name,
            async () => (await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), wantedName: "right", cancellationToken: Token)).Run.Name);

        var made = Assert.Single(results, result => result.Made is not null).Made!;
        var refused = Assert.Single(results, result => result.Refused is not null).Refused!;

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith($"run '{made}' of this repository is unfinished (running; owned by a live engine)", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Two sessions of one run at once, on two threads, claim it once: the other is refused by the live owner the first wrote.</summary>
    [Fact]
    public async Task TwoResumesAtOnce_OnlyOneClaimsTheRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient, harness.Platform);
        var (run, _) = await sessions.CreateAsync(layout, Definition(), () => ChildSweep.None, FlowService.RunCommand, InputGround(layout), cancellationToken: Token);
        Released(harness, run);

        var results = AtOnce(
            async () => await sessions.ClaimAsync(layout, run, () => ChildSweep.None, FlowService.ResumeCommand, Token) ?? "claimed",
            async () => await sessions.ClaimAsync(layout, run, () => ChildSweep.None, FlowService.ResumeCommand, Token) ?? "claimed");

        Assert.Single(results, result => result.Made == "claimed");
        Assert.StartsWith("run 'r1' is owned by a live engine (process ", Assert.Single(results, result => result.Refused is not null).Refused!.Message, StringComparison.Ordinal);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Runs two pieces of work on two threads of their own, let go at one moment, and says what each made or why it was
    /// refused: each thread waits on its own work, so neither ever runs on the other's.
    /// </summary>
    private static IReadOnlyList<(string? Made, HarnessException? Refused)> AtOnce(Func<Task<string>> left, Func<Task<string>> right)
    {
        using var barrier = new Barrier(2);
        var results = new (string? Made, HarnessException? Refused)[2];
        var works = new[] { left, right };
        var threads = Enumerable.Range(0, 2).Select(at => new Thread(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));

            try
            {
                results[at] = (works[at]().GetAwaiter().GetResult(), null);
            }
            catch (HarnessException ex)
            {
                results[at] = (null, ex);
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        Assert.All(threads, thread => Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "a thread did not end within a minute"));

        return results;
    }

    /// <summary>
    /// The path budget at a run's start counts each copy and worker its work keeps inside its deepest tree, as a copy's
    /// own check counts it - its build below it, once - and the longest of them is what it is held to.
    /// </summary>
    [Fact]
    public void ThePathBudgetAtAStartCountsEachCopyAndWorker_AsACopysOwnCheckDoes()
    {
        var budget = new PathBudget(new HostPlatform());
        var settings = new WorktreeSettings { PathLimit = 600 };
        var root = Path.Combine(Path.GetTempPath(), "trees", "r1");
        var tree = Path.Combine(root, "a99");
        var plain = new VariantKey("x64", "gcc", "Debug", null);
        var longer = new VariantKey("x64", "gcc-with-a-much-longer-toolchain-name", "RelWithDebInfo", "address");

        var copied = FlowRunPaths.Check(budget, settings, root, "a99", [plain], []);
        var copy = DssHarness.Core.Legs.LegCopies.PathOf(tree, plain);

        Assert.True(copied.IsWithinBudget);
        Assert.Equal(DssHarness.Core.Legs.CopyPathBudget.Check(budget, settings, copy, plain.DirectoryUnder(copy), settings.PathBudgetReserve).RequiredLength, copied.RequiredLength);

        // A worker is reckoned beside the copy, and the longest variant is what the run is held to.
        var swept = FlowRunPaths.Check(budget, settings, root, "a99", [plain], [plain]);
        var workers = DssHarness.Core.Mutations.MutationWorkers.Of(tree, plain);

        Assert.Equal(
            Math.Max(copied.RequiredLength, DssHarness.Core.Legs.CopyPathBudget.Check(budget, settings, workers.PathOf(1), workers.BuildDirectoryOf(1), settings.PathBudgetReserve).RequiredLength),
            swept.RequiredLength);
        Assert.True(FlowRunPaths.Check(budget, settings, root, "a99", [plain, longer], []).RequiredLength > copied.RequiredLength, "the longer variant is the one reckoned");

        var over = FlowRunPaths.Check(budget, new WorktreeSettings { PathLimit = copied.RequiredLength }, root, "a99", [plain], []);

        Assert.False(over.IsWithinBudget);
    }

    /// <summary>
    /// A run's start is held to the path budget of the legs this machine runs that its leg steps may name, under its
    /// deepest agent's worktree: refused by a sentence naming that worktree where it passes the limit. A leg that works
    /// in its tree rather than a copy keeps no copy to count, and its build in the tree is counted as the worktree's own
    /// check counts it; a file the tree holds is counted where it lies.
    /// </summary>
    [Fact]
    public void ARunsStartIsHeldToThePathBudgetOfItsLegSteps()
    {
        var platform = new HostPlatform();
        var layout = new HarnessLayout(Path.Combine(Path.GetTempPath(), "repo"), Path.Combine(Path.GetTempPath(), "repo"));
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: build
            """);
        // Its os written in another case is this machine's all the same, as the configuration's check reads it.
        var leg = new LegConfig { Os = platform.PlatformKey.ToUpperInvariant(), Processor = "x64", Toolchain = "gcc", Config = "Debug", LocalCopy = true };
        HarnessConfig Limited(int limit) => new() { Legs = new Dictionary<string, LegConfig>(StringComparer.Ordinal) { ["local"] = leg }, Worktrees = new WorktreeSettings { PathLimit = limit } };
        var config = Limited(40);

        var refusal = FlowRunPaths.RefusalAtStart(new PathBudget(platform), new HarnessContext(layout, config), "r1", definition, platform.PlatformKey);
        var (inCopy, tree) = FlowRunPaths.AtStart(new PathBudget(platform), new HarnessContext(layout, config), "r1", definition, platform.PlatformKey);

        Assert.NotNull(refusal);
        Assert.StartsWith("run 'r1' of 'flow' would work in paths too long for this machine - the deepest of its agents' worktrees with the copies and workers its leg steps keep inside it: ", refusal, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was made.", refusal, StringComparison.Ordinal);
        Assert.Contains($"'{tree}'", refusal, StringComparison.Ordinal);
        Assert.Null(FlowRunPaths.RefusalAtStart(new PathBudget(platform), new HarnessContext(layout, Limited(2000)), "r1", definition, platform.PlatformKey));

        var inTree = Limited(40);
        inTree.Legs["local"] = new LegConfig { Os = platform.PlatformKey, Processor = "x64", Toolchain = "gcc", Config = "Debug" };
        var (alone, _) = FlowRunPaths.AtStart(new PathBudget(platform), new HarnessContext(layout, inTree), "r1", definition, platform.PlatformKey);

        // A leg working in its tree keeps no copy to count, and builds in the tree: one separator, build/<variant>/, and
        // the reserve below that.
        var variant = VariantKey.For(inTree, inTree.Legs["local"], platform.PlatformKey);
        Assert.True(alone.RequiredLength < inCopy.RequiredLength, "a leg working in its tree keeps no copy to count");
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(tree).Length + 1 + "build".Length + 1 + variant.DirectoryName.Length + 1 + inTree.Worktrees.PathBudgetReserve + inTree.Worktrees.PathBudgetMargin,
            alone.RequiredLength);

        // A file the tree holds is counted as it lies below the tree: its path after one separator - alone where no leg
        // builds on this machine, and where one does only as it is the deeper.
        var bare = Limited(40);
        bare.Legs.Clear();
        var (written, _) = FlowRunPaths.AtStart(new PathBudget(platform), new HarnessContext(layout, bare), "r1", definition, platform.PlatformKey, written: 10);
        var (deeper, _) = FlowRunPaths.AtStart(new PathBudget(platform), new HarnessContext(layout, inTree), "r1", definition, platform.PlatformKey, written: 1000);
        Assert.Equal(Path.TrimEndingDirectorySeparator(tree).Length + 1 + 10 + bare.Worktrees.PathBudgetMargin, written.RequiredLength);
        Assert.Equal(Path.TrimEndingDirectorySeparator(tree).Length + 1 + 1000 + inTree.Worktrees.PathBudgetMargin, deeper.RequiredLength);
    }

    /// <summary>A runner that answers every step as asked.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult(FlowStepResult.Ended(passed: true, 0));

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>The facts every run of these tests asks of its runners.</summary>
    private sealed class Facts : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }
}
