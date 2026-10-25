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

    /// <summary>A web request, refused by the host it names: <c>Web(pastebin.com)</c>, or <c>Web(*)</c> for the
    /// web whole.</summary>
    Web,
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
/// A <c>Web</c> pattern is one domain - <c>Web(pastebin.com)</c> - which refuses the domain and
/// every name under it, or <c>*</c> alone, which refuses the web whole. No scheme, no path and no
/// port are read: a rule refuses a host, wherever an agent met it - a page, a redirect, a search's
/// source, or a command that runs a program which fetches.
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
            return $"'{text}' is no rule this tool reads: the verbs are Run, Write, Read and Web";
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

        if (parsed is AgentRuleVerb.Web)
        {
            return pattern == "*"
                ? null
                : DomainProblem(pattern) is { } domain
                    ? $"'{text}' does not read: {domain}"
                    : null;
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

        return new AgentRule(verb, pattern, verb is AgentRuleVerb.Run or AgentRuleVerb.Web ? null : GlobPattern.Parse(pattern));
    }

    /// <summary>
    /// Why <paramref name="domain"/> is no domain a <c>Web</c> rule or <c>webDomains</c> holds, or
    /// <see langword="null"/> where it is: labels joined by dots, each beginning and ending in a
    /// letter or a digit, with nothing else beside a hyphen inside one. A host is refused by the
    /// domain it is under, never by a piece of its name, so a pattern that would match a piece
    /// matches nothing and is refused as one that does not read.
    /// </summary>
    public static string? DomainProblem(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return "a domain is named: Web(pastebin.com)";
        }

        var text = domain.Trim();

        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var label in text.Split('.'))
        {
            if (label.Length == 0
                || !char.IsAsciiLetterOrDigit(label[0])
                || !char.IsAsciiLetterOrDigit(label[^1])
                || !label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                return $"'{text}' is no domain: labels joined by dots, each beginning and ending in a letter or a digit, as pastebin.com is";
            }
        }

        return text.Contains('.') ? null : $"'{text}' is no domain: one label alone is not a host an agent reaches, as pastebin.com is";
    }

    /// <summary>
    /// How the command <paramref name="words"/> meets this <see cref="AgentRuleVerb.Run"/> rule:
    /// each word of the pattern matches the command's word in turn - the program by its name, so
    /// <c>/usr/bin/gh</c> and <c>gh.exe</c> are the <c>gh</c> a rule names - and a trailing
    /// <c>*</c> the rest of the command, the empty rest included. Where a word the pattern reads is
    /// one the shell expands (<c>git $X</c>, <c>gh $(cat cmd)</c>), each word from it on may be any
    /// words at all: the rule may be what the command is, and that is said, not decided.
    /// </summary>
    /// <param name="words">The command's words, as <see cref="CommandAnalysis"/> flattened them.</param>
    /// <param name="comparison">How a word of the pattern and a word of the command compare: names compare as this platform compares paths.</param>
    /// <exception cref="InvalidOperationException">The rule is not a <see cref="AgentRuleVerb.Run"/> rule.</exception>
    public AgentRuleMatch Match(IReadOnlyList<string> words, StringComparison comparison)
    {
        if (Verb is not AgentRuleVerb.Run)
        {
            throw new InvalidOperationException($"'{Text}' refuses a path, not a command");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Pattern);

        var patternWords = Pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = patternWords[^1] == "*";
        var taken = tail ? patternWords.Length - 1 : patternWords.Length;

        for (var at = 0; at < taken; at++)
        {
            if (at >= words.Count)
            {
                return AgentRuleMatch.No;
            }

            // A word the shell expands may be one word, many or none: from here the command may be
            // anything the rule names. The program itself is never one here - the reader refuses a
            // program an expansion names - so an expansion where a program would stand is an argument.
            if (CommandAnalysis.Expansion(words[at]) is not null)
            {
                return at == 0 ? AgentRuleMatch.No : AgentRuleMatch.Expanded;
            }

            // A program is named in any case, as the analysis reads one: a file system that folds case runs GIT as git.
            var matched = at == 0
                ? string.Equals(CommandAnalysis.Name(patternWords[at]), CommandAnalysis.Name(words[at]), StringComparison.OrdinalIgnoreCase)
                : string.Equals(patternWords[at], words[at], comparison);

            if (!matched)
            {
                return AgentRuleMatch.No;
            }
        }

        if (tail || words.Count == taken)
        {
            return AgentRuleMatch.Yes;
        }

        // An exact rule meets a command with words past its own only where one of them may expand to none.
        return words.Skip(taken).Any(word => CommandAnalysis.Expansion(word) is not null) ? AgentRuleMatch.Expanded : AgentRuleMatch.No;
    }

    /// <summary>
    /// Whether the host <paramref name="host"/> is refused by this <see cref="AgentRuleVerb.Web"/>
    /// rule: the domain itself, or any name under it - <c>Web(pastebin.com)</c> refuses
    /// <c>www.pastebin.com</c> too. Hosts fold case, so the comparison always does.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rule is not a <see cref="AgentRuleVerb.Web"/> rule.</exception>
    public bool MatchesHost(string host)
    {
        if (Verb is not AgentRuleVerb.Web)
        {
            throw new InvalidOperationException($"'{Text}' refuses a path or a command, not a host");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (Pattern == "*")
        {
            return true;
        }

        return host.Equals(Pattern, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + Pattern, StringComparison.OrdinalIgnoreCase);
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

/// <summary>How a command meets a <see cref="AgentRuleVerb.Run"/> rule.</summary>
public enum AgentRuleMatch
{
    /// <summary>The command is not what the rule names.</summary>
    No,

    /// <summary>The command is what the rule names.</summary>
    Yes,

    /// <summary>A word the rule reads is one the shell expands: the command may be what the rule names, which cannot be told.</summary>
    Expanded,
}
