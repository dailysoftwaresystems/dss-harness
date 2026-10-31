using System.Globalization;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Ci;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;
using DssHarness.Core.Web.Search;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>Runs and lists the side-tasks of a repository: the two commands' use case.</summary>
public interface ISideTaskService : Application.IUseCase
{
    /// <summary>Begins a side-task - a chat in the terminal, or one message with <c>--prompt</c> - or opens one again.</summary>
    /// <param name="call">What the command line said and how its chat is driven.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    /// <returns>How the side-task ended, its records named on every exit.</returns>
    Task<CommandOutcome> RunAsync(SideTaskCall call, CancellationToken cancellationToken);

    /// <summary>Lists the repository's side-tasks, newest first.</summary>
    /// <param name="startDirectory">The directory the command was typed in.</param>
    /// <param name="json">Whether the answer is a document.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    Task<CommandOutcome> ListAsync(string startDirectory, bool json, CancellationToken cancellationToken);
}

/// <summary>
/// One side-task asked of the tool: what the command line said and how its chat is driven. A chat is driven by a front
/// end that reads the person's lines and counts the terminal's interrupts; a one-shot names none.
/// </summary>
/// <param name="StartDirectory">The directory the command was typed in.</param>
/// <param name="Prompt">The one message of a one-shot side-task; null for a chat.</param>
/// <param name="PromptFile">A file holding the one message of a one-shot side-task, read relative to the start directory, in the place of the prompt.</param>
/// <param name="Resume">The side-task to open again; null to begin one.</param>
/// <param name="Name">The side-task's own name, where the person gives one.</param>
/// <param name="Model">The model to run on, by its code.</param>
/// <param name="Tier">The tier whose first model runs.</param>
/// <param name="Client">The client to reach the model through.</param>
/// <param name="Efforts">The effort levels asked: one level, or <c>code=level</c> entries with <c>all=level</c> for the rest.</param>
/// <param name="Json">Whether the one-shot answers with a document.</param>
/// <param name="Chat">The chat's front end; null for a one-shot.</param>
public sealed record SideTaskCall(
    string StartDirectory,
    string? Prompt = null,
    string? PromptFile = null,
    string? Resume = null,
    string? Name = null,
    string? Model = null,
    string? Tier = null,
    string? Client = null,
    IReadOnlyList<string>? Efforts = null,
    bool Json = false,
    SideTaskChat? Chat = null);

