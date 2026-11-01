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
    /// <c>set</c>'s, <c>setenv</c>'s and <c>setx</c>'s name; a PowerShell item cmdlet's <c>env:</c> item of it. Each of
    /// those places given what the shell expands - a variable, a substitution, an unset's pattern (zsh's <c>unset -m</c>)
    /// - may be it; so may a PowerShell item cmdlet's path that begins with an expansion, and one given no path at all
    /// (its items piped to it) where <paramref name="environmentDrive"/> says the string names the <c>env:</c> drive. A
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

        if (Is(program, EnvironmentWriters) || (powerShell && Is(program, SharedEnvironmentWriters)))
        {
            if (operands.FirstOrDefault(operand => EnvironmentReference().Match(operand.Word) is { Success: true } item && Names(item.Groups["name"].Value.TrimStart('\\', '/'))) is { Word: not null } item)
            {
                return (item.Word, VariableTouch.Named);
            }

            var paths = ItemPaths(program, operands);

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

        return item.StartsWith("env:", StringComparison.OrdinalIgnoreCase);
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
    /// The readings of <paramref name="word"/> with each variable the shell expands in it given a value
    /// <paramref name="values"/> knows - <c>$NAME</c> and <c>${NAME}</c> where its dollar is not quoted away, and a leading
    /// <c>~</c> the shell expands as <c>HOME</c>'s - each value in turn, the text each takes standing to the shell as its
    /// dollar did; the word alone where it names none of them. Every other expansion stays as it is spelled.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
    /// <param name="values">The values a name may hold, or null where none is known.</param>
    /// <exception cref="NotSupportedException">The values spell more readings of the word than <see cref="MaxReadings"/>.</exception>
    public static IReadOnlyList<(string Word, string Quoted)> Substituted(string word, string? quoted, Func<string, IReadOnlyList<string>?> values)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(values);

        var classes = ClassesOf(word, string.IsNullOrEmpty(quoted) ? null : quoted);
        var references = new List<(int Start, int End, string Name, char Class)>();

        for (var at = 0; at < word.Length; at++)
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

            if (end == begin || (braced && (end >= word.Length || word[end] != '}')))
            {
                continue;
            }

            references.Add((at, braced ? end + 1 : end, word[begin..end], classes[at]));
        }

        var spelled = (Word: word, Quoted: classes);
        var readings = new List<(string Word, string Quoted)> { spelled };

        foreach (var name in references.Select(reference => reference.Name).Distinct(StringComparer.Ordinal))
        {
            if (values(name) is not { Count: > 0 } given)
            {
                continue;
            }

            readings = [.. readings.SelectMany(reading => given.Select(value => Replaced(reading, name, value)))];

            if (readings.Count > MaxReadings)
            {
                throw new NotSupportedException($"'{word}' spells more than {MaxReadings} readings by the values its variables may hold, which this does not follow");
            }
        }

        // A leading tilde the shell expands is HOME's value: read as each one known for it, as $HOME is.
        if (values("HOME") is { Count: > 0 } homes)
        {
            readings = [.. readings.SelectMany(reading => reading.Word.Length > 0 && reading.Word[0] == '~' && reading.Quoted[0] == WordQuoting.Open
                    && (reading.Word.Length == 1 || reading.Word[1] is '/' or '\\')
                ? homes.Select(home => (home + reading.Word[1..], new string(WordQuoting.Open, home.Length) + reading.Quoted[1..]))
                : [reading])];

            if (readings.Count > MaxReadings)
            {
                throw new NotSupportedException($"'{word}' spells more than {MaxReadings} readings by the values its variables may hold, which this does not follow");
            }
        }

        // The word as it is spelled first, then each reading a value gave it.
        return readings.Count == 1 && readings[0] == spelled ? readings : [spelled, .. readings];
    }

    /// <summary>The most readings of one word, or of one command, its variables' values are followed to; past them it is not decided, and so denied.</summary>
    public const int MaxReadings = 16;

    /// <summary><paramref name="reading"/> with each reference to <paramref name="name"/> the shell expands given <paramref name="value"/>, standing as its dollar stood.</summary>
    private static (string Word, string Quoted) Replaced((string Word, string Quoted) reading, string name, string value)
    {
        var word = new System.Text.StringBuilder();
        var mask = new System.Text.StringBuilder();

        for (var at = 0; at < reading.Word.Length; at++)
        {
            if (reading.Word[at] == '$' && reading.Quoted[at] != WordQuoting.Literal)
            {
                foreach (var spelled in new[] { "${" + name + "}", "$" + name })
                {
                    var end = at + spelled.Length;

                    if (string.CompareOrdinal(reading.Word, at, spelled, 0, spelled.Length) == 0
                        && (spelled[1] == '{' || end >= reading.Word.Length || !(char.IsAsciiLetterOrDigit(reading.Word[end]) || reading.Word[end] == '_')))
                    {
                        word.Append(value);
                        mask.Append(reading.Quoted[at], value.Length);
                        at = end - 1;
                        goto Next;
                    }
                }
            }

            word.Append(reading.Word[at]);
            mask.Append(reading.Quoted[at]);
            Next:;
        }

        return (word.ToString(), mask.ToString());
    }
}
