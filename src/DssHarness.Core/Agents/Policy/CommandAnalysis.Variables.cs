using DssHarness.Core.Text;

namespace DssHarness.Core.Agents.Policy;

/// <summary>How a command's words touch an environment variable (<see cref="CommandAnalysis.VariableTouched"/>).</summary>
public enum VariableTouch
{
    /// <summary>A word names the variable in a form that sets it or takes it away.</summary>
    Named,

    /// <summary>A word in such a form names a variable by what the shell expands, which may be this one.</summary>
    MayBe,

    /// <summary>The command starts what it runs with no environment at all, the variable gone with the rest.</summary>
    Cleared,
}

public static partial class CommandAnalysis
{
    /// <summary>The commands that take shell variables away by the names they are given: the shells' <c>unset</c>, csh's <c>unsetenv</c>.</summary>
    private static readonly string[] Unsetters = ["unset", "unsetenv"];

    /// <summary>The commands that declare a variable, its export taken away among what they may do (<c>export -n</c>, <c>declare +x</c>, <c>local</c>).</summary>
    private static readonly string[] Declarers = ["export", "declare", "typeset", "local", "readonly"];

    /// <summary>The commands that set a variable by a name and its value: cmd's and fish's <c>set</c>, csh's <c>setenv</c>, <c>setx</c>.</summary>
    private static readonly string[] Setters = ["set", "setenv", "setx"];

    /// <summary>
    /// PowerShell's cmdlets that set or take away an item of the <c>env:</c> drive they are given, each by its own name or
    /// an alias no shell shares; and the aliases the shells' own programs share with them, read so only where PowerShell
    /// reads the command (<see cref="SharedEnvironmentWriters"/>).
    /// </summary>
    private static readonly string[] EnvironmentWriters =
        ["Remove-Item", "ri", "Clear-Item", "cli", "Set-Item", "si", "Rename-Item", "rni", "Move-Item", "mi", "Set-Content", "sc", "Clear-Content", "clc", "New-Item", "ni", "Copy-Item", "cpi"];

    /// <summary>The aliases of PowerShell's item cmdlets that the shells' own programs share: read as PowerShell's where PowerShell reads the command.</summary>
    private static readonly string[] SharedEnvironmentWriters = ["rm", "del", "erase", "rd", "rmdir", "ren", "move", "mv", "copy", "cp"];

    /// <summary>
    /// How <paramref name="words"/> touch the environment variable <paramref name="name"/>, with the word that does it -
    /// in the forms that set it or take it away, and only those: an assignment of it before a program; an <c>unset</c>'s
    /// or <c>unsetenv</c>'s word; env's <c>-u</c> and <c>--unset</c> in every spelling; a declaration of it
    /// (<c>export -n</c>, <c>declare +x</c>, <c>local</c>, <c>readonly</c>), or a name reference to it (<c>declare -n</c>);
    /// <c>set</c>'s, <c>setenv</c>'s and <c>setx</c>'s name; the name read, mapfile, printf <c>-v</c>, getopts, wait
    /// <c>-p</c>, vared, a for or a select set (<see cref="NamesSet"/>), and one an arithmetic assigns
    /// (<see cref="ArithmeticAssigned"/>); a PowerShell item cmdlet's <c>env:</c> item of it. Each of those places given
    /// what the shell expands - a variable, a substitution, an unset's pattern (zsh's <c>unset -m</c>) - may be it; so
    /// may a PowerShell item cmdlet's path that begins with an expansion, an <c>env:</c> item named by a wildcard that may
    /// match it - a relative path's wildcard where <paramref name="environmentDrive"/> says the string names the
    /// <c>env:</c> drive, which a move may have made its place - and one given no path at all (its items piped to it)
    /// there. A
    /// start with no environment (<c>env -i</c>, <c>env -</c>, <c>exec -c</c>) takes it with the rest. A word that only
    /// mentions the name - a search's, a message's, its value read - touches nothing. Null where the words do none of these.
    /// </summary>
    /// <param name="words">One command's words, its environment first, or a wrapper's words as they stood.</param>
    /// <param name="quoted">How each word's characters stand to the shell, or null where their spelling was not kept.</param>
    /// <param name="name">The variable.</param>
    /// <param name="powerShell">Whether PowerShell reads the command, whose <c>rm</c>, <c>mv</c> and their kind are its item cmdlets.</param>
    /// <param name="environmentDrive">Whether a word of the string names PowerShell's <c>env:</c> drive (<see cref="NamesEnvironmentDrive"/>).</param>
    public static (string Word, VariableTouch Touch)? VariableTouched(IReadOnlyList<string> words, IReadOnlyList<string>? quoted, string name, bool powerShell, bool environmentDrive)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(name);

