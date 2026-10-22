using System.Text.Json;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The launch's settings file for Claude Code (<c>claude-settings.json</c> in the step's
/// directory), which outranks the user's own: what the engine decides and nothing else, rendered
/// from the same policy the engine answers with - never re-decided.
/// </summary>
/// <remarks>
/// <para>
/// An ask rule for every tool the launch has, so no call is allowed by a rule or a mode before the
/// engine has seen it; the restriction rules as Claude Code's own deny rules, a second layer behind
/// the engine's answers, never the one relied on; <c>"fallbackModel": []</c> and
/// <c>"switchModelsOnFlag": false</c>, for the engine's fallback is the only one; Claude Code's own
/// output style, because its prompt is kept; an <c>env</c> holding every name the engine controls,
/// so the user's own <c>env</c> cannot turn one back; and the user's hooks off in every launch.
/// No key is ever in it.
/// </para>
/// <para>
/// Every path in a deny rule is written as an absolute path, once for each root the launch has, so
/// no rule is read against a working directory it was not meant for. Claude Code's deny rules know
/// no exception, so a rule whose path would cover a root the launch writes - the main checkout's
/// <c>.orchestrators/**</c> holds the agent's own work directory - is not written as one: the engine
/// holds it alone, as it holds every rule first.
/// </para>
/// </remarks>
public static class ClaudeSettings
{
    /// <summary>
    /// The names the engine controls in every launch, with their values, in the order they are written: compaction off,
    /// for the engine compacts; the auto-updater off, for only this tool moves the installation; the key a launch signs
    /// in with kept from the commands it runs; and the retry watchdog off, which would retry a limit without end. Set
    /// in the settings' <c>env</c>, which outranks the user's own, and in the child's environment, which a host session's
    /// would otherwise outrank.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Controlled { get; } =
    [
        new("DISABLE_COMPACT", "1"),
        .. ClaudeCode.ClaudeCodeInstall.EveryStart.Select(start => new KeyValuePair<string, string>(start.Key, start.Value!)),
        new("CLAUDE_CODE_SUBPROCESS_ENV_SCRUB", "1"),
        new("CLAUDE_CODE_RETRY_WATCHDOG", "0"),
    ];

    /// <summary>
    /// Renders the settings file of one launch.
    /// </summary>
    /// <param name="tools">Every tool the launch has, by Claude Code's own names, as <c>--tools</c> names them.</param>
    /// <param name="ground">The launch's ground: its restrictions and the roots it reads and writes.</param>
    /// <param name="comparison">How a name in a pattern and a name in a path compare: the case rule of the tree the path is in.</param>
    public static string Render(IReadOnlyList<string> tools, AgentPolicyGround ground, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(ground);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteBoolean("disableAllHooks", true);
            writer.WriteStartArray("fallbackModel");
            writer.WriteEndArray();
            writer.WriteBoolean("switchModelsOnFlag", false);
            writer.WriteString("outputStyle", "default");

            writer.WriteStartObject("env");

            foreach (var (name, value) in Controlled)
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
    /// The deny rules of one ground: each restriction as Claude Code spells its own - a
    /// <c>Run</c> rule a <c>Bash</c> prefix rule, a path rule absolute once for each root that is
    /// not a root the launch writes inside, a <c>Web</c> rule a <c>WebFetch</c> domain rule and,
    /// where it refuses the web whole, <c>WebSearch</c> beside it.
    /// </summary>
    private static IEnumerable<string> DenyRules(AgentPolicyGround ground, StringComparison comparison)
    {
        var roots = ground.ReadRoots.Concat(ground.WritableRoots)
            .Select(root => root.Replace('\\', '/'))
            .Distinct(StringComparer.FromComparison(comparison))
            .ToList();

        var writable = ground.WritableRoots
            .Select(root => root.Replace('\\', '/'))
            .Distinct(StringComparer.FromComparison(comparison))
            .ToList();

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

    /// <summary>One path rule as Claude Code's own, absolute once for each root that covers no root the launch writes.</summary>
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
                var relative = Relative(root, writeRoot);

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

            // Claude Code's own absolute rules begin with two slashes whatever the system spells
            // beneath them: '//C:/x/y' on Windows, '//home/x/y' on Linux - the root's own leading
            // slash is one of the two.
            var absolute = root.StartsWith('/') ? "/" + root.TrimEnd('/') : "//" + root.TrimEnd('/');

            yield return $"{rule.Verb}({absolute}/{rule.Pattern})";
        }
    }

    /// <summary>The path of <paramref name="path"/> relative to <paramref name="root"/>, with forward separators, or null where it is not under it or is the root itself.</summary>
    private static string? Relative(string root, string path)
    {
        var relative = System.IO.Path.GetRelativePath(root, path);

        return relative == ".." || relative == "." || relative.StartsWith("../", StringComparison.Ordinal) || relative.StartsWith("..\\", StringComparison.Ordinal)
            || System.IO.Path.IsPathRooted(relative) ? null
            : relative.Replace('\\', '/');
    }
}
