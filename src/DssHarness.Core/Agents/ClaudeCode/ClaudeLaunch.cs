using System.Globalization;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// One launch of Claude Code, as the engine starts it: a new session streamed both ways, by the one argument list, in
/// the agent's own working directory, with the environment and the settings the engine decides - and nothing else. The
/// one place that says what a launch carries, so every launch carries all of it.
/// </summary>
/// <remarks>
/// <para>
/// Started as Claude Code's own SDK starts it: the two stream-json formats, no <c>-p</c>, the prompt never an argument but
/// the first user message on its input. Never <c>--resume</c>, <c>--continue</c>, an earlier launch's session id or
/// <c>--fallback-model</c>: the session id is one the engine made for this launch, and it keeps no session file.
/// </para>
/// <para>
/// <c>--permission-prompt-tool stdio</c> is what sends a call's ask to the host - the probe found it alone does, on every
/// version measured - so every call of every tool the launch has that its own deny rules do not refuse first is asked of
/// the engine, which decides it.
/// <c>--permission-prompts host</c> is the documented name for where the prompts go, and is carried beside it.
/// </para>
/// </remarks>
public static class ClaudeLaunch
{
    /// <summary>The launch's own settings, as sent, in the step's directory.</summary>
    public const string SettingsFileName = "claude-settings.json";

    /// <summary>The system prompt as sent, appended to Claude Code's own, in the step's directory.</summary>
    public const string SystemFileName = "system.md";

    /// <summary>The control messages both ways, masked, for whoever debugs the client.</summary>
    public const string ControlFileName = "claude-control.jsonl";

    /// <summary>What the launch wrote on its standard error, masked.</summary>
    public const string ErrorFileName = "claude-stderr.log";

    /// <summary>The MCP servers <c>--mcp-config</c> names, as sent, in the step's directory: the engine's tool server alone, where the launch has its tools.</summary>
    public const string McpFileName = "claude-mcp.json";

    /// <summary>
    /// What <see cref="McpFileName"/> holds: the one server, the engine's tool server, of type <c>sdk</c> - served in the
    /// engine's own process, every message of it sent to the host as an <c>mcp_message</c> control request.
    /// </summary>
    public static string McpConfiguration { get; } = new System.Text.Json.Nodes.JsonObject
    {
        ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
        {
            [ClaudeCodeTools.Server] = new System.Text.Json.Nodes.JsonObject { ["type"] = "sdk", ["name"] = ClaudeCodeTools.Server },
        },
    }.ToJsonString();

    /// <summary>The names a Claude Code session sets, whole, in the environment of everything it starts.</summary>
    public static IReadOnlyList<string> SessionNames { get; } = ["CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT"];

    /// <summary>
    /// What every other name of a Claude Code session's own begins with: Claude Code's, its Agent SDK's, its previews', and
    /// MCP's, by which a session tunes its servers' connections - one that would let a launch's init come before the
    /// engine's tool server is connected among them - where a launch's MCP is the engine's server alone.
    /// </summary>
    public static IReadOnlyList<string> SessionPrefixes { get; } = ["CLAUDE_CODE_", "CLAUDE_AGENT_SDK_", "CLAUDE_PREVIEW_", "MCP_"];

    /// <summary>
    /// The names of Claude Code's own a child keeps from the tool's environment: each says where a program Claude Code
    /// starts is - Git's bash on Windows, which a machine without Git on its PATH names - and nothing of who the launch
    /// is, what it may do or what it runs as. Every other is a session's, and is not inherited.
    /// </summary>
    public static IReadOnlyList<string> KeptNames { get; } = ["CLAUDE_CODE_GIT_BASH_PATH"];