        quoted ??= [];

        bool Names(string word) => word.Equals(name, StringComparison.OrdinalIgnoreCase);
        bool Expands(int at, string word) => Expansion(word, At(quoted, at)) is "a variable" or "a substitution";

        var environment = EnvironmentOf(words);

        if (words.Take(environment).FirstOrDefault(word => Names(AssignedName(word))) is { } assignment)
        {
            return (assignment, VariableTouch.Named);
        }

        if (environment == words.Count)
        {
            return null;
        }

        var program = Name(words[environment]);

        // env and exec, wherever a program runs: env's -u names, and a start with no environment.
        for (var at = environment; at < words.Count; at++)
        {
            if (!RunsAt(words, at) && at != environment)
            {
                break;
            }

            IReadOnlyList<string> rest = [.. words.Skip(at)];

            if (Is(Name(words[at]), "env"))
            {
                var (unsets, cleared) = EnvironmentTakenAway(rest);

                if (unsets.FirstOrDefault(unset => Names(unset.Name)) is { Name: not null } unset)
                {
                    return (rest[unset.Word], VariableTouch.Named);
                }

                if (unsets.FirstOrDefault(each => Expands(at + each.Word, each.Name)) is { Name: not null } expanded)
                {
                    return (rest[expanded.Word], VariableTouch.MayBe);
                }

                if (cleared)
                {
                    return (words[at], VariableTouch.Cleared);
                }
            }

            if (Is(Name(words[at]), "exec") && rest.Skip(1).TakeWhile(word => word.StartsWith('-')).Any(word => word.Length > 1 && word[1] != '-' && word.Contains('c', StringComparison.Ordinal)))
            {
                return (words[at], VariableTouch.Cleared);
            }
        }

        var operands = words.Select((word, at) => (Word: word, At: at)).Skip(environment + 1).ToList();

        if (Is(program, Unsetters))
        {
            foreach (var (word, at) in operands.Where(operand => !operand.Word.StartsWith('-')))
            {
                if (Names(word))
                {
                    return (word, VariableTouch.Named);
                }

                // zsh's unset -m reads its words as patterns, quoted or not.
                if (Expands(at, word) || word.IndexOfAny(['*', '?', '[']) >= 0)
                {
                    return (word, VariableTouch.MayBe);
                }
            }
        }

        if (Is(program, Declarers))
        {
            var reference = operands.Any(operand => operand.Word.StartsWith('-') && operand.Word.Contains('n', StringComparison.Ordinal) && program is not "export");

            foreach (var (word, at) in operands.Where(operand => !operand.Word.StartsWith('-') && !operand.Word.StartsWith('+')))
            {
                if (Names(DeclaredName(word) ?? string.Empty))
                {
                    return (word, VariableTouch.Named);
                }

                // A name reference stands for the variable its value names: unset through it takes that one away.
                if (reference && word.Contains('=', StringComparison.Ordinal) && word.Split('=', 2)[1] is var target)
                {
                    if (Names(target))
                    {
                        return (word, VariableTouch.Named);
                    }

                    if (Expands(at, word))
                    {
                        return (word, VariableTouch.MayBe);
                    }
                }
            }
        }

        if (Is(program, Setters)
            && operands.TakeWhile(operand => operand.Word != "--").FirstOrDefault(operand => !operand.Word.StartsWith('-') && !operand.Word.StartsWith('/')) is { Word: not null } set
            && Names(AssignedName(set.Word)))
        {
            return (set.Word, VariableTouch.Named);
        }