/// <summary>
/// Runs and lists the side-tasks of a repository (part 03, "Side-tasks"): an agent outside every workflow, a
/// conversation between a person and one agent over the main tree, with no step and no limit of a run's - every action
/// still decided by the engine before it runs and recorded, the built-in refusals kept. No second loop: each turn is an
/// <see cref="AgentStepRequest"/> handed to <see cref="IAgentRunner"/> as the engine hands it one, so the clients, the
/// tool runtime, the stored stream, compaction and the fallback are the ones every step uses.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="runner">Works each turn, whatever the client is.</param>
/// <param name="clients">The clients registered, whose kind tells the chat how it goes on between messages.</param>
/// <param name="platform">This machine.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="store">Guards the repository's key-spend record across runs.</param>
/// <param name="keys">The slot the entry's keys are read from.</param>
/// <param name="webKeys">Each web-search source's key, read as a step's is.</param>
/// <param name="updates">Makes the Claude Code a side-task needs ready, where its order holds the client that starts one.</param>
/// <param name="fileSystem">The file system the side-task's records are kept on.</param>
/// <param name="output">Says what each side-task's turns and refusals say.</param>
/// <param name="clock">Tells when each turn began and what the balancing of keys reads.</param>
public sealed class SideTaskService(
    IHarnessContextLoader contextLoader,
    IAgentRunner runner,
    IEnumerable<IAgentClient> clients,
    IHostPlatform platform,
    IProcessIdentity identity,
    OrchestrationStore store,
    AgentKeyStore keys,
    WebSearchKeys webKeys,
    ClaudeCodeUpdateService? updates,
    IFileSystem fileSystem,
    Output.IHarnessOutput output,
    TimeProvider? clock = null) : ISideTaskService
{
    /// <summary>The command that begins a side-task.</summary>
    public const string RunCommand = "run-side-task";

    /// <summary>The command that lists them.</summary>
    public const string ListCommand = "list-side-tasks";

    /// <summary>How many words of the first message name a side-task the person did not name.</summary>
    public const int NameWords = 5;

    /// <summary>The longest a derived name's words take.</summary>
    public const int MostNameCharacters = 40;

    /// <summary>What a failed agent is asked again before the side-task gives the turn up, as a workflow's own default.</summary>
    public const int DefaultRetries = 3;

    /// <summary>How long a failed agent's first retry waits, in seconds, as a workflow's own default.</summary>
    public const int DefaultBackoffSeconds = 30;

    /// <summary>
    /// The system prompt every side-task's agent runs under: no workflow, no step, no output envelope - a conversation
    /// with a person over the tree.
    /// </summary>
    public const string AgentSystemPrompt =
        "You are the agent of a DssHarness side-task: a conversation with one person over this repository's main tree. "
        + "Answer the person directly, and read, change and run what the task needs, as the person's own session could. "
        + "There is no workflow, no step and no output shape: your answer is text for the person. "
        + "Every action you ask for is decided and recorded by DssHarness.";

    /// <summary>The rule a side-task's name follows, as a refusal says it.</summary>
    public const string NameRule = "letters, digits, '-' and '_' only, at most 80 characters, beginning with a letter or a digit";

    private readonly IHarnessContextLoader _contextLoader = contextLoader ?? throw new ArgumentNullException(nameof(contextLoader));
    private readonly IAgentRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IReadOnlyList<IAgentClient> _clients = [.. (clients ?? throw new ArgumentNullException(nameof(clients)))];
    private readonly IHostPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly AgentKeyStore _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly WebSearchKeys _webKeys = webKeys ?? throw new ArgumentNullException(nameof(webKeys));
    private readonly ClaudeCodeUpdateService? _updates = updates;
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly Output.IHarnessOutput _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public async Task<CommandOutcome> RunAsync(SideTaskCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var prompt = await ReadPromptAsync(call, cancellationToken).ConfigureAwait(false);

        if (prompt is { Problem: { } problem })
        {
            return CommandOutcome.Usage(problem);
        }

        if (call.Json && prompt is null)
        {
            return CommandOutcome.Usage("--json without a prompt is a usage error: a chat is no document. Give --prompt or --prompt-file for one message.");
        }

        if (prompt is null && call.Chat is null)
        {
            return CommandOutcome.Usage("a side-task is a chat in the terminal or one message with --prompt; neither was given.");
        }

        var (effort, effortByModel, effortProblem) = SideTaskEffort.Parse(call.Efforts ?? []);

        if (effortProblem is { } refused)
        {
            return CommandOutcome.Usage(refused);
        }

        HarnessContext context;

        try
        {
            context = await _contextLoader.LoadAsync(call.StartDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message);
        }

        if (AgentProviders.Resolve(context.Config) is not { Providers: { } providers })
        {
            return CommandOutcome.Failed(HarnessExit.ConfigInvalid, "the configuration's agent providers do not resolve; list-agent-providers --template prints what init writes into a new config");
        }

        // The side-task's name: the person's, the one opened again, or the date and time it began with the first words
        // of its first message - settled once, for the checks and the records alike.
        var name = call.Resume ?? call.Name ?? Named(prompt?.Text, _clock.GetUtcNow().LocalDateTime);

        if (!IsName(name))
        {
            return CommandOutcome.Refused($"the side-task's name '{name}' is no name a side-task's directory takes: {NameRule}");
        }

        var task = SideTaskLayout.Of(context.Layout, name);
        var records = new SideTaskStore(_fileSystem);

        if (call.Resume is { Length: > 0 })
        {
            if (records.Read(task) is null)
            {
                return CommandOutcome.Refused($"side-task '{call.Resume}' names no side-task of this repository: list-side-tasks lists them. Nothing was changed.", [$"Records: {context.Layout.SideTasksDirectory}"]);
            }
        }
        else if (records.Read(task) is not null || _fileSystem.KindOf(task.Directory) == PathKind.Directory)
        {
            return CommandOutcome.Refused($"side-task '{name}' is taken: --resume {name} opens it again. Nothing was changed.", [$"Records: {task.Directory}"]);
        }

        var record = records.Read(task) ?? new SideTaskRecord(name, _clock.GetUtcNow());

        var owner = new SideTaskOwner(_fileSystem, _identity, _clock);

        try
        {
            var reclaimed = owner.Claim(task);

            if (reclaimed is { Length: > 0 })
            {
                _output.Warn(RunCommand, reclaimed);
            }

            return await WorkAsync(call, context, providers, task, record, prompt?.Text, effort, effortByModel, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message, [$"Records: {task.Directory}"]);
        }
        finally
        {
            owner.Release(task);
        }
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> ListAsync(string startDirectory, bool json, CancellationToken cancellationToken)
    {
        HarnessContext context;

        try
        {
            context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message);
        }

        var owner = new SideTaskOwner(_fileSystem, _identity, _clock);
        var tasks = new SideTaskStore(_fileSystem).List(context.Layout.SideTasksDirectory)
            .Select(listed => (listed.Layout, listed.Record, Open: owner.Open(listed.Layout)))
            .ToList();

        return SideTaskReports.List(context.Layout.SideTasksDirectory, tasks, json);
    }

    /// <summary>One side-task's work: its resolution, its turns and its records, however it ends.</summary>
    private async Task<CommandOutcome> WorkAsync(
        SideTaskCall call,
        HarnessContext context,
        AgentProviders providers,
        SideTaskLayout layout,
        SideTaskRecord record,
        string? prompt,
        string? effort,
        IReadOnlyDictionary<string, string>? effortByModel,
        CancellationToken cancellationToken)
    {
        // A side-task opened again runs on the model it last used, unless the person names another; a model named
        // outright has only its own other keys after it, as a step's has.
        var last = record.Turns is [_, ..] turns ? turns[^1] : null;
        var model = call.Model ?? (call.Tier is null ? last?.Model : null);
        var level = effort ?? (call.Model is null && call.Tier is null && call.Efforts is null or { Count: 0 } ? last?.Effort : null);

        var resolution = AgentResolution.Of(providers, call.Tier, model, level, effortByModel, call.Client, definition: null);

        if (resolution is null)
        {
            return CommandOutcome.Failed(HarnessExit.ConfigInvalid, "the side-task has no model to run on: name a tier or a model on it, or a tier under defaults.agentTier");
        }

        // Each model the turn may run on must take the level it is given, as a step's is checked (V38): a level a model
        // does not take is a usage error naming what it takes.
        IReadOnlyList<AgentModelEntry> every = [resolution.Serving, .. resolution.After];

        foreach (var entry in every)
        {
            if (entry.Effort is { } given && providers.Of(entry.Model) is { Model.Efforts: { Count: > 0 } takes } named && !takes.Contains(given, StringComparer.Ordinal))
            {
                return CommandOutcome.Usage($"the side-task runs '{entry.Model}' at effort '{given}', which it does not take. It takes: {string.Join(", ", takes)}");
            }
        }

        // The running order and the key it starts on, as a step's is; what it spends is counted against that key.
        var spend = new AgentKeySpend(_fileSystem, _store, _clock);
        var spent = context.Config.BalanceKeyUsage == true ? spend.Spent(context.Layout) : null;
        var order = AgentStepOrder.Of(providers, resolution, context.Layout, _keys, new ClaudeTranscripts(_fileSystem, _platform).ConfigDirectory, spent);

        string? program = null;
        string? programVersion = null;

        if (_updates is not null && order.Any(entry => string.Equals(entry.Client, ClaudeCodeClient.ClientName, StringComparison.Ordinal)))
        {
            var ready = await _updates.ReadyAsync(context.Layout, providers.Appointed(), providers.ClientOf(ClaudeCodeClient.ClientName), steps: $"side-task '{layout.Name}'", cancellationToken).ConfigureAwait(false);

            if (!ready.Ready)
            {
                return CommandOutcome.Failed(ready.ExitCode == HarnessExit.ConfigInvalid ? HarnessExit.ConfigInvalid : HarnessExit.ToolMissing, ready.Message, [$"Records: {layout.Directory}"]);
            }

            program = ready.Program;
            programVersion = ready.Version;
        }

        // What the side-task runs under: the ground of part 03's table, and the web search of every configured source.
        var sources = WebSearchSources.Of(context.Config, WebSearchKinds.OfThisBuild);

        if (sources.Problems is [var wrong, ..])
        {
            return CommandOutcome.Failed(HarnessExit.ConfigInvalid, $"the configuration's web search does not resolve: {wrong}");
        }

        var read = await _webKeys.ReadAsync(context.Layout, providers.Appointed(), [.. sources.All], cancellationToken).ConfigureAwait(false);
        var search = sources.All is [_, ..] ? SideTaskPolicy.WebSearch(sources, read) : null;
        var forms = sources.All
            .Where(source => read.TryGetValue(source.Name, out var key) && key.Value is { Length: > 0 })
            .SelectMany(source => source.Credential?.Forms(read[source.Name].Value!) ?? [read[source.Name].Value!])
            .Where(form => form is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var mask = forms is [_, ..] ? new Runners.ActionValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), forms.ToDictionary(form => form, form => form, StringComparer.OrdinalIgnoreCase)) : null;

        var ground = SideTaskPolicy.Ground(
            context.Layout,
            context.Config,
            [.. order.Select(entry => entry.Credential?.ConfigDirectory).OfType<string>().Append(new ClaudeTranscripts(_fileSystem, _platform).ConfigDirectory).Distinct(StringComparer.Ordinal)],
            context.Layout.WorkflowsEnvSlot);
        var workspace = new AgentWorkspace(
            Worktree: null,
            context.Layout.MainCheckoutRoot,
            context.Layout.MainCheckoutRoot,
            [],
            context.Layout.MainCheckoutRoot,
            layout.Directory);

        var working = new Working(
            _runner,
            _clients,
            _fileSystem,
            _output,
            _clock,
            call,
            context,
            layout,
            record,
            resolution,
            call.Tier,
            effort,
            effortByModel,
            order,
            program,
            programVersion,
            ground,
            workspace,
            search,
            mask,
            spend.RecordingIn(context.Layout));

        return prompt is null
            ? await working.ChatAsync(cancellationToken).ConfigureAwait(false)
            : await working.OneShotAsync(prompt, call.Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The prompt file read, where one was named.</summary>
    private sealed record ReadPrompt(string? Text, string? Problem);

    /// <summary>Reads the one message of a one-shot side-task, where its command line names one.</summary>
    private async Task<ReadPrompt?> ReadPromptAsync(SideTaskCall call, CancellationToken cancellationToken)
    {
        if (call.Prompt is { Length: > 0 } prompt)
        {
            return new ReadPrompt(prompt, null);
        }

        if (call.PromptFile is not { Length: > 0 } file)
        {
            return null;
        }

        try
        {
            var text = _fileSystem.ReadAllText(Path.GetFullPath(file, Path.GetFullPath(call.StartDirectory)));
            return new ReadPrompt(text.Length == 0 ? string.Empty : text, text.Length == 0 ? "--prompt-file is empty: one message is asked" : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ReadPrompt(null, $"--prompt-file '{file}' could not be read: {ex.Message}");
        }
    }

    /// <summary>Whether <paramref name="name"/> is a name a side-task's directory may take.</summary>
    /// <param name="name">The name.</param>
    public static bool IsName(string name)
        => name.Length > 0 && name.Length <= 80 && !name.StartsWith('.') && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// The name a side-task takes where the person gave none: the date and time it began with the first words of its
    /// first message, kebabed.
    /// </summary>
    /// <param name="message">The first message, where one was given.</param>
    /// <param name="began">When the side-task began, locally.</param>
    private static string Named(string? message, DateTime began)
    {
        var words = (message ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(NameWords)
            .Select(word => new string([.. word.Select(char.ToLowerInvariant).Where(char.IsAsciiLetterOrDigit)]))
            .Where(word => word.Length > 0)
            .ToList();

        var stem = string.Join("-", words);

        if (stem.Length > MostNameCharacters)
        {
            stem = stem[..MostNameCharacters].TrimEnd('-');
        }

        return began.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + (stem.Length == 0 ? string.Empty : "-" + stem);
    }
}
