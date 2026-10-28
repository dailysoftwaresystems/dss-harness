using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// Help is treated as documentation, so it is tested like documentation: every
/// command must appear in it, and every documented value must come from the code
/// that produces it rather than from prose someone has to remember to update.
/// </summary>
public sealed partial class HelpTests
{
    /// <summary>A help topic's text, as the command prints it: the core's renderer asked directly, no tool spawned.</summary>
    private sealed record HelpText(string StandardOutput)
    {
        /// <summary>The text of one topic, or of the overview where none is asked.</summary>
        public static HelpText Of(string? topic) => new(HelpTopics.Render(topic));
    }

    [Fact]
    public async Task EveryListedCommand_HasItsOwnWorkingHelp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listed = await CliRunner.CommandsAsync(cancellationToken);

        Assert.NotEmpty(listed);

        foreach (var command in listed)
        {
            var result = await CliRunner.RunAsync([command, "--help"], cancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(command, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The admission topic, under either name, names every key a section may declare with the value it takes when left
    /// out and its bounds, and the exit a leg not admitted ends with - each read from the code, so none can drift.
    /// </summary>
    [Theory]
    [InlineData("admission")]
    [InlineData("heavy")]
    public async Task TheAdmissionTopic_NamesEveryKeyWithItsDefault_FromTheCode(string topic)
    {
        var result = HelpText.Of(topic);


        var keys = typeof(AdmissionSettings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToList();

        Assert.Equal(["heavyLegs", "maxMemoryPercent", "settleSeconds", "pollSeconds", "maxWaitMinutes", "minFreeGiB"], keys);

        foreach (var key in keys)
        {
            Assert.Contains($"  {key}", result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var value in new[]
        {
            $"({AdmissionSettings.DefaultHeavyLegs})",
            string.Create(CultureInfo.InvariantCulture, $"({AdmissionSettings.DefaultMaxMemoryPercent})"),
            $"([{string.Join(", ", AdmissionSettings.DefaultSettleSeconds)}])",
            $"({AdmissionSettings.DefaultPollSeconds})",
            string.Create(CultureInfo.InvariantCulture, $"({AdmissionSettings.DefaultMaxWaitMinutes})"),
            string.Create(CultureInfo.InvariantCulture, $"from 0 to {AdmissionSettings.MostMinFreeGiB}; 0 stops no build ({AdmissionSettings.DefaultMinFreeGiB})"),
            $"at most {AdmissionSettings.MostSeconds}",
            string.Create(CultureInfo.InvariantCulture, $"at most {AdmissionSettings.MostWaitMinutes} minutes"),
            $"exit {LegExit.NotAdmitted}",
        })
        {
            Assert.Contains(value, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryListedCommand_HasADescription()
    {
        var result = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        var lines = CliRunner.CommandLines(result.StandardOutput).ToList();
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            // "  name   description" - a command with no description is undocumented.
            var described = CommandLinePattern().Match(line);

            Assert.True(
                described.Success && described.Groups["description"].Value.Trim().Length > 10,
                $"command line has no usable description: '{line.Trim()}'");
        }
    }

    /// <summary>The layout topic says each run is named however it ended, and one refused before it had a directory by its id alone.</summary>
    [Fact]
    public async Task LayoutTopic_SaysEachRunIsNamed_ARefusedOneByItsIdAlone()
    {
        var result = HelpText.Of("layout");

        Assert.Contains(
            "Each names its run too, in its first line, 'run <id>', and as runId in --json, however it ended: a run refused "
            + "before it had a directory keeps no records, and is cited by that id alone.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout topic names the agent clients' key slot, in the workflows directory beside the workflows' own: init
    /// creates its placeholder and ignores what it holds, the keys are read from it and from nowhere else, and no
    /// workflow may take its name. The agent-clients topic holds a sample file for it, one line per appointed name
    /// saying what its value is, none filled in.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_NamesTheAgentClientsKeySlot()
    {
        var result = HelpText.Of("layout");
        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains(
            ".harness-config/workflows/.env/ contents ignored; the agent clients' keys, NAME=value files, every file merged, "
                + "no key read from anywhere else; no workflow may be named .env",
            prose,
            StringComparison.Ordinal);

        var clients = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains("A sample for the providers init declares, none filled in:", clients, StringComparison.Ordinal);
        Assert.Contains("# .harness-config/workflows/.env/keys.env", clients, StringComparison.Ordinal);
        Assert.Contains("ANTHROPIC_API_KEY= # the Anthropic API key", clients, StringComparison.Ordinal);
        Assert.Contains("ZAI_API_KEY= # the Z.AI API key", clients, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says the gate: one lane per provider and key, the client's parallel bound with the
    /// built-in's own beside it, the API clients' optional buckets, the 429 cool-down holding every waiter, and that
    /// the step runner holds the lane - and the README says the same.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysTheGateOfOneLanePerProviderAndKey()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "The runs of one provider and one key go through one lane of the gate: at most "
                + "agentClients.<client>.maxParallel of them under way at once (claude-code 3, claude-api 4, glm 2 "
                + "where the entry says none; a client that signs in with a login is one lane by its client, and two keys of one "
                + "provider two lanes, keyed by a hash of the key).",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a 429's cool-down holds every waiter of the lane, a later or longer one holding past a sooner one",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Left out, the provider's answers pace the client; the orchestrator's maxReasoningAgents bounds agents beside the gate, and both apply",
            prose,
            StringComparison.Ordinal);

        Assert.Contains(
            "The runs of one provider and one key go through one lane of the gate: at most "
                + "`agentClients.<client>.maxParallel` of them under way at once (3 for claude-code, 4 for claude-api and 2 for glm where",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic names every agent section of the file's top level, agentClients among them - its one key, and
    /// that a client holds no size, a model's sizes being its own - and the agent-clients topic the name rules.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_NamesTheAgentClientsSection_AndTheAgentClientsTopicTheNameRules()
    {        var config = Prose.Words(HelpText.Of("config").StandardOutput);

        Assert.Contains(
            "agentClients the clients the tool builds in (claude-code, claude-api, glm), each by its name: update, claude-code's alone, says "
                + "whether the tool installs and updates Claude Code with npm; auth, claude-code's alone, how it signs in (login, apiKey, "
                + "oauthToken, authToken, login where nothing is said), and token the variable a token is held under; configDir and "
                + "executable, claude-code's alone, the variables that hold a specific login's Claude Code directory and the program it "
                + "starts (CLAUDE_CONFIG_DIR, CLAUDE_EXECUTABLE where none is named); maxRetries, maxRetryWaitSeconds and requestExtras, "
                + "the API clients' (claude-api, glm), their tries, longest wait and the fields merged into each request; "
                + "requestsPerMinute, inputTokensPerMinute and outputTokensPerMinute, the API clients' too, the buckets that pace a "
                + "lane's minute; cacheTtl and betas, claude-api's alone, how long its requests are cached and the betas sent beside "
                + "the tool's own; preserveThinking, glm's alone, whether Z.AI keeps the model's reasoning across turns; maxParallel, "
                + "every client's, the runs of one provider and key under way at once (claude-code 3, claude-api 4, glm 2 where "
                + "nothing is said) - the gate of 'help agent-clients'; a client holds no model, size or price - contextTokens and "
                + "maxOutputTokens are a model's ('help agent-clients')",
            config,
            StringComparison.Ordinal);

        Assert.Contains(
            "agentPolicy what an agent may do beyond the rules built into the tool: restrictions, each one rule string, that only ever refuse - "
                + "Run(git push *), Write(.github/**), Read(secrets/**), Web(pastebin.com) - applied to every agent of every run under this "
                + "configuration, beside a workflow's definitions and steps; webDomains, the allow list of every page an agent reads and every "
                + "result a search gives - a search source's own requests are held to the Web(...) rules alone (left out, any "
                + "address); worktreeRules (true where nothing is said) and protectedPaths (true too), the switches of the rule sets "
                + "'help agent-clients' names; keepEnvironment, the secret-shaped names a command an agent runs keeps; "
                + "commandTimeoutSeconds (120) and maxCommandTimeoutSeconds (3600), how long one runs where its call names no time, "
                + "and the most it may name, 2147483 at most ('help agent-clients')",
            config,
            StringComparison.Ordinal);

        Assert.Contains(
            "A provider's name and each tier's are held to the orchestration name rule - lower-case letters and digits, single hyphens - "
                + "and a tier is one name in one case everywhere; a model's code, what is sent to the API, is one word, and its name is the "
                + "one a person reads.",
            Prose.Words(HelpText.Of("agent-clients").StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says what this build does with keys and what the engine does with them: claude-code reads
    /// none, choosing one is the engine's where it runs a run's steps - the first the slot holds, or the least spent
    /// under balanceKeyUsage - what each key spent recorded by its name, and each request through the gate - and
    /// validate-workflow says the prices and the missing keys of a definition's steps; the workflows topic that a
    /// step's tier is one of tierConfig's.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysChoosingAKeyIsTheEngines_AndWhatValidateWorkflowSays_AndAStepsTierIsTierConfigs()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "claude-code signs in by its entry's auth (agentClients.claude-code.auth, one of login, apiKey, oauthToken, authToken): login, "
                + "the machine's login, where nothing is said, reading no key at all; apiKey, its provider's keys in their order; oauthToken or "
                + "authToken, the token the slot holds under the entry's token (CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_AUTH_TOKEN where it names "
                + "none) - a name and never a token, appointed once beside every provider's keys. Its configDir and executable name the "
                + "variables of the slot that hold a specific login's Claude Code directory and a program to start in place of npm's "
                + "(CLAUDE_CONFIG_DIR and CLAUDE_EXECUTABLE where they name none), appointed once the same way and never a key; a launch is "
                + "to sign in from that directory where the slot holds it, else from the tool's own CLAUDE_CONFIG_DIR, else from Claude "
                + "Code's own. A launch is given that credential and no "
                + "other, whatever the tool's own environment holds; claude-api and glm sign each request with their provider's keys. "
                + "Choosing a key is the engine's, where it runs a run's steps: a step starts on the first key of its model's provider the "
                + "slot holds and no remembered limit covers, or - where balanceKeyUsage is on (off unless turned on) - on the one that has "
                + "spent least in the last seven days, counted back from the moment it starts, keys that spent alike in the list's order, "
                + "whatever tier, model, step, run or side-task spent it; the key is kept for the step, its conversation staying with one "
                + "account, and a limit or a rejected key moves it to the next in the same order. What a key spent is what the engine "
                + $"counts for each request served with it, at the configuration's prices - or, for a Claude Code launch, what the launch reports - recorded by the key's name (never its value) in {HarnessLayout.OrchestratorsDirectoryName}/key-spend.jsonl, "
                + "one line for each entry of a step's order that ran, read for a key's week and its lines older than seven days dropped "
                + "when it is next written; what the same key spends elsewhere - another repository, another machine, another tool - is not "
                + "seen. Each request a step sends goes through the gate of its provider and key (above), beside the orchestrator's "
                + "maxReasoningAgents.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "validate-workflow says, under the line that says a definition holds, every step's models and levels, each provider's "
                + "prices for the models its steps may run on and the date they were read, and each key a step's model would read that "
                + "the slot lacks, with the steps that would read it.",
            prose,
            StringComparison.Ordinal);

        // A step's tier is one of tierConfig's, the section the configuration holds tiers under.
        Assert.Contains(
            "reason.tier a name for a class of model: one of the configuration's tierConfig, its models in the order they are tried",
            Prose.Words(HelpText.Of("workflows").StandardOutput),
            StringComparison.Ordinal);

        // A step's order is entries of a model and one of its provider's keys, each model's keys before the next model,
        // and a model named outright has none after its own keys.
        Assert.Contains(
            "The tiers a step runs on are tierConfig's: each tier its models in the order they are tried, the first serving "
                + "a step on the tier and the rest its fallback - a step's order as entries of a model and one of its "
                + "provider's keys, each model's keys before the next model, and a model named outright, on the step or in "
                + "its agent definition, running on that model with none after it ('help agent-definitions')",
            prose,
            StringComparison.Ordinal);

        // A step that goes on with earlier work is given the continuation record, its bounds said, and the first user
        // message's parts - the record only of a new conversation - with the message kept as user.md.
        Assert.Contains(
            "A step that goes on with earlier work is given the engine's record of it: the continuation record, text put "
                + "together from the step directories of the agent - what it is, the steps so far with each one's notes and "
                + "output, the files changed, the last red build or test, the handoff, and the last iterations of the newest "
                + "attempt word for word from its stored stream - nothing in it written or shortened by a model, and "
                + "whatever is cut named with a copy in the agent's work/continuation/. Its bounds are "
                + "agentContext.continuation's - recordChars (60000), stepChars (16000), paths (300), lastIterations (3) and "
                + "iterationChars (120000), each at its default where the key says none, each a positive number",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The record is sent only where the conversation is a new one - every launch on claude-code, and on the API "
                + "clients a conversation that cannot be gone on with - for a handed conversation goes on with one more "
                + $"user message alone; the message as sent is kept in the step's {OrchestratorLayout.StepUserFileName}, masked.",
            prose,
            StringComparison.Ordinal);

        // The compaction: the engine's own reduction, its bounds said, a new conversation on every client.
        Assert.Contains(
            "A conversation that has filled its model's context is compacted by the engine, and no model: a reduction "
                + "worked out from the stored stream, the same input always the same record - the run context and the task "
                + "whole as they were sent, the last iterations word for word by the continuation record's rule, every "
                + "earlier message of the agent its text whole with its thinking left out, every earlier call one line (the "
                + "tool, its input cut at callInputChars, how it ended, the path where its input names one), every earlier "
                + "result left out, and every message the engine sent since the first whole. The reduced part is at most "
                + "agentContext.compaction.reducedChars (200000); a call's input is cut at callInputChars (400)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "It begins a new conversation, on every client - the record is the first message of one and the old is kept "
                + "whole, never edited or sent again; a claude-code child is started with DISABLE_COMPACT=1, so it never "
                + "compacts by itself and a full context ends the step",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-definitions topic says what an agent file is, the keys it takes, that a definition is the level below
    /// a step's, that a fallback is no key of one, and that the shipped files name no model, tier or effort.
    /// </summary>
    [Fact]
    public async Task TheAgentDefinitionsTopic_SaysWhatAnAgentFileIsAndTakes()
    {
        var topic = HelpText.Of("agent-definitions").StandardOutput;

        Assert.Equal(topic, HelpText.Of("definitions").StandardOutput);
        Assert.Contains(HelpTopics.Described, described => described.Name == "agent-definitions");

        var prose = Prose.Words(topic);

        Assert.Contains(
            "A definition is the level below a step's: where a step names a tier, a model, an effort or a client, its own "
                + "wins; where it names none, the definition's stands, each on its own.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A fallback is no key of a definition or of a step: it is the order of the tier's models in tierConfig, each "
                + "model's keys before the next model.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The sample agent files name no model, tier or effort, and no shipped one does: they are a step's "
                + "configuration, and the shipped workflow names them on its steps, where a person reads and changes them.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a step's prompt {skill.<name>}", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The skills topic says the placeholder, the three places and their order beside Claude Code's own, what is put
    /// into a prompt, the folder handed to the step, the keys of the frontmatter and the body's reads and bounds, and
    /// what is not looked for - and the README and the layout topic say the same of it.
    /// </summary>
    [Fact]
    public async Task TheSkillsTopic_SaysThePlaceholderThePlacesAndTheFolderHandedToTheStep()
    {
        var topic = HelpText.Of("skills").StandardOutput;

        Assert.Equal(topic, HelpText.Of("skill").StandardOutput);
        Assert.Contains(HelpTopics.Described, described => described.Name == "skills");

        var prose = Prose.Words(topic);

        Assert.Contains(
            "A reason step's prompt says {skill.<name>} where a skill's text goes, and the engine puts the text there "
                + "before the prompt is sent, on every client alike",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The repository's come before the user's, so a run reads the same skill on every machine that has the "
                + "repository; Claude Code's own order is the other way round.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A SKILL.md that is there and cannot be used is refused: the next place is not tried in its stead, for a "
                + "broken skill of the repository must not turn into the user's without a word.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "What is put into the prompt is the body, not the frontmatter, after its substitutions:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "The folder is copied whole into the agent's work directory, work/<agent>/skills/<name>/, in place of any "
                + $"earlier copy; that path is the directory the text states and what ${{CLAUDE_SKILL_DIR}} becomes. The work directory is a root every agent already reads and runs commands in, on every client. A folder that holds a link, more than {DssHarness.Core.Orchestration.Flow.AgentSkills.FileLimit} files or more than 16 MB is refused.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "It grants nothing: what an agent may do is its definition's and the policy's",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "refused: each changes what runs the skill in Claude Code, and text put into a prompt runs in the step's own "
                + "agent, on its model, under its policy. Any other key is refused too, naming the keys this tool knows.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A skill that is missing or unusable fails loud, twice: when the definition is checked - validate-workflow, "
                + "and the start of run-orchestrator and resume-orchestrator (exit 12, nothing started, every problem of "
                + "every prompt together) - and when the step starts",
            prose,
            StringComparison.Ordinal);

        // The README says the same, and the layout topic names the tool's own place.
        var readme = Prose.Document("README.md");

        Assert.Contains(
            "A reason step's prompt says `{skill.<name>}` where a skill's text goes, and the engine puts the text there "
                + "before the prompt is sent, on every client alike (`help skills`)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "The repository's come before the user's - Claude Code's own order is the other way round - and a `SKILL.md` "
                + "that is there and cannot be used is refused, the next place not tried in its stead.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "A skill that is missing or unusable fails loud, twice: when the definition is checked (`validate-workflow`, "
                + "and the start of `run-orchestrator` and `resume-orchestrator`; exit 12, nothing started, every problem "
                + "of every prompt together, each refusal naming every place looked for it), and when the step starts",
            readme,
            StringComparison.Ordinal);

        var layout = Prose.Words(HelpText.Of("layout").StandardOutput);

        Assert.Contains(
            "tracked; the tool's own place a step's prompt names a skill from; init creates nothing there",
            layout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says which Claude Code the tool uses and why - npm's, and never a native launcher -,
    /// that a run which needs it needs npm, what the tool runs to install it and bring it up to date, what it never
    /// runs - beside a session of that installation, each kind of process that counts as one said, a claude installed
    /// otherwise whose program is read not counting, and a table read by names alone refusing -, that a slot
    /// that cannot give a named program is refused rather than passed over, that what npm holds is said after every
    /// change, the two ways to turn it off, and that no key is read from the process environment.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysWhichClaudeCodeItUses()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains("The Claude Code this tool uses is the one npm installs, and no other", prose, StringComparison.Ordinal);
        Assert.Contains($"'npm install -g @anthropic-ai/claude-code@{DssHarness.Core.Agents.ClaudeCode.ClaudeCodeUpdateService.Needed}'", prose, StringComparison.Ordinal);
        Assert.Contains("never with sudo, never 'npm update -g', never past the version needed", prose, StringComparison.Ordinal);
        Assert.Contains(
            "never beside a session of that installation that is running (its program, node running its script, a claude node runs, or a "
                + "claude whose program the machine will not say, such as another user's on Linux; a claude installed any other way whose "
                + "program it names does not count, and a process table read by names alone refuses the change)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a slot that cannot give it is refused, never passed over for npm's", prose, StringComparison.Ordinal);
        Assert.Contains("After a change, whether it ran or failed, what npm holds since is said.", prose, StringComparison.Ordinal);
        Assert.Contains("and no key is ever read from the process environment", prose, StringComparison.Ordinal);
        Assert.Contains("A run that needs a Claude model through the claude-code client needs npm, and no other run", prose, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_EXECUTABLE", prose, StringComparison.Ordinal);
        Assert.Contains("agentClients.<client>.update", prose, StringComparison.Ordinal);
        Assert.Contains("DISABLE_UPDATES", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says the agents' policy whole: one policy on every client, default allow except the
    /// root actions and the restrictions, each root action named, the slot and the Claude Code directories refused,
    /// the command read whole (with what it cannot read said), policy-not-sandbox, and the fail-closed rule.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysTheAgentsPolicy()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains("What an agent may do is one policy, the same on every client: everything is allowed except", prose, StringComparison.Ordinal);
        Assert.Contains(
            "A restriction is one string, Run(git push *), Write(.github/**), Read(secrets/**) or Web(pastebin.com): Run a "
                + "command prefix, its trailing * any arguments and the bare command too; Write and Read path patterns as "
                + "gitignore reads them",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("Declared under agentPolicy.restrictions in config.json", prose, StringComparison.Ordinal);
        Assert.Contains("agentPolicy.webDomains is the allow list beside them", prose, StringComparison.Ordinal);
        Assert.Contains("curl, wget, Invoke-WebRequest, Invoke-RestMethod, iwr, irm, http, https, gh api - is", prose, StringComparison.Ordinal);
        Assert.Contains("the root actions (sudo, su, doas, pkexec, run0, gsudo, sudoedit, runas,", prose, StringComparison.Ordinal);
        Assert.Contains("psexec, PowerShell's -Verb RunAs, wsl -u root, systemd-run, schtasks with /ru, wherever", prose, StringComparison.Ordinal);
        Assert.Contains("writes outside an agent's writable roots, every read and write of a", prose, StringComparison.Ordinal);
        Assert.Contains("this tool's own verbs and run-side-task, run by any program or from a response file, whatever any switch says: an agent", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"nor leave its step's limits - nor do the gh commands that hand its work to another agent or install what the next launch loads ({string.Join(", ", AgentPolicy.GhOutOfStepCommands)}), by gh's own aliases too - nor does any command print a credential, whatever the worktree rules say ({string.Join(", ", AgentPolicy.CredentialPrintingCommands)}): a credential leaves its slot only by the key store that holds it, never through an agent's words.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("every read and write of a credential slot or a Claude Code directory of the run's and every search over one", prose, StringComparison.Ordinal);
        Assert.Contains("a refusal's places compare in any case on every system, a Windows short name read as its long one", prose, StringComparison.Ordinal);
        Assert.Contains(
            @"On Windows a device prefix (\\?\, \\.\, \??\) is dropped from a path and from what the runtime makes of it - one naming no "
                + "drive and no share, a volume by its id or a pipe, is a place it cannot tell, and denied - and a shell's "
                + @"spelling of a drive (/c/x, /cygdrive/c/x, /mnt/c/x) is read both as the drive (C:\x) and as the directory it names on the "
                + "current drive, an action allowed only where both are; a cd is followed the same way - pushd, PowerShell's Set-Location and "
                + "Push-Location, and the directory env -C or pwsh -WorkingDirectory starts its command in, each word it may go to - from every "
                + "place the command reached before it, up to 64 places, past which the command is denied.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("No agent starts while this tool itself runs elevated: its step is recorded never started, saying so, before any client is taken.", prose, StringComparison.Ordinal);
        Assert.Contains("a comment and a here-document's lines never read as commands", prose, StringComparison.Ordinal);
        Assert.Contains(
            "wrappers (env, nohup, time, timeout, nice, xargs, command, builtin, exec, eval, busybox) stripped, what "
                + "xargs reads an expansion among the words it runs; and it goes into sh -c and every other shell's (fish's -C scripts too, "
                + "its options read as fish reads them)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a variable - so set, or declared by export and its kind, cmd's set, fish's set, csh's setenv, setx or PowerShell's env: items - "
                + "whose value is a program git or another tool runs (GIT_SSH_COMMAND, GIT_PAGER,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("refused as such ('git $VERB' under Run(git push *))", prose, StringComparison.Ordinal);
        Assert.Contains("a file:// address is read as the path it names", prose, StringComparison.Ordinal);
        Assert.Contains(
            "What it cannot read whole it denies, said here, not guessed at: a script nested past four levels, a shell that reads its "
                + "script from its input (curl ... | sh), from a process substitution or from another open descriptor (/dev/fd/3, <&3), "
                + "an env -S string holding a backslash, whose escapes are env's own, an alias it gives, a script another program fills in "
                + "(find -exec sh -c 'echo {}'), PowerShell's code no word spells (a static method, an instance method "
                + "but a string's, a collection's or a date's, a function's or an alias's text, a class, using, its stop-parsing --%, "
                + "a script Invoke-Expression is handed by no one string, and a command line Start-Process, WMI, the task scheduler or Add-Type is handed), and a program named by a variable, a substitution, "
                + "a pattern or a brace, a brace that spells more than 64 words, and an option of git's, gh's, fish's or Windows PowerShell's own",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("the inside of a script file a command runs", prose, StringComparison.Ordinal);
        Assert.Contains(
            "a command find -exec, fd -x, rg --pre or --hostname-bin, parallel or an option of a git subcommand runs - a pack "
                + "program a transport runs (--upload-pack, --receive-pack, --exec, clone's -u), difftool's --extcmd, rebase's "
                + "--exec, grep's --open-files-in-pager, bisect run, submodule foreach, filter-branch's filters and send-email's "
                + "commands - read as its own",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("The worktree rules (agentPolicy.worktreeRules, true where nothing is said)", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"git's state-writing verbs ({string.Join(", ", AgentPolicy.GitStateVerbs)}) are refused, and so are the gh commands that run them or write gh's own configuration ({string.Join(", ", AgentPolicy.GhStateCommands)}; "
                + "a command gh names by an alias of its own, pr co for pr checkout among them, read as the command it names, and "
                + "where gh reads its configuration from - GH_CONFIG_DIR, XDG_CONFIG_HOME, AppData, HOME - set for a command the gh config set it is) - reading git",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"and so are the gh commands that merge or start CI ({string.Join(", ", AgentPolicy.NeverCiCommands)}), the gh api endpoints and GraphQL "
                + $"mutations that reach the same, gh alias set, gh extension exec, and a gh command named by none of gh's own ({string.Join(", ", AgentPolicy.GhCommands)}) - an alias, gh's own co among them, or an extension, a name for any command, where gh runs - its command's own program, or past one that runs the words after it "
                + $"({string.Join(", ", CommandAnalysis.CommandRunners)}), never among another program's words, its data - and, where the request writes, the gh api endpoints "
                + "that push, open a pull request or make a release - for never merges,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "a -c or --config-env of any key but the ones that only shape what git prints, --exec-path, clone's own -c or "
                + "--config and clone's and init's --template, GIT_CONFIG, the names through which git finds its programs and "
                + "hooks, and the homes it reads its global configuration from - "
                + "HOME, XDG_CONFIG_HOME, and the HOMEDRIVE, HOMEPATH and USERPROFILE git on Windows takes one from) is the git config it is",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-Expression's one string - by whatever name it is called, the call operator's string or a module's name before it among "
                + "them - read as a script of its own",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "git's own files - a .git, file or directory, at any depth beneath a root and in any case, a nested repository's and a "
                + "submodule's too (Write(**/.git/**)) - are not written by a tool, whatever protectedPaths says",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("an agent does not edit its own policy or what the next launch loads", prose, StringComparison.Ordinal);
        Assert.Contains("The protected paths (agentPolicy.protectedPaths, true where nothing is said)", prose, StringComparison.Ordinal);
        Assert.Contains("written out once as rules, each said whole: Write(.git/**), Write(.config/git/**),", prose, StringComparison.Ordinal);
        Assert.Contains("Write(Gemfile.lock), Write(composer.json), Write(composer.lock).", prose, StringComparison.Ordinal);
        Assert.Contains("Claude Code's own guard stays Claude Code's", prose, StringComparison.Ordinal);
        Assert.Contains("A step's writes is the one source that narrows", prose, StringComparison.Ordinal);
        Assert.Contains("the reporter's reads are narrowed to the run's directory alone", prose, StringComparison.Ordinal);
        Assert.Contains(
            @"an ask rule for every tool the launch has, and the restriction rules as deny rules, absolute once for each root in the POSIX "
                + @"form Claude Code matches (//c/x for C:\x), a Write rule as the Edit rule Claude Code holds its file tools' writes to",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("This is policy, not a sandbox", prose, StringComparison.Ordinal);
        Assert.Contains("Where this tool cannot decide a call it denies it, never allows", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says what the claude-code client does with a launch - a new session each time, streamed
    /// both ways, the tools of the one table and the ones never given, every call asked of the engine, the repair, the
    /// message at work, the handoff, the model held, the environment, a key passed over - its tools and names read from
    /// the types that hold them.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysWhatTheClaudeCodeClientDoesWithALaunch()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "The claude-code client starts Claude Code once for each launch, a new session every time: never resumed and never continued, "
                + "keeping no session file (one a launch leaves all the same is removed once it has ended, however it ended, and said), "
                + "streamed both ways - the step's prompt is "
                + "the first message on its input, never an argument -",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "the tools of the agent's capabilities and no other - read: Read, Grep, Glob; write: Write, Edit, NotebookEdit; run: Bash; web: "
                + "none of Claude Code's, but mcp__dssharness__web_fetch and mcp__dssharness__web_search of the engine's tool server beside them "
                + "- and never Agent, Task, Skill, AskUserQuestion, EnterPlanMode, ExitPlanMode.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "A step with the web takes the harness's own web_fetch - and web_search, where it has a source to search - from the engine's tool "
                + "server: the one MCP server, of type sdk, named dssharness, that the launch's --mcp-config names beside --strict-mcp-config (the "
                + "step's claude-mcp.json), served in the engine's own process over the launch's control channel - its initialize, its tool list "
                + "and its calls, our own code with no MCP library; each call of it is asked of the engine first, by its ask rule, and run by the "
                + "tool runtime below as on the API clients, each address decided and recorded. A launch whose init does not show that server "
                + "connected with its tools taken, or that connects another, never started;",
            prose,
            StringComparison.Ordinal);

        Assert.Contains("a call of it the engine was not asked about and did not allow runs nothing and fails the step.", prose, StringComparison.Ordinal);
        Assert.Contains("The run's reporter is given Read, Grep, Glob alone.", prose, StringComparison.Ordinal);
        Assert.Contains("Every call its own deny rules do not refuse first is asked of the engine before it runs and answered from the policy above", prose, StringComparison.Ordinal);
        Assert.Contains("An answer sent back (below) and a message the engine gives an agent at work are written to the same launch, and a result written before the launch took it is not the answer;", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("each problem by its path, up to", prose, StringComparison.Ordinal);
        Assert.Contains("a handoff asked interrupts the turn and asks the agent for its handoff in the same launch", prose, StringComparison.Ordinal);
        Assert.Contains("Every message is held to the model asked for, a date after its id the same model", prose, StringComparison.Ordinal);
        Assert.Contains("(CLAUDECODE, CLAUDE_PID and CLAUDE_EFFORT, and every name beginning CLAUDE_CODE_, CLAUDE_AGENT_SDK_, CLAUDE_PREVIEW_ or MCP_ but CLAUDE_CODE_GIT_BASH_PATH, which says only where a program is, and a name agentPolicy.keepEnvironment gives back), none of what every child of an agent loses (the tool runtime below)", prose, StringComparison.Ordinal);
        Assert.Contains(
            "the names the engine sets: DISABLE_COMPACT=1, DISABLE_AUTOUPDATER=1, CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1, CLAUDE_CODE_RETRY_WATCHDOG=0, "
                + "CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR=1, and BASH_DEFAULT_TIMEOUT_MS and BASH_MAX_TIMEOUT_MS as agentPolicy.commandTimeoutSeconds "
                + "and maxCommandTimeoutSeconds say, in milliseconds, so a shell command it runs is held to the times of every command an agent runs.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("A key Claude Code reports invalid is passed over, and while the step has done no work the next key is given a new launch", prose, StringComparison.Ordinal);
        Assert.Contains(
            "the claude-code client below asks it of every call, the tool runtime below decides by it each call it runs - the web's of the claude-code client, served through "
                + "the engine's tool server, and every call of the API clients, the claude-api and glm clients below - and no command of this build runs an agent yet.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("a second layer, for the engine is asked about every other call and answers from this policy in its own process, and a call those rules refuse first is counted and recorded as Claude Code's own denial.", prose, StringComparison.Ordinal);
        Assert.Contains(
            "a call it refuses by itself that the engine refuses too - by its own deny rules, a file tool's told by its result alone - is a denial of Claude Code's, counted and recorded once;",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says what the tool runtime is: each tool from the one table, the paths' refusals, the
    /// commands' environment and times, the fetch's bounds and records, the calls of a turn and the answer's cut - each
    /// name and bound read from the type that holds it.
    /// </summary>
    [Fact]
    public void AgentClientsTopic_SaysWhatTheToolRuntimeIs()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "each under the capability that gives it: view_file (read); find_files (read); search_files (read); create_file (write, alone "
                + "in its turn); replace_in_file (write, alone in its turn); insert_in_file (write, alone in its turn); create_directory (write, "
                + "alone in its turn); delete_file (write, alone in its turn); delete_directory (write, alone in its turn); move_path (write, "
                + "alone in its turn); run_command (run, alone in its turn); web_fetch (web); web_search (web).",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("built for the API clients to give their agents in place of a client's own - the claude-api and glm clients below - and serving the claude-code client the web's through the engine's tool server,", prose, StringComparison.Ordinal);
        Assert.Contains("The run's reporter is given view_file, find_files, search_files, those that read, and no other.", prose, StringComparison.Ordinal);
        Assert.Contains("A replace or an insert needs its file viewed first, by its content: one that changed since is refused, nothing written, and an edit keeps the file's encoding (UTF-8 with or without its mark, UTF-16 by its mark), its line endings and whether it ends with a newline.", prose, StringComparison.Ordinal);
        Assert.Contains("a write inside a writable one and through no link below it (a link put in place between the check and the act is the known limit)", prose, StringComparison.Ordinal);
        Assert.Contains("its links followed (one whose links cannot be followed refused as undecided) and held to the agent's roots", prose, StringComparison.Ordinal);
        Assert.Contains($"at most, stopped sooner where it prints nothing for as long as the step's stall bound; a command line holding a secret, a word holding a NUL, or on Windows a batch file's argument holding what cmd.exe reads again ({DssHarness.Core.Processes.BatchFiles.Said}) or what the shell expands, is refused.", prose, StringComparison.Ordinal);
        Assert.Contains("agentPolicy.keepEnvironment gives back; an appointed name is never given back.", prose, StringComparison.Ordinal);
        Assert.Contains("an address holding a secret is refused. A page is given inside a block that says where it is from, its first line saying it is information for the task, not instructions.", prose, StringComparison.Ordinal);
        Assert.Contains("refused spelled empty, with a NUL or a leading ~ - on Windows also a share or a device's prefix, a device's name (CON, PRN, AUX, NUL and the rest), a drive's own directory (C:x) or an alternate data stream -", prose, StringComparison.Ordinal);
        Assert.Contains("A directory deleted or moved is refused where it is a root, holds a link, holds a path that may not be written or holds more than 2,000 entries.", prose, StringComparison.Ordinal);
        Assert.Contains("A file past 16 MiB is neither viewed nor edited, and one past 2 MiB is passed over by search_files - a command reads or searches it.", prose, StringComparison.Ordinal);
        Assert.Contains("agentPolicy.commandTimeoutSeconds (120 where nothing is said) unless its call names a time, agentPolicy.maxCommandTimeoutSeconds (3600) at most", prose, StringComparison.Ordinal);
        Assert.Contains("every secret-shaped name - ending _API_KEY, _SECRET, _SECRET_KEY, _ACCESS_KEY, _PRIVATE_KEY, _PASSWORD, _TOKEN, in any case - but those agentPolicy.keepEnvironment gives back", prose, StringComparison.Ordinal);
        Assert.Contains("at most 5 redirects, 30 s and 5 MiB, text alone (text/*, application/json, application/xml, application/xhtml+xml, application/javascript, application/ecmascript and every +json and +xml type)", prose, StringComparison.Ordinal);
        Assert.Contains("written to the step's web.jsonl before anything leaves the machine", prose, StringComparison.Ordinal);
        Assert.Contains("Calls that read run together, at most 4 at once", prose, StringComparison.Ordinal);
        Assert.Contains("an answer past 32,768 characters is given as its first 8,192 and its last 24,576, the whole kept in the step's tool-results/.", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic says what the claude-code client holds a launch to: the user's settings and CLAUDE.md read
    /// and their hooks off, a credential those settings would give refused by the file and the directory key, the checks
    /// of the protocol that name a version where it does not hold, the stall, a decline and an account at its limit - its
    /// names and its bound read from the types that hold them.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_SaysWhatTheClaudeCodeClientHoldsALaunchTo()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "the user's settings.json in its Claude Code directory and the user's CLAUDE.md reach every agent on this client, but no hook of theirs runs - every launch's settings say disableAllHooks -",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("each launch's start names that settings file and its SHA-256 and says the hooks are off", prose, StringComparison.Ordinal);
        Assert.Contains(
            "(ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN, CLAUDE_CODE_OAUTH_TOKEN, ANTHROPIC_BASE_URL in its env, in any case, or apiKeyHelper) refuses the run before any launch",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("give the client a directory of its own in the slot: CLAUDE_CONFIG_DIR, or the name agentClients.claude-code.configDir gives.", prose, StringComparison.Ordinal);
        Assert.Contains("a launch that does not answer the host's initialize request within 30 s, that works before its init says what it runs as,", prose, StringComparison.Ordinal);
        Assert.Contains("a call it ran that the engine was never asked about, or refused, fails the step by the tool's name", prose, StringComparison.Ordinal);
        Assert.Contains("within the step's stall bound - counted from when the launch answered its initialize request, and from the turn's end for one written while a turn ran - ends it Stalled", prose, StringComparison.Ordinal);
        Assert.Contains("A request the model declines and an account at its limit - a rate limit event", prose, StringComparison.Ordinal);
        Assert.Contains("that says rejected, or a turn Claude Code ends on rate_limit or billing_error after its own tries - each move", prose, StringComparison.Ordinal);
        Assert.Contains("the step to its next entry, a new launch on another model, ending it Refused and", prose, StringComparison.Ordinal);
        Assert.Contains("RateLimited where none is left, with what the declined request was of where the", prose, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_EXECUTABLE, or the name agentClients.claude-code.executable gives", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent-clients topic's protected-path list is the one the policy holds, word for word: a change in it is a
    /// change someone made, in both places at once or in neither.
    /// </summary>
    [Fact]
    public async Task AgentClientsTopic_PinsTheProtectedPaths()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        foreach (var rule in AgentPolicy.ProtectedPathRules)
        {
            Assert.Contains($"Write({rule})", prose, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The README and the architecture say the agents' policy as the help does: one policy, default allow except the
    /// root actions and the restrictions, the command read whole, and the fail-closed rule - and the architecture
    /// names its owners (<c>AgentPolicy</c>, <c>CommandAnalysis</c>, <c>GlobPattern</c>).
    /// </summary>
    [Fact]
    public async Task TheDocumentsSayTheAgentsPolicy()
    {
        Assert.Contains("What an agent may do is one policy, the same on every client", Prose.Document("README.md"), StringComparison.Ordinal);
        Assert.Contains("A repository declares them under `agentPolicy.restrictions`", Prose.Document("README.md"), StringComparison.Ordinal);
        Assert.Contains("This is policy, not a sandbox", Prose.Document("README.md"), StringComparison.Ordinal);
        Assert.Contains("Where this tool cannot decide a call it denies it, never allows", Prose.Document("README.md"), StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains("What an agent of a run may do is one policy, `Agents\\Policy\\AgentPolicy`, the same on every client", architecture, StringComparison.Ordinal);
        Assert.Contains("read whole before it is matched, `Agents\\Policy\\CommandAnalysis`", architecture, StringComparison.Ordinal);
        Assert.Contains("no agent starts while the tool itself runs elevated (`IHostPlatform.IsPrivilegedProcess`", architecture, StringComparison.Ordinal);
        Assert.Contains("Where the policy cannot decide a call it denies it, never allows", architecture, StringComparison.Ordinal);

        // The credential-printing refusal and the quote-aware reading of expansions, in every document that says them.
        Assert.Contains(
            "and so is any command that prints a credential (`gh auth token`, `git credential fill`, `git credential-manager get`,",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains("(`AgentPolicy.CredentialPrintingCommands`, pinned by a test and printed by `help agent-clients`", architecture, StringComparison.Ordinal);
        Assert.Contains(
            "an expansion is read only where the shell would make it, for the reader keeps, per word, which of its parts were quoted",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The README and the architecture say the claude-code client and how it signs in as the help does, and the
    /// architecture names its owners by the types that are them.
    /// </summary>
    [Fact]
    public async Task TheDocumentsSayTheClaudeCodeClient()
    {
        var readme = Prose.Document("README.md");

        Assert.Contains("signs in by the client's `auth` (`agentClients.claude-code.auth`): `login`, the machine's login,", readme, StringComparison.Ordinal);
        Assert.Contains("The `claude-code` client starts Claude Code once for each launch, a new session every time", readme, StringComparison.Ordinal);
        Assert.Contains("Every call its own deny rules do not refuse first is asked of the engine before it runs and answered from the policy", readme, StringComparison.Ordinal);
        Assert.Contains("but no hook of theirs runs: every launch's settings say `disableAllHooks`", readme, StringComparison.Ordinal);
        Assert.Contains("The client holds every launch to the protocol itself", readme, StringComparison.Ordinal);
        Assert.Contains("Its `configDir` and `executable` name the variables of the slot that hold a specific login's Claude Code directory", readme, StringComparison.Ordinal);
        Assert.Contains("A program the slot names (`CLAUDE_EXECUTABLE`, or the name the client's `executable` gives)", readme, StringComparison.Ordinal);
        Assert.Contains("and for each setting a client reads from the slot and each web-search source's key `present` or `absent` (none is needed)", readme, StringComparison.Ordinal);
        Assert.Contains(
            "A step with the web takes the harness's own `web_fetch` - and `web_search`, where it has a source to search - from the engine's tool server: "
                + "the one MCP server, of type `sdk`, named `dssharness`, that the launch's `--mcp-config` names beside `--strict-mcp-config` (the step's `claude-mcp.json`)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("a call of it the engine did not allow runs nothing and fails the step.", readme, StringComparison.Ordinal);
        Assert.Contains("a call those rules refuse before the engine is asked is counted and recorded as Claude Code's own denial.", readme, StringComparison.Ordinal);
        Assert.Contains(
            "the tool runtime below decides by it each call it runs - the web's of the `claude-code` client, served through the engine's tool "
                + "server, and every call of the API clients, the `claude-api` and `glm` clients below - and no command of this build runs an agent yet.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "`web`: none of Claude Code's, but `mcp__dssharness__web_fetch` and `mcp__dssharness__web_search` of the engine's tool server beside them)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "served in the engine's own process over the launch's control channel; each call of it is asked of the engine first, by its ask "
                + "rule, and run by the tool runtime below as on the API clients. A launch whose init does not show that server connected with its "
                + "tools taken, or that connects another, never started,",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("which the same query again is answered from; written by every client whose step searches", readme, StringComparison.Ordinal);
        Assert.Contains(
            "a call it refuses by itself that the engine refuses too - by its own deny rules, a file tool's told by its result alone - is a denial of Claude Code's, counted and recorded once;",
            readme,
            StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains($"The first client that asks it is `Agents\\ClaudeCode\\{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeClient)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLaunch),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeToolDecider),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools),
            nameof(DssHarness.Core.Agents.StructuredOutputGate),
            nameof(DssHarness.Core.Agents.AgentHandoffSignal),
            nameof(DssHarness.Core.Agents.AgentCredential),
            nameof(DssHarness.Core.Agents.AgentAuth),
            nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeUserSettings),
            nameof(DssHarness.Core.Agents.AgentAccountLimit),
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLaunch)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLaunch.McpConfiguration)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools.ServerTools)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeCodeTools.ServedName)}",
            $"{nameof(DssHarness.Core.Agents.AgentToolRunner)}.{nameof(DssHarness.Core.Agents.AgentToolRunner.Harness)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine.CallResult)}.{nameof(DssHarness.Core.Agents.ClaudeCode.ClaudeLine.CallResult.RefusedByRule)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The loop the API clients share is said alike by help agent-clients and the README - each bound and number read from
    /// the type that holds it - and the architecture names its owners by the types that are them.
    /// </summary>
    [Fact]
    public void TheLoopTheApiClientsShare_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var interrupted = DssHarness.Core.Agents.Runtime.AgentToolRuntime.Interrupted(DssHarness.Core.Agents.Loop.ToolLoop.Pausing);
        var nudges = DssHarness.Core.Agents.Loop.ToolLoop.MostNudges;
        var pauses = DssHarness.Core.Agents.Loop.ToolLoop.MostPausesInARow;
        var tries = DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetries;
        var (first, longest) = (DssHarness.Core.Agents.Loop.ToolLoop.FirstWait.TotalSeconds, DssHarness.Core.Agents.Loop.ToolLoop.LongestDoubledWait.TotalSeconds);
        var wait = DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetryWait.TotalSeconds;

        Assert.Contains(
            $"A turn cut off at its length that called no tool, its request holding it to no schema, is stored and nudged on, {nudges} times at most; one cut off while it called a tool - its calls never run - before it wrote anything, or while its request held it to a schema, which the answer to a nudge would be held to whole, is thrown away and asked again with twice the room, up to the most; past either the step is Truncated. "
                + $"A turn the provider paused is sent again with nothing added, {pauses} in a row failing the step. An answer gone on from, cut off or paused, is every piece of it. A failed request is asked again up to {tries} times after a wait that doubles from {first} s to {longest} s, "
                + "or the one the provider names; a service failing past them, or naming such a wait, fails the step, and one silent past the stall bound - nothing of it heard, not even the provider's keep-alive - stalls it; "
                + "a model the provider does not serve the key never starts the step before its first answer and fails it after. The engine's fallback works inside the one conversation: a key "
                + $"the provider refuses - said, and passed over for the rest of the run - an account at its limit (a rate limit past those tries, or naming a wait past {wait} s: "
                + "the model's on the key it was sent with, or the key's whole where its balance is spent, each named) and a "
                + "request the model declined each move the run to its next entry of the same provider and client, the same "
                + "request sent again with the model or the key changed and nothing else, the declined turn thrown away with any "
                + "part of an answer it had written; at a limit the same model's next key is tried first and every entry the "
                + "limit covers is passed over until it lifts, a declined request passes over the model's other keys, and one the "
                + "provider marks about what the prompt asks (reasoning_extraction) moves no step. "
                + $"A handoff asked abandons the request under way and cuts the calls running, each answered '{interrupted}', then asks the agent in one more message of the conversation, held to the handoff's bound instead of the step's time, no tool run in that turn;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("A turn is one answer of the agent's - a request the provider paused, or one thrown away and asked again, goes on as the same turn - counted against the step's maxTurns but a handoff turn;", prose, StringComparison.Ordinal);
        Assert.Contains("A turn is one answer of the agent's - a request the provider paused, or one thrown away and asked again, goes on as the same turn - counted against the step's `maxTurns` but a handoff turn;", readme, StringComparison.Ordinal);
        Assert.Contains("no request but the handoff turn's is sent once the step's maxBudgetUsd is spent, and a step held to a budget on a model missing a price of what its provider's requests are billed by - input, cache reads and output, and the cache writes of a provider whose requests write one, in the model's large-prompt set too where it has one - is never started, naming the key to fill.", prose, StringComparison.Ordinal);
        Assert.Contains("and a step held to a budget on a model missing a price of what its provider's requests are billed by - input, cache reads and output, and the cache writes of a provider whose requests write one, in the model's large-prompt set too where it has one - is never started, naming the key to fill.", readme, StringComparison.Ordinal);
        Assert.Contains("The loop the claude-api and glm clients share (ToolLoop) runs a step of either over the provider each gives it - the Claude API's and Z.AI's below.", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"a step that goes on in it sends what it began with; where a provider begins sending it shortened to keep it within its model's context, the point is kept beside it ({DssHarness.Core.Agents.StoredConversation.ShortenedFileName}) and given with every later request, a notice saying so once.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"a step that goes on in it sends what it began with; where a provider begins sending it shortened to keep it within its model's context, the point is kept beside it (`{DssHarness.Core.Agents.StoredConversation.ShortenedFileName}`) and given with every later request, a notice saying so once.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"under the agent's {DssHarness.Core.Orchestration.OrchestratorLayout.SessionsDirectoryName}/<id>/, each message as it was sent and received", prose, StringComparison.Ordinal);

        Assert.Contains(
            $"A turn cut off at its length that called no tool, its request holding it to no schema, is stored and nudged on, {nudges} times at most; one cut off while it called a tool - its calls never run - before it wrote anything, or while its request held it to a schema, which the answer to a nudge would be held to whole, is thrown away and asked again with twice the room, up to the most; past either the step is `Truncated`. "
                + $"A turn the provider paused is sent again with nothing added, {pauses} in a row failing the step. An answer gone on from, cut off or paused, is every piece of it. A failed request is asked again up to {tries} times after a wait that doubles from {first} s to {longest} s, "
                + $"or the one the provider names; a service failing past them, or naming such a wait, fails the step, and one silent past the stall bound - nothing of it heard, not even the provider's keep-alive - stalls it; "
                + "a model the provider does not serve the key never starts the step before its first answer and fails it after. The engine's fallback works inside the one conversation: a key "
                + $"the provider refuses - said, and passed over for the rest of the run - an account at its limit (a rate limit past those tries, or naming a wait past {wait} s: "
                + "the model's on the key it was sent with, or the key's whole where its balance is spent, each named) and a "
                + "request the model declined each move the run to its next entry of the same provider and client, the same "
                + "request sent again with the model or the key changed and nothing else, the declined turn thrown away with any "
                + "part of an answer it had written; at a limit the same model's next key is tried first and every entry the "
                + "limit covers is passed over until it lifts, a declined request passes over the model's other keys, and one the "
                + "provider marks about what the prompt asks (`reasoning_extraction`) moves no step. "
                + $"A handoff asked abandons the request under way and cuts the calls running, each answered `{interrupted}`, then asks the agent in one more message of the conversation, held to the handoff's bound instead of the step's time, no tool run in that turn;",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"under the agent's `{DssHarness.Core.Orchestration.OrchestratorLayout.SessionsDirectoryName}/<id>/`, each message", readme, StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains($"`Agents\\Loop\\{nameof(DssHarness.Core.Agents.Loop.ToolLoop)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.Loop.IChatProvider),
            nameof(DssHarness.Core.Agents.Loop.ChatFailure),
            $"{nameof(DssHarness.Core.Agents.Loop.IChatProvider)}.{nameof(DssHarness.Core.Agents.Loop.IChatProvider.Uses)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatPart)}.{nameof(DssHarness.Core.Agents.Loop.ChatPart.Heard)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatPart)}.{nameof(DssHarness.Core.Agents.Loop.ChatPart.Used)}",
            nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms),
            nameof(DssHarness.Core.Agents.Runtime.AgentTurnCut),
            nameof(DssHarness.Core.Agents.AgentModelIds),
            nameof(DssHarness.Core.Agents.AgentEndings),
            nameof(DssHarness.Core.Agents.AgentUsageTally),
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost)}.Unserved",
            nameof(DssHarness.Core.Agents.StoredConversation),
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.RunTurnAsync)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.Interrupted)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.NotRun)}",
            $"{nameof(DssHarness.Core.Agents.AgentPrices)}.{nameof(DssHarness.Core.Agents.AgentPrices.BudgetRefusal)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoop)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoop.Backoff)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoop)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoop.FirstMaxTokens)}",
            $"{nameof(DssHarness.Core.Agents.AgentMessages)}.{nameof(DssHarness.Core.Agents.AgentMessages.TryTake)}",
            $"{nameof(DssHarness.Core.Agents.AgentRun)}.{nameof(DssHarness.Core.Agents.AgentRun.SessionsDirectory)}",
            $"{nameof(DssHarness.Core.Orchestration.OrchestratorLayout)}.{nameof(DssHarness.Core.Orchestration.OrchestratorLayout.SessionDirectoryIn)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatPart)}.{nameof(DssHarness.Core.Agents.Loop.ChatPart.Shortened)}",
            $"{nameof(DssHarness.Core.Agents.StoredConversation)}.{nameof(DssHarness.Core.Agents.StoredConversation.Shorten)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatRequest)}.{nameof(DssHarness.Core.Agents.Loop.ChatRequest.ShortenedBefore)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The claude-api client is said by the help and the README alike - its version, its betas, its cache, its keys, what
    /// each failure is - each name and number read from the type that holds it, and the architecture names its owners.
    /// </summary>
    [Fact]
    public void TheClaudeApiClient_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");
        var (tries, wait) = (DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetries, DssHarness.Core.Agents.Loop.ToolLoopTerms.DefaultMaxRetryWait.TotalSeconds);
        const string Version = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Version;
        const string Context = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.ContextManagementBeta;
        const string Credit = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.FallbackCreditBeta;
        const string Clear = DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.ClearToolUses;

        Assert.Contains(
            $"each request is posted to its provider's api with /{DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.MessagesPath} after it, signed with a key of the provider's (x-api-key), under anthropic-version: {Version}, and its answer streamed.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains($"old tool uses cleared by the server as the context fills ({Clear}, under the {Context} beta), each clearing said in a notice;", prose, StringComparison.Ordinal);
        Assert.Contains("the system prompt and the conversation cached for agentClients.claude-api.cacheTtl (5m or 1h, 5m where nothing is said), so a budget needs the model's price of that cache's writes too: priceCacheWrite, or priceCacheWrite1h for an hour.", prose, StringComparison.Ordinal);
        Assert.Contains($"A step with a fallback that can go on here sends the {Credit} beta, and a request the model declines moves the step to its next model of the same provider and client - the same request sent again with the model changed and the credit the API gave sent back as its fallback_credit_token, a failure naming it answered by the same request without it - Refused with its category only where none is left; no request carries fallbacks or a server-side-fallback-* beta,", prose, StringComparison.Ordinal);
        Assert.Contains("Two keys are claude-api's alone: betas, sent beside the tool's own, each one word and never the API's fallback's, and cacheTtl; three are both API clients':", prose, StringComparison.Ordinal);
        Assert.Contains($"refused where it sets what the tool sends itself or, here, fallbacks or a sampling parameter (temperature, top_p, top_k), which anthropic-version {Version} refuses on every model, and free to add a tool choice or thinking.budget_tokens where the step's model takes one; and maxRetries and maxRetryWaitSeconds, the loop's tries and longest wait ({tries} and {wait} where nothing is said).", prose, StringComparison.Ordinal);
        Assert.Contains("a 403, a 404, or a 400 answering a conversation's first request - a model the key's organization is not given, such as one that needs a data retention the organization does not keep - is a model the provider does not serve the key, naming it;", prose, StringComparison.Ordinal);
        Assert.Contains($"A spend limit reached - the organization's monthly cap, a 429 whose error names {DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures.SpendCapCode}, or one the organization or a workspace set, a 400 that says so - is the key's limit, every entry on that key passed over until the date the API names; a 401 is a key refused, said and passed over for the rest of the run; a 402 the key's balance spent, every entry on that key passed over; a 429 the model's limit on that key, waited out within those tries and the same model's next key tried past them;", prose, StringComparison.Ordinal);
        Assert.Contains("every request's usage - one that ended without its turn, failed, silent or left, by what it said it used so far - is counted at the model's prices,", prose, StringComparison.Ordinal);

        Assert.Contains(
            $"signed with a key of the provider's (`x-api-key`), under `anthropic-version: {Version}`, and its answer streamed.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"old tool uses cleared by the server as the context fills (`{Clear}`, under the `{Context}` beta), each clearing said in a notice;", readme, StringComparison.Ordinal);
        Assert.Contains("`agentClients.claude-api.cacheTtl` (`5m` or `1h`, `5m` where nothing is said), so a budget needs the model's price of that cache's writes too: `priceCacheWrite`, or `priceCacheWrite1h` for an hour.", readme, StringComparison.Ordinal);
        Assert.Contains($"A step with a fallback that can go on here sends the `{Credit}` beta, and a request the model declines moves the step to its next model of the same provider and client - the same", readme, StringComparison.Ordinal);
        Assert.Contains("Two keys are `claude-api`'s alone: `betas`, sent beside the tool's own, each one word and never the API's fallback's, and `cacheTtl`; three are both API clients':", readme, StringComparison.Ordinal);
        Assert.Contains($"refused where it sets what the tool sends itself or, here, `fallbacks` or a sampling parameter (`temperature`, `top_p`, `top_k`), which `anthropic-version {Version}` refuses on every model, and free to add a tool choice or `thinking.budget_tokens` where the step's model takes one; and `maxRetries` and `maxRetryWaitSeconds`, the loop's tries and longest wait ({tries} and {wait} where nothing is said).", readme, StringComparison.Ordinal);
        Assert.Contains("a 403, a 404, or a 400 answering a conversation's first request - a model the key's organization is not given, such as one that needs a data retention the organization does not keep - is a model the provider does not serve the key, naming it;", readme, StringComparison.Ordinal);
        Assert.Contains($"A spend limit reached - the organization's monthly cap, a 429 whose error names `{DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures.SpendCapCode}`, or one the organization or a workspace set, a 400 that says so - is the key's limit, every entry on that key passed over until the date the API names; a 401 is a key refused, said and passed over for the rest of the run; a 402 the key's balance spent, every entry on that key passed over; a 429 the model's limit on that key, waited out within those tries and the same model's next key tried past them;", readme, StringComparison.Ordinal);
        Assert.Contains("every request's usage - one that ended without its turn, failed, silent or left, by what it said it used so far - is counted at the model's prices,", readme, StringComparison.Ordinal);

        Assert.Contains($"`Agents\\ClaudeApi\\{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiClient)}`", architecture, StringComparison.Ordinal);
        Assert.Contains($"`Agents\\Http\\{nameof(DssHarness.Core.Agents.Http.ApiConnection)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiProvider),
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest),
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures),
            nameof(DssHarness.Core.Agents.AgentEntries),
            nameof(DssHarness.Core.Agents.Http.ApiEvent),
            $"{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest)}.{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Owned)}",
            nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.ExtrasProblems),
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The envelope every client asks for where a step names an output is said alike by help agent-clients and the README -
    /// each member, file, bound and number from its owner - and the architecture names its owners by the types that are them.
    /// </summary>
    [Fact]
    public void TheEnvelope_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");
        var repairs = new DssHarness.Core.Agents.AgentOutput("o", "{}").MaxRepairs;
        static string Members(System.Text.Json.Nodes.JsonObject shape) => string.Join(", ", shape["properties"]!.AsObject().Select(member => member.Key));
        var (assumption, question) = (Members(DssHarness.Core.Agents.AgentEnvelope.Assumption()), Members(DssHarness.Core.Agents.AgentEnvelope.Question()));

        Assert.Contains("Where a step names an output, every client asks the agent for one envelope - notes, output, assumptions, and questions where the step may ask and has rounds left - and the harness's own validator holds the answer to it, whatever a client was sent.", prose, StringComparison.Ordinal);
        Assert.Contains("Where a step names an output, every client asks the agent for one envelope - `notes`, `output`, `assumptions`, and `questions` where the step may ask and has rounds left - and the harness's own validator holds the answer to it, whatever a client was sent.", readme, StringComparison.Ordinal);
        Assert.Contains($"The notes are not empty, and are kept, masked, as the step's {DssHarness.Core.Orchestration.OrchestratorLayout.StepNotesFileName} beside its result;", prose, StringComparison.Ordinal);
        Assert.Contains($"the output is read against the step's whole schema, each problem named by its path under $.output, a text an enum or a const holds in another case read as the schema spells it; each assumption is {{{assumption}}}; each question is {{{question}}}, with at least {DssHarness.Core.Agents.AgentEnvelope.FewestOptions} options, no two alike, recommending one of them word for word; no text of either is empty.", prose, StringComparison.Ordinal);
        Assert.Contains($"the output is read against the step's whole schema, each problem named by its path under `$.output`, a text an `enum` or a `const` holds in another case read as the schema spells it; each assumption is `{{{assumption}}}`; each question is `{{{question}}}`, with at least {DssHarness.Core.Agents.AgentEnvelope.FewestOptions} options, no two alike, recommending one of them word for word; no text of either is empty.", readme, StringComparison.Ordinal);
        Assert.Contains("The output is null only in a handoff, whose notes are the handoff, or where the answer asks, and the step then ends Asked with its questions; an answer with both is sent back, and so is one that asks where the step may not or has asked as often as it may, told to write what it would have asked under assumptions.", prose, StringComparison.Ordinal);
        Assert.Contains("The output is null only in a handoff, whose notes are the handoff, or where the answer asks, and the step then ends `Asked` with its questions; an answer with both is sent back, and so is one that asks where the step may not or has asked as often as it may, told to write what it would have asked under assumptions.", readme, StringComparison.Ordinal);
        Assert.Contains("The claude-code client holds the answer to the envelope by --json-schema;", prose, StringComparison.Ordinal);
        Assert.Contains("The `claude-code` client holds the answer to the envelope by `--json-schema`;", readme, StringComparison.Ordinal);
        Assert.Contains("an answer sent back (below) and a message the engine gives an agent at work are written to the same launch;", readme, StringComparison.Ordinal);
        Assert.Contains($"the claude-api client sends it as output_config.format on every request, the handoff's too, each array's minItems above {DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.MostLeastItems} and every maxItems moved into the array's description in words, for structured outputs take no other - and where the API refuses it as more than it compiles, the run's later requests carry no format, and a message of the conversation says it in words, the system prompt untouched;", prose, StringComparison.Ordinal);
        Assert.Contains($"the `claude-api` client sends it as `output_config.format` on every request, the handoff's too, each array's `minItems` above {DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.MostLeastItems} and every `maxItems` moved into the array's description in words, for structured outputs take no other - and where the API refuses it as more than it compiles, the run's later requests carry no format, and a message of the conversation says it in words, the system prompt untouched;", readme, StringComparison.Ordinal);
        Assert.Contains("and asks an answer that read as no JSON again in JSON alone - no tool offered, response_format a JSON object.", prose, StringComparison.Ordinal);
        Assert.Contains("and asks an answer that read as no JSON again in JSON alone - no tool offered, `response_format` a JSON object.", readme, StringComparison.Ordinal);
        Assert.Contains($"An answer that does not read is sent back in the same conversation, {repairs} times at most, and the step is then InvalidOutput.", prose, StringComparison.Ordinal);
        Assert.Contains($"An answer that does not read is sent back in the same conversation, {repairs} times at most, and the step is then `InvalidOutput`.", readme, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.AgentEnvelope),
            nameof(DssHarness.Core.Agents.AgentAssumption),
            nameof(DssHarness.Core.Agents.AgentQuestion),
            nameof(DssHarness.Core.Agents.StructuredOutputGate),
            $"{nameof(DssHarness.Core.Agents.AgentOutput)}.{nameof(DssHarness.Core.Agents.AgentOutput.AskRounds)}",
            $"{nameof(DssHarness.Core.Schemas.JsonSchemaSubset)}.{nameof(DssHarness.Core.Schemas.JsonSchemaSubset.Respelled)}",
            $"{nameof(DssHarness.Core.Schemas.JsonSchemaSubset)}.{nameof(DssHarness.Core.Schemas.JsonSchemaSubset.SchemasIn)}",
            $"{nameof(DssHarness.Core.Orchestration.OrchestratorLayout)}.{nameof(DssHarness.Core.Orchestration.OrchestratorLayout.StepNotesFileName)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatRequest)}.{nameof(DssHarness.Core.Agents.Loop.ChatRequest.Schema)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatRequest)}.{nameof(DssHarness.Core.Agents.Loop.ChatRequest.JsonAlone)}",
            $"{nameof(DssHarness.Core.Agents.Loop.IChatProvider)}.{nameof(DssHarness.Core.Agents.Loop.IChatProvider.SendsSchema)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest)}.{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Sent)}",
            $"{nameof(DssHarness.Core.Agents.AgentEnvelope)}.{nameof(DssHarness.Core.Agents.AgentEnvelope.InWords)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ChatFailureKind)}.{nameof(DssHarness.Core.Agents.Loop.ChatFailureKind.SchemaRefused)}",
            $"{nameof(DssHarness.Core.Agents.AgentMessageKind)}.{nameof(DssHarness.Core.Agents.AgentMessageKind.EnvelopeInWords)}",
            $"{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures)}.{nameof(DssHarness.Core.Agents.ClaudeApi.ClaudeApiFailures.RefusesTheFormat)}",
            $"{nameof(DssHarness.Core.Agents.AgentAssumption)}.{nameof(DssHarness.Core.Agents.AgentAssumption.Problems)}",
            $"{nameof(DssHarness.Core.Agents.AgentQuestion)}.{nameof(DssHarness.Core.Agents.AgentQuestion.Problems)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The glm client is said by the help and the README alike - its request, its efforts, the reasoning it keeps, where
    /// it sends a conversation shortened, its keys and each of Z.AI's codes from the one table of them - each name and
    /// number read from the type that holds it, and the architecture names its owners.
    /// </summary>
    [Fact]
    public void TheGlmClient_IsSaidByTheHelpAndTheDocuments()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");
        var (shorten, full, whole) = (Percent(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider.ShortenAt), Percent(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider.FullAt), DssHarness.Core.Agents.ZaiApi.ZaiApiProvider.WholeTurns);
        const string Path = DssHarness.Core.Agents.ZaiApi.ZaiApiRequest.CompletionsPath;

        Assert.Contains($"each request is posted to its provider's api with /{Path} after it, signed with a key of the provider's (Authorization: Bearer), and its answer streamed.", prose, StringComparison.Ordinal);
        Assert.Contains($"each request is posted to its provider's `api` with `/{Path}` after it, signed with a key of the provider's (`Authorization: Bearer`), and its answer streamed.", readme, StringComparison.Ordinal);
        Assert.Contains("the model's thinking on, the step's effort as reasoning_effort (low, medium, high, xhigh, max); and max_tokens - no sampling parameter or tool choice of the tool's own, which requestExtras may add, and no tool of Z.AI's, for the web is the harness's own.", prose, StringComparison.Ordinal);
        Assert.Contains("the model's thinking on, the step's effort as `reasoning_effort` (`low`, `medium`, `high`, `xhigh`, `max`); and `max_tokens` - no sampling parameter or tool choice of the tool's own, which `requestExtras` may add, and no tool of Z.AI's, for the web is the harness's own.", readme, StringComparison.Ordinal);
        Assert.Contains("unless agentClients.glm.preserveThinking is true (false where nothing is said), which keeps it (thinking.clear_thinking: false).", prose, StringComparison.Ordinal);
        Assert.Contains("unless `agentClients.glm.preserveThinking` is true (false where nothing is said), which keeps it (`thinking.clear_thinking: false`).", readme, StringComparison.Ordinal);
        Assert.Contains($"passes {shorten} of the model's contextTokens, the answers to the calls of every turn but the last {whole} that called tools go out in a short form that says so, from that request on - the calls and the reasoning as they were, and what is stored whole; past {full} after that, the context is exhausted (ContextExhausted).", prose, StringComparison.Ordinal);
        Assert.Contains($"passes {shorten} of the model's `contextTokens`, the answers to the calls of every turn but the last {whole} that called tools go out in a short form that says so, from that request on - the calls and the reasoning as they were, and what is stored whole; past {full} after that, the context is exhausted (`ContextExhausted`).", readme, StringComparison.Ordinal);
        Assert.Contains("and one is glm's alone: preserveThinking.", prose, StringComparison.Ordinal);
        Assert.Contains("and one is `glm`'s alone: `preserveThinking`.", readme, StringComparison.Ordinal);

        // Every row of the codes' table is said, in its order, with what the loop takes it for.
        foreach (var row in DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Codes)
        {
            Assert.Contains($"{row.What} ({string.Join(", ", row.Codes)})", prose, StringComparison.Ordinal);
            Assert.Contains($"{row.What} ({string.Join(", ", row.Codes)})", readme, StringComparison.Ordinal);
        }

        Assert.Contains("a usage limit that names when it resets (1308, 1310, 1316, 1317, 1318, 1319, 1320, 1321) is the key's limit, every", prose, StringComparison.Ordinal);
        Assert.Contains("entry on that key passed over until it resets;", readme, StringComparison.Ordinal);
        Assert.Contains("a model unknown, an API the key may not call or a plan without the model (1211, 1220, 1311) is a model the provider does not serve the key;", prose, StringComparison.Ordinal);
        Assert.Contains("a plan that does not cover the call or has ended (1309, 1313, 1314, 1315) fails the step. A code that list does not hold is read by the status:", prose, StringComparison.Ordinal);
        Assert.Contains("read by the status: a 401 is a key refused, said and passed over for the rest of the run; a 429 is the model's limit on that key, waited out within the tries and the same model's next key tried past them; a 413 is the context exhausted (ContextExhausted); a 403 or a 404 is a model the provider does not serve the key; a request timed out, a conflict or a server's error is asked again, and any other fails the step.", prose, StringComparison.Ordinal);
        Assert.Contains("read by the status: a 401 is a key refused, said and passed over for the rest of the run; a 429 is the model's limit on that key, waited out within the tries and the same model's next key tried past them; a 413 is the context exhausted (`ContextExhausted`); a 403 or a 404 is a model the provider does not serve the key; a request timed out, a conflict or a server's error is asked again, and any other fails the step.", readme, StringComparison.Ordinal);
        Assert.Contains("a failure Z.AI names is said in its own words.", prose, StringComparison.Ordinal);
        Assert.Contains("a failure Z.AI names is said in its own words.", readme, StringComparison.Ordinal);

        // Every row of the statuses' table is said, with what the loop takes it for.
        foreach (var row in DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Statuses)
        {
            Assert.Contains($"{string.Join(" or ", row.Statuses.Select(status => $"a {status}"))} is ", prose, StringComparison.Ordinal);
        }
        Assert.Contains("a plan that does not cover the call or has ended (1309, 1313, 1314, 1315) fails the step. A code that list does not hold is read by the status:", readme, StringComparison.Ordinal);
        Assert.Contains($"(1211, 1220, 1311) is a model the provider does not serve the key;", readme, StringComparison.Ordinal);

        Assert.Contains($"`Agents\\ZaiApi\\{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiClient)}`", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider),
            nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest),
            nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures),
            nameof(DssHarness.Core.Agents.Http.ApiBody),
            nameof(DssHarness.Core.Agents.Http.ApiAnswers),
            nameof(DssHarness.Core.Agents.Http.OwnedMember),
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms.Of)}",
            $"{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms)}.{nameof(DssHarness.Core.Agents.Loop.ToolLoopTerms.Unstartable)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest.Owned)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiRequest.ShortAnswer)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider.ShortenAt)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiProvider.FullAt)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Codes)}",
            $"{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures)}.{nameof(DssHarness.Core.Agents.ZaiApi.ZaiApiFailures.Statuses)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }

        static string Percent(double share) => $"{(int)Math.Round(share * 100)}%";
    }

    /// <summary>
    /// The README says the tool runtime as the help does - each tool, each bound and default read from the type that holds
    /// it - and the architecture names its owners by the types that are them.
    /// </summary>
    [Fact]
    public void TheDocumentsSayTheToolRuntime()
    {
        var readme = Prose.Document("README.md");
        var defaults = DssHarness.Core.Agents.AgentCommandSettings.Default;
        var suffixes = DssHarness.Core.Agents.AgentChildEnvironment.SecretSuffixes.Select(suffix => $"`{suffix}`").ToList();

        Assert.All(DssHarness.Core.Agents.Runtime.AgentTools.All(defaults), tool => Assert.Contains($"`{tool.Name}`", readme, StringComparison.Ordinal));
        var schemes = DssHarness.Core.Agents.Policy.AgentPolicy.SlashedSchemes.Select(scheme => $"`{scheme}`").ToList();
        Assert.Contains(
            $"An address whose slashes after its scheme readers read apart - any scheme but its two slashes and a host after them (`http:/\\host`, `gopher:/host`, `http:///host`), or no slash after an {schemes[0]}, {string.Join(", ", schemes.Skip(1).SkipLast(1))} or {schemes[^1]} scheme (`https:host`) - or whose host is spelled beyond ASCII (its `xn--` form is read as written), names no host the policy can decide, and is denied.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("the run's reporter is given those that read and no other.", readme, StringComparison.Ordinal);
        Assert.Contains(
            "built for the API clients to give their agents in place of a client's own - the `claude-api` and `glm` clients below - and serving the `claude-code` client the web's through the engine's tool server:",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"its links followed (one whose links cannot be followed refused as {DssHarness.Core.Agents.Policy.AgentPolicy.UndecidedRule}) and held to the agent's roots", readme, StringComparison.Ordinal);
        Assert.Contains($"holds more than {DssHarness.Core.Agents.Runtime.AgentTools.MostDeletedEntries:N0} entries.", readme, StringComparison.Ordinal);
        Assert.Contains($"A file past {DiskSpace.Size(DssHarness.Core.Agents.Runtime.AgentTools.MostViewedBytes)} is neither viewed nor edited, and one past {DiskSpace.Size(DssHarness.Core.Agents.Runtime.AgentFiles.MostSearchedBytes)} is passed over by search_files - a command reads or searches it.", readme, StringComparison.Ordinal);
        Assert.Contains(
            $"`agentPolicy.commandTimeoutSeconds` ({defaults.TimeoutSeconds}) unless its call names a time, `agentPolicy.maxCommandTimeoutSeconds` "
                + $"({defaults.MaxTimeoutSeconds}) at most, stopped sooner where it prints nothing for as long as the step's stall bound; a command "
                + $"line holding a secret, a word holding a NUL, or on Windows a batch file's argument holding what `cmd.exe` reads again (`{DssHarness.Core.Processes.BatchFiles.Said.Replace(", a", "`, a", StringComparison.Ordinal)}) or what the shell expands, is refused.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"(ending {string.Join(", ", suffixes[..^1])} or {suffixes[^1]}) but those `agentPolicy.keepEnvironment` gives back.", readme, StringComparison.Ordinal);
        Assert.Contains(
            $"at most {DssHarness.Core.Web.HarnessFetch.MostRedirects} redirects, {DssHarness.Core.Web.HarnessFetch.Bound.TotalSeconds} s and "
                + $"{DiskSpace.Size(DssHarness.Core.Web.HarnessFetch.MostBytes)}, text alone (`text/*`, "
                + $"{string.Join(", ", DssHarness.Core.Web.HarnessFetch.TextTypes.Select(type => $"`{type}`"))} and every `+json` and `+xml` type)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"written to the step's `{DssHarness.Core.Agents.AgentCallRecords.WebFileName}` before anything leaves the machine", readme, StringComparison.Ordinal);
        Assert.Contains($"Calls that read run together, at most {DssHarness.Core.Agents.Runtime.AgentToolRuntime.MostTogether} at once", readme, StringComparison.Ordinal);
        Assert.Contains(
            $"an answer past {DssHarness.Core.Agents.AgentCallRecords.ResultCap:N0} characters is given as its first {DssHarness.Core.Agents.AgentCallRecords.ResultHead:N0} "
                + $"and its last {DssHarness.Core.Agents.AgentCallRecords.ResultCap - DssHarness.Core.Agents.AgentCallRecords.ResultHead:N0}, the whole kept in the step's "
                + $"`{DssHarness.Core.Agents.AgentCallRecords.ToolResultsDirectoryName}/`.",
            readme,
            StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains($"`Agents\\Runtime\\{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}`", architecture, StringComparison.Ordinal);
        Assert.Contains($"`Agents\\Runtime\\{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer)}`", architecture, StringComparison.Ordinal);
        Assert.Contains($"`Web\\{nameof(DssHarness.Core.Web.HarnessFetch)}`", architecture, StringComparison.Ordinal);
        Assert.Contains(
            $"recorded by the one `{nameof(DssHarness.Core.Agents.AgentCallRecords)}` (`{DssHarness.Core.Agents.AgentCallRecords.DenialsFileName}`, "
                + $"`{DssHarness.Core.Agents.AgentCallRecords.WebFileName}`, `{DssHarness.Core.Agents.AgentCallRecords.ToolResultsDirectoryName}/`)",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains($"A turn runs its reads together, at most {DssHarness.Core.Agents.Runtime.AgentToolRuntime.MostTogether} at once", architecture, StringComparison.Ordinal);
        Assert.Contains($"the names `{nameof(DssHarness.Core.Agents.Policy.ClaudeSettings)}.{nameof(DssHarness.Core.Agents.Policy.ClaudeSettings.Launched)}` sets", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost.Serve)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost.Open)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer.Read)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer.CallAsync)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.ServeAsync)}",
            nameof(DssHarness.Core.Agents.Runtime.AgentTools),
            nameof(DssHarness.Core.Schemas.JsonSchemaSubset),
            nameof(DssHarness.Core.Agents.Runtime.ConfinedPaths),
            nameof(DssHarness.Core.Agents.Runtime.AgentFiles),
            nameof(EditedText),
            nameof(EncodedText),
            $"{nameof(DssHarness.Core.Codebase.CodeFileText)}.{nameof(DssHarness.Core.Codebase.CodeFileText.Spans)}",
            nameof(PhaseRunner),
            nameof(DssHarness.Core.Agents.AgentCommandSettings),
            nameof(DssHarness.Core.Web.WebAddresses),
            nameof(DssHarness.Core.Web.WebPageText),
            nameof(DssHarness.Core.Agents.AgentCallRecords),
            $"{nameof(DssHarness.Core.Agents.AgentChildEnvironment)}.{nameof(DssHarness.Core.Agents.AgentChildEnvironment.Own)}",
            nameof(DssHarness.Core.Web.SystemWebNames),
            $"{nameof(DssHarness.Core.Web.HarnessFetch)}.{nameof(DssHarness.Core.Web.HarnessFetch.Connecting)}",
            $"{nameof(IFilePermissions)}.{nameof(IFilePermissions.CopyMode)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The verdicts topic says what becomes of a build stopped from outside, which is stopped rather than failed - but for
    /// one the harness stopped for hanging, or under another build tool - and of a run killed before it finished, which
    /// reaches no verdict and says nothing itself: the next run in its tree on that machine says it was abandoned, and
    /// releases it. Each verdict a leg reaches without one of its own shows the code its run exits
    /// with, and its remedy.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysAKilledRunIsReportedAbandoned_ByTheNextRun()
    {
        var result = HelpText.Of("verdicts");

        Assert.Contains(
            "A leg that never ran proves nothing about the code, and nor does a build something stopped from outside before it "
            + "finished, so counting either among the legs that passed reports evidence nobody gathered. A CMake build under "
            + "ninja that fails with ninja saying nothing of why - which it says whenever it ends a build itself - or saying it "
            + "was interrupted is stopped, not failed: run again, it finishes, where a failed one fails again. Read under the "
            + "name it says it under, which for samurai, run by CMake for ninja where it is installed, is its file's. A build "
            + "the harness stopped for hanging stays failed, saying it hung, and so does one under any other build tool, since "
            + "only ninja's lines are read.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains($" {HarnessExit.Incomplete}  stopped                    Find out what stopped its work, then run again", result.StandardOutput, StringComparison.Ordinal);

        // Each verdict a leg reaches without one of its own shows the code its run exits with, and a remedy.
        Assert.All(
            ["stopped", "skipped-unavailable", "skipped-tool-missing"],
            verdict => Assert.Contains($"  {verdict,-22} not a failure      exit {HarnessExit.Incomplete}", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains($" {HarnessExit.Incomplete}  skipped-unavailable        Make what its line names available, then run again", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($" {HarnessExit.Incomplete}  skipped-tool-missing       Install or update the tool its line names,", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "A run killed, or stopped with its machine, before it finished says nothing more: the next build, test, run or "
            + "check-mutations in its tree on that machine to own its run directory says it was abandoned - its run id, process "
            + "and start, and where its records are - and releases its claim on them.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says how a run is stopped from another terminal from the code's own names and bounds: how often
    /// its dispatcher looks for the stop, how long stop-run waits, the codes it ends with, and what a run's legs are bound to
    /// on Windows.
    /// </summary>
    [Fact]
    public void VerdictsTopic_SaysHowStopRunStopsARun_FromItsOwnBounds()
    {
        var prose = Prose.Words(HelpText.Of("verdicts").StandardOutput);

        Assert.Contains(
            $"A run is stopped with Ctrl+C where it was typed, and from another terminal with '{RunStopService.CommandName} <run id>' typed in the "
                + $"tree that ran it: its dispatcher looks for the stop every {StopCadence.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s "
                + $"and stops as an interrupt stops it - each leg stopped where it stands, its ledger written with what was reached, exit {HarnessExit.Cancelled} "
                + $"- and {RunStopService.CommandName} waits {RunStopService.StopWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s for it, saying "
                + $"the run stopped only where its dispatcher took the stop: one that ended on its own first is nothing it stopped ({HarnessExit.Refused}). "
                + "--now ends the dispatcher's process where it stands, with all it started, and its ledger is never written. On Windows every phase a "
                + "leg runs on its own machine, its keepAwake command, and the ssh or wsl.exe that carries a leg to a host, is bound to its dispatcher - "
                + "a set-up's up line apart - and ends with it however "
                + $"it ends, a kill among them. On Linux and macOS a kill of the dispatcher alone does not reach its legs, and {RunStopService.CommandName} "
                + "--now does.",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic names the three verdicts only a mutation arm reaches, each with the code its run exits with and
    /// its remedy, and says an arm that was due and never driven is stopped, saying why, rather than any of them.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_NamesWhatAMutationArmReaches_WithItsCodeAndRemedy()
    {
        var result = HelpText.Of("verdicts");

        Assert.Contains($"{LegExit.Violated,3}  violated                   Fix the arm's declaration, or the code it guards", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{LegExit.Survived,3}  survived                   Strengthen the test that should have failed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{LegExit.Unattributed,3}  unattributed               Contain the crash or hang in the case, or make", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(new string(' ', 32) + "the runner write its report", result.StandardOutput, StringComparison.Ordinal);
        Assert.All(
            ["violated", "survived", "unattributed"],
            verdict => Assert.Contains($"  {verdict,-22} counts as failure ", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains(
            "An arm not driven to a verdict - its sweep stopped, or ended by a refusal of the run, while it was driven or before; its "
            + "own build, or its paired control's, stopped from outside; no worker left to run it; or the unmutated run of its test "
            + "binary not passing - is stopped, saying why ('help mutations').",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic, under each of its names, lists every key the section takes, read from the code with the
    /// defaults the code gives, every row the registry reads, and the codes a sweep moving from a mutation harness of a
    /// repository's own reads in place of that harness's - each verdict's code read from the verdict table.
    /// </summary>
    [Theory]
    [InlineData("mutations")]
    [InlineData("check-mutations")]
    [InlineData("arms")]
    public async Task TheMutationsTopic_NamesEveryKey_EveryRow_AndTheCodesItMapsOnto(string topic)
    {
        var result = HelpText.Of(topic);
        var keys = ConfigKeys.Of<MutationSettings>();

        Assert.Equal(["registry", "textDirectory", "workers", "reportArgs", "runTimeFactor", "runReads"], keys.Select(key => key.Name));
        Assert.All(keys, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"workers is {MutationSettings.DefaultWorkers} and runTimeFactor {MutationSettings.DefaultRunTimeFactor} where left out."),
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);

        foreach (var row in new[]
        {
            "  A | arm | site | before | after | red kind | target | runner | cases | diag | why",
            "  C | arm | case | why ",
            "  G | arm | case | why ",
            "  B | arm | control before | control after | why",
            "  M | arm | site | before | after | why ",
            "  S | arm | legs | why ",
        })
        {
            Assert.Contains(row, result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var (theirs, verdict) in new[]
        {
            ("0  as declared", LegVerdict.Passed),
            ("1  a violation, a missing diagnostic included", LegVerdict.Violated),
            ("5  nothing reddened", LegVerdict.Survived),
            ("6  a red no case name could be read for", LegVerdict.Unattributed),
            ("7  a build that passed missed a declared unit", LegVerdict.Unwitnessed),
            ("8  the build failed upstream of the unit", LegVerdict.Failed),
            ("4  the harness broke", LegVerdict.Poisoned),
        })
        {
            Assert.Matches(
                $@"(?m)^  {Regex.Escape(theirs)} +{Verdicts.ExitCodeFor(verdict)}  {Regex.Escape(Verdicts.Display(verdict))}\r?$",
                result.StandardOutput);
        }

        Assert.Contains($"4  an arm not driven                               stopped: the run exits {HarnessExit.Incomplete}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"a live owner                               {HarnessExit.Refused,3}  refused-locked", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says what the code does where a reader would otherwise guess: where a failed build makes an
    /// arm failed, every verdict an arm can reach beside those its own judge decides, what a control that does not pass
    /// is, which arms the ARMS block names, what a worker's build is reckoned to come to, how a text is read, and which
    /// verdicts the fixture's arms are designed to.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatTheCodeDoes_WhereAReaderWouldGuess()
    {
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);

        foreach (var text in new[]
        {
            "failed its build failed at a step that is no object depending on its site - a link, another object - or named no step that failed",
            "violated its declaration did not hold: a site or a cited text that is not there, or a site spelt otherwise than the tree "
            + "spells it, or that is no file the sweep's reading of the tree holds; its before-text not in its site exactly once, or "
            + "replaced by itself; its target or its runner not built, or no object they build depending on a site;",
            "stopped not driven to a verdict: the sweep was stopped, or ended by a refusal of the run, while it was driven or before; "
            + "its own build, or its paired control's, was stopped from outside; no worker was left to run it; or the unmutated run of "
            + "its binary did not pass",
            "a cited text that is not there, or that a sync withholds from every copy of the tree - one sync.neverTransfer, sync.exclude "
            + "or worktrees.root covers, or git ignores - as the registry itself is refused where git ignores it;",
            "An arm reaches others where nothing of its own decides: skipped-not-selected where --arms or its S row leaves it out of a "
            + "leg, or --pending leaves it out, its passed holding; skipped-unavailable, with every arm of its leg, where no worker fits the room left or the path limit; unmeasured "
            + "where ninja's log could not be read around its build, or the report its run wrote could not be read from its file, "
            + "which is no finding about the binary; whatever a guard of its build reaches, as a leg's build does - "
            + "inputs-moved, contended; and whatever a failure that ends its driving comes to, as a leg's does - skipped-tool-missing "
            + "for a tool that is not there, failed for a program that will not start.",
            "one that does not pass - a build that does not pass, a red case, no report, a failing exit, a hang - decides the leg's own verdict",
            "unattributed the run failed and nothing ties that to a case: no report, one that is no JUnit report - its line says why - a "
            + "failing exit whose report names no failing case, or a run past its bound, or silent for defaults.stallSeconds, stopped as hung",
            $"an unknown one is refused ({HarnessExit.UsageError}) - once the registry itself can be swept: its own problems are listed "
            + "first, every one of them, whatever --arms names.",
            "ARMS, below the table, names each arm selected that did not pass, and why;",
            "Each leg is swept in worker copies of its tree, kept in it as <tree>/.mutations/<key>w-<n>, numbered from 1:",
            "directory is named: its first worker adds 22 character(s) to its tree's path whatever the variant is called,",
            "Each is a repository of its own, as every copy a sync makes is, and init keeps .mutations in git and everything made in "
            + $"it out: a sweep in a tree where git does not ignore what it would make there is refused ({HarnessExit.Refused}), naming "
            + "init, with nothing made - asked on the machine that makes the workers, so a host answers for its own copy.",
            "A worker is claimed while a sweep uses it, in <worker>.claim.json beside it in .mutations,",
            "a worker a sweep still running holds keeps its worktree, or its host's copy, until the sweep has ended, and one that "
            + "cannot be removed keeps it as a failure until that is put right - or --force deletes the worktree with that worker in it. "
            + "A directory there under a worker's name that no sync made goes with the worktree, or the host's copy, as everything "
            + "else in it does, and is said. No copy of a tree carries its workers, and a sync into a host's copy never deletes that "
            + "copy's own. Workers an earlier version of this tool kept beside a tree, as <tree>.mutation-<key><mark>-<n>, are no "
            + "workers to a sweep: nothing counts one, and clean --former-workers alone removes them - beside each tree of the "
            + "repository on each machine a selected leg runs on, here and on hosts, and beside each one deleted since, a dry run "
            + "listing each with its size - keeping one a sweep of that version still claims by the claim file beside it. Each holds a .git of its own that git records "
            + "no worktree at, so until then one below the worktrees root is said as any such directory: "
            + "list-worktree warns of it, and delete-orchestrator, and delete-worktree of an orchestrator's directory with or "
            + $"without --force, refuse ({HarnessExit.Refused}) while one is below it.",
            "built whole before it drives an arm: that sync withholds the worker's own build directory itself, "
            + "whatever sync.neverTransfer says, and sync.maxDeleteFraction judges only what mirrors the tree the sweep read. "
            + "Of the tree itself a sweep reads every file and writes none: its workers, and "
            + "the claims on them, are in .mutations, and its run's records in .harness-config, as every run's are.",
            "Its workers are kept in the leg's own tree, as a sweep's are, in a family of their own - <tree>/.mutations/<key>s-<n>,",
            "What the sweep timed across a clock step or a host sleep is said on the leg's line, among why its timings are suspect, and "
            + "changes no verdict: an arm's run and the unmutated run that bounds it, however each ended, and what each of its builds says "
            + "of its own - a worker's rebuilt from clean with it.",
            "a build of the variant coming to the leg's buildSpaceGiB where it declares one, else to what the leg's own build last "
            + "recorded, else to the most any other tree of the repository on its machine recorded of it",
            "each read as its file holds it, less a UTF-8 byte order mark at its start and one line ending at its end,",
            "eight arms, one to each verdict an arm's design can reach on any machine - passed, violated, survived, unattributed and "
            + "failed - with a second that passes as the other red kind, a third whose mutation is coupled across two files and a fourth "
            + "whose mutation is two sites of one file",
            "A mutant that is several places of one file, apart from each other, is one arm: an M row naming a file its arm already "
            + "mutates - its own, or another M row's - adds a text to that file, as many as the mutant has, spelt as the arm's first row "
            + "for the file spells it. Every text of a file is replaced together, as one edit of it: each must stand exactly once in the "
            + "file as the tree holds it - never as another of them left it - and differ from what replaces it, the file is written once "
            + "and put back whole, checked once against the tree's,",
            "an M row naming a file its arm already mutates under another spelling, as the tree's own file system compares their names; "
            + "two texts of one arm that overlap in one file as the tree holds it - one lying across or within the other, or the same "
            + "text cited twice;",
            "it has exactly one B row, no C or G row, and no M row but for its own file, which its paired control rewrites whole.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("upstream of every object", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says each arm carries the key of what it was judged on, what of the registry, the tree, the
    /// build, the configuration, what a runner reads at run time and the host that is and is not - and that a file read at
    /// run time and named nowhere moves no key - and that a self-test compares its workers' keys, failing where two differ
    /// or one cannot be read, and saying where a worker had none to compare with; the config topic names what runReads is.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatAnArmsKeyIsMadeOf_AndThatASelfTestComparesItsWorkersKeys()
    {
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var config = Prose.Words((HelpText.Of("config")).StandardOutput);

        Assert.Contains("its test binaries' report, and what each runner reads at run time, part of each arm's key ('help mutations')", config, StringComparison.Ordinal);

        foreach (var text in new[]
        {
            "arm, verdict, failure, detail, durationSeconds, worker, cases, declaredCases, reds, declaredReds, records and key; "
            + "under --pending an arm it left out has held - the run and the time of the passed that holds - and an arm it swept "
            + "has pending - the first reason, its code and the words it is said in - and each leg has pendingSweep: how many "
            + "of its arms held, and how many were swept; and each control that did not pass is beneath the leg as controls: "
            + "its runner, verdict, detail, reds, records, and what ctest registered for its red cases - how they were "
            + "matched, and what of the tests it names a sweep does not apply.",
            "An arm's key is the SHA-256 of what it was judged on, read from the first worker made before it drove an arm: the judge's "
            + "revision; the rows the sweep and the judge read of it - never a why, a comment, a line's number, the letter case of its id "
            + "or its S row;",
            "what the sweep's reading of the tree holds of each text it cites and each site it mutates, found as the tree's file system "
            + "compares names;",
            "its build lines, every file of the tree they read, the dependency sources the worker was given, what the worker's configure "
            + "made and what they read outside the tree, the worker's own path told wherever it stands; the compilers CMake identified; ",
            "An arm a part of whose key could not be read has none.",
            "the leg's developer environment; what of the configuration the build, the run and the judge are given; what its runner "
            + "reads at run time, as runReads names it, each file by the path the reading spells, so one renamed by case alone "
            + "moves the key; and the host.",
            "runReads names, for each runner as the A rows name it, the files of the tree its runs read at run time - a data file, a "
            + "golden output, a plugin it opens - each a path, everything below a directory, or **/name at any depth: what the "
            + "sweep's reading of the tree holds of each is part of the key of every arm that runner runs.",
            "A runner given no file, an entry outside the tree, one holding a '*' anywhere but a leading '**/', and one naming the "
            + "whole tree ('.'), are refused; so is a runner no TEST-RED arm's A row names, when the registry is read.",
            "A file a test binary reads at run time that runReads does not name is read by no part of a key: editing it moves "
            + "none, and a recorded passed it would change still holds.",
            "a key two workers read otherwise, or one a worker could not read, fails the leg as violated, naming the arm, the workers and "
            + "the part that differs; a self-test with one worker says it compared its keys with no other's. A self-test's keys are on no "
            + "arm's line.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The mutations topic says what holds and what is pending, and of the record of verdicts: what is recorded and what
    /// is not, where it is and what it keeps, that clean and the deletion of a worktree or a host's copy leave it, and
    /// what a sweep does with one it cannot read; the layout topic names its directory.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatIsRecorded_WhereTheRecordIs_AndWhatLeavesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var layout = Prose.Words((HelpText.Of("layout")).StandardOutput);

        foreach (var text in new[]
        {
            "What holds, and what is pending A sweep records each arm's verdict on each leg with what it was judged on. A recorded "
            + "passed holds where what the arm would be judged on now, computed in full on the leg's machine, is what the record "
            + "says; every other arm is pending.",
            "Never an arm its machine did not admit, one the sweep's own stop or a refusal of the run ended, one no worker reached, "
            + "a skipped one, or anything of a self-test",
            $"The record is {HarnessLayout.MutationRecordDirectoryRelative} in the main checkout, whichever tree swept: <leg>.json "
            + $"for each leg, and the tables its entries name in {ArmVerdictRecord.TablesDirectoryName}/<leg>/",
            "a host's copy keeps none, so a leg swept there tells this machine instead - each verdict as a "
            + "line on the host's standard error that this machine reads as the arm ends there and never relays, a sweep "
            + "stopped part way or cut off keeping what it had judged - and a sweep typed by hand in a copy the harness "
            + "synced records nothing, saying so once as a warning: under --pending it then holds nothing, and sweeps every "
            + "selected arm.",
            $"an arm keeps {ArmVerdictRecord.KeptPerArm} keys on a leg, the oldest by its place in the file going first, never by its date.",
            "clean, a deleted worktree and a removed host copy leave it as it was; deleting it is the one way to forget it, and "
            + "every arm is then pending.",
            $"A record that cannot be read, or that a newer {ToolPackage.Id} wrote, is never written: a sweep of that leg says so "
            + "once, as a warning naming the file and on the leg's line, and records none. A sweep this machine "
            + "records names the record once, in its lines and in --json, as a listing does.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"{HarnessLayout.MutationRecordDirectoryRelative}/ ignores itself; each leg's verdicts of its mutation arms, with what "
            + "each was judged on, in the main checkout ('help mutations')",
            layout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says how a runner is started - by the sweep itself, never through ctest, with the test
    /// settings' working directory and environment and nothing else - what of a test's ctest entry a sweep does not
    /// apply, the red control's line that names it, and that a mutated build is a build of its own.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysHowARunnerIsStarted_AndThatAMutatedBuildIsItsOwn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);

        foreach (var text in new[]
        {
            "A runner is started by the sweep itself, never by ctest. It is the program the runner's target builds, in the "
            + "worker's build directory, given mutations.reportArgs and nothing else.",
            "Nothing else of the test settings is applied: not the runner, its args, a filter, an exclusion, a label, the "
            + "core count or the success pattern. And nothing of a test's ctest entry is",
            "not ENVIRONMENT or ENVIRONMENT_MODIFICATION; not WORKING_DIRECTORY; not a "
            + "fixture (FIXTURES_REQUIRED, FIXTURES_SETUP, FIXTURES_CLEANUP), DEPENDS, REQUIRED_FILES, RESOURCE_LOCK or "
            + "RUN_SERIAL; not TIMEOUT",
            "Where the leg's tests run "
            + "through ctest, the binary's line then names what ctest registers for the red cases that a sweep does not "
            + "apply, read from ctest's own description (ctest --show-only=json-v1), bounded by two minutes",
            "A sweep starts the binary itself, never through ctest, and runs no fixture.",
            "A mutated build is a build of its own.",
            "and each control that did not pass is beneath the leg as controls: "
            + "its runner, verdict, detail, reds, records, and what ctest registered for its red cases",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The mutations topic says what a listing does and refuses, what it reads and what it cannot - so that unmoved here is
    /// never to say an arm holds - and what it exits with, each code read from the code; the command's own help names it.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatAListingReads_WhatItCannot_AndWhatItExitsWith()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var command = Prose.Words((await CliRunner.RunAsync(["check-mutations", "--help"], cancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "--list sweeps nothing (see 'What holds, and what is pending'), so it is refused with --pending, --self-test, "
            + $"--force-lock, --use-staged, --here and the hidden --held ({HarnessExit.UsageError}), each in one sentence naming both; and --pending "
            + "is refused with --self-test, of which nothing is ever recorded.",
            "check-mutations --list says, without sweeping, each selected arm's last verdict on each selected leg its S row names, "
            + "and whether anything it was judged on that this machine reads has moved since.",
            "it asks no host, takes no lock, begins no run and writes nothing, and needs nothing only a sweep needs - report "
            + "arguments, a leg built with Ninja, a selection that drives an arm.",
            "the newest entry nothing read here moved for, else the newest",
            "the verdict shown is no passed, it was swept with no key, or the judge's revision, its rows, a text it cites, a site "
            + "it mutates, a file of the tree its targets are built from, a file its runner reads at run time, what of the "
            + "configuration its build, its run or its judge is given, or the host its leg names changed",
            "the row says the first that applies, in that order, and --json every one.",
            "A leg that names no host is read as though swept where its verdict was judged - which host a sweep would place it "
            + "on is not read here - so the environment its build is given is the one that host declares.",
            "Every other arm is unmoved here, which is never to say it holds: a listing reads the rows, the texts, the tree's "
            + "files and the settings, never how a target is built, a file its build makes, the dependency sources it fetched, "
            + "a file outside the tree it reads, the compilers or the developer environment - only a sweep on the leg's machine "
            + "reads those.",
            "A leg that cannot be swept is said once, above the rows, and never refused",
            $"A listing exits {HarnessExit.Success} whatever is pending - it is no gate - and otherwise as a sweep would: "
            + $"{HarnessExit.CommandFailed} where git could not say what a tree ignores, and {LegExit.InputsMoved} where a tree "
            + "changed while it was read.",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.Contains("--list Sweep nothing: say each selected arm's last verdict on each leg", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutations topic says what --pending drives and what it leaves out: the first worker made alone and every key
    /// read before any arm is mutated, an arm held by a recorded passed of its key and how its line says so, why each
    /// other is pending, how many workers it makes, and what a leg with nothing pending is and exits with; and the
    /// option's own description says what it drives.
    /// </summary>
    [Fact]
    public async Task TheMutationsTopic_SaysWhatPendingDrives_WhatItHolds_AndWhatNothingPendingIs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var said = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var command = Prose.Words((await CliRunner.RunAsync(["check-mutations", "--help"], cancellationToken)).StandardOutput);

        foreach (var text in new[]
        {
            "check-mutations --pending drives, on each leg, only the arms with no recorded passed that holds",
            "the first worker is made alone - claimed, synced, built whole - and the key of every arm is read from its build before any arm is mutated.",
            "An arm whose key is a recorded passed's holds: it is not driven, and is skipped-not-selected on that leg, its detail naming the run whose passed holds and when",
            "in as many workers beyond the first as the pending arms need",
            "how a target is built, a file joined or left what it is built from, a file its build makes, the dependency sources it fetched or a file outside the tree it reads moved, its compiler or its developer environment changed, or what it would be judged on cannot be read now - and, where nothing else says how its key moved, that it moved.",
            "A host sweeping one leg of a run under --pending is given what the record holds of that leg's own arms - each arm's newest entry "
            + "and every recorded passed, as bytes in a hidden option of that leg alone - and decides held and pending "
            + "there, from its own reading of its copy and its own worker; given --pending and no record it holds nothing "
            + "and sweeps every selected arm.",
            "With nothing pending no other worker is made and the leg passes on its own verdict, saying nothing is pending",
            $"A sweep under --pending exits as a sweep does - {HarnessExit.Success} where nothing is pending",
            "or --pending leaves it out, its passed holding",
        })
        {
            Assert.Contains(text, said, StringComparison.Ordinal);
        }

        Assert.Contains("--pending Drive on each leg only the arms with no recorded passed that still holds", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tools topic says a minVersion holds every copy a leg could start, the one that meets it raised ahead of the
    /// PATH and a leg with none turned away; that install-missing-tools judges a tool by the host's own survey, a shadowed
    /// one outdated and one it could not establish unknown, installing neither; and that the package manager an install
    /// runs is started where it was found.
    /// </summary>
    [Fact]
    public async Task TheToolsTopic_SaysAMinVersionHoldsEveryCopy_AndAManagerRunsWhereItWasFound()
    {
        var tools = Prose.Words((HelpText.Of("tools")).StandardOutput);

        foreach (var text in new[]
        {
            "The package manager an install runs is started the same way, where the search found it - brew in /opt/homebrew/bin, "
                + "which no login-free PATH on macOS names - save one run through sudo, which sudo looks up on a PATH of its own.",
            "A minVersion holds every copy of the tool a leg could start: the first one - on the PATH, then in the searched "
                + "directories - whose version meets it is the one a leg there starts",
            "That tool is found where its first copy that meets it is, and that copy's directory goes ahead of the PATH of every "
                + "process every leg on that host starts - one whose environment, or developer environment, sets PATH included",
            "A leg the tool is declared for is turned away where every copy is older, naming each copy, what it said, and - where "
                + "the tool declares an install there - the install-missing-tools command that would install one; where the copy that "
                + "meets it is behind an older one in a "
                + "directory raised for another tool, naming both - only where no order of the raised directories, nor a further "
                + "copy of a tool in the way, keeps both on copies that meet them; and where no copy could be shown to meet it, as "
                + "unknown, 'skipped-unavailable'.",
            "Where the copy that meets it is behind an older one a directory raised for another tool holds, the tool is outdated, "
                + "naming both, and nothing is installed: an install would not change which copy a leg starts.",
            "Where none met it and a copy's version could not be read, or a place a copy could be could not be looked in, the "
                + "tool is unknown and nothing is installed: nothing says it is behind.",
            "Each host is asked about its tools by its own survey, the one 'legs' runs there, so a tool is installed exactly "
                + "where a leg would be turned away over it, and never a second time where a leg there finds it.",
            "a host whose survey cannot run - one a dry run finds with no SDK yet - has every tool unknown, saying why, and "
                + "nothing is installed there.",
            "install-missing-tools asks the same survey, on the host's own PATH and on the PATH each developer environment its "
                + "legs start in sets up there; a tool the survey could not establish is unknown rather than missing, and nothing "
                + "is installed for it.",
            "A minVersion is written major.minor.patch and needs a probe to read versions with; brew installs and updates with "
                + "'brew install'",
            "A leg in a developer environment holds the copy that environment's PATH names to it, once set up.",
            "A leg turned away for a missing program, or for one below its minVersion, is 'skipped-tool-missing'",
        })
        {
            Assert.Contains(text, tools, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Help says what a command exits with where no selected leg can run, wherever it speaks of it: 1 of a build, a test
    /// and a run, and of a sweep incomplete, whose 1 is an arm violated - each code read from the code.
    /// </summary>
    [Fact]
    public async Task Help_SaysWhatASweepGives_WhereNoSelectedLegCanRun()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var mutations = Prose.Words((HelpText.Of("mutations")).StandardOutput);
        var tools = Prose.Words((HelpText.Of("tools")).StandardOutput);

        Assert.Contains(
            $"A sweep none of whose selected legs can run is incomplete, exit {MutationService.NothingRuns}, saying why each cannot - never the "
            + $"{LegsExit.Unavailable} a build, a test and a run give there, which of a sweep is an arm violated.",
            mutations,
            StringComparison.Ordinal);
        Assert.Contains(
            $"and the run is incomplete, exit {HarnessExit.Incomplete} - or exit {LegsExit.Unavailable} when no selected leg can run at all, save of "
            + $"check-mutations, which stays {MutationService.NothingRuns}: its {LegsExit.Unavailable} is an arm violated.",
            tools,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The admission topic says a waiting leg says where it stands at least every five minutes, the minutes read from
    /// the code, and that a poll or a settle longer than that keeps to it.
    /// </summary>
    [Fact]
    public async Task TheAdmissionTopic_SaysAWaitNeverGoesSilent_WhateverThePollAndTheSettle()
    {
        var said = Prose.Words((HelpText.Of("admission")).StandardOutput);

        Assert.Contains(
            $"It says so again, as it reads then and with how long it has waited, at least every {LegAdmission.SaidAgainEvery.TotalMinutes:0} minutes - "
            + "a poll longer than that is cut at it, and a settle longer than that waited whole in pieces of it - so a long wait never goes silent.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The admission topic says a heavy leg's build is held to minFreeGiB - read every few seconds, the seconds read from
    /// the code, and again as a build or a write of its fails - and stopped under it, writing nothing where it starts under
    /// it; that WSL's page cache is dropped before a wait on the
    /// memory and as a WSL leg ends, a drop that could not be made said with why; that a holder's line never names its
    /// machine; and that a wait with only legs of its own run ahead of it does not count.
    /// </summary>
    [Fact]
    public async Task TheAdmissionTopic_SaysTheFloor_TheCacheDrop_TheHolderUnnamed_AndTheOwnRunsWait()
    {
        var said = Prose.Words((HelpText.Of("admission")).StandardOutput);

        foreach (var expected in new[]
        {
            $"While a heavy leg builds, each filesystem its build fills is read again every {RoomFloorWatch.Every.TotalSeconds:0} seconds, and the "
                + $"build is stopped once one has less free than minFreeGiB: stopped, exit {HarnessExit.Incomplete}, naming what was free and where, "
                + "and what it built left for clean - and so is a build that failed with one under it, read again as it failed, since a full disk "
                + "says nothing of its code, and one whose own write failed then - a phase's log, its record. A build under it as it starts writes "
                + "nothing, its record included, and a record it has no room left for is said, the one it wrote as it began standing.",
            "So a leg about to wait on the memory has that cache dropped first, as root (sync, then drop_caches) in a running distribution a WSL "
                + "host of its repository reaches - at most once a minute, whichever leg asks - and reads the memory again once what was dropped "
                + "has come back, a minute later at the most, its line saying how much was dropped and the memory before and after, and goes on "
                + "from that reading as from any other.",
            "A WSL leg sent from a machine that admits drops it as it ends, without waiting, its line saying so - or that WSL listed none of its "
                + "distributions running, though the leg had just run in one.",
            "a drop that could not be made is said once - a WSL host whose item cannot be read, where no other reaches one, or a list of the "
                + "distributions running that cannot be read, among the reasons; that list is read in UTF-16 too, which an older wsl.exe writes "
                + "whatever WSL_UTF8 says.",
            "the distribution reaches through its mount there: /mnt/c, as /proc/mounts lists it by the drive, or by the path its options name "
                + "where an older WSL lists what it mounts as drvfs.",
            "process, run, and since when it asked, never the machine's name, which only the record keeps",
            "A wait with only legs of its own run ahead of it - holding slots, or in line first: a command's WSL leg behind its Windows legs, "
                + "asked for at once - is certain to end and does not count: its line says they are its own, and a refusal after it says how much "
                + "of the wait did not count. Another command's leg in line ahead makes it count, whoever holds the slots. On an ssh host, the "
                + "legs one command sends there ask under that command's run.",
        })
        {
            Assert.Contains(expected, said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The verdicts topic says a heavy leg's build stopped for room is stopped, and that a shared tool working in a build
    /// directory of another tree of the repository is named as that tree's leg's, only what no tree accounts for being
    /// nobody's known.
    /// </summary>
    [Fact]
    public async Task TheVerdictsTopic_SaysABuildStoppedForRoomIsStopped_AndWhoseAToolInAnotherTreeIs()
    {
        var said = Prose.Words((HelpText.Of("verdicts")).StandardOutput);

        Assert.Contains(
            "A heavy leg's build stopped for leaving less room than its machine's minFreeGiB is stopped too ('help admission').",
            said,
            StringComparison.Ordinal);
        Assert.Contains(
            "and one working in a build directory of another tree of the repository on that machine, a worktree's, an agent's or the main "
            + "checkout's, or a copy one of them keeps in its .legs, as that tree's leg's: 'worktree o1/xa's leg 'linux-debug'', or "
            + "'worktree o1/xa's leg copy's leg 'linux-debug''. Only what no tree there accounts for is nobody's known, and where the "
            + "other trees could not be listed the line says so.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The space topic says a leg's need, where its own tree's copy recorded none, is the most any other copy of the
    /// repository on that host recorded, named in the line - its example the line as a leg says it.
    /// </summary>
    [Fact]
    public async Task TheSpaceTopic_SaysANeedIsTheMostAnyOtherCopyRecorded_NamingWhose()
    {
        var said = Prose.Words((HelpText.Of("space")).StandardOutput);

        Assert.Contains(
            "in this tree's copy, or else the most any other copy of the repository on that host recorded - the main checkout's, a "
            + "worktree's, an agent's or a plain one, or a leg copy of any of them, named in the line - less what the directory already "
            + "holds.",
            said,
            StringComparison.Ordinal);
        Assert.Contains(
            "ssh vps: this leg needs ~11.4 GiB, what worktree o1/xa's copy of the same variant came to there; 3.2 GiB free on '/'",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The exit codes topic gives the run verbs' own code - a question waiting is 1, which a wrapper that retries 21 would
    /// otherwise loop on - beside what else a session ends with, from the constants the session exits by.
    /// </summary>
    [Fact]
    public void ExitCodeTopic_SaysARunPausedOnAQuestionExitsOne()
    {
        var result = HelpText.Of("exit-codes");

        Assert.Contains($"  {FlowService.RunCommand}, {FlowService.ResumeCommand}, {FlowService.AnswerCommand}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"      {FlowExit.Paused}  paused: a question waits - a step's, or the budget's at its reserve - for {FlowService.AnswerCommand}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "A session otherwise ends 0 done, 20 failed, 21 failed where every failure that ended it was incomplete after its retries, and 130 at a safe pause or a stop",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The exit codes topic, and the table the architecture keeps by hand, say that 21 is also an update of a program begun
    /// and not finished - not finished within its bound, or its download not reached - which both updaters exit with: a
    /// code whose words named only a leg, a set-up and an agent's verbs was what each updater answered differently.
    /// </summary>
    [Fact]
    public void ExitCodeTopic_SaysAnUpdateStoppedPartWayIsIncomplete()
    {
        const string Said = "or a deletion, a fold, a hand-over, a move of an agent's base or an update of a program stopped part way (not "
            + "finished within its bound, or its download not reached)";

        Assert.Contains(Said, Prose.Words(HelpText.Of("exit-codes").StandardOutput), StringComparison.Ordinal);
        Assert.Contains($"| {HarnessExit.Incomplete} | Ran with nothing failing, but a leg reached no verdict or left a set-up it brought up running, {Said};", Prose.Document("docs", "architecture.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic names the five run verbs as they are typed, the one-unfinished-run rule, the codes a session
    /// ends with, and what each of the two interrupts does - the first a safe pause that starts no clock - from the code's
    /// own names and bounds.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_NamesTheRunVerbs_AndWhatEachInterruptDoes()
    {
        var result = HelpText.Of("orchestrators");
        var prose = Prose.Words(result.StandardOutput);

        foreach (var command in new[] { FlowService.RunCommand, FlowService.ResumeCommand, FlowService.AnswerCommand, FlowService.PauseCommand, FlowService.StopCommand })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} ", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains($"  {ToolPackage.Command} list-orchestrator [<o>] [--steps] [--questions] [--json]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Only one run of a repository is unfinished with work in the tree at once: a start, or a session of another run, is refused (13) "
                + "while one is, and so it is where an agent's record of another run cannot be read.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "done (0), failed (20, or 21 where every failure that ended it was incomplete after its retries), paused on a question (1), or "
                + "paused or stopped on request (130)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "In the terminal a session runs in, the first Ctrl+C - and off Windows SIGTERM or a hang-up - asks the safe pause, which starts no "
                + "clock: an agent's handoff and a build may take as long as they take. The second stops the run where it stands - a step whose "
                + "runner does not end within 90 s is recorded stopped all the same - and the command ends within 120 s whatever is left;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("which it reads every 2 s", prose, StringComparison.Ordinal);
        Assert.Contains("waits 30 s, and --now ends its process: only on this machine, and only the process its owner names by its start.", prose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCodeTopic_DocumentsEverySharedExitCode()
    {
        var result = HelpText.Of("exit-codes");

        // Generated from the constants, so a new exit code cannot ship undocumented.
        foreach (var description in HarnessExit.All)
        {
            Assert.Contains(description.Name, result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(
                description.Code.ToString(CultureInfo.InvariantCulture),
                result.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConfigTopic_DocumentsCoresAndParallelLegs_WithTheRealDefault()
    {
        var result = HelpText.Of("config");

        foreach (var setting in new[] { "buildCores", "testCores", "maxParallelLegs", "sanitizer", "hosts", "emulators", "developerEnvironments" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"({HarnessDefaults.DefaultCores.ToString(CultureInfo.InvariantCulture)} each)",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says what a failed test invocation keeps as evidence - the paths under the build directory, the
    /// cap with its real default read from the code, kept-on-failure beside the leg's log, a pass keeping nothing, a
    /// later step reading nothing from it, a path naming nothing said rather than refused, and a host's copy crossing
    /// back over its transport.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhatAFailedTestInvocationKeeps()
    {
        var said = Prose.Words((HelpText.Of("config")).StandardOutput);

        Assert.Contains(
            "A test invocation may name what a failed run keeps as evidence: keepOnFailure, paths relative to the leg's "
                + "build directory - files, directories, or patterns, {buildDir} among the names they may use. When the "
                + "invocation does not pass, whatever the verdict, what they name is copied into kept-on-failure/ beside "
                + "the leg's log, each file's tree as it stood under the build directory, within keepOnFailureCapBytes kept "
                + $"in all ({DiskSpace.Size(DssHarness.Core.Testing.KeptOnFailure.DefaultCapBytes)} where it is left out). A pass keeps "
                + "nothing, and a later step reads nothing from it: it is evidence beside the verdict, never an output, and "
                + "a failed step still keeps nothing of what one reads. The leg's line names the directory, how many files "
                + "were kept and their size, and how many were refused over the cap, could not be read, or named nothing - "
                + "a path naming nothing is said, not refused, for the failure may have been before it was written. On a "
                + "leg a WSL or ssh host runs, the copy is kept beside that host's own log and crosses back over the host's "
                + "transport: 'sync --pull' takes the kept-on-failure directory within that host's run.",
            said,
            StringComparison.Ordinal);

        // The number is the decision, not a constant nobody reads: said as the words that decided it.
        Assert.Contains("within keepOnFailureCapBytes kept in all (64 MiB where it is left out)", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says a leg's inputs are watched only as deep as they lie, and the directories above them for
    /// the names in them alone, so a build in another tree below the worktrees root is never heard: on Linux however
    /// many directories they lie in, through one inotify instance for the process; on Windows and macOS up to the number
    /// of directories .NET's watcher takes, read from the code, past which some are watched all the way down - and where
    /// inotify cannot be called directly, that .NET's watcher stands in, said once.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysInputsAreWatchedOnlyAsDeepAsTheyLie()
    {
        var result = HelpText.Of("config");

        Assert.Contains(
            "Its inputs are watched only as deep as they lie - each directory holding one, for its own files, and each directory "
                + "above one, the root among them, for the names of the directories in it alone - so a build in another tree below the "
                + "worktrees root, which init keeps in git by a placeholder, is never heard. On Linux every such directory is watched, "
                + "however many there are, through one inotify instance for the whole process. On Windows and "
                + $"macOS a watch takes at most {FileSystemWatcherKind.MostDirectoriesWatched} directories besides the root's own, those "
                + "holding inputs and those above them alike, so past them the top-level directories watching the most are watched all "
                + "the way down, until the rest fit, and a tree whose inputs lie in more top-level directories than that is watched "
                + "whole. Where inotify cannot be called directly on Linux, .NET's "
                + "watcher stands in, as on Windows and macOS, each directory taking an inotify instance of its own, and the process "
                + "says so once.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says what a directory that holds inputs or lies above some going, arriving or changing its name
    /// counts - every input in or below it, though it be back when the work ends - and that a leg whose inputs cannot be
    /// watched runs none of its work.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhatADirectoryThatWentCounts_AndThatAnUnwatchedLegRunsNothing()
    {
        var said = Prose.Words((HelpText.Of("config")).StandardOutput);

        Assert.Contains(
            "and the process says so once. A directory that holds inputs, or lies above some, going, arriving or changing its name "
                + "counts every input in or below it, under both names of a rename, though it be back when the work ends; one merely "
                + "written to counts none. "
                + "A leg whose inputs cannot be watched at all runs none of its work, and ends unmeasured at once, saying why ('help "
                + "verdicts').",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says a leg whose inputs cannot be watched is unmeasured with none of its work run, in the words
    /// its line opens with, read from the code; and that a watch lost while the work ran ends it unmeasured as it ends.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysALegWhoseInputsCannotBeWatched_IsUnmeasuredWithNoneOfItsWorkRun()
    {
        var said = Prose.Words((HelpText.Of("verdicts")).StandardOutput);

        Assert.Contains(
            "A leg whose inputs cannot be watched - the machine refusing the watch as its work was to begin - is unmeasured with no "
                + $"phase of it run: '{InputsUnwatchedException.Opening.TrimEnd()}' and the machine's reason. Run first and called unmeasured "
                + "after, the whole of the work was spent to learn what was known before it began. A watch that started and was lost "
                + "while the work ran still leaves it unmeasured, as the work ends.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The remedy for a leg whose inputs could not be watched is the machine's, never the tree's settling: the verdicts
    /// topic says so where it says why such a leg ran nothing, and again among the verdicts' remedies, and the exit codes
    /// topic beside the code it exits with.
    /// </summary>
    [Fact]
    public async Task TheRemedyForInputsThatCouldNotBeWatched_IsTheMachines_WhereverExitThreesRemedyIsSaid()
    {
        var verdicts = (HelpText.Of("verdicts")).StandardOutput;
        var codes = (HelpText.Of("exit-codes")).StandardOutput;

        Assert.Contains(
            "Its remedy is the machine's, not the tree's: free or raise what the reason names - end a program holding this user's "
                + "inotify instances or watches, or raise the limit - or put back the tree the watch was to be put on, then run again.",
            Prose.Words(verdicts),
            StringComparison.Ordinal);
        Assert.Contains(
            "  3  inputs-moved, unmeasured   Let the tree settle, then run again; where its" + Environment.NewLine
                + "                                inputs could not be watched, free or raise what" + Environment.NewLine
                + "                                the machine's reason names, or restore the tree" + Environment.NewLine,
            verdicts,
            StringComparison.Ordinal);
        Assert.Contains(
            $"    {LegExit.InputsMoved,3}  inputs-moved or unmeasured: let the tree settle, then run again; where the inputs" + Environment.NewLine
                + "         could not be watched, free or raise what the machine's reason names, or restore the tree" + Environment.NewLine,
            codes,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic names the two inotify limits a watch on Linux can meet, and says that the leg is said with the
    /// processes holding them and waits for neither.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_NamesTheTwoInotifyLimits_AndThatALegWaitsForNeither()
    {
        var said = Prose.Words((HelpText.Of("verdicts")).StandardOutput);

        Assert.Contains(
            "On Linux the machine's reason can be this user's inotify instances all taken (fs.inotify.max_user_instances) - "
                + "a process of this tool holds one, however many legs it runs - or its inotify watches run out "
                + "(fs.inotify.max_user_watches), one for each directory the work watches - those its inputs lie in and those "
                + "above them; each is said with the limit and the "
                + "processes /proc shows holding it, the most first. The leg does not wait for either: what holds them is "
                + "another program, whose end nothing here can read.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says a test count that differs is marked on its own, never as a timing, and
    /// how a set that differs on purpose is declared.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysATestCountThatDiffers_IsNoTimingMark_AndHowATestSetIsNamed()
    {
        var result = HelpText.Of("config");

        foreach (var text in new[]
        {
            "and test set is marked on its own - 'test count differs', and testCountDiffers and",
            "testCountNote in --json - never as a timing, and never changes a verdict either. A",
            "\"windows\": { \"testSet\": \"windows\" }",
            "A leg's os names its section however it is spelled - Windows as windows - as every",
            "per-system setting is read.",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says how test --filter, --exclude and --label reach a runner, what each is
    /// for ctest, that a label can now be chosen as well as left out, how several exclusions reach
    /// a runner that would not leave out each of them given apart, what a leg on a host's copy
    /// leaves out beside them, that a selection ctest could only find empty is refused before the
    /// build and how its values are judged, and which invocations are refused when the file is read.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysHowTheTestSelectionReachesTheRunner()
    {
        var result = HelpText.Of("config");

        foreach (var text in new[]
        {
            "filterArg, excludeArg and labelArg, so one set of options serves every runner. For",
            "ctest, '-R' chooses tests by name, '-L' chooses them by label and '-LE' leaves a label",
            "apart - ctest leaves out only what every -LE matches - declares excludeJoin, and several",
            "one the sync made, or one it took over. Each sync makes its HEAD name the commit this",
            "tree is at, so what a step asks git about HEAD - which commit, what a file held there -",
            "untracked as added, and one git stores converted here - line endings, a clean filter -",
            "pack of git's own, less what the copy holds, and nothing crosses while its HEAD names",
            "about a commit behind it that the copy lacks fails there, in git's own words.",
            "\"sync\": { \"history\": \"full\" } gives the copy every commit behind it too, once, and",
            "each later one as it is made; a partial clone then has git fetch what it left out. A",
            "copy the sync took over has its HEAD moved where it names another commit - detached,",
            "reads it as a shallow repository from then on, said too, which 'git fetch --unshallow'",
            "sync makes its index hold the files it carried, so a build there fingerprints its",
            "                 how much git history a copy is given: 'head' (the commit HEAD names,",
            "remoteExcludes are given to every leg that works in a copy - a host's, or its own in",
            "reads there unless the args or a preset choose tests by name too - or beside a test",
            "preset that takes a union or cannot be read. So is a selection ctest could only find",
            "another leaves out by the same part of a test - its name, or a label - where either is",
            "only -LE and no test preset they name adds one. Values are judged as written: spelled",
            "has been built; where it still finds none, the detail of a leg that did not pass says",
            "so, unless --no-tests=ignore keeps ctest quiet. A label beside labels the args or a",
            "When the file is read, ctest is also refused an invocation that would pass an option",
            "'|', which ctest would read as part of one pattern that leaves out nothing.",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says when a combination's build directory is kept and when it starts from
    /// clean, and what rebuildableFormats decides: a consumer found both only by reading the source.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhenABuildDirectoryIsKept_AndWhatCountsAsAnInput()
    {
        var result = HelpText.Of("config");

        foreach (var text in new[]
        {
            "A combination's directory is kept between builds, and its build system decides",
            "  - a compiler CMake identified is not what is at its path now: CMake identifies a",
            "  - an input that changed since that build began is dated no later than the newest",
            "  - nothing can say: no record of what it was built from, the files git tracks",
            "DEPENDS is not remade. A project's rebuildableFormats says which files are inputs,",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The verdicts topic says when a leg on a copy is inputs-moved with nothing run - the tree moving while it was read,
    /// a file to carry changing after, another command's sync of the copy in between or the copy gone or part made by
    /// then, and under --use-staged a copy part made, absent or unmarked - what its line and its --json say of the tree it
    /// tested, and the remedy a leg that worked in its tree itself is named, and where it is not.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysWhenALegOnACopyIsInputsMoved_AndWhichTreeItTested()
    {
        var result = HelpText.Of("verdicts");

        Assert.Contains(
            "A leg on a copy of its tree - a host's, or one of its own in the tree's .legs - is inputs-moved with nothing of it "
                + "run where the copy cannot be the tree the run read: a file written, appearing or going while the tree was read, "
                + "named; a file to carry changed after it was read; another command synced the copy again between this run's sync "
                + "and the leg taking it, naming that run, or by then the copy is not there, is part made, or holds no copy this "
                + "tool finished; or, under --use-staged, the copy is part made, is not there, or holds no copy this tool made. Each "
                + "leg on a copy says which tree it tested with its verdict - 'tree read at <commit>', with the uncommitted changes "
                + "the reading carried, or under --use-staged 'tree staged at <commit> by run <id>' as the copy records it - and "
                + "--json's 'tree' holds its path, the tree it is a copy of, and the commit, the count and the run. A leg that "
                + "worked in its tree itself and is inputs-moved names the remedy after its detail, where its command could have run "
                + "it on a copy: the leg's localCopy, defaults.localCopy, or --local-copy ('help legs'). A leg another machine sent "
                + "here, or one typed in a copy a sync made, is named none: it works in a copy already, and never in one of its own.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// --use-staged says, for each command that takes it, which copies end their legs inputs-moved: one part made, one
    /// that is not there, and one this tool did not make - of each copy, a host's as a leg's own alike.
    /// </summary>
    [Theory]
    [InlineData("build", "Build")]
    [InlineData("test", "Test")]
    [InlineData("run", "Run against")]
    [InlineData("check-mutations", "Sweep")]
    public async Task UseStaged_SaysWhichCopiesEndTheirLegsInputsMoved(string command, string verb)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            $"{verb} what is already staged on each copy, without syncing again. Legs on a copy a sync or a takeover began and did "
                + "not finish, that is not there, or that holds no copy this tool made are inputs-moved.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says where a leg works - its tree, its host's copy, or its own in the tree's .legs - and the order
    /// of precedence in one sentence: the command's option, a runner's writesTree, the leg's own key, the default, the
    /// tree.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysWhereALegWorks_AndTheOrderOfPrecedence()
    {
        var result = HelpText.Of("legs");

        Assert.Contains(
            "A leg a host runs works in that host's copy of its tree, whatever anything else says. A leg this machine runs "
                + "itself works where the first of these says anything: --local-copy or --in-place on the command, for that run "
                + "(build, test, run); a runner's writesTree, which is in place ('help runners'); the leg's own localCopy, true "
                + "or false; defaults.localCopy, a default for every leg of the repository; and, where none says, its tree itself.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "made from the run's one reading of the tree and kept warm between runs: a write the tree receives while the leg "
                + "runs - a fold, a commit, an edit - ends nothing of the leg's",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic says what a writesTree runner does: its legs work in place whatever localCopy says, a leg a host
    /// would run is refused before any host is measured, and so is --local-copy.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatARunnerThatWritesTheTreeDoes()
    {
        var result = HelpText.Of("runners");

        Assert.Contains(
            "A runner whose steps change files of the tree says writesTree: its legs work in their trees themselves, "
                + "whatever the leg's own localCopy or defaults.localCopy says ('help legs'), for what it wrote in a copy "
                + "the next sync would make the tree again. A leg a host would run is refused before any host is measured, "
                + "and so is --local-copy on the command.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "A run check's runner runs within the leg's own work, so it is held to the same: one that writes the tree is "
                + "refused wherever the leg it checks would work in a copy, --local-copy among them.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout topic says how a leg's records are named - the logs of line, and records and logs in --json - and how
    /// the run a host ran a leg under is said: once, by the dispatcher, and as hostRunId.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_SaysHowALegsRecordsAndAHostsRunAreNamed()
    {
        var result = HelpText.Of("layout");

        Assert.Contains(
            "Each leg that kept records has a line of its own naming its directory and the logs in it - 'logs of <leg>[ on <host>]' - as its verdict is said and in the run's closing lines, and as records and the phase logs as logs in --json, a host's paths with its home written as ~",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "A host runs a leg under a run of its own: the dispatcher names it once, '<leg>: run <id> on <host>', never relaying the host's own line, and the leg's --json holds it as hostRunId.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>build, test and run each take --local-copy and --in-place, one description for the three.</summary>
    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("run")]
    public async Task EachCommandThatRunsLegs_TakesLocalCopyAndInPlace(string command)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "Every leg this machine runs itself works in a copy of its tree kept in the tree's .legs, for this run: over the "
                + "leg's own localCopy and defaults.localCopy.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "Every leg this machine runs itself works in its tree itself, for this run: over the leg's own localCopy and "
                + "defaults.localCopy.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Each command's own description says what it does to the copies a tree keeps, and list-orchestrator's --json the
    /// listing it prints - never the outcome of an --apply it does not have.
    /// </summary>
    [Theory]
    [InlineData("list-orchestrator", "--json Print the listing as one JSON document.")]
    [InlineData("fold-agent", "--json Print the outcome as one JSON document, the whole of standard output:")]
    [InlineData("delete-worktree", "and the copies kept in each - the mutation workers in .mutations, and on this machine the leg copies in .legs;")]
    [InlineData("clean", "and the copies of its tree it works in on this machine (.legs), wherever the leg runs,")]
    [InlineData("clean", "remove nothing, and exit as the clean would: 13 where a run holds the leg, a sweep its workers or a run its copy.")]
    public async Task EachCommandsOwnHelp_SaysWhatItDoesToCopies_AndWhatItsJsonPrints(string command, string expected)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expected, Prose.Words(result.StandardOutput), StringComparison.Ordinal);
    }

    /// <summary>Both given, --local-copy and --in-place are a usage error; neither is passed to a host.</summary>
    [Theory]
    [InlineData("build", "")]
    [InlineData("test", "")]
    [InlineData("run", "probe")]
    public async Task LocalCopyAndInPlaceTogether_AreAUsageError(string command, string argument)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig
            {
                PredefinedRunners = { ["probe"] = new RunnerConfig { Phases = [new RunnerPhase { Name = "p", Command = ["true"] }] } },
            });

        string[] arguments = command is "build" or "test"
            ? [command, "--local-copy", "--in-place", "--directory", temp.Path]
            : [command, argument, "--local-copy", "--in-place", "--directory", temp.Path];

        var outcome = await CliRunner.RunAsync([.. arguments], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Contains(
            "--local-copy and --in-place say opposite things about where this run's own legs work. Give one.",
            outcome.StandardOutput + outcome.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>stop-orchestrator's --now says it ends what the engine's steps left running with the engine, by kind.</summary>
    [Fact]
    public async Task StopOrchestratorsNow_SaysWhatItsStepsLeftRunningIsEndedWithIt()
    {
        var result = await CliRunner.RunAsync([FlowService.StopCommand, "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "End the engine's process and all it started, without asking it - only an engine of this machine, and only the process "
                + "its owner names by its start: for an engine that no longer answers. What its steps left running - a contained "
                + "child's session, the process group of what its legs started - is ended with it.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>sync's --dry-run says it exits as the sync would, naming each refusal it would make.</summary>
    [Fact]
    public async Task SyncsDryRun_SaysItExitsAsTheSyncWould()
    {
        var result = await CliRunner.RunAsync(["sync", "--help"], TestContext.Current.CancellationToken);

        Assert.Contains(
            "List what would be written and deleted, change nothing, and exit as the sync would: 13 where it would refuse - a "
                + "directory it did not make, without --adopt; more deleted than sync.maxDeleteFraction allows; a worktree's copy "
                + "another worktree of the same name holds.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic says that a run check whose own runner's inputs cannot be watched was not run, and confirms
    /// nothing: the failure it was to excuse stays genuine.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysARunCheckWhoseWatchCannotStart_ConfirmsNothing()
    {
        var result = HelpText.Of("runners");

        Assert.Contains(
            "A check whose own runner's watch cannot start was not run, and confirms nothing: the failure stays genuine, and the run fails.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic names a step's successPattern, which line it is matched against, and which of
    /// that line's streams.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatAStepsSuccessPatternIsMatchedAgainst()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "successPattern: <regular expression> what its last run line must print, besides exiting 0",
            "matched against what that line printed on standard output and standard error, a line of output at a time - "
                + "^ and $ its start and end, whether it ends in CRLF or LF, as its log keeps it - after secrets are redacted.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic lists every key a runner and an action file take, each indented to the depth
    /// it is nested at and with what it does, read from what the files are read with: a key cannot be
    /// taken without being listed here.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_ListsEveryKeyARunnerAndAnActionFileTake()
    {
        var result = HelpText.Of("runners");
        var lines = result.StandardOutput.ReplaceLineEndings("\n").Split('\n');

        foreach (var (key, depth) in Listed(ConfigKeys.Of<RunnerConfig>(), 0).Concat(Listed(ActionFileKeys.File, 0)))
        {
            var pattern = $"^ {{{2 + (2 * depth)}}}{Regex.Escape(key.Name)} +{Regex.Escape(key.Meaning)}{(key.Required ? "; required" : string.Empty)}$";

            Assert.True(lines.Any(line => Regex.IsMatch(line, pattern)), $"'{key.Name}' is not listed as: {pattern}");
        }
    }

    /// <summary>
    /// The runners topic says which stall bound a phase or step runs under, and where the order its
    /// environment is built in is given.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhichStallBoundApplies()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "The stall bound a phase or step runs under is its own stallSeconds, else its runner's, else "
                + "defaults.stallSeconds: the first of them set, where 0 means none, as does setting none.",
            "'help config' gives the order its environment is built in.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic names the repository's own stall bound, and every layer a runner's phase or
    /// step is given in order, its action's inputs among them.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_NamesTheDefaultStallBound_AndEveryLayerOfARunnersEnvironment()
    {
        var result = HelpText.Of("config");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "stallSeconds, the stall bound nothing more specific replaces ('help runners')",
            "run host env, developer environment, then the runner's values and secrets, its env, the action's "
                + $"inputs as INPUT_<NAME> where a {PredefinedActions.ReadInputs} step read them, the phase's or step's",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Wherever the runners topic shows how a step key is written, what it does is what 'Every key'
    /// says of it: one meaning, from one place, however many times the key is shown.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatAStepKeyDoes_InTheWordsItsListUses()
    {
        var result = HelpText.Of("runners");
        var meanings = ActionFileKeys.Step.ToDictionary(key => key.Name, key => key.Meaning, StringComparer.Ordinal);
        var shown = result.StandardOutput.ReplaceLineEndings("\n").Split('\n')
            .Select(line => SpeltKeyLine().Match(line))
            .Where(line => line.Success && meanings.ContainsKey(line.Groups["key"].Value))
            .ToList();

        Assert.NotEmpty(shown);

        foreach (var line in shown)
        {
            Assert.Equal(meanings[line.Groups["key"].Value], line.Groups["meaning"].Value);
        }
    }

    /// <summary>
    /// The runners topic says a step that performs a predefined action is refused with each key only a
    /// run block reads, naming every one of them from the list the parser refuses them by.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysAStepThatUsesAPredefinedAction_TakesNoKeyOnlyARunBlockReads()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        Assert.Contains(
            $"A step that uses {string.Join(" or ", PredefinedActions.All)} runs no program, so it takes none of the keys "
                + $"only a run block reads, and is refused with any of them: '{string.Join("', '", KeyDescription.Names(ActionFileKeys.RunBlock))}'.",
            words,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runners topic says what a step's outputs are relative to, and what its name names, by the
    /// name a run line uses for that directory, {stepBuild}: 'its own directory' read as the action's,
    /// which workingDirectoryRoot names.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysAStepsOutputsAreRelativeToItsBuildDirectory()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        Assert.Contains("outputs: [<path>, ...] what it writes, relative to its build directory, {stepBuild}", words, StringComparison.Ordinal);
        Assert.Contains("name names its log file and its build directory, {stepBuild}; required", words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorktreesTopic_QuotesTheLimitsFromTheCode()
    {
        var result = HelpText.Of("worktrees");

        Assert.Contains(
            WorktreeSettings.DefaultMaxNameLength.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            HostPlatform.WindowsMaxPath.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);

        foreach (var setting in new[] { "worktrees.maxNameLength", "worktrees.pathBudgetReserve", "worktrees.pathLimit" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says what runOn takes - the systems quoted from the code - where a leg of
    /// another system says it left a step out, and that a leg left with no step is refused.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatRunOnTakes_AndWhatALegLeftWithoutAStepGets()
    {
        var result = HelpText.Of("runners");

        foreach (var text in new[] { "runOn: [<system>, ...]", $"of {string.Join(", ", PlatformNames.OperatingSystems)}; absent, all", "skippedSteps", "refused before anything starts" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says where an input's value comes from, in which order, that a secret never
    /// gives it one, what becomes of one with none, what --input refuses, and that a secret does not
    /// go there.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhereAnInputsValueComesFrom()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "resolved from 'run --input <name>=<value>' first, .harness-config/runner/.env second and each "
                + "input's own 'default' last; .secrets never gives one a value.",
            "A required input with none of them is refused before the first step runs, and so is a step naming "
                + "one that is not required and has none, naming the input and how it can have one.",
            "default its value when neither --input nor the runner's .env gives one",
            "--input takes one name=value each time it is given",
            "an empty value and a name given twice",
            "a secret stays in",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says what a name in braces is made of, that the runner's .env values are
    /// names too, that one this leg has nothing for is refused before the first program starts, and
    /// which names an input or a value may not have.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatANameIs_AndWhichNamesAreRefused()
    {
        var result = HelpText.Of("runners");
        var words = Prose.Words(result.StandardOutput);

        foreach (var text in new[]
        {
            "refused over the whole file before the first program starts, as is one this leg has nothing for.",
            $"A name {LegPathNames.NameRule}.",
            "<value name> any value the runner's .env holds, by its name",
            "An input or a .env value named like a name above is refused, since a run line naming it would get "
                + "this tool's value while the environment held its own; so is an input whose name a run line "
                + "cannot write.",
        })
        {
            Assert.Contains(text, words, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic names the two compiler names with what each is filled in with; says a line may start with one,
    /// that a step naming one builds its leg first, when one is refused - before anything starts, and once the leg is
    /// built - and that a test invocation may name one too. The tools and admission topics count a step naming one among
    /// those that build their leg.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatACompilersNameIs_WhereItMayStand_AndWhenItIsRefused()
    {
        var token = TestContext.Current.CancellationToken;
        var written = (HelpText.Of("runners")).StandardOutput;
        var runners = Prose.Words(written);

        foreach (var text in new[]
        {
            $"{{{LegPathNames.CompilerC}}} {{{LegPathNames.CompilerCxx}}} the C and the C++ compiler the leg's build identified: the whole path of the program CMake built with, as this machine spells it",
            "The first token must be a program declared under tools, a path in the repository, or the name of one of the leg's compilers alone ({compiler_C}, below); anything else is refused before a single step runs.",
            "starts it, a step naming {product}, {buildDir} or a compiler has its leg built first (below).",
            "A step whose run line or workingDirectory names {buildDir}, {product}, {compiler_C} or {compiler_CXX} reads what the build made or identified, so every leg of a run that runs it is built first",
            "{compiler_C} and {compiler_CXX} name what built the leg, never what a toolchain declares: the program CMake's own record in the leg's build directory names for that language - gcc on one leg and cl on another, from one run line.",
            "A line may start with one, written alone: the leg's own compiler may run though nothing declares it under tools.",
            "A leg whose project CMake does not build is refused before anything starts, naming each leg and step, since no build of it identifies a compiler;",
            "a name the leg's build has nothing for is refused once the leg is built, before its first step, saying which case it is - no compiler identified for that language, a record of one that could not be read or that names no program, or one the build runs with words after it, a launcher such as ccache given its compiler or a compiler given options: the name is a program alone, so the program and the words are named and neither is filled in.",
            "A test invocation's args, coresArgs and workingDirectory may name one too: the check made before the build lets the name stand, and it is filled in, or refused, once the leg is built - under test --no-build, from what the build already in its directory identified.",
        })
        {
            Assert.Contains(text, runners, StringComparison.Ordinal);
        }

        // The rows of its tables of names are headed by the names this tool fills in, each of them and no other: one
        // added to the code cannot be left out, and one the code dropped cannot stay.
        var rows = written
            .Split('\n')
            .Select(line => Regex.Match(line, @"^  ((?:\{[A-Za-z_]+\} ?)+) +\S"))
            .Where(row => row.Success)
            .SelectMany(row => Regex.Matches(row.Groups[1].Value, @"\{([A-Za-z_]+)\}").Select(name => name.Groups[1].Value));

        Assert.Equal(LegPathNames.All.Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));

        Assert.Contains(
            "runs a step or phase naming {product}, {buildDir} or a compiler on that leg's system",
            Prose.Words((HelpText.Of("tools")).StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains(
            "requires the build or runs a step or phase naming {product}, {buildDir} or a compiler, and is heavy, too,",
            Prose.Words((HelpText.Of("admission")).StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The tools topic says what --dry-run does, that init installs only when asked, how a tool
    /// narrows the legs that need it, and that a leg in a developer environment is told about a tool
    /// as the PATH it sets up on the leg's host holds it; the layout topic says init writes the tree it
    /// runs in and names a rule by git's own answer.
    /// </summary>
    [Fact]
    public async Task ToolsAndLayoutTopics_SayWhatInitAndInstallMissingToolsDo()
    {
        var tools = HelpText.Of("tools");
        var layout = HelpText.Of("layout");

        foreach (var text in new[]
        {
            "--install-tools' runs it too; plain init installs nothing and says how.",
            "--dry-run reaches and asks every host as a run does, and installs no tool",
            "\"toolchains\": [\"msvc\"]",
            "\"legs\": [\"win-arm\", \"gate\"]",
            "\"processors\": [\"arm64\"]",
            "\"emulators\": [\"qemu-arm64\"]",
            "a developer environment, on the PATH that environment sets up for its processor -",
            "set up on the leg's host for the look, by the DssHarness there, so cl is found where",
            "the environment could not be looked at or set up, a tool its legs need is unknown,",
            "covering no declared leg, is refused.",
            "  check-mutations",
            "          what build starts: each worker, and each arm, is built as the leg builds",
        })
        {
            Assert.Contains(text, tools.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var text in new[]
        {
            "block on later runs and leaving every other rule untouched. It then asks git which",
            "overrules does nothing there - named only where taking it out would take from git",
            "configuration, a placeholder, an action's files, an anchor registry - and no",
            "the slot a placeholder is kept in is overruled for the slot's contents and needed",
            "init writes the tree it runs in, a worktree's own included",
            $"('{GitIgnoreManager.RetiredBeginMarker}') is the managed block too: init",
            "rewrites it in place under the current markers and says so, and none of its lines",
        })
        {
            Assert.Contains(text, layout.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The layout topic says the worktrees root, the orchestrators directory and the slots a tree keeps its copies in
    /// are kept in git by their placeholders with everything made in them ignored, what that costs, and that sync
    /// withholds each.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_SaysTheWorktreesRootTheOrchestratorsDirectoryAndEachSlotAreKeptInGit()
    {
        var result = HelpText.Of("layout");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            ".worktrees/ contents ignored, .gitkeep tracked; the worktrees, at worktrees.root, which init writes as this",
            ".orchestrators/ contents ignored, .gitkeep tracked; what each orchestrator and its agents keep, in the main checkout, "
                + "and never sent to a host by sync",
            ".mutations/ contents ignored, .gitkeep tracked; the tree's mutation workers, in every tree, never sent to a host by "
                + "sync ('dssharness help mutations')",
            ".legs/ contents ignored, .gitkeep tracked; the copies of the tree its legs work in, in every tree, never sent to a "
                + "host by sync",
            "The worktrees root, the orchestrators directory, .mutations and .legs are kept in git by their placeholders, so each "
                + "reads as not ignored itself while everything made in it is, and none can be a link: init names one it finds, and "
                + "writes nothing through it. Sync withholds each by name. Each copy in them is a whole copy of the tree, so a "
                + "program of the repository's own that walks the tree from its root - a census, a guard over every file - must "
                + "leave out what the block ignores, as git does, or it reads every file once more for each copy kept there.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The legs topic says a toolchain names its compiler, that a build directory is held to the
    /// compiler it was configured with by the file it starts, not by its name, and where a language
    /// only a subproject enables is identified.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysAToolchainNamesItsCompiler_AndHowABuildDirectoryIsHeldToIt()
    {
        var result = HelpText.Of("legs");

        Assert.Contains("A toolchain CMake builds with also names its compiler - CC or CXX under env,", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("One only .NET or Dart builds with names none.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("build directory is then held to the compiler it was configured with by the file that", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("and by its name only where that PATH holds", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"compilerId\": { \"C\": \"MSVC\", \"CXX\": \"MSVC\" }", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("A build CMake configured with another compiler fails before anything is built with", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("loaded: CMakeFiles/<version>/CMake<language>Compiler.cmake - where the record names", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "where the record names the compiler the answer names and no configure has failed since that answer - which the error "
            + "index CMake 4 writes in an answer's place says - since a configure that identified the compiler again and then failed "
            + "leaves a record of one nothing built with. Never by when either was written, which a clock that steps gets wrong; "
            + "before CMake 4 a failed configure leaves nothing of itself, and the record is read as it names it.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.DoesNotContain("no newer than the answer", Prose.Words(result.StandardOutput), StringComparison.Ordinal);

        // And which ssh reaches a host, what it is given, and when it is given nothing.
        Assert.Contains("The ssh that runs is the first on the PATH. It is asked first what it would do, with", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("refused before ssh starts. Every call the run makes is then given the address it", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the address. Each still goes to the address declared, so every Host block written for", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("where ssh reaches the host through a ProxyJump or a ProxyCommand, which do their own", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorktreesTopic_SaysWhenDeleteWorktreeRefuses_AndHowToForceIt()
    {
        var result = HelpText.Of("worktrees");

        foreach (var text in new[] { "delete-worktree", "uncommitted changes", "skip-worktree", "locked", "--force", "--delete-evidence", "--discard-uncommitted", $"exits {HarnessExit.Refused}", $"exits {HarnessExit.CommandFailed}" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says what becomes of the mutation workers kept in a worktree that is deleted: asked about
    /// before anything of it is removed, so a worktree kept for one removes nothing; which code each one kept leaves it
    /// with; what an interruption says; that --force deletes every copy with the worktree; and that a host's copy is
    /// treated as the worktree is - the leg copies of the worktree among the families that go with it, never sent to a
    /// host.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysWhatBecomesOfTheCopiesKeptInAWorktree()
    {
        var result = HelpText.Of("worktrees");

        Assert.Contains(
            "The copies kept in a worktree go before it, of its two families: the mutation workers its sweeps made "
            + "('help mutations'), in .mutations, and the copies of it its legs work in ('help legs'), in .legs - never "
            + "sent to any host, so only this machine holds them. They are asked about before anything of it is removed or "
            + $"looked through: one a sweep still running holds - or a run holds a leg copy - keeps the worktree ({HarnessExit.Refused}), and one that cannot "
            + $"be removed, or told for the harness's, keeps it as a failure ({HarnessExit.CommandFailed}), as workers that cannot be looked "
            + "for do, with nothing removed. A run holds a leg copy however either spells it: the run lock tells a tree on this "
            + "machine by where it leads, every link followed, whichever way the run holding it spelled it - a build before this one's "
            + "included - so a run in a worktree kept on another disk through a linked worktrees root, or in a copy kept through a "
            + ".legs that is a link, keeps it from a deletion that reaches it the other way. A sweep that takes one meanwhile keeps the worktree as "
            + "well, and so does one whose removal "
            + "then fails, the others gone by then, which is said. An interruption as they go says which went, and that nothing else of "
            + $"the worktree was removed ({HarnessExit.Cancelled}). --force deletes the worktree with every copy in it, one held or one "
            + "that could not be removed among them, saying which went with it. A worktree whose own removal then fails, or is stopped, "
            + "says which copies went before it. In a host's copy the workers are asked about first as well, and a copy kept once some had "
            + "gone says which went.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnchorsTopic_DocumentsEveryStatus_Command_AndDefault()
    {
        var result = HelpText.Of("anchors");


        // Each command as its list gives it, with what it takes: named in prose alone, one was missing
        // from the list a reader scans.
        var expected = AnchorStatus.Cells
            .Concat(["write-anchor", "set-anchor", "read-anchor", "read-anchors", "check-anchor-balance [--base REF]"])
            .Concat(["check-anchor-citations --current-commit|--current-tree|--current-pr", "--anchor-dry-run"])
            .Concat([AnchorSettings.DefaultPendingAnchorsPath, AnchorSettings.DefaultDoneAnchorsPath, AnchorRegistryDocument.TableHeader]);

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The balance says what it measures from, what it does not credit and what it refuses - a base that moved on, an
    /// unfinished merge, the bookkeeping pair, the open copy of an anchor closed already, an id with two rows, a lost row
    /// and the one it only notes - where the anchors are described.
    /// </summary>
    [Fact]
    public async Task AnchorsTopic_StatesWhatTheBalanceMeasuresFrom_AndDoesNotCredit()
    {
        var result = HelpText.Of("anchors");

        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains("A base that has moved on since HEAD left it is measured from where HEAD left it", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"a closure whose Trigger opens with {AnchorStatus.ClosedMark}{AnchorStatus.BookkeepingMark} records work that already existed",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("an anchor open or closed where the change began is in neither registry now", prose, StringComparison.Ordinal);
        Assert.Contains("a lost one that was open is not credited either", prose, StringComparison.Ordinal);
        Assert.Contains("Nor is it for taking away the open row of an anchor that had a closed row already", prose, StringComparison.Ordinal);
        Assert.Contains("an id has more than one row", prose, StringComparison.Ordinal);
        Assert.Contains("one whose Anchor cell named no id, or more than one, is noted instead", prose, StringComparison.Ordinal);
        Assert.Contains("during an unfinished merge, HEAD is taken merged with what the merge brings in", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The citation check says what it cannot see - a break inside a segment, with no hyphen on either
    /// side - both where its command is described and in the topic that lists it.
    /// </summary>
    [Fact]
    public async Task TheCitationCheck_StatesTheOneWrapItCannotSee()
    {
        // A command's own --help is built by the command line from where it declares the command, so only the tool itself
        // prints it; the topic is the core's, and is asked of its renderer.
        var described = await CliRunner.RunAsync(["check-anchor-citations", "--help"], TestContext.Current.CancellationToken);
        var listed = HelpText.Of("anchors");

        Assert.Equal(HarnessExit.Success, described.ExitCode);
        // Read as prose: a description is wrapped to the terminal, with its continuation indented.
        Assert.Contains("with no hyphen on either side", Prose.Words(described.StandardOutput), StringComparison.Ordinal);
        Assert.Contains("with no hyphen on either side", Prose.Words(listed.StandardOutput), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegsTopic_DocumentsTheNamesTheRulesAndTheExitCodes_FromTheCode()
    {
        var result = HelpText.Of("legs");


        // Every name the validator accepts for a leg is listed, so the help never leaves out a name a leg can use.
        foreach (var name in PlatformNames.OperatingSystems.Concat(PlatformNames.Processors))
        {
            Assert.Contains(name, result.StandardOutput, StringComparison.Ordinal);
        }

        var expected = new[]
        {
            "--legs",
            "host-exec --ssh",
            "--wsl",
            ".harness-config/sshItems/<name>/",
            $".NET {ToolPackage.MinimumSdkMajor} SDK",
            "never downgraded",
            "witness",
        };

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains($"{LegsExit.Unavailable,3}  legs, sync:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{HarnessExit.HostUnavailable,3}  host-exec:", result.StandardOutput, StringComparison.Ordinal);

        // What ends a run on its hosts, from the numbers both ends count by.
        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "No leg, host-exec or sync's operation a host runs for this machine outlives the command that asked for it, whatever carries it there; "
            + "only what holds a host awake between commands (holdAwakeSeconds) is started to outlast one.",
            prose,
            StringComparison.Ordinal);

        // What a host's shell prints while it starts is dropped by the mark it writes first, before the command's fixed words.
        Assert.Contains(
            "The command line ssh hands a remote shell holds only fixed words, after a mark the shell writes first so that what its "
            + "startup files print is dropped, and no argument is ever reinterpreted by sh, cmd or PowerShell.",
            prose,
            StringComparison.Ordinal);

        // The silence a host lets pass, how often it looks for it, and so the longest a leg runs on unheard: 15, 8 and 4.
        Assert.Equal((15, 8, 4), (HostAgentProtocol.BeatSeconds, HostAgentProtocol.BeatsMissed, HostAgentProtocol.SilenceLooks));
        Assert.Contains(
            "when the beat this machine writes on it every 15 seconds has gone unheard for 120, which the host looks for every 30: a dispatcher "
            + "killed where nothing closes its end of the connection leaves a leg that is cancelled within 150 seconds of its last beat, on an ssh "
            + "host as in a WSL distribution",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("On Linux and macOS a session hung up cancels it the same way.", prose, StringComparison.Ordinal);

        // What a cancelled command is given to stop in, from the patience a host is built with: one that finishes what
        // it began as long as the command line gives it where it is typed.
        Assert.Equal(
            (TimeSpan.FromSeconds(60), DssHarness.Core.Worktrees.WorktreeService.DefaultInterruptionGrace),
            (new HostAgentPatience().Unwind, new HostAgentPatience().Finishing));
        Assert.Contains(
            "What the host started that has not stopped 60 seconds after it was cancelled - a command reading the output of something its child "
            + "left running - is ended with its processes, and the host's DssHarness with it, so the host is never held by a run nobody reads; a "
            + "command that finishes what it began - a deletion, a fold, a hand-over - is given 120, as it is where it is typed and interrupted. "
            + "Where this machine can no longer write to the connection while a host works, it says so here, with why: the host hears no more of "
            + "it, and stops what it was asked. To end a run on its hosts, stop the command that dispatched it.",
            prose,
            StringComparison.Ordinal);

        // The one door this machine's own tool is updated through, from the names and the codes the command uses.
        Assert.Contains(
            "This machine's own DssHarness is updated through 'dssharness update-tool', never by a 'dotnet tool update' typed beside running "
            + "commands:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            $"only while no other DssHarness process runs on the machine: with one running it changes nothing and names each by its process and "
            + $"command ({HarnessExit.Refused}), and with --wait it waits for them to end, saying who it waits for as it starts and every "
            + $"{ToolUpdateService.SaidAgainEvery.TotalMinutes} minutes - but for a wait asked from inside a running DssHarness, which would never "
            + "end and is refused, as is one where the process table names no process's parent.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Two updates of one place never change it together: the change holds a machine-wide lock on the directory the tool is kept in, "
            + "and an update that finds it held - two run beside the installed tool are each the program dotnet, and neither counts the "
            + $"other - changes nothing and is refused ({HarnessExit.Refused}), with {ToolUpdateService.WaitOption} or without, since it "
            + "read the machine before the other changed it.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Once the update has begun this command does not stop it, interrupted or not, and reads back what it left: it says the update done only once "
            + $"the tool is listed as that release, and a failure with what dotnet said and what is listed since, or why that is not known "
            + $"({HarnessExit.CommandFailed}; {HarnessExit.Incomplete} where dotnet did not finish within {ToolPackage.ChangeBudget.TotalMinutes} "
            + "minutes, an update begun and not finished, which running it again can finish, as every program's update this tool makes answers it).",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Where nuget.org does not say which release is the newest it says why - no route to it, no answer in time, an answer it cannot read - "
            + "and changes nothing, unless the copy asking is itself a release newer than the installed one, which it then moves to, saying so.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows replaces no program while it runs, and 'dssharness update-tool' is itself a DssHarness running: there it changes nothing and "
            + "says the command that does, with the options it was typed with - the door on every system, which is the newest release run beside "
            + "the installed one, without installing it, its exit code the update's. With --wait:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "  dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool --wait",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>The overview lists update-tool among the commands a machine is set up with.</summary>
    [Fact]
    public async Task TheOverview_ListsUpdateTool()
    {
        var result = HelpText.Of(null);

        Assert.Contains("  dssharness update-tool             Update this machine's DssHarness, while none runs here", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner topic says a leg's line names what its steps kept, as the path sync --pull takes.
    /// </summary>
    [Fact]
    public async Task RunnerTopic_SaysALegsLineNamesWhatItKept_AsSyncPullTakesIt()
    {
        var result = HelpText.Of("runner");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("names each file its steps kept as keptOutputs, relative to the tree", text, StringComparison.Ordinal);
        Assert.Contains(
            "A leg that did not pass carries, in --json, the last lines its last failing phase printed as logTail and that phase's "
                + "name as tailPhase - a host's leg among them, whose log stays there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("sync --pull' takes to bring it back", text, StringComparison.Ordinal);
        Assert.Contains(
            "A path given to --pull that ends with '/' names a directory: every file below it is brought back, each checked as a file named is and "
            + "each named in the report, as the path a later command takes. The host that holds it lists it first, and a directory that is not "
            + "there, holds no file, holds more than 256 files or more than 1024 MiB together fails the pull before any file crosses",
            text,
            StringComparison.Ordinal);
        Assert.Contains("A link below it is never followed: it is named, and nothing is brought back for it.", text, StringComparison.Ordinal);
        Assert.Contains(
            "A pull that stops part way - a file gone, a host that stopped answering - fails naming each file it had brought back, which stays "
            + "where it was written, and each it had not.",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner topic names the largest file that can cross to a host or back, the one the refusal names: a file crosses
    /// whole, inside one request, and that request is one line of text a string must hold.
    /// </summary>
    [Fact]
    public async Task RunnerTopic_NamesTheLargestFileThatCanCross_AsTheRefusalDoes()
    {
        var result = HelpText.Of("runner");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            $"A file crosses whole, whichever way it goes - to a host inside one request, back in one answer: one larger than {SyncServe.LargestFile.ToString(CultureInfo.InvariantCulture)} bytes is refused by name before anything is sent.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(SyncServe.LargestFile.ToString(CultureInfo.InvariantCulture), SyncServe.TooLargeToCarry(SyncServe.LargestFile + 1, "a.bin", "ssh vps"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says a Ninja build leaves its dead outputs out of the path budget's warning, and names
    /// the ninja command that removes them, removing nothing itself.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysDeadOutputsAreLeftOutOfTheWarning_AndWhatRemovesThem()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("A Ninja build leaves out of that warning the outputs ninja says no target of it produces any more", text, StringComparison.Ordinal);
        Assert.Contains("naming 'ninja -t cleandead', which removes them. The harness removes nothing.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says list-worktree shows the copies hosts keep and those a gone worktree left, what --hosts
    /// asks the hosts, and what a host that cannot be asked does to the command.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysListWorktreeFindsTheCopiesAGoneWorktreeLeft()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            "list-worktree lists each worktree with the copies this machine records hosts keeping of it, and the copies "
                + "left by worktrees that are gone - removed by hand, or by another tool - each with the delete-worktree "
                + "that deals with them.",
            "With --hosts it also asks each declared host which worktree copies it keeps beside its repositoryPath, and "
                + "how large each is, and sets them against that record",
            $"{HarnessExit.HostUnavailable} where a host cannot be reached. --json prints the listing as one JSON document.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says where an orchestrator's agents' worktrees sit, that they are named by their address, that
    /// a directory holding worktrees is never deleted as one, and how their copies are named apart.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysAgentsWorktreesAreNamedByTheirAddress()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "An orchestrator's agents' worktrees sit below the directory named for it, <worktrees.root>/<orchestrator>/<agent>, made by "
            + "create-agent with its records, never by create-worktree. list-worktree lists each as orchestrator/agent, and delete-worktree "
            + "takes that address. The directory named for an orchestrator is never deleted as one while worktrees are below it, or a "
            + "directory holding a .git of its own that git records no worktree at - what a removal left, a person's to look in and "
            + "delete, or a worker an earlier version of this tool kept beside an agent's worktree, which clean --former-workers "
            + "removes - --force "
            + $"or not ({HarnessExit.Refused}), nor any directory with no .git of its own while git cannot list its worktrees "
            + $"({HarnessExit.CommandFailed}); a worktree's own submodules are never taken for worktrees. A plain worktree cannot take an "
            + "orchestrator's name. An agent's copies on hosts are kept under orchestrator--agent, so two orchestrators' agents of one name "
            + "keep theirs apart.",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says that on Windows each junction is removed as a link before git runs, what stops that,
    /// and that git worktree repair is named only while git still records the worktree.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysJunctionsAreRemovedAsLinks_AndWhenRepairIsNamed()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            "On Windows, each directory junction in the worktree is removed first, as a link, never what it leads to: git leaves "
                + "every junction, and the directories above it, while reporting the worktree removed.",
            $"One that cannot be removed stops the deletion before git runs ({HarnessExit.CommandFailed}), and a volume mounted "
                + "on a directory is never unmounted.",
            "A directory under the root with no .git and no record of git's - what a removal that stopped part way leaves - is "
                + $"refused ({HarnessExit.Refused}), naming --force; git worktree repair is named only while git still records a worktree there.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says that on Windows a worktree something holds part of is refused whole, with nothing
    /// removed, what holds one and what does not, what to do where it is the command's own current directory, and
    /// that --force does not look.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysAWorktreeSomethingHoldsIsRefusedWholeOnWindows()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            $"On Windows, without --force, a worktree something holds part of is refused ({HarnessExit.Refused}) whole once "
                + "every check has passed, with nothing removed, naming what is held: a directory that is a process's current "
                + "directory, this command's own among them, a file open without sharing its deletion, or one a program is "
                + "running from.",
            "git's removal would stop there part way, with the worktree's .git file and git's record of it already gone.",
            "Close what holds it and run delete-worktree again, from outside the worktree where it is this command's own.",
            "A watcher on a directory, as an editor keeps, holds nothing; --force looks for nothing, and goes as far as it can.",
            "or, on Windows, a directory in it cannot be looked through for what holds it, nothing is deleted and it exits "
                + $"{HarnessExit.CommandFailed}.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says a hold between commands runs the host's keepAwake, ends when a command's own starts,
    /// and needs keepAwake.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysAHoldBetweenCommandsEndsWhenACommandsOwnKeepAwakeStarts()
    {
        var result = HelpText.Of("config");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("holdAwakeSeconds holds it awake between commands, until a command's own keepAwake takes over", text, StringComparison.Ordinal);
        Assert.Contains("The next command's own keepAwake ends it there", text, StringComparison.Ordinal);
        Assert.Contains("It needs keepAwake, which it runs", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says a developer environment may hold its legs to one MSVC toolset with the optional toolsVersion -
    /// what it names, that it is optional and only its form is read, with no toolset built in - and how the survey and
    /// the setup hold a leg to it; the config topic names it beside developerEnvironments.
    /// </summary>
    [Fact]
    public void LegsTopic_SaysADeveloperEnvironmentMayHoldItsLegsToOneToolset()
    {
        var legs = Prose.Words(HelpText.Of("legs").StandardOutput);

        Assert.Contains(
            @"toolsVersion, optional, holds an environment's legs to one MSVC toolset, named as VC\Tools\MSVC names its toolsets: "
            + "by two numbers, for the newest of that line, or by all three, for that one - for a compiler such as a CUDA "
            + "toolkit's nvcc, which refuses an MSVC newer than it knows. Left out, vcvarsall.bat sets up the instance's default "
            + "toolset. Only its form is checked when the file is read: no toolset is built into this tool. The survey then "
            + "takes the newest instance holding a toolset it names, with the newest of those, and a host with none turns the "
            + "leg away as a tool missing, naming what each instance holds. vcvarsall.bat is run with -vcvars_ver= and that "
            + "toolset, and one that sets up another fails the leg; the leg's line names the MSVC set up. Two environments of "
            + "one kind keep two toolsets apart on one machine:",
            legs,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"developerEnvironments\": { \"visualStudio\": { \"kind\": \"visualStudio\" }, \"cuda\": { \"kind\": \"visualStudio\", "
            + "\"toolsVersion\": \"14.44\" } }",
            legs,
            StringComparison.Ordinal);
        Assert.Contains(
            "developerEnvironments what a toolchain's legs start in, set up on the host that runs them: visualStudio runs that "
            + "instance's vcvarsall.bat, for the MSVC toolset an optional toolsVersion names ('help legs')",
            Prose.Words(HelpText.Of("config").StandardOutput),
            StringComparison.Ordinal);

        // The README and the architecture say the same: optional, its form alone checked, and held to on both sides.
        Assert.Contains(
            "Left out, `vcvarsall.bat` sets up the instance's default toolset. Only its form is checked when `config.json` is "
            + "read: no toolset is built into the tool.",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "**A developer environment may hold its legs to one MSVC toolset**, with the optional `toolsVersion`",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
        Assert.Contains(
            "and fails the leg where `VCToolsVersion` comes out as anything else.",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says what a wake window retries and what it never does, with the delay the code uses.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysWhatAWakeWindowTriesAgain_AndWhatItNeverDoes()
    {
        var result = HelpText.Of("legs");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("hosts.ssh.<name>.wakeWaitSeconds", text, StringComparison.Ordinal);
        Assert.Contains($"every {SshWakeWindow.DefaultPollDelay.TotalSeconds:0} seconds until that many seconds have passed", text, StringComparison.Ordinal);
        Assert.Contains("a key or a login the host refuses is never waited on", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The legs topic says that, for a host whose name is looked up here, no address its name resolved to is named,
    /// wherever ssh names one - and where the name is looked up again, and how far that reaches.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysNoAddressAHostsNameResolvedToIsNamed()
    {
        var result = HelpText.Of("legs");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "For a host whose name is looked up here, no reason, relayed line or document names an address the name resolved "
            + "to: wherever ssh, or a program there, names one as a word, it reads as the address declared.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The name is looked up again, afresh, before every call that lets ssh look it up itself - one never pinned, though "
            + "not through a ProxyJump or a ProxyCommand, or once its pin is dropped - so an address it has moved to is withheld "
            + "too, wherever this machine's own lookup finds it as well.",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The space topic says what clean leaves alone and why it frees a full disk: nothing is written first,
    /// a build of the leg holds it off, and a host behind this machine's build needs room to be updated.
    /// </summary>
    [Fact]
    public async Task SpaceTopic_SaysCleanWritesNothingFirst_AndWhatItLeavesAlone()
    {
        var result = HelpText.Of("space");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("It writes nothing where it removes before it has removed", text, StringComparison.Ordinal);
        Assert.Contains("refused-locked", text, StringComparison.Ordinal);
        Assert.Contains("--dry-run", text, StringComparison.Ordinal);
        Assert.Contains("A build directory that is a link is left alone", text, StringComparison.Ordinal);
        Assert.Contains("both full and behind has to be freed by hand once", text, StringComparison.Ordinal);
        Assert.Contains("A leg is placed only where its host has the room its build still needs", text, StringComparison.Ordinal);
        Assert.Contains("the leg's buildSpaceGiB", text, StringComparison.Ordinal);
        Assert.Contains("Commands that build nothing - sync, clean - need no room.", text, StringComparison.Ordinal);
        Assert.Contains(
            "all of it as each leg's 'space', with its mutation workers' as 'workerBytes' and its copies' as 'copyBytes': what "
                + "was removed of them - 0 where every one there was kept - or, in a dry run, what is there. A leg whose build "
                + "directory was not measured - a link, locked or not removable - carries a space saying its workers and copies "
                + "alone, where any is there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "one a live sweep claims is kept, one that cannot be removed fails the leg, which still says what went, and a directory "
                + "under a worker's name that no sync made is said and left",
            text,
            StringComparison.Ordinal);
        Assert.Contains("The mutation workers a sweep of the leg keeps in that tree go with it", text, StringComparison.Ordinal);
        Assert.Contains(
            "The copies of the tree its legs work in, in the tree's .legs slot, go the same way - the leg's own variant's and "
                + "those of a variant no leg builds any more, whatever the configuration says now",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "a leg a run is building, or whose workers a sweep holds, or whose copy a run holds, is refused-locked in the dry run "
                + "too",
            text,
            StringComparison.Ordinal);
        Assert.Contains("A host holding no copy of the tree holds nothing of it to remove.", text, StringComparison.Ordinal);

        Assert.Contains("legs -v' says the room on each host it measured", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// check-ci-legs assumes no workflow of its own, and its topic says what it reads a workflow by instead.
    /// </summary>
    [Fact]
    public async Task CiTopic_SaysCheckCiLegsReadsOnlyWhatTheSettingsDeclare()
    {
        var result = HelpText.Of("ci");

        Assert.Contains("It knows no workflow of its own", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("with {leg} standing for the leg's", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("A green leg past 80% of its", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Every call of gh, whoever makes it, and every git command is started so that it cannot ask anything (GH_PROMPT_DISABLED, "
                + "GCM_INTERACTIVE, GIT_TERMINAL_PROMPT), and a call of gh is ended where it has not answered within the timeoutMinutes of "
                + $"the workflow step that made it, where the step says one, else within {ReportText.Limit(GhProcess.Limit)}: "
                + $"a call that did not answer in time is not done ({HarnessExit.Incomplete}), and asked again it may answer.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Equal(UnattendedCommands.Variables.Keys.Order(StringComparer.Ordinal), ["GCM_INTERACTIVE", "GH_PROMPT_DISABLED", "GIT_TERMINAL_PROMPT"]);

        // The example reads what it says it reads: a leg and its budget from a whole job name, and the leg from one
        // the forge cut short, which it would otherwise not read at all.
        var example = new Regex(
            JsonSerializer.Deserialize<string>(ExamplePattern().Match(result.StandardOutput).Groups["pattern"].Value)!,
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
        var whole = example.Match("test (linux-gcc-debug, 45)");
        var cut = example.Match("test (linux-gcc-debug-with-a-name-long-enou");

        Assert.Equal(("linux-gcc-debug", "45"), (whole.Groups["leg"].Value, whole.Groups["budget"].Value));
        Assert.Equal(("linux-gcc-debug-with-a-name-long-enou", false), (cut.Groups["leg"].Value, cut.Groups["budget"].Success));
    }

    /// <summary>
    /// Every command the help tells a reader to type - in the overview's list, its examples and every topic - is spelt
    /// as the package installs it, lower case: a Linux filesystem is case-sensitive, and the product's name,
    /// capitalised, runs nothing there. The product's name in prose is no command, and stays as it is.
    /// </summary>
    /// <remarks>
    /// A line that stands in for a verb rather than naming one tells the reader to type the product's name just as
    /// surely: 'DssHarness &lt;command&gt;' is a command line, and was missed while only literal verbs were looked for.
    /// </remarks>
    [Fact]
    public async Task EveryCommandTheHelpSaysToType_IsSpeltAsTheToolInstallsIt()
    {
        var overview = HelpText.Of(null);
        var texts = new List<string> { overview.StandardOutput };

        foreach (Match topic in TopicPattern().Matches(overview.StandardOutput))
        {
            texts.Add(HelpText.Of(topic.Groups["topic"].Value).StandardOutput);
        }

        var commands = InstalledCommandPattern().Matches(overview.StandardOutput)
            .Select(match => match.Groups["verb"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("legs", commands);
        Assert.Contains("help", commands);
        Assert.All(texts, text => Assert.DoesNotContain(
            ProductNamePattern().Matches(text).Select(match => match.Groups["verb"].Value),
            word => commands.Contains(word) || word.StartsWith('<')));
    }

    /// <summary>
    /// The usage line of a command's own help is spelt as the package installs it, as the help topics are.
    /// </summary>
    /// <remarks>
    /// Nothing here writes that line: the command-line library builds it from the executable's own name, which is
    /// why the assembly is named for the command and not for the product. One mechanism serves every command, so a
    /// few stand for all of them rather than starting a process per command.
    /// </remarks>
    [Theory]
    [InlineData()]
    [InlineData("init")]
    [InlineData("build")]
    [InlineData("help")]
    public async Task ACommandsOwnHelp_SpellsItsUsageAsTheToolInstallsIt(params string[] command)
    {
        var rendered = await CliRunner.RunAsync([.. command, "--help"], TestContext.Current.CancellationToken);

        var usage = CliRunner.UsageLine(rendered.StandardOutput);

        Assert.NotNull(usage);
        Assert.StartsWith(ToolPackage.Command + " ", usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The set-ups topic, asked for by any of its names, says how a set-up is declared, what up and down run and exit
    /// with, and what a run asked to set up does with one; config names setUp, and exec no longer.
    /// </summary>
    [Theory]
    [InlineData("setups")]
    [InlineData("set-up")]
    [InlineData("up")]
    [InlineData("down")]
    public async Task SetUpsTopic_SaysHowOneIsDeclared_RunByHand_AndAroundARun(string topic)
    {
        var token = TestContext.Current.CancellationToken;
        var result = HelpText.Of(topic);
        var text = Prose.Words(result.StandardOutput);


        foreach (var said in new[]
        {
            "config.json names each set-up under setUp, with an up and a down command line",
            "a double-quoted word kept whole, and no shell between",
            "dssharness up <name> run its up line at the root of the tree, on this machine",
            "dssharness down <name> run its down line there",
            "test and run bring them up only when given --set-up; without it a run runs as it did.",
            "so a line must be safe to run twice.",
            "the run exits 21 where nothing failed.",
            "--input name=value gives an input its value, once per input, over its default;",
            "\"projects\": [ { \"name\": \"core\", \"test\": { \"all\": { \"setUp\": [\"e2e\"] } } } ]",
            "and 13 where a line a leg would run could not run on its system",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }

        var config = Prose.Words((HelpText.Of("config")).StandardOutput);

        Assert.Contains("setUp what a run brings up around its work and down after, by name", config, StringComparison.Ordinal);
        Assert.DoesNotContain(" exec ", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every topic the overview advertises renders, and none is missing from it: the overview, the topic argument and the
    /// answer to an unknown topic all read one list of topics, which this reads back through the overview.
    /// </summary>
    [Fact]
    public async Task EveryTopicTheOverviewAdvertises_Renders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overview = HelpText.Of(null);


        var topics = TopicPattern().Matches(overview.StandardOutput).Select(match => match.Groups["topic"].Value).ToList();

        Assert.Contains("orchestrators", topics);
        Assert.Contains("ci", topics);

        foreach (var topic in topics)
        {
            var rendered = HelpText.Of(topic);

            Assert.True(rendered.StandardOutput.Length > 200, $"topic '{topic}' produced little output");
            Assert.DoesNotContain("Unknown topic", rendered.StandardOutput, StringComparison.Ordinal);
        }

        var unknown = await CliRunner.RunAsync(["help", "not-a-topic"], cancellationToken);
        Assert.Contains($"Try: {string.Join(", ", topics)}.", unknown.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic names every orchestrator and agent command, what an orchestrator keeps where, and that
    /// deleting an agent is never forced.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_NamesEveryCommand_WhatIsKeptWhere_AndThatDeletingIsNeverForced()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);


        foreach (var command in new[] { "create-orchestrator", "delete-orchestrator", "list-orchestrator", "create-agent", "seed-agent", "refresh-agent", "rebase-agent", "fold-agent", "delete-agent" })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} ", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains("agents/<agent>/seed.json", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("agents/<agent>/shared/<sha256>", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the bytes of each file it shares, kept to merge from", text, StringComparison.Ordinal);
        Assert.Contains(
            "Every command that writes an agent's records names where they are on each way out once it has: its record, its seed, the directory "
            + "of the bytes it shares where it keeps any, and the rows its folds applied - each that is there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "--settled <path> names a path you reconciled in the main tree by hand: once the rest is written, the main tree's copy - a file, or its "
            + "absence - is handed to the agent, over only what was weighed there, and recorded as shared, so its next fold starts from the "
            + "reconciliation.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "one only the agent changed is refused, since nothing was reconciled there and handing the main tree's copy would undo its change. It "
            + "is not a --force.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("put back as the main tree's HEAD holds it included", text, StringComparison.Ordinal);
        Assert.Contains($"  {ToolPackage.Command} refresh-agent <o> <a> [<path>... | --all] [--except <path>]...", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("      [--stale <path>]... [--merge <path>]... [--apply]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "--stale <path>, once for each, says its copy is not the agent's work and hands it the main tree's - taken only where the agent's copy, "
            + $"as git would store it, is a blob the main tree's history held at that path, among the last {TwoChanges.MostCommits} commits that "
            + "changed it, and refused otherwise, nothing copied, as is one naming a path the agent holds no file at. The refusal over the agent's "
            + "changes names each such copy with the commit whose bytes it holds, and the arguments that hand them; it says too where the search "
            + "stopped short of the main tree's history. It reaches a path the main tree did not move as well - a copy put back by hand to an older "
            + "version - and one naming a path the agent holds as the main tree does is let stand.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A path the agent changed that the main tree moved too, each holding a file there, is weighed in full as a fold weighs one: the refusal "
            + "says beside it what a three-way merge of the two gives - from what the agent last shared, or its base - shows the conflicts, and names "
            + "the arguments that take the clean ones. Two such paths that differ in case alone, one file in the agent's worktree, are weighed once: "
            + "under the name the main tree's git gives the file, from the base's file found in any case, whatever core.ignorecase says. "
            + "--merge <path>, once for each, takes a clean merge: written into the agent's worktree as its "
            + "change, for its fold to write, and the path recorded as handed the main tree's copy, which the merge descends from. It is never taken "
            + "unasked, and a clean textual merge proves nothing about the program; one naming no path under the paths refreshed is refused as a "
            + "misspelling, and one naming a path with nothing left to merge is let stand. The arm registry both changed is merged by arm, unasked, "
            + "and taken where the result reads as a registry of the worktree as the refresh leaves it; one that does not, or that conflicts, is "
            + "among the agent's changes, and a --merge naming it or an anchor registry is refused. A path named by two of --except, --stale and "
            + $"--merge is a usage error, exit {HarnessExit.UsageError}.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("the anchor registries' directory where none are, and anywhere in the tree with --all, which takes no path beside it", text, StringComparison.Ordinal);
        Assert.Contains(
            "A refresh refused over the agent's changes names every path it changed, and the arguments that leave them: --except <path>, once for "
            + "each, hands it every other path and leaves that one as the agent changed it, its own work for its fold to weigh against what the main "
            + "tree holds - said in the report, never silently; it is not a --force, nothing is left out that nobody named, and one naming a path "
            + "the refresh would not have refused is refused, nothing copied.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("refuses the move, nothing changed, unless --settled <path> names it", text, StringComparison.Ordinal);
        Assert.Contains(
            $"a move that stopped part way, exit {HarnessExit.Incomplete}, is finished where it was going by running it again - the command its "
            + "last words name, with every option the move was given - what it wrote held as the new base holds it, and every other command "
            + "refuses the agent until then, naming that same command.",
            text,
            StringComparison.Ordinal);
        Assert.Contains($"  {ToolPackage.Command} rebase-agent <o> <a> [--apply] [--json] [--merge <path>]... [--settled <path>]...", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Where a three-way merge of the two is clean - git's, from what the old base held, of the new base's version and the agent's - "
            + "--merge <path>, once for each, writes it into the agent's worktree as its own change on the new base, whether it can be run merged "
            + "with its content: it is never taken unasked, and a clean textual merge proves nothing about the program. Nothing is recorded for it: "
            + "its fold weighs it against the new base. The refusal says what a merge of each such path gives, and names the command that takes the "
            + "clean ones.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("a file turned into a directory, or a directory into a file or a submodule's entry - and its own changes", text, StringComparison.Ordinal);
        Assert.Contains("never forced, and its evidence check kept, so a file written late stops it", text, StringComparison.Ordinal);
        Assert.Contains("a file changed or new since is work, left for you", text, StringComparison.Ordinal);
        Assert.Contains("one not found is said, and one found and not kept stops it before anything is closed", text, StringComparison.Ordinal);
        Assert.Contains("A path this process cannot look at is never read as absent: the fold fails, nothing written.", text, StringComparison.Ordinal);
        Assert.Contains("It removes the record last, so one that stops part way finishes when run again.", text, StringComparison.Ordinal);
        Assert.Contains("Seeding again hands besides every path the main tree holds otherwise than the agent shares it, committed or not.", text, StringComparison.Ordinal);
        Assert.Contains(
            "a symbolic link the main tree committed is named and never handed, and so is a submodule's entry. A file the main tree turned into a "
            + "directory, or a directory it turned into a file, is handed as git holds it: what goes is removed first, then what comes is copied.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "and records them as handed to it, so its fold leaves them out; one the agent already holds as the main tree does is recorded as shared - "
            + "listed, and written only with --apply - so its record says what it holds;",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "refuses the hand-over, forced or not, naming each: copied, it would write over them, or through the link out of its worktree. A file of "
            + "the agent's written after it was weighed is never written over or removed by either, forced or not: the hand-over stops there, exit "
            + $"{HarnessExit.Incomplete}, naming it, and run again weighs it anew. Neither "
            + "hands anything while a move of the agent's base stands stopped part way, or its HEAD is off its base. Both say when the agent's base "
            + "is not the main tree's HEAD. A path named otherwise than in UTF-8, committed or not, refuses any hand-over, naming it as git quotes it: "
            + "no file opens here by such a name.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "or anything of its own where the main tree committed a file, ignored or staged included - refuses the move",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A file it made that is the very file the main tree committed there - the same content as git compares it, and the same mode where git "
            + "trusts one - is no such change: it is listed as held as the new base holds it already, and nothing is written.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "a file or a link where the new base holds a directory, and what it keeps - its own, settled or shared - in a directory where the new "
            + "base holds a file, or a submodule's entry that would hide it from git",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Only such a move is ever finished, and only from its base or where it goes: a HEAD moved by hand - back, forward or beside its base - "
            + "or naming no commit is never taken for one, and each command refuses it, saying how to put it back; a commit made inside the agent "
            + "is said to be one, and where git cannot say which a HEAD is, each says so and fails, never guessing.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "deleted, removed first, with the directories that leaves empty, so a file it turned into a directory, or a directory into a file, "
            + "makes room for what replaced it",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says that delete-orchestrator refuses while a directory below the orchestrator's holds a
    /// .git of its own that git records no worktree at, as it does while a worktree is left there, and that an agent with
    /// no worktree never stands in its way.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysDeleteOrchestratorRefuses_WhileADirectoryGitRecordsNoWorktreeAtIsBelowIt()
    {
        var result = HelpText.Of("agents");

        Assert.Contains(
            "delete-orchestrator deletes the orchestrator once every agent of it with a worktree is deleted and no worktree is left "
                + "below its directory, nor any directory holding a .git of its own that git records no worktree at - until then it "
                + $"refuses ({HarnessExit.Refused}), naming each - and the evidence, transcripts, steps and sessions its agents kept, "
                + "whatever their kind or state, only with --delete-evidence; an agent with no worktree holds nothing but records, "
                + "and goes with it, deleted or not, and a run's codebase graph - made from the tree, and made again whenever asked - "
                + "goes with it and is never counted among what --delete-evidence guards.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what an agent with no worktree is to the verbs a person types: never open, so the
    /// limit leaves it out; nothing recorded of it when create-agent runs again; no session, --session beside
    /// --no-worktree refused; and a deletion that is one write, saying what has nothing to act on.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatTheVerbsDoWithAnAgentWithNoWorktree()
    {
        var text = Prose.Words(HelpText.Of("agents").StandardOutput);

        foreach (var said in new[]
        {
            "It records no session - --session beside --no-worktree is a usage error - and delete-agent marks it deleted in one "
                + "write, folding nothing, removing no worktree and looking for no transcript; --discard-uncommitted and the "
                + "allowances have nothing to act on there, and it says so.",
            $"--parallel, {OrchestratorRecord.DefaultParallel} unless given, is the most open agents at once - every agent with a "
                + "worktree not yet deleted, and every worktree git lists below the orchestrator's directory: create-agent refuses "
                + "the next, naming them, and list-orchestrator counts them. An agent with no worktree is never open. Run again, "
                + "create-orchestrator changes only the limit and the session, and create-agent records only the session, and "
                + "nothing of an agent with no worktree;",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The layout topic names where a run keeps a step no agent owns, and where an agent keeps its steps - a step's
    /// handoff among them - and its conversations with an API client, each from the layout's own names.
    /// </summary>
    [Fact]
    public async Task LayoutTopic_NamesWhereARunsStepsAndAnAgentsStepsAndSessionsAre()
    {
        var result = HelpText.Of("layout");
        var agent = $"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.AgentsDirectoryName}/<agent>";

        foreach (var said in new[]
        {
            $"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.StepsDirectoryName}/<instance>/ ignored; one step of a "
                + "run that no agent owns, the create-agent that makes an agent included",
            $"{agent}/{OrchestratorLayout.StepsDirectoryName}/<instance>/ ignored; one step the agent runs, or that runs in or for "
                + $"its worktree: everything of the step, its {OrchestratorLayout.HandoffFileName} where one stopped it",
            $"{agent}/{OrchestratorLayout.SessionsDirectoryName}/<id>/ ignored; API clients only: one conversation with the "
                + "provider, in the provider's own form",
        })
        {
            Assert.Contains(said, Prose.Words(result.StandardOutput), StringComparison.Ordinal);
        }
    }

    /// <summary>The orchestrators topic says where a run starts from, and which steps are served so far.</summary>
    [Fact]
    public void OrchestratorsTopic_SaysWhereARunStartsFrom_AndWhichStepsAreServed()
    {
        var orchestrators = Prose.Words(HelpText.Of("orchestrators").StandardOutput);

        Assert.Contains(
            "A run starts from the main checkout's HEAD on a branch of its own, and records that commit and branch: it is refused (13), having made nothing, where HEAD is detached, where the checkout is on the branch the run's pull request is opened against - the pull-request step's base, filled from the run's inputs and its name, else the remote's default branch - where a merge is under way, and where changes are staged. Where the repository has a remote and neither base can be told, the start says so, its branch held to none.",
            orchestrators,
            StringComparison.Ordinal);
        Assert.Contains(
            "The steps served so far are orient, graphify-codebase, validate-plan, create-agent, reason, build, test, run, check-mutations, anchor-balance, anchor-citations, ci-read, ci-wait, fold, delete-agent, refresh, rebase, commit, push, pull-request ('dssharness help workflows'): a step of any other type fails, saying no runner serves its type.",
            orchestrators,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators and layout topics say where a step's contained child's session, and the group of its legs' work, is recorded, and that every command
    /// over an orchestrator first ends what a killed process left of them - and on which systems - from the layout's own names.
    /// </summary>
    [Fact]
    public void OrchestratorsTopic_SaysWhatAKilledProcessLeftRunning_IsEndedByTheNextCommand()
    {
        var orchestrators = Prose.Words(HelpText.Of("orchestrators").StandardOutput);
        var layout = Prose.Words(HelpText.Of("layout").StandardOutput);
        var record = $"<step>/{OrchestratorLayout.ChildrenDirectoryName}/<session>{OrchestratorLayout.ChildExtension}";

        Assert.Contains($"{record} in either kind of step, on Linux and macOS: a session a contained child of the step leads while it runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a leg to a host leads while anything is in it", orchestrators, StringComparison.Ordinal);
        Assert.Contains(
            "What a killed process left running. On Linux and macOS a contained child of a step leads a session of its own, and "
            + "what a machine step's legs start a process group of its own, apart from the engine's terminal; nothing ends either "
            + "when the process that started it is killed, so each session, and the group of each phase - a set-up's up line apart - "
            + $"keepAwake command and ssh that carries a leg to a host, is recorded in its step's {OrchestratorLayout.ChildrenDirectoryName}/ "
            + "while it runs, and the record taken back once nothing is left in it: a group still holding what its child left at its "
            + "own end keeps it. Every command over an orchestrator - the three "
            + "orchestrator commands, each agent command, and each run verb over a run already made - first ends each recorded "
            + "session or group whose starter has gone, its leader standing or not (SIGTERM, then SIGKILL five seconds later), "
            + "removes its record, and says so in a line, and in the document's "
            + "ended where it answers with --json; stop-orchestrator --now does it again once it has ended the engine and its "
            + "tree, and says what it ended. A session or group whose starter still runs is left; one whose id a later process "
            + "carries ends nothing, and its record goes; a record that does not read as one is said and left; a starter on another "
            + "machine is never taken to have gone. Windows records nothing: a job ends a contained or bound child with the process "
            + "that started it.",
            orchestrators,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{record} ignored; in either kind of step, on Linux and macOS: a session a contained child of the step leads while it "
            + "runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a "
            + "leg to a host leads while anything is in it, ended by the next command over the orchestrator - and by "
            + "stop-orchestrator --now - once its starter has gone",
            layout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says what a fold does with a path both trees changed: what a three-way merge of it gives
    /// is said, a clean one is taken only where --merge names it, what is never merged, and that whether a file can be
    /// run is merged with its content. The bound it names is the code's.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatAFoldMerges_WhatItNeverMerges_AndHowTheBitIsMerged()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);


        foreach (var command in new[] { "fold-agent", "delete-agent" })
        {
            Assert.Contains($"  {ToolPackage.Command} {command} <o> <a> [--apply] [--json] [--merge <path>]... [--settled <path>]...", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            "already in the main tree; merged, where both trees changed it and --merge names it, or it is the arm registry, merged by arm unasked; "
            + "or settled",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "its refusal says what a three-way merge of the two gives - git's merge, of the forms git stores, from what both held, which for a shared "
            + "path are the bytes kept beside the agent's seed, or found by their digest in the main tree's history where none were kept, and for any "
            + "other its base's - clean, with the hunks each side brought; conflicting, with the conflicts shown; or none offered, with why - and names "
            + "the command that takes the clean ones.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "--merge <path>, once for each, takes a clean merge: written into both trees, the main tree's first, and recorded as shared once both "
            + "hold it, so a fold that stopped between the two merges again to the same bytes. It is never taken unasked, a conflict never, and a "
            + "clean textual merge proves nothing about the program",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A --merge naming no path the fold weighs is refused as the misspelling it most likely is, and one naming a path with nothing left to "
            + "merge is let stand, so the command line of a fold that stopped part way runs again; a path named with both --merge and --settled is "
            + $"a usage error, exit {HarnessExit.UsageError}, and a --merge naming an anchor registry or the arm registry is refused.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never merged, whatever is named: a deletion against a change, a path both trees made, a version that is a link or is reached through "
            + "one, a file git reads as binary or will not merge, one "
            + $"whose merge attribute asks for another merge, a file above {DiskSpace.Size(TwoChanges.MostBytes)}, and an anchor registry; nor is the "
            + "arm registry ever merged by line.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The arm registry (mutations.registry) both trees changed is merged by arm, unasked - each arm, and each section's heading (the comments a "
            + "blank line parts from the row below them), from the side that alone added, removed or "
            + "changed it, each version as its tree holds it, from nothing where neither held a registry before - and taken only where the merged text "
            + "reads as a registry of the tree as the command leaves it, as check-mutations reads one ('help mutations'); an arm or a heading the two "
            + "changed differently, a heading no row of the merge would stand under, a version that cannot be read as a registry, or a merged text that "
            + "does not read as one refuses the path, each shown, and so "
            + "do shared bytes that cannot be found, since an arm one side removed would come back.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "The arm registry is merged by arm, unasked, as a fold merges it (below), from the old base's version, of the new base's as the worktree "
            + "checks it out and the agent's, and judged by the worktree as the move leaves it.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Whether a file can be run is merged with its content: the merged file takes the bit of the side that changed it, and the bit alone never "
            + "conflicts - but a registry both trees made, one so that it can be run and one not, is offered no merge, since nothing either held "
            + "before says which is meant; Windows keeps no such bit, and none is read or set there.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("mode is never merged", text, StringComparison.Ordinal);
        Assert.Contains(
            "A --merge naming a path the two commits hold alike is refused as the misspelling it most likely is, and one naming any other path "
            + "they changed is let stand, so the command that finishes a stopped move runs again; a path named with both --merge and --settled is "
            + $"a usage error, exit {HarnessExit.UsageError}; and a path one of whose versions is a link, or that the agent reaches through one, is "
            + "offered no merge.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("a change, a new file, a deletion, a file renamed in case alone, a link, a submodule's entry,", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic, the architecture and the README say alike that a file the new base renamed in case alone
    /// comes in only with its old name, never over the agent's copy or its deletion of it, that a file the agent's session
    /// wrote after it was weighed stops a move before git writes anything, that a fold refuses a rename of case alone the
    /// agent made, naming git mv -f for the main tree, and weighs it against the base's file under its old name, and that a
    /// refresh weighs two names of one file once.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatRenamesOfCaseAlone_AndASessionsWrites_DoToAMoveAFoldAndARefresh()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);
        var architecture = Prose.Document("docs", "architecture.md");
        var readme = Prose.Document("README.md");

        Assert.Contains(
            "So does a file the new base renamed in case alone, where the agent's file system finds a name in any case, unless its old name comes in "
            + "with it: where the agent keeps that - its own change, settled or shared - writing the new name would write over it, and both are named, "
            + "a shared one it changed to be folded first, since no refresh hands it, and one it did not to be refreshed under the new name; where the "
            + "agent's worktree no longer holds it, the two are named as a rename and a deletion of one file - its old name to be put back and the move "
            + "run again, the new name deleted after only where the agent's session deleted the old - unless a move that stopped part way removed it, "
            + "as its record says, which running it again finishes. "
            + $"A file of the agent's written after it was weighed is never written over or removed: the move stops there, exit {HarnessExit.Incomplete}, "
            + "before git writes anything, naming it.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A rename of case alone the agent made, where both trees find a name in any case and the main tree's git index names the file as before, is "
            + "refused by name: a fold writes files, and the main tree's git would go on naming the file as before - git mv -f there makes the rename, of "
            + "a file in a directory renamed in case too, and the fold run again finds the old name absent from both. The renamed file is weighed "
            + "against the base's under its old name, found in any case whatever core.ignorecase says: an edit made with the rename merges with the main "
            + "tree's, and the main tree's "
            + "removal of the file refuses it, never undone.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "It comes in only with its old name: where the agent keeps what it holds there - declared settled, shared with the main tree, or its own "
            + "change - the new name refuses the move, naming both, since writing it would write over that; a shared file the agent changed is to be "
            + "folded first, since no refresh hands a path the agent changed, and one it did not to be refreshed under the new name, which records the "
            + "file it holds as shared there. Where it holds nothing at the old name, the new name refuses the move as a rename and a deletion of one "
            + "file - its old name to be put back with `git checkout HEAD` and the move run again, the new name deleted after only where the agent's "
            + "session deleted the old, or, shared, never taken by a move and so the agent's deletion, folded and committed in the main tree - unless "
            + "the move being finished removed it, the new name written last, as its record says (`MovingRemoved`: the old names the agent's index no "
            + "longer holds once the git checking out what the move writes first ends, having written it all or stopped part way). git leaves the "
            + "index and the disk alike after that checkout and after a `git rm` of the agent's session; a removal of the move's its record missed - a "
            + "move killed between git and its record - is refused so, costing a refusal, never a file. The record names where the "
            + "move goes (`Moving`) before anything is written; then each path the move takes is asked to hold what was measured there "
            + "(`RebasePlan.Held`, `AgentFold.RequireTakenAsync`), so a file the agent's session wrote since stops the move, exit 21, before git "
            + "writes anything, never written over;",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "The base's entry and HEAD's are found alike, as git spells the path and, where the main tree's file system finds a name in any case, in "
            + "any case, whatever `core.ignorecase` says, so a rename of case alone is the commit's that made it, and none other's.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "where its git index spells that file otherwise than its base does, found in any case, the agent renamed it in case alone, and each "
            + "path is read only where a tree spells the file so (`AgentFold.Spellings`) - as its git index does, where it holds the file under one "
            + "spelling, since a rename git makes in the index alone leaves a directory's name on disk as it was, and otherwise as its file system "
            + "does (`PathCase.Spelled`) - never as the other. Where the main tree's git index spells that file otherwise, each tree "
            + "holding it, and the main tree finds a name in any case too, a fold cannot write the rename - the main tree's git would go on naming the "
            + "file as before - so it is refused by name, saying to run `git mv -f` there, forced since git finds the new name taken where the "
            + "repository does not say it ignores case; once that is run, the old name is already in, absent from both. Every path the agent shares "
            + "nothing for is weighed against the base's entry found as git spells it, as `Came` finds it, so a file renamed in case alone is weighed "
            + "against the base's under its old name: an edit made with the rename merges with the main tree's, and a removal of the file in the main "
            + "tree refuses it, never undone.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "(`TwoChanges`, from what the seed records or the agent's base holds, found as git spells the path; the main tree's file the side merged "
            + "into) - two such paths that differ in case alone, one file in the agent's worktree where it finds a name in any case, once, under the "
            + "name the main tree's git index spells the file by, from the base's entry found in any case, the others - one left as the agent changed "
            + "it among them - no file of the main tree's, whatever its file system finds under them:",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains("a path the agent already holds as the main tree does - the same file, or no file in either, a directory of the agent's own files there included -", architecture, StringComparison.Ordinal);
        Assert.Contains(
            "refused over anything of its own what comes in would go over, a file renamed in case alone over the agent's copy at its old name, or its "
            + "deletion of it, among it; a file the agent's session wrote after it was weighed stops it, exit 21, never written over;",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "a rename of case alone the agent made is refused, saying to `git mv -f` it in the main tree, and once made there is weighed against the "
            + "base's file under its old name;",
            readme,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The orchestrators topic says the one rule of what a dry run exits with, where its summary then stands, that a row
    /// an earlier fold applied is weighed cell by cell with --accept-over, and that a closed agent's husk is answered by
    /// its dry run as every --apply of it answers.
    /// </summary>
    [Fact]
    public async Task OrchestratorsTopic_SaysWhatADryRunExitsWith_AndWeighsRowsCellByCell()
    {
        var result = HelpText.Of("agents");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "Dry runs. A dry run of refresh-agent, rebase-agent, fold-agent or delete-agent exits with what its --apply, typed "
            + "with the same arguments, would exit with at that moment: 0 only where that --apply would go through. Where a path, "
            + $"a row, or a consent nobody gave would refuse it, the dry run exits {HarnessExit.Refused} - {HarnessExit.UsageError} "
            + "where its arguments are a usage error, a path never handed to an agent among them - and where it would fail "
            + "before writing anything, with that failure's code - and either way it still prints all it weighed on standard "
            + "output, its summary last, on standard error. A run holding what the --apply takes refuses the dry run too: every "
            + "holder is named on a 'held now:' line, said with where its run's leg stands where it asked for a heavy-leg slot, "
            + $"and it exits {HarnessExit.Refused} - save where the dry "
            + "run already failed before writing anything, or met a usage error its --apply answers before it takes anything, "
            + "which keeps its code and names the holder still; "
            + $"delete-agent, whose --apply would go as far as the removal and leave it unfinished, exits {HarnessExit.Incomplete} "
            + "instead where only a copy of the worktree is held, by a run or a sweep; and where nothing refuses its --apply "
            + "before the removal - a tree held, a lock file that cannot be read, a path, a row or a consent - it exits "
            + $"{HarnessExit.Incomplete} too where the copies kept in the worktree cannot be removed as they stand, one that "
            + "cannot be told or removed or none that can be looked for, its request then refused; a directory there that no "
            + "sync made goes with the worktree, and is only said. A record of the copies hosts keep that cannot be read refuses "
            + $"delete-agent ({HarnessExit.Refused}), its dry run and its --apply alike, before anything is written. What an "
            + "--apply can only find by "
            + "writing - a file changed after it was weighed, a write "
            + "that fails - is not in a dry run, so 0 never promises that the --apply will finish. --json answers with one "
            + "document, the whole of standard output, whose request member says what is wanted of you - 'sound' (nothing; wait, "
            + "where heldNow names a holder), 'needs-consent' (give what the dry run names: cells, rows or paths) or 'refused' (put right what the "
            + "last line names) - and whose heldNow holds each holder that decides its code: each a 'held now:' line names, with "
            + "where its leg stands - null for a leg of this machine where this machine's record of heavy legs cannot be read - "
            + "and each holding a copy kept in the "
            + "worktree, said as its own line says it, a leg copy's holder with where its leg stands, a mutation worker's with "
            + "the process, the run and since when its sweep's claim records. Beside them, needs names each consent the "
            + "request lacks - 'fold-new-rows' (--new), 'fold-lost-rows' (--accept-lost), 'fold-over-cells' (--accept-over) "
            + "or 'fold-two-changes' (--merge) - and refusals each reason it is refused for, 'conflict' where a path both "
            + "trees changed has a merge that conflicts and 'other' for anything else, so a request refused for conflicting "
            + "merges alone says 'conflict' and nothing else; folded says what the --apply of a fold and of a deletion did, gone "
            + "through or stopped part way after writing some of it - what it wrote, deleted, merged and handed, the arm registry "
            + "merged by arm, the registries and rows it applied, and the cells --accept-over took - and is null for every other "
            + "answer and where it did nothing at all; planned says what the plan of the verb names, its dry run's and its "
            + "--apply's alike - what a rebase takes and keeps, what a refresh or a fold hands, what each merges, what a fold, a "
            + "deletion's among them, writes and deletes, the registry files its rows change, as git names them, and the rows it "
            + "applies - and what each consent it lacks and each conflict it meets is about: mergeable, the paths both trees "
            + "changed whose clean merge nobody asked for; conflicting, those whose merge conflicts; newRows, lostCells and "
            + "overCells; null where no plan was measured, a deletion that folds nothing among them. A refresh or a rebase over a "
            + "path both trees changed whose merge is clean lacks --merge as a fold does - needs-consent, its code 13 still - and "
            + "one over conflicting paths alone is refused for a conflict. An --apply refused at its hold answers the same "
            + "document, applied true: request 'sound', and the run it met in heldNow. Stopped before "
            + "anything is weighed - a command line the parser refuses, a --directory that is not there, a repository not "
            + "initialised, another usage error, or a refusal met before then, an agent that is not there among them - it "
            + "answers with the same document, empty: applied false, request and heldNow null, no lines, no needs, no "
            + "refusals, and folded and planned null.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "A dry run of the four asks with the very hold its --apply takes, so a run holding what it needs is named and the dry "
            + $"run refused itself, exit {HarnessExit.Refused}, saying that nothing in the request needs correcting; the refusal an "
            + "--apply gives names the holder in the words of the dry run's 'held now:' line, where its run's leg stands included. "
            + "A holder is a fact about one moment, so 0 from a dry run never promises the "
            + "tree will still be free when the --apply is typed.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "delete-agent alone names a copy a run holds, since it removes every copy with the worktree: a host's, a leg copy kept "
            + "in it, and a mutation worker, each in its own words.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "and --apply refuses the fold, writing nothing, until each is named - and so the dry run that shows it "
            + $"exits {HarnessExit.Refused} itself.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "One declared anew over it is weighed cell by cell - priority, status, trigger, closing, cross-refs - from what that "
            + "fold recorded as declared, against the registries as the write finds them: a cell only the agent changed is "
            + "written; one only the registries changed is kept, and said, and stays kept at each fold until the agent changes "
            + "it; one both changed alike is left; and one both changed otherwise is written over the registries' only where "
            + "--accept-over <ID>:<cell> names it, the dry run showing the three texts - what --accept-over consents to includes "
            + "what the registries' text loses. The fold never writes the agent's own row files: before it changes a kept cell, "
            + "the agent writes the registries' text into it, or its change is two changes of one cell.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "since every --apply of it ends so, its dry run exits " + HarnessExit.Incomplete + " too, naming the same command and keeping nothing.",
            text,
            StringComparison.Ordinal);
        Assert.Contains("delete-agent refuses to run from inside the worktree it removes, and so does its dry run,", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTopic_SaysSoAndListsTheRealOnes()
    {
        var result = await CliRunner.RunAsync(
            ["help", "not-a-topic"],
            TestContext.Current.CancellationToken);

        // A mistyped topic is a usage error. Reporting it on stdout with exit 0 would
        // break both "zero always means success" and the pipeable-output rule.
        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("Unknown topic", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("exit-codes", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown topic", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A known topic is recognised and rendered: what the command writes to standard output with exit 0, where an unknown
    /// one is written to standard error as a usage error.
    /// </summary>
    [Fact]
    public async Task KnownTopic_IsRecognised_AndRendersItsText()
    {
        var recognised = HelpTopics.TryRender("layout", out var text);

        Assert.True(recognised);
        Assert.Contains(".harness-config", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_ExplainsTheGlobalOptions()
    {
        var result = HelpText.Of(null);

        Assert.Contains("--directory", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--verbose", result.StandardOutput, StringComparison.Ordinal);
    }

    private static IEnumerable<(KeyDescription Key, int Depth)> Listed(IReadOnlyList<KeyDescription> keys, int depth)
        => keys.SelectMany(key => Listed(key.Keys, depth + 1).Prepend((key, depth)));

    [GeneratedRegex(@"^\s{2}(?<name>\S+)(\s+\[?<\S+)*\s{2,}(?<description>.+)$")]
    private static partial Regex CommandLinePattern();

    [GeneratedRegex(ToolPackage.Command + @" help (?<topic>[a-z-]+)")]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"^\s{2}" + ToolPackage.Command + @" (?<verb>[a-z-]+)", RegexOptions.Multiline)]
    private static partial Regex InstalledCommandPattern();

    /// <summary>The product's name followed by a verb, or by something standing in for one.</summary>
    [GeneratedRegex(ToolPackage.Id + @" (?<verb><[a-z-]+>|[a-z-]+\b)")]
    private static partial Regex ProductNamePattern();

    [GeneratedRegex(@"""legJobPattern"": (?<pattern>""(?:[^""\\]|\\.)*"")")]
    private static partial Regex ExamplePattern();

    /// <summary>
    /// The workflows topic says what the fold family's steps do - read their dry run's standing, ask with what it names,
    /// hold the apply to it and a fold to its ledger and its within, take others in turn - and the orchestrators topic
    /// names the files they keep, each from the code's own names.
    /// </summary>
    [Fact]
    public void TheHelp_SaysWhatTheFoldFamilysStepsDo_AndTheFilesTheyKeep()
    {
        var workflows = Prose.Words(HelpText.Of("workflows").StandardOutput);
        var orchestrators = Prose.Words(HelpText.Of("orchestrators").StandardOutput);

        Assert.Contains(
            "fold, delete-agent, refresh and rebase make their verb's dry run first and read what it stood, typed, never its "
                + "code: sound, they apply; a run holding the tree the apply takes is waited out, as a held "
                + $"lock is ({HarnessExit.Refused} after the retries), and one holding only a copy a deletion removes is run again, as an "
                + $"incomplete step is ({HarnessExit.Incomplete} after them);",
            workflows,
            StringComparison.Ordinal);

        // Only a fold and a deletion hand their verb what the dry run named: a refresh's and a rebase's requests take none.
        Assert.Contains(
            "A fold's apply, and a deletion's, is held to exactly what its dry run named: measured again under its hold and found "
                + "to write anything else - the trees moved between the two - it writes nothing; an apply of any of the four refused "
                + $"under its hold so is weighed again, {FoldFamilyStepRunner.MostPasses} times at most. A fold, and a deletion that "
                + $"folds, write each path they mean to write, with what the main tree holds there, to the run's ledger ({OrchestratorLayout.FoldsFileName}) "
                + "before the verb is called, and what they left after",
            workflows,
            StringComparison.Ordinal);
        Assert.Null(typeof(RefreshAgentRequest).GetProperty(nameof(FoldAgentRequest.Expected)));
        Assert.Null(typeof(RebaseAgentRequest).GetProperty(nameof(FoldAgentRequest.Expected)));
        Assert.NotNull(typeof(DeleteAgentRequest).GetProperty(nameof(FoldAgentRequest.Expected)));

        // Whether a fold is committed is the run's commits' and the main tree's to say, whoever committed it.
        Assert.Contains(
            "a run stopped with a fold in its ledger whose change no commit took yet - a path it changed that no commit step of the "
                + "run took, that git does not ignore and that the main tree still holds otherwise than its HEAD, whoever commits it - "
                + "is unfinished, as one with an agent open is. A deletion run again "
                + "once its agent is deleted - its ending cut off before it was recorded - is done, with what its directory kept of its apply.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"recorded in the step's directory ({OrchestratorLayout.StepTargetsDirectoryName}/<agent>/), one closed before its turn "
                + "passed over; a target that fails ends its own turn, and the step ends done, or failed with the code of the first "
                + "that failed, once each has had its turn. Each step keeps what its dry run named and how its apply ended in its "
                + $"directory ({OrchestratorLayout.StepFoldFileName}).",
            workflows,
            StringComparison.Ordinal);
        // What a commit step does, held to the codes it ends with, the file it writes and the types that are checks.
        Assert.Contains(
            "commit commits the folds upstream of it in the flow - every visit and iteration of them, never another work item's - "
                + "and no other work: each path one of them changed that no commit of the run took yet, holding what the newest fold "
                + "that names it left there, so a registry two items' folds wrote is taken whole. A fold upstream whose intent "
                + "nobody settled - a crash cut it off, and its step has not run again - leaves what it wrote untold, and fails the "
                + $"step ({HarnessExit.Refused}) naming it, nothing staged. With none owed it is done, "
                + "committed false, and runs no git at all.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "Otherwise it works on the branch the run started on alone: the main checkout on another - a person checked one out "
                + $"while the run was paused - or its HEAD detached fails it ({HarnessExit.Refused}) naming both, nothing staged, as it "
                + "does push and pull-request. The index must be empty and each path hold what its fold left there, and where either "
                + $"does not - a path edited and not committed, or its fold's change undone - the step fails ({HarnessExit.Refused}) naming each, nothing staged",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "told by the blob git would store against HEAD's and never by git's status, which does not look at a path marked "
                + "assume-unchanged or skip-worktree - was committed by hand, and is marked taken by HEAD; one left to stage that the "
                + $"index marks so fails it ({HarnessExit.Refused}) naming each with its mark, since git add would not stage it.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "The commit is held to the set, by what it changes against its parent: one that took more - a hook staged another "
                + $"file - or left a path of the set out fails the step ({HarnessExit.CommandFailed}) naming each, and is left as it is "
                + "for a person to keep or undo, the set staged in the ledger beside it. A set an attempt before left staged is "
                + "settled first: committed where HEAD moved and holds each of its paths as the set staged it, else unstaged and "
                + "staged again whole. It answers committed, sha, paths and ignored - each path its folds changed that git ignores, "
                + "folded and never committed, which what the run's end leaves for a person names too",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"else the step's own message - none, or a blank one, fails it ({HarnessExit.ConfigInvalid}) - and the rest is written to the ledger",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"the message written to the step's directory ({OrchestratorLayout.StepCommitMessageFileName}), with -s where commit.signOff - "
                + $"and marked committed with each fold path it took; git refusing, a hook among the reasons, unstages it and fails the step ({HarnessExit.CommandFailed}).",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"The checks are the steps of type {string.Join(", ", WorkflowStepTypes.All.Where(type => type.Checks).Select(type => type.Type))} and a reason step marked independent, whatever each is named.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains($"{OrchestratorLayout.FoldsFileName} the ledger of the run's folds:", orchestrators, StringComparison.Ordinal);
        Assert.Contains("and each commit a commit step staged of them and what became of it", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepCommitMessageFileName} a commit step: the message it commits with", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepPullRequestBodyFileName} a pull-request step that opened one: the body it opened it with", orchestrators, StringComparison.Ordinal);

        // What push and pull-request do, held to the codes they end with, the file one writes and the closed list of
        // commands the publishing steps run, each with its own limit.
        Assert.Contains(
            "{run.leftover}, {run.assumptions} and {run.preExisting} - what the run's loops left, what its agents assumed (no step "
                + "records an assumption yet) and the issues it found there before - say 'none' alike where they name nothing.",
            workflows,
            StringComparison.Ordinal);
        Assert.Equal(WorkflowExpressions.RunTexts.Order(StringComparer.Ordinal), ["assumptions", "leftover", "notRun", "preExisting", "ran", "red"]);
        Assert.Contains(
            "push pushes the commit the main checkout's HEAD names to the branch of its name on a remote - the step's remote, else "
                + "the one the run's branch goes to (the push step's, else the one the branch tracks, else origin, else the only one) - "
                + "as a fast-forward and never forced, and answers pushed",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every push step that names a remote names the same one, the remote the run's branch goes to: a definition whose push "
                + "steps name two is refused. HEAD detached, the main checkout on another branch than the run started on, a branch "
                + "with no commit yet, no remote it can name and one the repository does not have each refuse it",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"each refuse it ({HarnessExit.Refused}), nothing pushed; the remote refusing the push - its branch moved on since - fails it "
                + $"({HarnessExit.CommandFailed}) in git's words. A checkpoint push runs only where the workflow says pushCheckpoints, and is skipped otherwise.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"written to the step's directory ({OrchestratorLayout.StepPullRequestBodyFileName}) for gh to read. One already open from "
                + "that branch to that base is answered as it is, created false, and never edited: a step run again opens no second. "
                + "One open from a fork's branch of the same name is none of the run's, as gh says of each (isCrossRepository).",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"A title blank once filled fails it ({HarnessExit.ConfigInvalid}), as a body that cannot be read or reads nothing does; "
                + "HEAD detached, the main checkout on another branch than the run started on, no remote, no base it can tell and a "
                + $"base that is its own branch each refuse it ({HarnessExit.Refused}), nothing opened; gh not there fails it "
                + $"{HarnessExit.ToolMissing}, and gh saying no {HarnessExit.CommandFailed}.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "each one it opened or found is left for a person, by its address, in what the run's end says (leftForAPerson), "
                + "as is each path its folds changed that git ignores, never committed.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "The commands commit, push and pull-request run are a closed list - git --literal-pathspecs add -- (15 minutes); "
                + "git --literal-pathspecs rm --cached --ignore-unmatch -q -- (15 minutes); git --literal-pathspecs reset -q -- (15 minutes); "
                + "git commit --cleanup=whitespace -F (15 minutes); git push --porcelain (5 minutes); "
                + "gh pr list --state open --json number,url,isCrossRepository (5 minutes); gh pr create (5 minutes) - each given the step's "
                + $"timeoutMinutes where it says one, else its own; one past it is not done ({HarnessExit.Incomplete}), and the step runs "
                + "again, a commit's set left staged settled by its next attempt.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepFoldFileName} a fold, delete-agent, refresh or rebase step:", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepTargetsDirectoryName}/<agent>/ a refresh or a rebase of others:", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepGraphFileName} a reason step that has a codebase graph: one line for each call of the graph tool", orchestrators, StringComparison.Ordinal);
        Assert.Contains($"<step>/{OrchestratorLayout.StepWorkingSetFileName} a reason step that has a codebase graph: the nodes it edited, served, read and found", orchestrators, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows topic says what a workflow is, where it lives, what this build does with one and what it does not,
    /// and lists every key from the tables the reader itself reads - so what is listed is what is read.
    /// </summary>
    [Fact]
    public async Task TheWorkflowsTopic_SaysWhatAWorkflowIs_AndListsEveryKeyTheReaderTakes()
    {
        var result = HelpText.Of("workflows");


        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "A workflow is a flow of typed steps that depend on one another by name, declared by name under workflows in config.json and defined by a yml file of its own:",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "This build checks a workflow's definition, with 'dssharness validate-workflow <name>', and starts and drives a run of one with "
                + "'dssharness run-orchestrator <name>' (see 'help orchestrators'). The steps served so far are orient, graphify-codebase, validate-plan, "
                + "create-agent, reason, build, test, run, check-mutations, anchor-balance, anchor-citations, ci-read, ci-wait, fold, delete-agent, "
                + "refresh, rebase, commit, push, pull-request, each below; a step of "
                + "any other type fails, saying no runner serves its type.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "every path the yml names is relative to that directory and stays inside it, as an action's files are relative to its own directory",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "What the steps served so far do. orient reads the main checkout and the repository once, and answers: the branch (failed, refused, where HEAD is detached), baseBranch - the pull-request step's base, filled from the run's inputs and its name, else the default branch of the remote the run's branch goes to (the push step's remote, else the one the branch tracks, else origin, else the only one), null where there is none - startCommit, the commit the run started from;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "validate-plan checks the work items its items yields and answers ok, problems and the items, never a failure: at least one item and at most 8;",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "create-agent makes the agent the engine named for it - a<N>, one past the highest the run's agent directories and step records hold, never given twice - for the model of the first reason step that works in it, or the default tier's first model where none does, both written to the step's record before the agent is made.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "reason works the step's agent: the one its in names or the work it goes on with (resume), or - where it names neither - the engine's own read-only agent, with no worktree, made before the step runs and deleted once no "
                + "step can go on with its work; the step's model resolved with its agent definition below its own and its order built as entries of a model and one of its provider's keys, each entry's credential read from the slot "
                + "('help agent-clients'); its prompt file read and filled where the step starts, each skill it names looked up again and the folder handed over in the agent's work directory; the work it goes on with rendered as its continuation record, "
                + "and its going on by the client's kind - the stored conversation handed where the client goes on with one, a new launch rendered where it does not; and its answer, held to the schema its outputs names, the step's output. A failed "
                + "agent is tried again on its model - the step's retries, absent the workflow's maxRetries, waiting its backoffSeconds, absent the workflow's, twice as long before each later try - and then on each model of its order; what its agents "
                + "assumed without asking is kept, read as the step's own assumptions fact and named in the run's. Where the step may ask (ask) its agent's questions pause it - askRounds times in one attempt, the answer or decide going on in the same "
                + "attempt as the agent's next message; a limit of its own (turns, budget, a truncated answer, a full context) asks, more offered only where a stored conversation of the attempt can be handed, and compact for a full context goes on "
                + "from the engine's reduction of it, the attempt's bounds counting still; every account at a limit, and a provider's service failing past the client's own tries, pause the run safely, and the resume runs that very step.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("params - its own knobs, defaulted in the file and read as param.<name>", prose, StringComparison.Ordinal);
        Assert.Contains(
            "A step gives a failure nothing handles to the workflow's recover by saying onFailure: recover, itself or through a group around it - never "
                + "a reasoning step, whose agent is tried again (retries, waiting backoffSeconds, twice as long each time) and on each model of its fallback, and which asks "
                + "where it still fails; where every account it could use is at its limit, or its provider's service fails past the client's own tries, the run pauses safely "
                + "instead and resumes that very step. The recovery's steps, written once, read the step that failed as source and see every step of the flow, find what "
                + "went wrong and put it right; then the run goes where its onSuccess says - failed-step, the step that gave it its failure, which runs again "
                + "from where it stopped - and a failure of its own work, past its steps' retries, does what its onFailure says: pause or stop; the recovery "
                + "never calls itself. A step that fails again goes to the recovery again by its own onFailure, until it passes, a person is asked, or maxVisits is met (absent, "
                + $"the engine's bound of {FlowRecover.MostVisits} visits); past it the run asks (recoveryBound), with the advisor's recommendation of a fix: "
                + "more, one more recovery; direct, with the fix in words, for the next one; skip, where the step is skippable; or abort.",
            prose,
            StringComparison.Ordinal);

        // A type's required key is said to be required, from the one table the parser refuses its absence by.
        Assert.Contains("the prompt file the step is asked with; required", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the predefined runner the step runs; required", result.StandardOutput, StringComparison.Ordinal);
        Assert.All(
            WorkflowStepTypes.All.SelectMany(type => type.Keys.Where(key => key.Required)),
            key => Assert.Contains($"{key.Meaning}; required", result.StandardOutput, StringComparison.Ordinal));

        // What init writes of the implementation workflow, and where: the one owner of that rule is InitService, held to it by
        // InitServiceTests, and this sentence says what those hold.
        Assert.Contains(
            "init writes one - the implementation workflow, from a request to a pull request whose checks are green - and declares it in "
                + $"the config it creates. Where config.json declares {WorkflowTemplates.ShippedWorkflowName}, init writes each of its "
                + "files that is missing - the whole set where the repository has no definition of it yet, and where it has one only "
                + "the shipped files that definition names, so a file taken out with its step stays out - and keeps each one that is "
                + "there, whatever it holds, so an init that stopped part way is "
                + "finished by the next - where a file is missing and the definition there does not read, init refuses (12; 13 where it "
                + "cannot be read), since which files it names cannot be told; nothing that exists is ever overwritten, and a config that "
                + "does not declare it gets none of it.",
            prose,
            StringComparison.Ordinal);

        // What an expression's reads are typed by, and what each operator, each key and the run's own standing take: the
        // statuses are the code's one list of them, so the sentence cannot name another.
        Assert.Contains(
            "every path it reads is typed by what holds it - a reason step's schema, the output a built-in type answers, the "
                + "engine's facts of a step, the run's own names - and every operator takes what it says: !, && and || a boolean, == and != "
                + "no list and no object, any a list and a test that yields a boolean. A when, an until and a blocking yield a boolean, and "
                + "a forEach and a key that takes a list a list; how the run stands without its always steps "
                + $"({string.Join(", ", WorkflowExpressions.RunStatuses.Select(status => $"run.{status}"))}) is read by an always step "
                + "alone. A placeholder in braces - '{step.member}' - in a template key reads the same names, typed the same way, as one in "
                + "a prompt does; one in any other key is refused, and a pull request's base reads nothing but the run's inputs and "
                + "run.name, the same for every pull request. A brace meant literally is written twice, {{ and }} alike, another "
                + "expander's ${...} is left as written, and a placeholder that names nothing is refused. Every template key of a step "
                + $"is filled where the step starts, and one that reads nothing fails it ({HarnessExit.ConfigInvalid}) before its runner is asked.",
            prose,
            StringComparison.Ordinal);

        // What the checks that run no legs do and answer, and what red does to them: each default and code is its owner's.
        Assert.Contains(
            "anchor-balance and anchor-citations run the checks of those names in the main checkout - the balance from its base, the "
                + $"citations of its subject ({string.Join(", ", AnchorCitationSubjects.Names)}; "
                + $"{AnchorCitationSubjects.Default} where it names none) - each held to its rows or its paths where it names them: the anchors "
                + "among those rows the change opened, closed and lost, a problem at a line of one of them and every problem of a registry as a "
                + "whole, or the citations in those files; a rows or paths expression that reads nothing names nothing. Each answers passed and "
                + "findings, each a line as the check prints it. ci-read reads the legs check-ci-legs reads, of its branch - the one checked out "
                + "in the main checkout where it names none - and its newest limit runs, answering passed, branch, matrixRan, legs and red, each leg as "
                + "check-ci-legs --json gives it. ci-wait waits for every check run and commit status the forge lists for its commit - the "
                + $"commit the run last pushed where it names none, refused ({HarnessExit.Refused}) where it pushed none - less those its ignore "
                + "names - a job by its own name, as its workflow names it, a status by its context - written out or with * for any run of "
                + $"characters, reading them every pollSeconds ({CiChecksRequest.DefaultPollSeconds}): "
                + "a red one - one that concluded failure, a status of failure or error - ends it at once, answered with its name, url and, of a "
                + "job of a workflow run, the job, its workflow, the step that failed, its leg as check-ci-legs classifies it, whether it overran "
                + "its budget, and its whole log kept in the step's directory "
                + $"({OrchestratorLayout.StepLogsDirectoryName}/<job>.<id>{PhaseLogs.Extension}, the job's id telling two of one name apart) "
                + "with its last lines in tail, both masked by the repository's runner secrets, and in unread why any of that could not be "
                + "read; every check ended green - success, neutral or skipped - on two reads running that found the same checks, at its "
                + "wait's end on the last alone, passes it; and no check within "
                + $"appearSeconds ({CiChecksRequest.DefaultAppearSeconds}) - or waitMinutes where that is shorter - checks still running at waitMinutes ({CiChecksRequest.DefaultWaitMinutes}) "
                + "or one ended neither green nor red asks (noChecks, checksPending, checksUnsettled), never failing it; answered accept, it "
                + "reads once more, waiting for nothing, and is done with the checks as they are, its passed true only where they are green. "
                + "A check that finds red is "
                + "red: under failOn red - every check's default but ci-read's, never - it fails with its check's code "
                + $"(an anchor finding {AnchorExit.Findings}, a red CI leg {CiExit.LegRed} and no matrix run {CiExit.MatrixDidNotRun}, a red check "
                + $"{CiExit.LegRed}), and under never it is done, its passed false. Every read of the forge is given the step's "
                + $"timeoutMinutes where it says one, else {ReportText.Limit(GhProcess.Limit)}, and never outlasts the step's attempt, which "
                + $"ends a read in flight with it: a ci-read's read past its limit ends the step incomplete ({HarnessExit.Incomplete}), which "
                + "runs it again; a ci-wait reads again at its next poll where a read failed or passed its limit, saying each, and ends with "
                + $"that read's code only where {CiChecksService.FailedReadsInARow} did running or its wait is spent.",
            prose,
            StringComparison.Ordinal);

        // What a leg step runs, answers, lets go and keeps: the names of its records are the layout's own.
        Assert.Contains(
            "build, test, run and check-mutations run their legs as the commands of those names do, in the worktree of the agent "
                + "their in names, else the main checkout: test with its filter, exclude and labels and run with its inputs - a filter, "
                + "an exclusion or a label that fills to nothing is not given - and check-mutations with its arms, every arm the registry "
                + "declares where it names none, and pending. Each answers passed, true where every leg passed; exitCode, the command's; "
                + "verdicts, each leg's own; red, the red legs alone - a leg that failed, with the phase that did not pass, the ledger's "
                + "line and the last lines it printed, and of a sweep each arm that violated, survived or was unattributed, with its arm "
                + "and its line; and runDirectory, null where the run made none. A leg that works in a copy - its own under .legs, or a "
                + "host's - lets the step's hold on its tree go once its copy is made, or once it ends without one, and a leg in the tree "
                + "itself once it ends: the step's record then says copied. A run whose runner, or a runner its run checks name, writes "
                + "the tree holds write on it, and its legs work in the tree itself. A leg run refused part way, cut off, or ended with a "
                + "leg it placed left with no line ends the step with the run's own code; one its legs ended - none could run, another "
                + "run owns its records - is read by their verdicts. The step keeps the ledger in its directory "
                + $"({OrchestratorLayout.StepLedgerFileName}) and, in an agent's worktree, the log of each red leg's phase that did not "
                + "pass, whole, where this machine ran the leg "
                + $"({OrchestratorLayout.StepLogsDirectoryName}/<leg>.<phase>{PhaseLogs.Extension}, the phase named as its log is), "
                + "each attempt's own.",
            prose,
            StringComparison.Ordinal);

        // The refusals' codes are the ones the command answers with, held here so the sentence cannot drift from them.
        Assert.Contains(
            "'dssharness validate-workflow' checks the definition - the shape of every key, every name an expression, a template key "
                + "or a prompt reads, every file the definition names - and refuses "
                + $"({HarnessExit.ConfigInvalid}) naming each problem, without running anything, as a "
                + "config.json that does not hold is refused. Where config.json declares no workflow there is nothing to check, and it "
                + $"is refused ({HarnessExit.Refused}), saying that \"workflows\": [\"{WorkflowTemplates.ShippedWorkflowName}\"] "
                + "declares the one init writes.",
            prose,
            StringComparison.Ordinal);

        // Every key of the file, of every step and of every group, from the tables the reader reads.
        Assert.All(WorkflowKeys.File, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Common, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Group, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.Running, key => Assert.Contains($"  {key.Name}", result.StandardOutput, StringComparison.Ordinal));
        Assert.All(WorkflowStepTypes.All, type => Assert.Contains($"  {type.Type}", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains(
            "What every step that runs takes, beyond the common keys - every typed step but pause, which only asks:",
            prose,
            StringComparison.Ordinal);

        // A step's time limit, from the bounds the validator and the coordinator hold.
        Assert.Contains(
            $"A step that runs may bound each of its attempts with timeoutMinutes, from 1 to {WorkflowStep.MostTimeoutMinutes.ToString("N0", CultureInfo.InvariantCulture)}: "
                + "the engine counts it from each start of the step - an attempt, or its run after a question of its own was answered - and "
                + "never while it waits on a question. Past it the step is stopped as a stop stops one and ends incomplete "
                + $"({HarnessExit.Incomplete}), saying it passed its timeoutMinutes: a reasoning step goes to what handles its failure, any other "
                + $"runs again as an incomplete step does; one whose runner does not end within {PointOfNoReturn.SafePauseDrain.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s "
                + "of that stop fails all the same, goes to what handles its failure and is not retried - a step of others fails the target "
                + "whose turn it was and goes on with the next - and what it claimed stays held until that runner ends. A reasoning step's "
                + "handoff turn at a pause is bounded by handoffSeconds, not by this. A ci-wait's is always more than its waitMinutes (absent, "
                + $"{WorkflowStep.DefaultWaitMinutes}); a group and a pause step take none, running nothing of their own; a type whose step "
                + "has a limit of its own where it says none says so beside its name below.",
            prose,
            StringComparison.Ordinal);

        // The type that has one says it beside its name, from the table.
        Assert.Contains("graphify-codebase (timeoutMinutes 30 by default)", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic names the workflows key and the layout topic the workflows directory, each saying where the
    /// definition lives: never an entry in the one, always a file of its own under the other.
    /// </summary>
    [Fact]
    public async Task TheConfigAndLayoutTopics_NameTheWorkflowsKeyAndItsDirectory()
    {
        var config = HelpText.Of("config");
        var layout = HelpText.Of("layout");


        Assert.Contains(
            "  workflows      the workflows this repository runs, each defined by a yml file of its",
            config.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "  .harness-config/workflows/<name>/<name>.yml",
            layout.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "each missing file written by init where config.json declares the implementation one",
            Prose.Words(layout.StandardOutput),
            StringComparison.Ordinal);

        // workflows, keepEnvironment and keepOnFailure are refused given empty though leaving each out means none,
        // which neither of the other two kinds of list the topic names is: said apart from both, with the spelling the
        // refusal itself asks for.
        Assert.Contains(
            "workflows, which declares no workflow left out, agentPolicy.keepEnvironment, which gives no name back left "
                + "out, and a test invocation's keepOnFailure, which keeps no evidence left out, are refused given empty "
                + "too, so none is spelt one way: leave the key out for none.",
            Prose.Words(config.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>A step key shown as it is written, then what it does: '  outputs: [&lt;path&gt;, ...]  what it writes'.</summary>
    [GeneratedRegex(@"^  (?<key>[A-Za-z]+): \S.*?  +(?<meaning>\S.*)$")]
    private static partial Regex SpeltKeyLine();

    /// <summary>The codebase-graph topic says the context pack's seeds, walk, weights and cap, and find's ties, as the code holds them.</summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysTheContextPacksSeedsWalkAndCap_FromTheCode()
    {
        var prose = Prose.Words(HelpText.Of("codebase-graph").StandardOutput);

        Assert.Contains(Prose.Words($"the {DssHarness.Core.Codebase.CodebaseContextPack.WordSeeds} nodes its words match best"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"The walk goes {DssHarness.Core.Codebase.CodebaseContextPack.NamedHops} hops from a node named or worked with and {DssHarness.Core.Codebase.CodebaseContextPack.OtherHops} from the others"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"({string.Join(", ", DssHarness.Core.Codebase.CodeConfidences.All.Select(confidence => $"{confidence} {DssHarness.Core.Codebase.CodebaseContextPack.Confidence(confidence).ToString(System.Globalization.CultureInfo.InvariantCulture)}"))})"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"more edges of than the view's 99th percentile, and at least {DssHarness.Core.Codebase.CodebaseGraphQueries.CrowdFloor}, which its line says"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("none at all where the cap cannot hold its first and last lines"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A step that starts from its changes is packed first from the declarations its tree changed against its base - an agent's where its worktree was made or last moved to, the main checkout's the run's start - each with where its lines differ"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("then their tests and those of their direct callers, then their callers to two hops, overrides, implementations and dispatches, and derived types; where nothing changed, its first line says so and the task's seeds stand in."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"within its cap ({DssHarness.Core.Codebase.CodebaseContextPack.DefaultTokens} estimated tokens - bytes over {DssHarness.Core.Codebase.EstimatedTokens.BytesPerToken} - by default)"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a tie broken by how near the step's own work each is - its context pack or a working set it was given, a file it writes, a file its agent changed, a path or a name its prompt holds - then by id"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"each node it {string.Join(", ", DssHarness.Core.Codebase.WorkingSetUses.All.SkipLast(1))} or {DssHarness.Core.Codebase.WorkingSetUses.All[^1]}, with its handle, file, lines and hash at the step's end, at most {DssHarness.Core.Codebase.CodebaseWorkingSet.MostNodes},"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A reason step's pack is to be given the working sets of the step it goes on with (resume), of each step it depends on directly, a group by each reason step in it, and of its own earlier visits of the same work item, newest first"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("edited first, then by how often each was touched; a node the view no longer holds kept by its id, marked gone"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("by where its new text stands once there - every place, with replace_all - else by the lines it names"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a deletion writes no lines, and is counted with the writes placed nowhere, as a whole file's write is where the file does not read as the step left it."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A file that differs and could not be compared - over its size cap, generated, or not read, there or at the base - is named on its first line with why, and nothing in it is said unchanged."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("body and edit reach a file only inside the tree, each link resolved, never through a link out of it."), prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The codebase-graph topic, under each of its names, says every implementation this build holds - its version, what it
    /// claims, what it reads and what it does not see - every key of codebaseGraph, the graph's files, the step's output,
    /// its failOn and its time limit, each read from the code; and the orchestrators topic lists the graph's files.
    /// </summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysEveryImplementationKeyAndFile_FromTheCode()
    {
        var topic = HelpText.Of("codebase-graph").StandardOutput;
        var prose = Prose.Words(topic);

        Assert.Equal(topic, HelpText.Of("graph").StandardOutput);
        Assert.Equal(topic, HelpText.Of("graphify-codebase").StandardOutput);

        foreach (var extractor in DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild.All)
        {
            Assert.Contains($"{extractor.Language} (version {extractor.Version.ToString(CultureInfo.InvariantCulture)};", topic, StringComparison.Ordinal);
            Assert.Contains(Prose.Words($"reads {extractor.Help.Reads}."), prose, StringComparison.Ordinal);
            Assert.All(extractor.Help.DoesNotSee, unseen => Assert.Contains(Prose.Words($"does not see {unseen}."), prose, StringComparison.Ordinal));
        }

        Assert.Contains("listed (version 1; every file no other claims)", topic, StringComparison.Ordinal);
        Assert.Contains("markdown (version 1; .md)", topic, StringComparison.Ordinal);
        Assert.All(WorkflowKeys.CodebaseGraph, key => Assert.Contains(key.Name, topic, StringComparison.Ordinal));
        Assert.Contains($"failOn: {WorkflowStepTypes.GraphIncomplete}, which fails it ({HarnessExit.CommandFailed.ToString(CultureInfo.InvariantCulture)})", prose, StringComparison.Ordinal);
        Assert.Contains("30 minutes where it says no timeoutMinutes", prose, StringComparison.Ordinal);
        Assert.Contains($"{HarnessLayout.OrchestratorsDirectoryName}/<run>/{OrchestratorLayout.GraphDirectoryName}/", prose, StringComparison.Ordinal);
        Assert.Contains(OrchestratorLayout.GraphManifestFileName, prose, StringComparison.Ordinal);
        Assert.Contains(OrchestratorLayout.GraphSyncFileName, prose, StringComparison.Ordinal);
        var answers = System.Text.Json.Nodes.JsonNode.Parse(WorkflowStepTypes.Find("graphify-codebase")!.Output!)!["properties"]!.AsObject().Select(member => member.Key).ToList();
        Assert.Contains("seconds", answers);
        Assert.Contains($"A graphify-codebase step answers {string.Join(", ", answers)}:", prose, StringComparison.Ordinal);
        Assert.Contains($"one of every {DssHarness.Core.Codebase.CodebaseGraphService.ReadShare.ToString(CultureInfo.InvariantCulture)} of the machine's processors", prose, StringComparison.Ordinal);
        Assert.Contains($"first {DssHarness.Core.FileSystem.BinaryContent.Window.ToString("N0", CultureInfo.InvariantCulture)} bytes - git's own window", prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"{OrchestratorLayout.GraphViewsDirectoryName}/<agent>.json, an agent's view"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("with in, its view is that agent's - the base for one that only reads - and its output's view names the one it synced"), prose, StringComparison.Ordinal);
        Assert.All(DssHarness.Core.Codebase.CodeEdgeKinds.Described, described => Assert.Contains(Prose.Words($"{described.Kind}, {described.Meaning}"), prose, StringComparison.Ordinal));
        Assert.Contains(Prose.Words("a file it could not read is tried again at every sync"), prose, StringComparison.Ordinal);
        Assert.Contains(HelpTopics.Described, described => described.Name == "codebase-graph");

        var layout = HelpText.Of("orchestrators").StandardOutput;

        foreach (var file in new[] { OrchestratorLayout.GraphManifestFileName, OrchestratorLayout.GraphSymbolsFileName, OrchestratorLayout.GraphExtractsDirectoryName, OrchestratorLayout.GraphSyncFileName })
        {
            Assert.Contains($"{OrchestratorLayout.GraphDirectoryName}/{file}", layout, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The web-search topic, under each of its names, says every key of agentWebSearch and of every source, every kind this
    /// build holds with its version, help and keys, and every built-in source with its tags, pace, description and terms -
    /// each read from the code - and where a run's searches are kept; the config topic names the section.
    /// </summary>
    [Fact]
    public void TheWebSearchTopic_SaysEveryKindKeyAndBuiltInSource_FromTheCode()
    {
        var topic = HelpText.Of("web-search").StandardOutput;
        var prose = Prose.Words(topic);

        Assert.Equal(topic, HelpText.Of("search").StandardOutput);
        Assert.Equal(topic, HelpText.Of("web_search").StandardOutput);
        Assert.Equal(topic, HelpText.Of("agentwebsearch").StandardOutput);
        Assert.Contains(HelpTopics.Described, described => described.Name == "web-search");
        Assert.Contains("The claude-code client gives them a step through the engine's tool server ('help agent-clients'), and the API clients are to offer them.", prose, StringComparison.Ordinal);

        // The files the web's tools leave are written by each client whose step has them ('help orchestrators').
        var files = Prose.Words(HelpText.Of("orchestrators").StandardOutput);
        Assert.Contains("each search source's part ('help web-search'); written by every client whose step has the web", files, StringComparison.Ordinal);
        Assert.Contains("which the same query again is answered from; written by every client whose step searches", files, StringComparison.Ordinal);

        foreach (var key in DssHarness.Core.Web.Search.WebSearchSources.Keys.Concat(DssHarness.Core.Web.Search.WebSearchSource.OwnKeys))
        {
            Assert.Contains($"  {key.Name}", topic, StringComparison.Ordinal);
        }

        foreach (var kind in DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild.All)
        {
            Assert.Contains(Prose.Words($"Kind {kind.Kind} (version {kind.Version.ToString(CultureInfo.InvariantCulture)}): {kind.Help}"), prose, StringComparison.Ordinal);
            Assert.All(kind.Keys, key => Assert.Contains($"  {key.Name}", topic, StringComparison.Ordinal));
        }

        var builtIn = DssHarness.Core.Web.Search.WebSearchSources.Of(new HarnessConfig(), DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild);

        foreach (var source in builtIn.All)
        {
            Assert.Contains(Prose.Words($"{source.Name} ({source.Kind}; tags {string.Join(", ", source.Tags)}; perMinute {source.PerMinute.ToString(CultureInfo.InvariantCulture)}"), prose, StringComparison.Ordinal);
            Assert.Contains(Prose.Words($"{source.Description}. Its owner's terms: {source.Terms}."), prose, StringComparison.Ordinal);
        }

        Assert.Contains(Prose.Words($"A step that says no search asks the defaults: {string.Join(", ", builtIn.Defaults)}."), prose, StringComparison.Ordinal);
        Assert.Contains($"{OrchestratorLayout.WebDirectoryName}/{OrchestratorLayout.WebSearchesFileName}", prose, StringComparison.Ordinal);
        Assert.Contains($"{DssHarness.Core.Web.Search.WebSearchPace.FilePrefix}-<machine>.json", prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"the snippet at most {DssHarness.Core.Web.Search.WebSearchResult.MostSnippet.ToString(CultureInfo.InvariantCulture)} characters"), prose, StringComparison.Ordinal);

        // A source's key goes where its configuration writes alone, never to a host a document it read names.
        Assert.Contains(Prose.Words("a variable of the .env slot, sent to the scheme, host and port the source's own address writes alone, or the machine's gh login"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a host a document the source read names, an OpenSearch description's template or what discover found, is never sent one"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A source whose address takes its host from what discover finds is refused a key of the .env slot where the configuration is read."), prose, StringComparison.Ordinal);

        // The README names every built-in source, as the template holds them.
        var named = DssHarness.Core.Web.Search.WebSearchSources.BuiltIn.Sources!.Keys.ToList();

        Assert.Contains(
            Prose.Words($"The built-in sources ({string.Join(", ", named.SkipLast(1).Select(name => $"`{name}`"))}, `{named[^1]}`)"),
            Prose.Document("README.md"),
            StringComparison.Ordinal);

        // The orchestrators topic lists where a run's and a step's web records are.
        var layout = HelpText.Of("orchestrators").StandardOutput;

        foreach (var file in new[] { $"<step>/{DssHarness.Core.Agents.AgentCallRecords.WebFileName}", $"<step>/{DssHarness.Core.Agents.AgentCallRecords.DenialsFileName}", $"<step>/{DssHarness.Core.Agents.AgentCallRecords.ToolResultsDirectoryName}/<id>.txt", $"{OrchestratorLayout.WebDirectoryName}/{OrchestratorLayout.WebSearchesFileName}", $"../{OrchestratorLayout.WebIndexesDirectoryName}/<source>/" })
        {
            Assert.Contains($"  {file}", layout, StringComparison.Ordinal);
        }

        var config = Prose.Words(HelpText.Of("config").StandardOutput);

        Assert.Contains("agentWebSearch the harness's own web search", config, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"maxPerStep ({DssHarness.Core.Web.Search.WebSearchSources.DefaultMaxPerStep}) and resultsPerSource ({DssHarness.Core.Web.Search.WebSearchSources.DefaultResultsPerSource}) ('help web-search')"), config, StringComparison.Ordinal);
    }
}