        // The shells' other forms that set a variable by its name: read's names and its array (-a, zsh's -A), mapfile's and
        // readarray's array, printf -v's, getopts' name, wait -p's, zsh's vared and set -A, let's arithmetic, and a for's or
        // a select's name - each given what the shell expands may be it.
        foreach (var (word, at) in NamesSet(program, operands, words, environment))
        {
            if (Names(word) || Names(AssignedName(word)))
            {
                return (word, VariableTouch.Named);
            }

            if (Expands(at, word))
            {
                return (word, VariableTouch.MayBe);
            }
        }

        // let's arithmetic, a ((...)) among it (read as let's): each name an expression assigns, one the shell expands
        // there maybe this one.
        if (Is(program, "let"))
        {
            foreach (var (word, _) in operands)
            {
                foreach (var assigned in ArithmeticAssigned(word))
                {
                    if (Names(assigned))
                    {
                        return (word, VariableTouch.Named);
                    }

                    if (assigned.StartsWith('$'))
                    {
                        return (word, VariableTouch.MayBe);
                    }
                }
            }
        }

        if (Is(program, EnvironmentWriters) || (powerShell && Is(program, SharedEnvironmentWriters)))
        {
            if (operands.FirstOrDefault(operand => EnvironmentReference.Match(operand.Word) is { Success: true } item && Names(item.Groups["name"].Value.TrimStart('\\', '/'))) is { Word: not null } item)
            {
                return (item.Word, VariableTouch.Named);
            }

            var paths = ItemPaths(program, operands);

            // An env: item named by a wildcard that may match it, and - after a move into the env: drive - any path named so.
            foreach (var (word, _) in paths)
            {
                var entry = EnvironmentReference.Match(word) is { Success: true } reference ? reference.Groups["name"].Value.TrimStart('\\', '/')
                    : environmentDrive && !word.Contains(':', StringComparison.Ordinal) ? word.TrimStart('\\', '/')
                    : null;

                if (entry is not null && entry.IndexOfAny(['*', '?', '[']) >= 0 && WildcardMayMatch(entry, name))
                {
                    return (word, VariableTouch.MayBe);
                }
            }

            // A path PowerShell computes may name an env: item, and one given no path takes the items piped to it.
            if (paths.FirstOrDefault(operand => operand.Word.StartsWith('$') && Expands(operand.At, operand.Word)) is { Word: not null } computed)
            {
                return (computed.Word, VariableTouch.MayBe);
            }

            if (paths.Count == 0 && environmentDrive)
            {
                return (words[environment], VariableTouch.MayBe);
            }
        }

