using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Json;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The launch's settings file for Claude Code (<c>claude-settings.json</c> in the step's
/// directory), which outranks the user's own: what the engine decides and nothing else, rendered
/// from the same policy the engine answers with - never re-decided.
/// </summary>
/// <remarks>
/// <para>
/// An ask rule for every tool the launch has, so no call is allowed by a rule or a mode before the
/// engine has seen it; the restriction rules as Claude Code's own deny rules, and a read of every
/// credential slot, Claude Code directory and place the user keeps credentials in of the run's beside them - a second
/// layer behind the engine's answers, never the one relied on; <c>"fallbackModel": []</c> and
/// <c>"switchModelsOnFlag": false</c>, for the engine's fallback is the only one; Claude Code's own
/// output style, because its prompt is kept; an <c>env</c> holding every name the engine controls,
/// so the user's own <c>env</c> cannot turn one back; the user's hooks off in every launch; and, where the launch runs
/// whole inside this tool's isolation, Claude Code's own sandbox off, for no sandbox nests in this one. No key is ever in it.
/// </para>
/// <para>
/// Every path in a deny rule is written as an absolute path, once for each root the launch has, so
/// no rule is read against a working directory it was not meant for, and in the form Claude Code
/// matches - a Windows path as its POSIX spelling, <c>//c/x</c> for <c>C:\x</c> - and a rule that
/// refuses a write is an <c>Edit</c> rule, for Claude Code holds every file tool's path to its
/// <c>Read</c> and <c>Edit</c> rules alone. Claude Code's deny rules know
/// no exception, so a rule is not written for a root where its pattern there would cover a root the
/// launch writes - the main checkout's <c>.orchestrators/**</c> holds the agent's own work
/// directory: the engine holds it alone there, as it holds every rule first.
/// </para>
/// </remarks>
public static class ClaudeSettings
{
    /// <summary>The variable Claude Code reads for the most tokens it takes into the conversation of one answer of an MCP tool.</summary>
    public const string McpOutputVariable = "MAX_MCP_OUTPUT_TOKENS";

    /// <summary>
    /// The tokens one answer of the engine's tool server may take, as <see cref="McpOutputVariable"/> says: three times
    /// the characters of one page of a tool's answer to a model (<see cref="PageRoom.ModelAnswer"/>). Every tool the
    /// server serves answers a page at most, and the rest by asking the next page; a page reaches Claude Code as the
    /// text of a JSON-RPC answer - each quote, backslash and control character escaped, a character outside ASCII as six
    /// at most, inside the answer's envelope - and a token is never less than one character, so three times the page
    /// holds the whole of one answer however it is spelt, and Claude Code never cuts an answer the engine paged. Left to
    /// itself, Claude Code cuts an answer at a number of its own, which a page of text that tokenizes densely passes.
    /// </summary>
    public static int McpOutputTokens { get; } = 3 * PageRoom.ModelAnswer;

