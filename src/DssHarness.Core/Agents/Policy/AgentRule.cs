using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents.Policy;

/// <summary>The kind of action a rule refuses.</summary>
public enum AgentRuleVerb
{
    /// <summary>A command, refused by its program and arguments: <c>Run(git push *)</c>.</summary>
    Run,

    /// <summary>A write, refused by the path it writes: <c>Write(.github/**)</c>.</summary>
    Write,

    /// <summary>A read, refused by the path it reads: <c>Read(secrets/**)</c>.</summary>
    Read,
}

/// <summary>
/// One restriction rule, read once and matched against many actions: a rule is one string, its verb
/// and its pattern in parentheses - <c>Run(git push *)</c>, <c>Write(.github/**)</c>,
/// <c>Read(secrets/**)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Run</c> pattern is a command prefix: words separated by spaces, compared with the command's
/// own words in turn, where a trailing <c>*</c> stands for any arguments - the bare command too, for
/// a command run with none is still that command. A <c>*</c> anywhere else is refused: a rule that
/// matches a command's middle would have to be read against arguments the command has not reached.
/// </para>
/// <para>
/// A <c>Write</c> and <c>Read</c> pattern is a path pattern as <see cref="GlobPattern"/> reads it -
/// the grammar of a rule the engine hands to a client as one of its own deny rules, which is
/// gitignore's - matched against the path as it stands relative to each of the agent's roots.
/// </para>
/// <para>
/// Deliberately no <c>Web</c> verb yet: the web rules arrive with what decides them, and a rule that
/// loads is a rule that is read.
/// </para>
/// </remarks>
public sealed class AgentRule
{
    private readonly GlobPattern? _pattern;

    private AgentRule(AgentRuleVerb verb, string pattern, GlobPattern? pathPattern)
    {
        Verb = verb;
        Pattern = pattern;
        _pattern = pathPattern;
    }

    /// <summary>The kind of action the rule refuses.</summary>
    public AgentRuleVerb Verb { get; }

    /// <summary>The pattern inside the parentheses, as it was written, trimmed.</summary>
    public string Pattern { get; }

    /// <summary>The rule as it is written, the one spelling a denial says.</summary>
    public string Text => $"{Verb}({Pattern})";

    /// <summary>
    /// Why <paramref name="rule"/> is no rule this tool reads, or <see langword="null"/> where it is:
    /// the sentence a configuration or a definition is refused with, whole.
    /// </summary>
    public static string? Problem(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return "a rule is one string, its verb and its pattern in parentheses: Run(git push *), Write(.github/**), Read(secrets/**)";
        }

        var text = rule.Trim();
        var open = text.IndexOf('(');

        if (open <= 0 || !text.EndsWith(')'))
        {
            return $"'{text}' is no rule this tool reads: a rule is one string, its verb and its pattern in parentheses - Run(git push *), Write(.github/**), Read(secrets/**)";
        }

        var verb = text[..open].Trim();
        var pattern = text[(open + 1)..^1].Trim();

        if (pattern.Length == 0)
        {
            return $"'{text}' names no pattern: Run(git push *), Write(.github/**) and Read(secrets/**) each name one";
        }

        if (!Enum.TryParse<AgentRuleVerb>(verb, ignoreCase: false, out _))
        {
            return $"'{text}' is no rule this tool reads: the verbs are Run, Write and Read";
        }

        var parsed = Enum.Parse<AgentRuleVerb>(verb);

        if (parsed is AgentRuleVerb.Run)
        {
            var words = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // '*' is any arguments, and it is the last word or it is nothing: a word that holds it
            // beside other characters is read against commands as itself, which is not what its
            // writer wrote, so it is refused as one that does not read.
            foreach (var (word, at) in words.Select((word, at) => (word, at)))
            {
                if (!word.Contains('*'))
                {
                    continue;
                }

                return word == "*" && at == words.Length - 1
                    ? null
                    : $"'{text}' does not read: a Run rule's '*' stands for any arguments, at its end";
            }

            return null;
        }

        if (GlobPattern.Problem(pattern) is { } shape)
        {
            return $"'{text}' does not read: the pattern '{pattern}' {shape}";
        }

        return null;
    }

    /// <summary>Reads <paramref name="rule"/> into a matcher.</summary>
    /// <exception cref="HarnessException"><paramref name="rule"/> is no rule this tool reads, said by <see cref="Problem"/>.</exception>
    public static AgentRule Parse(string rule)
    {
        if (Problem(rule) is { } problem)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, problem);
        }

        var text = rule.Trim();
        var open = text.IndexOf('(');
        var verb = Enum.Parse<AgentRuleVerb>(text[..open].Trim());
        var pattern = text[(open + 1)..^1].Trim();

        return new AgentRule(verb, pattern, verb is AgentRuleVerb.Run ? null : GlobPattern.Parse(pattern));
    }

    /// <summary>
    /// Whether the command <paramref name="words"/> is refused by this <see cref="AgentRuleVerb.Run"/>
    /// rule: each word of the pattern matches the command's word in turn, and a trailing
    /// <c>*</c> the rest of the command, the empty rest included.
    /// </summary>
    /// <param name="words">The command's words, as <see cref="CommandAnalysis"/> flattened them.</param>
    /// <param name="comparison">How a word of the pattern and a word of the command compare: names compare as this platform compares paths.</param>
    /// <exception cref="InvalidOperationException">The rule is not a <see cref="AgentRuleVerb.Run"/> rule.</exception>
    public bool Matches(IReadOnlyList<string> words, StringComparison comparison)
    {
        if (Verb is not AgentRuleVerb.Run)
        {
            throw new InvalidOperationException($"'{Text}' refuses a path, not a command");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Pattern);

        var patternWords = Pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = patternWords[^1] == "*";

        if (!tail && words.Count != patternWords.Length)
        {
            return false;
        }

        var taken = tail ? patternWords.Length - 1 : patternWords.Length;

        if (words.Count < taken)
        {
            return false;
        }

        for (var at = 0; at < taken; at++)
        {
            if (!string.Equals(patternWords[at], words[at], comparison))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the path <paramref name="relativePath"/> is refused by this <see cref="AgentRuleVerb.Write"/>
    /// or <see cref="AgentRuleVerb.Read"/> rule: the path as it stands relative to one of the agent's
    /// roots, matched as <see cref="GlobPattern"/> reads the rule's pattern.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rule is not a path rule.</exception>
    public bool Matches(string relativePath, StringComparison comparison)
    {
        if (_pattern is null)
        {
            throw new InvalidOperationException($"'{Text}' refuses a command, not a path");
        }

        return _pattern.Matches(relativePath, comparison);
    }
}