        return null;
    }

    /// <summary>
    /// The words of the shells' forms that set a variable by the name they are given, other than an assignment, an unset,
    /// a declaration or an arithmetic (<see cref="ArithmeticAssigned"/>): read's names and the array its <c>-a</c> (zsh's
    /// <c>-A</c>) names, mapfile's and readarray's array, printf's <c>-v</c>, getopts' name, wait's <c>-p</c>, zsh's
    /// <c>vared</c> and <c>set -A</c>, and the name a <c>for</c> or a <c>select</c> goes over its words by.
    /// </summary>
    private static IEnumerable<(string Word, int At)> NamesSet(string program, IReadOnlyList<(string Word, int At)> operands, IReadOnlyList<string> words, int environment)
    {
        // A short option's value: the rest of its word, or the next word.
        IEnumerable<(string Word, int At)> Valued(char letter)
        {
            for (var index = 0; index < operands.Count; index++)
            {
                var (word, at) = operands[index];

                if (word.Length > 1 && word[0] == '-' && word[1] != '-' && word.IndexOf(letter, 1) is var found and > 0)
                {
                    if (found + 1 < word.Length)
                    {
                        yield return (word[(found + 1)..], at);
                    }
                    else if (index + 1 < operands.Count)
                    {
                        yield return operands[index + 1];
                    }
                }
            }
        }

        IEnumerable<(string Word, int At)> Plain() => operands.Where(operand => !operand.Word.StartsWith('-'));

        return program switch
        {
            "read" => [.. Valued('a'), .. Valued('A'), .. Plain().Where(operand => !Valued('d').Concat(Valued('p')).Concat(Valued('t')).Concat(Valued('u')).Concat(Valued('i')).Concat(Valued('n')).Concat(Valued('N')).Contains(operand))],
            "mapfile" or "readarray" => [.. Plain().Where(operand => !Valued('d').Concat(Valued('n')).Concat(Valued('O')).Concat(Valued('s')).Concat(Valued('u')).Concat(Valued('C')).Concat(Valued('c')).Contains(operand)).TakeLast(1)],
            "printf" => Valued('v'),
            "getopts" => Plain().Skip(1).Take(1),
            "wait" => Valued('p'),
            "vared" => Plain(),
            "set" when operands.Any(operand => operand.Word == "-A") => Valued('A'),
            "for" or "select" when environment + 1 < words.Count => [(words[environment + 1], environment + 1)],
            _ => [],
        };
    }

    /// <summary>
    /// The names an arithmetic expression - let's, a <c>((...))</c>'s - assigns: each before an assignment's operator
    /// (<c>=</c>, <c>+=</c>, <c>&lt;&lt;=</c> and their kind, an element's subscript between), and each a <c>++</c> or a
    /// <c>--</c> stands against; a name the shell expands there (<c>$v</c>, <c>${v}</c>, <c>$(...)</c>) as it is spelled.
    /// A name only read for its value (<c>i &lt; $n</c>, <c>a == b</c>) is none.
    /// </summary>
    /// <param name="expression">The expression.</param>
    internal static IEnumerable<string> ArithmeticAssigned(string expression)
    {
        var at = 0;

        while (LinearRegex.MatchFrom(ArithmeticAssignment, expression, at) is { Success: true } assignment)
        {
            yield return assignment.Groups["name"].Value;

            // An assignment's match takes the character after its operator, which it reads to know the '=' is no '==': the
            // next is sought from the operator's end, so that character is read again.
            at = assignment.Groups["operator"] is { Success: true } assigned
                ? assigned.Index + assigned.Length
                : assignment.Index + assignment.Length;
        }
    }

    /// <summary>
    /// A name an arithmetic expression assigns (<see cref="ArithmeticAssigned"/>), sought from where the last one's
    /// operator ended (<see cref="LinearRegex.MatchFrom"/>): either a name and then its assignment's operator, in the group
    /// <c>operator</c>, or a <c>++</c> or a <c>--</c> and then the name.
    /// The first stands where no letter, digit, <c>_</c> or <c>$</c> is right before the name: that character is
    /// matched, or the name begins what is read - the last match ended on an operator, a brace or a parenthesis, or on a
    /// run of a name's characters it took whole, so none stands against it. Its <c>=</c> is one no second <c>=</c>
    /// follows: the character after it is matched too, or the expression's end. After the second's unbraced name, the
    /// rest of a run of a name's characters is matched too, since no name begins inside it.
    /// </summary>
    /// <remarks>
    /// The <c>=</c>'s next character is matched as <c>[^=]|$</c>, not <c>[^=]|\z</c>: the first takes any line break, so
    /// <c>$</c> stands only at the end, and .NET's linear matcher reports no group of a match a <c>\z</c> ends after a
    /// final line break.
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex ArithmeticAssignment = LinearRegex.Compile(
        @"(?:\A|[^A-Za-z0-9_$])(?<name>" + ArithmeticName + @")\s*(?:\[[^\]]*\]\s*)?(?:(?<operator>(?:[-+*/%&|^]|<<|>>)?=)(?:[^=]|$)|(?<operator>\+\+|--))"
        + @"|(?:\+\+|--)\s*(?:(?<name>\$\{[^}]*\}|\$\([^)]*\))|(?<name>\$[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*)[A-Za-z0-9_$]*)");

    /// <summary>A name as arithmetic spells one: <c>${...}</c>, <c>$(...)</c>, <c>$name</c> or a bare name.</summary>
    private const string ArithmeticName = @"\$\{[^}]*\}|\$\([^)]*\)|\$[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*";

    /// <summary>
    /// Whether PowerShell's wildcard <paramref name="pattern"/> - a star, a question mark, a bracket - may match
    /// <paramref name="name"/>, in any case. A wildcard is a regular pattern, matched by the linear matcher
    /// (<see cref="LinearRegex"/>) in time linear in the name however its stars are written, with no match cut short.
    /// </summary>
    internal static bool WildcardMayMatch(string pattern, string name)
    {
        if (pattern.Contains('[', StringComparison.Ordinal))
        {
            return true;
        }

        var expression = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";

        return LinearRegex.Compile(expression, System.Text.RegularExpressions.RegexOptions.IgnoreCase).IsMatch(name);
    }

    /// <summary>The parameters of PowerShell's item cmdlets that take a path, by any prefix of two letters or more.</summary>
    private static readonly string[] ItemPathParameters = ["path", "literalpath", "pspath", "lp", "destination", "newname"];

    /// <summary>The switches of PowerShell's item cmdlets, the common ones among them: each takes no value.</summary>
    private static readonly string[] ItemSwitches = ["recurse", "force", "passthru", "nonewline", "asbytestream", "confirm", "whatif", "verbose", "debug", "container"];

    /// <summary>
    /// The words a PowerShell item cmdlet binds to a path, among <paramref name="operands"/>: each value of a parameter that
    /// takes one (<see cref="ItemPathParameters"/>), and its first word by place - its second too where it moves, copies or
    /// renames - every other parameter's value passed (<c>-Value</c>, <c>-Encoding</c>), its switches taking none.
    /// </summary>
    private static List<(string Word, int At)> ItemPaths(string program, IReadOnlyList<(string Word, int At)> operands)
    {
        var named = new List<(string Word, int At)>();
        var positional = new List<(string Word, int At)>();

        for (var index = 0; index < operands.Count; index++)
        {
            var (word, at) = operands[index];

            if (word.Length < 2 || word[0] != '-')
            {
                positional.Add((word, at));
                continue;
            }

            var spelled = word[1..].Split(':', 2);
            var parameter = spelled[0].ToLowerInvariant();
            var path = parameter.Length >= 2 && ItemPathParameters.Any(name => name.StartsWith(parameter, StringComparison.Ordinal));

            if (spelled.Length == 2)
            {
                if (path)
                {
                    named.Add((spelled[1], at));
                }

                continue;
            }

            if (ItemSwitches.Any(name => name.StartsWith(parameter, StringComparison.Ordinal)) || index + 1 >= operands.Count)
            {
                continue;
            }

            index++;

            if (path)
            {
                named.Add(operands[index]);
            }
        }

        var places = Is(program, "Rename-Item", "rni", "ren", "Move-Item", "mi", "move", "mv", "Copy-Item", "cpi", "copy", "cp") ? 2 : 1;

        return [.. named, .. positional.Take(places)];
    }

    /// <summary>Whether <paramref name="word"/> names PowerShell's <c>env:</c> drive, or an item of it - after a parameter's name glued before it too.</summary>
    /// <param name="word">The word.</param>
    public static bool NamesEnvironmentDrive(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var item = word.StartsWith('-') && word.IndexOf(':', StringComparison.Ordinal) is var colon and > 1 ? word[(colon + 1)..] : word;

        return item.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("Environment::", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith(@"Microsoft.PowerShell.Core\Environment::", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The word among <paramref name="words"/> that names the variable <paramref name="name"/> where a command may set it
    /// or take it away: any word that spells the name - in any case, wherever it stands in the word, an option's letters
    /// glued before it - but where it is read for its value (<c>$NAME</c>, <c>${NAME}</c>), and but where it is only data:
    /// every word of a program that runs none of its words (<see cref="RunsNoWords"/>, <c>printf</c> but with its
    /// <c>-v</c>) or of a git command that reads only what git tracks (<see cref="ReadsTrackedOnly"/>), and a word that
    /// holds a blank, which no name does. Null where none does.
    /// </summary>
    /// <param name="words">One command's words, its environment first, or a wrapper's words as they stood.</param>
    /// <param name="name">The variable.</param>
    public static string? NameSpelled(IReadOnlyList<string> words, string name)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(name);

        var environment = EnvironmentOf(words);

        if (environment < words.Count
            && ((RunsNoWords(words[environment]) && !(Is(Name(words[environment]), "printf") && words.Skip(environment + 1).Any(word => word.StartsWith("-v", StringComparison.Ordinal))))
                || ReadsTrackedOnly(words, environment)))
        {
            words = [.. words.Take(environment)];
        }

        return words.FirstOrDefault(word => !word.Any(char.IsWhiteSpace) && Spells(word));

        bool Spells(string word)
        {
            for (var at = word.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = word.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var end = at + name.Length;
                // $NAME, ${NAME}, ${!NAME}, ${#NAME} and PowerShell's $env:NAME read its value.
                var read = (at >= 1 && word[at - 1] == '$')
                    || (at >= 2 && word[at - 1] is '{' or '!' or '#' && word.LastIndexOf("${", at, StringComparison.Ordinal) is var brace and >= 0 && brace >= at - 3)
                    || (at >= 5 && word.AsSpan(at - 5, 5).Equals("$env:", StringComparison.OrdinalIgnoreCase));

                if (!read && (end >= word.Length || !(char.IsAsciiLetterOrDigit(word[end]) || word[end] == '_')))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Whether the shell expands a pattern in <paramref name="word"/>: a star or a question mark it reads in the open, or a
    /// bracket it reads open to its close. A word whose spelling was not kept (<paramref name="quoted"/> empty, or short of
    /// it) is read as open.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <param name="quoted">How each of its characters stands to the shell (<see cref="WordQuoting"/>).</param>
    public static bool HasOpenPattern(string word, string? quoted)
    {
        ArgumentNullException.ThrowIfNull(word);

        var classes = ClassesOf(word, string.IsNullOrEmpty(quoted) ? null : quoted);

        for (var at = 0; at < word.Length; at++)
        {
            if (word[at] is '*' or '?' && classes[at] == WordQuoting.Open)
            {
                return true;
            }

            if (word[at] == '[' && classes[at] == WordQuoting.Open
                && word.IndexOf(']', Math.Min(at + 2, word.Length)) is var close && close > at + 1 && classes[close] == WordQuoting.Open)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The programs that name, test or list the paths they are given, and read nothing a file holds: a directory one of
    /// them is handed is no read of what it holds. <c>find</c> is one where it runs no command and deletes and writes
    /// nothing (<see cref="NamesOnly"/>).
    /// </summary>
    public static IReadOnlyList<string> NamingPrograms { get; } =
    [
        "echo", "printf", "test", "[", "[[", "dirname", "basename", "realpath", "readlink", "df", "stat", "mkdir", "which", "whereis",
        "type", "pwd", "ls", "dir", "du", "tree", "find", "md", "Test-Path", "Resolve-Path", "Split-Path", "Join-Path", "Get-ChildItem",
        "gci", "Get-Item", "gi", "Write-Output", "Write-Host",
    ];

    /// <summary>find's actions that run a command, delete or write a file: a find with one reads or writes past the names it lists.</summary>
    private static readonly string[] FindActions = ["-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls"];

    /// <summary>
    /// Whether the program of <paramref name="command"/>, at <paramref name="start"/>, names, tests or lists the paths it is
    /// given and reads nothing a file holds (<see cref="NamingPrograms"/>): a <c>find</c> with none of its actions that run,
    /// delete or write (<see cref="FindActions"/>).
    /// </summary>
    /// <param name="command">A command's words, its environment first.</param>
    /// <param name="start">Where its program stands.</param>
    public static bool NamesOnly(IReadOnlyList<string> command, int start)
    {
        ArgumentNullException.ThrowIfNull(command);

        return start < command.Count
            && Is(Name(command[start]), [.. NamingPrograms])
            && !(Is(Name(command[start]), "find") && command.Skip(start + 1).Any(word => FindActions.Contains(word, StringComparer.Ordinal)));
    }

    /// <summary>
    /// <paramref name="word"/> as the pattern of its spellings (<see cref="WordPattern"/>), each variable the shell
    /// expands in it that <paramref name="values"/> knows - <c>$NAME</c> and <c>${NAME}</c> where its dollar is not quoted
    /// away, a leading <c>~</c> the shell expands as <c>HOME</c>'s and a leading <c>~+</c> as <c>PWD</c>'s - standing for
    /// the pattern of every value it may hold, each one kept, however many: one choice wherever the variable stands, every
    /// character it spells standing to the shell as its dollar did. Every other expansion stays as it is spelled. The word
    /// alone, its one spelling, where it names none of them.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
    /// <param name="values">The pattern of the values a name may hold, or null where none is known.</param>
    public static WordPattern Patterned(string word, string? quoted, Func<string, WordPattern?> values)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(values);

        var classes = ClassesOf(word, string.IsNullOrEmpty(quoted) ? null : quoted);
        var parts = new List<WordPart>();
        var start = 0;

        // A leading tilde the shell expands is HOME's value, and a leading ~+ PWD's: each pattern known for it, as $HOME
        // and $PWD are.
        foreach (var (tilde, name) in new[] { ("~+", "PWD"), ("~", "HOME") })
        {
            if (word.StartsWith(tilde, StringComparison.Ordinal)
                && classes[..tilde.Length].All(character => character == WordQuoting.Open)
                && (word.Length == tilde.Length || word[tilde.Length] is '/' or '\\')
                && values(name) is { } known)
            {
                parts.Add(new WordPart.Choice(new WordChoice([known]), WordQuoting.Open));
                start = tilde.Length;
                break;
            }
        }

        var text = start;

        for (var at = start; at < word.Length; at++)
        {
            if (word[at] != '$' || classes[at] == WordQuoting.Literal || at + 1 >= word.Length)
            {
                continue;
            }

            var braced = word[at + 1] == '{';
            var begin = at + (braced ? 2 : 1);
            var end = begin;

            while (end < word.Length && (char.IsAsciiLetterOrDigit(word[end]) || word[end] == '_') && !(end == begin && char.IsAsciiDigit(word[end])))
            {
                end++;
            }

            if (end == begin || (braced && (end >= word.Length || word[end] != '}')) || values(word[begin..end]) is not { } given)
            {
                continue;
            }

            if (at > text)
            {
                parts.Add(new WordPart.Text(word[text..at], classes[text..at]));
            }

            parts.Add(new WordPart.Choice(new WordChoice([given]), classes[at]));
            text = braced ? end + 1 : end;
            at = text - 1;
        }

        if (text < word.Length)
        {
            parts.Add(new WordPart.Text(word[text..], classes[text..]));
        }

        return parts.Count == 0 ? WordPattern.Of(word, classes) : new WordPattern(parts);
    }

    /// <summary>
    /// Whether a spelling of <paramref name="pattern"/> may hold what the shell expands: a dollar, a cmd percent or a
    /// backtick anywhere it is not quoted away - in its own text, or in what a choice of it spells - for text the choices
    /// join may make one the parts alone do not (<c>${D}(id)</c> where <c>D</c> holds a dollar). Read of the pattern's text
    /// once, whatever its spellings.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    internal static bool MayExpand(WordPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var waiting = new Stack<(WordPattern Pattern, char? Class)>([(pattern, null)]);
        var seen = new HashSet<WordChoice>(ReferenceEqualityComparer.Instance);

        while (waiting.TryPop(out var each))
        {
            foreach (var part in each.Pattern.Parts)
            {
                switch (part)
                {
                    case WordPart.Text text:
                        for (var at = 0; at < text.Spelled.Length; at++)
                        {
                            var stands = each.Class ?? (at < text.Quoted.Length ? text.Quoted[at] : WordQuoting.Open);

                            if (text.Spelled[at] is '$' or '%' or '`' && stands != WordQuoting.Literal)
                            {
                                return true;
                            }
                        }

                        break;
                    case WordPart.Choice choice when seen.Add(choice.Of):
                        foreach (var alternative in choice.Of.Alternatives)
                        {
                            waiting.Push((alternative, choice.Class ?? each.Class));
                        }

                        break;
                }
            }
        }

        return false;
    }
}