    /// <summary>
    /// Whether <paramref name="name"/> is a Claude Code session's own, which a launch inherits only where
    /// <c>agentPolicy.keepEnvironment</c> gives it back by name: one of
    /// <see cref="SessionNames"/>, or beginning one of <see cref="SessionPrefixes"/> but <see cref="KeptNames"/>. Told in any
    /// case, as the runner takes a name away in every spelling: on Windows a name in another case is the same variable to
    /// the program that reads it.
    /// </summary>
    /// <param name="name">The variable's name.</param>
    public static bool IsSessionName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return SessionNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || (SessionPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !KeptNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The variable a key a launch signs in with is given under.</summary>
    public const string KeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>The variable the provider's address is given under, where a launch signs in with a key or a token.</summary>
    public const string AddressVariable = "ANTHROPIC_BASE_URL";

    /// <summary>
    /// The names that say who a launch is - a key, a token, an address - which a child never inherits: it is given only
    /// what its way of signing in asks for, under the name Claude Code reads, so an ambient key never bills in place of
    /// the one the configuration chose.
    /// </summary>
    public static IReadOnlyList<string> IdentityNames { get; } =
    [
        KeyVariable,
        AgentAuth.TokenVariable(AgentAuth.AuthToken)!,
        AgentAuth.TokenVariable(AgentAuth.OAuthToken)!,
        AddressVariable,
    ];

    /// <summary>
    /// What <c>system/init</c>'s <c>apiKeySource</c> says of a launch that signed in this way: under the machine's login
    /// <c>none</c> (a subscription's sign-in) or <c>/login managed key</c> (a Console login's key); with a key
    /// <c>ANTHROPIC_API_KEY</c>; with a token <c>none</c>, since Claude Code names no key for a bearer token. Any other is
    /// a credential the launch was not given, which the client's check refuses.
    /// </summary>
    /// <param name="auth">The way the launch signs in.</param>
    public static IReadOnlyList<string> ApiKeySources(string auth)
        => auth switch
        {
            AgentAuth.ApiKey => [KeyVariable],
            AgentAuth.OAuthToken or AgentAuth.AuthToken => ["none"],
            _ => ["none", "/login managed key"],
        };

    /// <summary>The argument list of one launch, whole.</summary>
    /// <param name="run">The run.</param>
    /// <param name="model">The model it runs on, by its id.</param>
    /// <param name="sessionId">The session id the engine made for this launch.</param>
    /// <param name="tools">The tools it is given.</param>
    /// <param name="otherRoots">Every root of the agent's other than its working directory, each an <c>--add-dir</c>.</param>
    /// <param name="settingsFile">The launch's settings file.</param>
    /// <param name="systemFile">The system prompt's file.</param>
    /// <param name="mcpFile">The file naming the engine's tool server, where the launch takes its tools; null where it takes none.</param>
    public static IReadOnlyList<string> Arguments(
        AgentRun run,
        string model,
        string sessionId,
        IReadOnlyList<string> tools,
        IReadOnlyList<string> otherRoots,
        string settingsFile,
        string systemFile,
        string? mcpFile = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var arguments = new List<string>
        {
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--replay-user-messages",
            "--session-id", sessionId,
            "--no-session-persistence",
            "--model", model,
        };

        if (run.Effort is { Length: > 0 } effort)
        {
            arguments.AddRange(["--effort", effort]);
        }

        if (run.Limits.MaxTurns is { } turns)
        {
            arguments.AddRange(["--max-turns", turns.ToString(CultureInfo.InvariantCulture)]);
        }

        if (run.Limits.MaxBudgetUsd is { } budget)
        {
            arguments.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(
        [
            "--permission-mode", "default",
            "--permission-prompt-tool", "stdio",
            "--permission-prompts", "host",
            "--tools", string.Join(',', tools),
            "--setting-sources", "user",
            "--strict-mcp-config",
        ]);

        if (mcpFile is not null)
        {
            arguments.AddRange(["--mcp-config", mcpFile]);
        }

        arguments.AddRange(
        [
            "--settings", settingsFile,
            "--append-system-prompt-file", systemFile,
        ]);

        if (run.Output is { } output)
        {
            arguments.AddRange(["--json-schema", AgentEnvelope.Schema(output)]);
        }

        foreach (var root in otherRoots)
        {
            arguments.AddRange(["--add-dir", root]);
        }

        return arguments;
    }

    /// <summary>
    /// The environment one launch is given, as overrides of the tool's own (null removes a name): what every child of an
    /// agent loses (<see cref="AgentChildEnvironment"/> - every name the configuration appoints a value for, and every
    /// secret-shaped name not given back), nothing of a Claude Code session the engine was started in but what is given
    /// back, no name that says
    /// who the launch is but what its way of signing in gives, the names the engine controls, and the Claude Code directory
    /// whose login and settings it reads.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="key">The value it signs in with, where its way of signing in takes one; null for the machine's login.</param>
    /// <param name="own">The names the tool's own environment holds, which the child would otherwise inherit.</param>
    public static IReadOnlyDictionary<string, string?> Environment(AgentRun run, AgentKey? key, IEnumerable<string> own)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(own);

        // What every child of an agent loses, its own names set after it: the key its way of signing in gives among them.
        var known = own.ToList();
        var environment = AgentChildEnvironment.Of(known, run.Withheld, run.Commands.KeepEnvironment);

        // A host session's own names would outrank the launch's settings, the probe found: none of them is inherited but a
        // secret-shaped one agentPolicy.keepEnvironment gives back, as it gives it back to every child of an agent.
        foreach (var name in known.Where(name => IsSessionName(name) && !run.Commands.KeepEnvironment.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            environment[name] = null;
        }

        foreach (var name in IdentityNames)
        {
            environment[name] = null;
        }

        foreach (var (name, value) in ClaudeSettings.Launched(run.Commands))
        {
            environment[name] = value;
        }

        if (run.Credential is { } credential && key is not null)
        {
            environment[AgentAuth.TokenVariable(credential.Auth) ?? KeyVariable] = key.Value;

            if (credential.Api is { Length: > 0 } api)
            {
                environment[AddressVariable] = api;
            }
        }

        if (run.Credential?.ConfigDirectory is { Length: > 0 } directory)
        {
            environment[ClaudeTranscripts.ConfigDirectoryVariable] = directory;
        }

        return environment;
    }
}