    /// <summary>
    /// The names the engine sets in every launch, with their values, in the order they are written: compaction off,
    /// for the engine compacts; the auto-updater off, for only this tool moves the installation; the key a launch signs
    /// in with kept from the commands Claude Code runs itself; the retry watchdog off, which would retry a limit without
    /// end; and the tokens one answer of the engine's tool server may take, a page whole (<see cref="McpOutputTokens"/>).
    /// Set in the settings' <c>env</c>, which outranks the user's own, and in the child's environment, which a host
    /// session's would otherwise outrank.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Launched { get; } =
    [
        new("DISABLE_COMPACT", "1"),
        .. ClaudeCode.ClaudeCodeInstall.EveryStart.Select(start => new KeyValuePair<string, string>(start.Key, start.Value!)),
        new("CLAUDE_CODE_SUBPROCESS_ENV_SCRUB", "1"),
        new("CLAUDE_CODE_RETRY_WATCHDOG", "0"),
        new(McpOutputVariable, McpOutputTokens.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>
    /// The names Claude Code reads for how long what a request caches is kept: its main conversation's, and every other
    /// request's. Each outranks a setting of the same and a login's own default - an hour for a Claude subscription's
    /// main conversation, whose every cache write is then priced at the hour's rate however soon the next request reads it.
    /// </summary>
    public static IReadOnlyList<string> CacheTtlVariables { get; } = ["CLAUDE_CODE_PROMPT_CACHE_TTL", "CLAUDE_CODE_SUBAGENT_PROMPT_CACHE_TTL"];

    /// <summary>
    /// How long a launch's requests are cached: <c>agentClients.claude-code.cacheTtl</c> as the run's entries hold it, else
    /// <see cref="ClaudeApi.ClaudeApiRequest.FiveMinutes"/> - the same word and default the Claude API client's own
    /// <c>cacheTtl</c> takes. A step's requests come seconds apart, so five minutes keeps every read of the cache and an
    /// hour only raises the price of each write.
    /// </summary>
    /// <param name="entry">The claude-code client's entry, where the configuration declares one.</param>
    public static string CacheTtlOf(Configuration.AgentClientConfig? entry) => entry?.CacheTtl ?? ClaudeApi.ClaudeApiRequest.FiveMinutes;

    /// <summary>
    /// Every name a launch sets, with its value, in the order written: <see cref="Launched"/>, then each of
    /// <see cref="CacheTtlVariables"/> at <paramref name="cacheTtl"/> - in the settings' <c>env</c> and in the child's
    /// environment alike.
    /// </summary>
    /// <param name="cacheTtl">How long the launch's requests are cached (<see cref="CacheTtlOf"/>).</param>
    public static IReadOnlyList<KeyValuePair<string, string>> LaunchedWith(string cacheTtl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheTtl);

        return [.. Launched, .. CacheTtlVariables.Select(name => new KeyValuePair<string, string>(name, cacheTtl))];
    }

    /// <summary>
    /// Renders the settings file of one launch.
    /// </summary>
    /// <param name="tools">Every tool the launch has, by Claude Code's own names: the engine's tool server's, and a side-task's own web tools.</param>
    /// <param name="ground">The launch's ground: its restrictions and the roots it reads and writes.</param>
    /// <param name="comparison">How a name in a pattern and a name in a path compare: the case rule of the tree the path is in.</param>
    /// <param name="isolated">
    /// Whether the launch runs whole inside the operating system's isolation of this tool's: Claude Code's own sandbox is
    /// then turned off (<c>"sandbox": {"enabled": false}</c>), over any the user's settings ask for - no mechanism of this
    /// tool's starts another inside it (macOS no sandbox inside a sandbox, bubblewrap no namespace inside its own), so a
    /// sandbox of Claude Code's would fail what it starts; on Windows Claude Code has none of its own.
    /// </param>
    /// <param name="cacheTtl">How long the launch's requests are cached (<see cref="CacheTtlOf"/>); five minutes where none is given.</param>
    public static string Render(IReadOnlyList<string> tools, AgentPolicyGround ground, StringComparison comparison, bool isolated = false, string? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(ground);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, JsonDepth.Writer with { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteBoolean("disableAllHooks", true);
            writer.WriteStartArray("fallbackModel");
            writer.WriteEndArray();
            writer.WriteBoolean("switchModelsOnFlag", false);
            writer.WriteString("outputStyle", "default");

            if (isolated)
            {
                writer.WriteStartObject("sandbox");
                writer.WriteBoolean("enabled", false);
                writer.WriteEndObject();
            }

            writer.WriteStartObject("env");

            foreach (var (name, value) in LaunchedWith(cacheTtl ?? ClaudeApi.ClaudeApiRequest.FiveMinutes))
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();

            writer.WriteStartObject("permissions");
            writer.WriteStartArray("ask");

            foreach (var tool in tools)
            {
                writer.WriteStringValue(tool);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("deny");

            foreach (var deny in DenyRules(ground, comparison))
            {
                writer.WriteStringValue(deny);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The deny rules of one ground: a read of each credential slot and Claude Code directory, and of
    /// each place the user keeps credentials in - all a directory holds, a file itself - and
    /// each restriction as Claude Code spells its own - a <c>Run</c> rule a <c>Bash</c> prefix rule,
    /// a path rule absolute once for each root of the launch, but where its pattern there would
    /// cover a root the launch writes, a <c>Write</c> rule as an <c>Edit</c> rule, a <c>Web</c> rule a <c>WebFetch</c> domain rule and, where it
    /// refuses the web whole, <c>WebSearch</c> beside it.
    /// </summary>
    private static IEnumerable<string> DenyRules(AgentPolicyGround ground, StringComparison comparison)
    {
        var roots = ground.Roots(comparison)
            .Select(root => root.Replace('\\', '/'))
            .Distinct(StringComparer.FromComparison(comparison))
            .ToList();

        var writable = ground.WritableRoots
            .Select(root => root.Replace('\\', '/'))
            .Distinct(StringComparer.FromComparison(comparison))
            .ToList();

        // The places no file tool of an agent reads, whole: a search over a directory above one is
        // left without what it holds.
        foreach (var place in ground.SlotDirectories.Concat(ground.ClaudeCodeDirectories).Select(place => place.Replace('\\', '/')).Distinct(StringComparer.FromComparison(comparison)))
        {
            yield return $"Read({Absolute(place)}/**)";
        }

        foreach (var place in ground.CredentialPlaces.DistinctBy(place => place.Path, StringComparer.FromComparison(comparison)))
        {
            yield return place.Directory ? $"Read({Absolute(place.Path.Replace('\\', '/'))}/**)" : $"Read({Absolute(place.Path.Replace('\\', '/'))})";
        }

        foreach (var declared in ground.Restrictions)
        {
            var rule = AgentRule.Parse(declared.Text);

            switch (rule.Verb)
            {
                case AgentRuleVerb.Run:
                    yield return rule.Pattern.EndsWith(" *", StringComparison.Ordinal)
                        ? $"Bash({rule.Pattern[..^2]}:*)"
                        : $"Bash({rule.Pattern})";

                    break;

                case AgentRuleVerb.Write:
                case AgentRuleVerb.Read:
                    foreach (var deny in PathDenyRules(rule, roots, writable, comparison))
                    {
                        yield return deny;
                    }

                    break;

                case AgentRuleVerb.Web:
                    yield return rule.Pattern == "*" ? "WebFetch" : $"WebFetch(domain:{rule.Pattern})";

                    if (rule.Pattern == "*")
                    {
                        yield return "WebSearch";
                    }

                    break;
            }
        }
    }

    /// <summary>One path rule as Claude Code's own, absolute once for each root, but where its pattern there would cover a root the launch writes.</summary>
    private static IEnumerable<string> PathDenyRules(AgentRule rule, IReadOnlyList<string> roots, IReadOnlyList<string> writable, StringComparison comparison)
    {
        foreach (var root in roots)
        {
            // A rule whose path would cover a root the launch writes is not written for that root:
            // Claude Code's deny rules know no exception, and the engine - which does - holds the
            // rule alone.
            var coversWritable = false;

            foreach (var writeRoot in writable)
            {
                var relative = AgentPolicy.Beneath(root, writeRoot, comparison);

                if (relative is null)
                {
                    // A root it writes is the root itself: only a pattern that stands for
                    // everything beneath - '**' and '**/**' - covers it whole.
                    if (root.Equals(writeRoot, comparison) && rule.Pattern.Replace('\\', '/').Trim('/').Split('/').All(segment => segment == "**"))
                    {
                        coversWritable = true;
                    }
                }
                else if (rule.Matches(relative, comparison))
                {
                    coversWritable = true;
                }

                if (coversWritable)
                {
                    break;
                }
            }

            if (coversWritable)
            {
                continue;
            }

            // Claude Code checks every file tool's path against its Read and Edit rules alone: a write is
            // refused by an Edit rule, which it holds its Write and NotebookEdit tools to as well.
            yield return $"{(rule.Verb is AgentRuleVerb.Write ? "Edit" : "Read")}({Absolute(root)}/{rule.Pattern})";
        }
    }

    /// <summary>
    /// A directory as Claude Code's own absolute rules begin: two slashes before the path in the POSIX
    /// form Claude Code matches every path in - '//home/x/y' on Linux, the root's own leading slash one of
    /// the two, and on Windows the drive a lowercase segment of its own, its colon gone, as Claude Code
    /// spells a Windows path before it matches one: 'C:\x\y' is '//c/x/y'.
    /// </summary>
    private static string Absolute(string directory)
    {
        var path = directory.Replace('\\', '/').TrimEnd('/');

        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] == '/')
            ? $"//{char.ToLowerInvariant(path[0])}{path[2..]}"
            : path.StartsWith('/') ? "/" + path : "//" + path;
    }
}
