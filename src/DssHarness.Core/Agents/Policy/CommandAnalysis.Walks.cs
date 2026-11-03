namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// A walk of the file tree a command makes that removes what it matches - a <c>find</c> that deletes (<c>-delete</c>) or
/// runs a remover on each entry it matches (<c>-exec rm {}</c> and its kind), an <c>fd</c> that runs one (<c>-x</c>,
/// <c>-X</c>) - as <see cref="CommandAnalysis.WalkOf"/> reads it: where it starts, what it asks of each entry it reaches,
/// and how deep it goes. What it removes of a place is told by asking its expression of each entry on the way there and
/// each entry the place holds.
/// </summary>
/// <param name="Points">Where it starts, as the command spells each: the working directory where it names none.</param>
/// <param name="Expression">What it asks of each entry and does to it.</param>
/// <param name="MaxDepth">How deep below a start point it reaches, the start point at 0.</param>
/// <param name="MinDepth">How deep an entry stands before it is asked anything: shallower ones are walked through, never matched.</param>
/// <param name="DepthFirst">Whether it walks an entry's contents before the entry, where a prune keeps nothing from being walked.</param>
internal sealed record FileWalk(IReadOnlyList<string> Points, WalkNode Expression, int MaxDepth, int MinDepth, bool DepthFirst);

/// <summary>What a walk's expression is made of: tests, actions and the operators that join them, as find evaluates them.</summary>
internal abstract record WalkNode
{
    /// <summary>A test of the entry.</summary>
    internal sealed record Test(WalkTest Asked) : WalkNode;

    /// <summary>The test or action negated: <c>!</c>, <c>-not</c>.</summary>
    internal sealed record Not(WalkNode Operand) : WalkNode;

    /// <summary>Both, the right asked only where the left holds: <c>-a</c>, or two side by side.</summary>
    internal sealed record And(WalkNode Left, WalkNode Right) : WalkNode;

    /// <summary>Either, the right asked only where the left does not hold: <c>-o</c>.</summary>
    internal sealed record Or(WalkNode Left, WalkNode Right) : WalkNode;

    /// <summary>Both asked, the right's answer the whole's: find's <c>,</c>.</summary>
    internal sealed record Both(WalkNode Left, WalkNode Right) : WalkNode;

    /// <summary>An action, and whether it always holds (<c>-print</c>, <c>-prune</c>, <c>-delete</c>) or holds as the command it runs ends (<c>-exec</c>).</summary>
    internal sealed record Act(WalkAction Action, bool Holds) : WalkNode;
}

/// <summary>What a walk's test asks of an entry.</summary>
internal abstract record WalkTest
{
    /// <summary>
    /// Whether the entry's name, or a path of it, matches a pattern: a shell pattern as find matches one, every character
    /// matched by a star, or a regular expression searched for as fd searches one.
    /// </summary>
    internal sealed record Named(string Pattern, bool IgnoreCase, bool Regular, WalkName Of) : WalkTest;

    /// <summary>Whether the entry is of one of the kinds the letters name: <c>f</c> a file, <c>d</c> a directory, <c>l</c> a link.</summary>
    internal sealed record Kind(string Letters) : WalkTest;

    /// <summary>Whether the entry is empty: a directory that holds nothing, a file of no bytes.</summary>
    internal sealed record Empty : WalkTest;

    /// <summary>A test that always holds or never does: <c>-true</c>, <c>-false</c>, an option.</summary>
    internal sealed record Constant(bool Value) : WalkTest;

    /// <summary>A test whose answer this does not tell - a size, a time, an owner, a regular expression of find's.</summary>
    internal sealed record Unknown : WalkTest;
}

/// <summary>What of an entry a <see cref="WalkTest.Named"/> test matches.</summary>
internal enum WalkName
{
    /// <summary>Its name.</summary>
    Name,

    /// <summary>Its path as find prints it: the start point as spelled, then the entry's path below it.</summary>
    Printed,

    /// <summary>Its path below the start point.</summary>
    Relative,

    /// <summary>Its absolute path.</summary>
    Absolute,
}

/// <summary>What an action does to the entry it is run on.</summary>
internal enum WalkAction
{
    /// <summary>Nothing to the tree: a print, a listing, a command that removes nothing.</summary>
    None,

    /// <summary>Walks nothing the entry holds, where the walk is not depth-first.</summary>
    Prune,

    /// <summary>Removes or moves the entry with all it holds: a recursive <c>rm</c>, an <c>mv</c>.</summary>
    RemovesWhole,

    /// <summary>Removes the entry where it is a file or a link: an <c>rm</c> that does not recurse, <c>unlink</c>, <c>shred</c>.</summary>
    RemovesFiles,

    /// <summary>Removes the entry where it is an empty directory: <c>rmdir</c>.</summary>
    RemovesEmpty,

    /// <summary>Removes the entry where it is a file, a link or an empty directory: find's <c>-delete</c>.</summary>
    RemovesFilesAndEmpty,
}

public static partial class CommandAnalysis
{
    /// <summary>
    /// The walk the program at <paramref name="start"/> makes that removes what it matches (<see cref="FileWalk"/>): a
    /// <c>find</c>'s or an <c>fd</c>'s; null where it makes none, or one that removes nothing.
    /// </summary>
    /// <param name="analyzed">The command, as the analysis read it.</param>
    /// <param name="start">Where its program stands among its words.</param>
    /// <exception cref="NotSupportedException">The walk removes what it matches by words this does not read.</exception>
    internal static FileWalk? WalkOf(AnalyzedCommand analyzed, int start)
    {
        var words = analyzed.Words;

        if (start < 0 || start >= words.Count)
        {
            return null;
        }

        var program = Name(words[start]);
        IReadOnlyList<string> command = [.. words.Skip(start)];

        return Is(program, "find") ? FindWalk(command)
            : Is(program, "fd", "fdfind") ? FdWalk(command)
            : null;
    }

    /// <summary>
    /// What a command a walk runs on an entry does to it: a remover's kind (<see cref="WalkAction"/>) where a program that
    /// runs among its words (<see cref="RunsAt"/>) removes or moves what it is handed - <c>rm</c>, <c>rmdir</c>,
    /// <c>unlink</c>, <c>shred</c>, <c>mv</c> - and the entry is among its words; nothing else.
    /// </summary>
    /// <param name="executed">The command's words.</param>
    /// <param name="handed">Whether the entry is among its words: a <c>{}</c> find fills in, or the path fd always hands.</param>
    private static WalkAction RemovalBy(IReadOnlyList<string> executed, bool handed)
    {
        if (!handed)
        {
            return WalkAction.None;
        }

        for (var at = 0; at < executed.Count && RunsAt(executed, at); at++)
        {
            var name = Name(executed[at]);

            if (Is(name, "rm"))
            {
                var (options, _, _) = Getopt([.. executed.Skip(at)], RmOptions);

                return options.Any(option => option.Option is "r" or "R" or "recursive") ? WalkAction.RemovesWhole : WalkAction.RemovesFiles;
            }

            if (Is(name, "mv"))
            {
                return WalkAction.RemovesWhole;
            }

            if (Is(name, "rmdir"))
            {
                return WalkAction.RemovesEmpty;
            }

            if (Is(name, "unlink", "shred"))
            {
                return WalkAction.RemovesFiles;
            }
        }

        return WalkAction.None;
    }

    /// <summary>GNU rm's options.</summary>
    private static readonly OptionTable RmOptions = new(
        string.Empty,
        string.Empty,
        [],
        ["interactive", "preserve-root"],
        ["force", "recursive", "dir", "verbose", "one-file-system", "no-preserve-root", "help", "version"]);

    /// <summary>
    /// A <c>find</c>'s walk, read as GNU find reads its words: its own options, its start points - the words before the
    /// first that begins with <c>-</c> or is <c>(</c>, <c>!</c>, <c>)</c> or <c>,</c> - and its expression; null where no
    /// action of it removes what it matches.
    /// </summary>
    /// <exception cref="NotSupportedException">The expression of a find that removes what it matches holds what this does not read.</exception>
    private static FileWalk? FindWalk(IReadOnlyList<string> command)
    {
        var at = 1;

        // How it follows links, its debug options and its optimisation level, before the start points.
        while (at < command.Count && (command[at] is "-H" or "-L" or "-P" or "-D" || (command[at].StartsWith("-O", StringComparison.Ordinal) && command[at].Length > 2)))
        {
            at += command[at] == "-D" ? 2 : 1;
        }

        var points = new List<string>();

        while (at < command.Count && !(command[at].StartsWith('-') || command[at] is "(" or "!" or ")" or ","))
        {
            points.Add(command[at++]);
        }

        IReadOnlyList<string> tokens = [.. command.Skip(at)];

        // A find none of whose actions removes what it matches is no walk of this: its expression is not read.
        if (!FindRemoves(tokens))
        {
            return null;
        }

        var reader = new FindReader(tokens);
        var expression = reader.Read();

        return new FileWalk(points.Count > 0 ? points : ["."], expression, reader.MaxDepth, reader.MinDepth, reader.DepthFirst);
    }

    /// <summary>Whether an action among a find's expression removes what it matches: <c>-delete</c>, or a command <c>-exec</c> and its kind run that removes the entry it is handed.</summary>
    private static bool FindRemoves(IReadOnlyList<string> tokens)
    {
        for (var at = 0; at < tokens.Count; at++)
        {
            if (tokens[at] == "-delete")
            {
                return true;
            }

            if (tokens[at] is "-exec" or "-execdir" or "-ok" or "-okdir")
            {
                var executed = FindExecuted(tokens, ref at);

                if (RemovalBy(executed, executed.Any(word => word.Contains("{}", StringComparison.Ordinal))) is not WalkAction.None)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The command a find's <c>-exec</c> at <paramref name="at"/> runs, to its <c>;</c> or the <c>+</c> after a <c>{}</c>; <paramref name="at"/> left at its end.</summary>
    private static List<string> FindExecuted(IReadOnlyList<string> tokens, ref int at)
    {
        var executed = new List<string>();

        for (at++; at < tokens.Count && tokens[at] != ";" && !(tokens[at] == "+" && executed.Count > 0 && executed[^1] == "{}"); at++)
        {
            executed.Add(tokens[at]);
        }

        return executed;
    }

    /// <summary>
    /// A find's expression read as GNU find parses one: <c>,</c> loosest, then <c>-o</c>, then <c>-a</c> and two side by
    /// side, then <c>!</c>, a parenthesis grouping - each test, action and option with the values it takes, the depth
    /// options kept as the walk's own.
    /// </summary>
    private sealed class FindReader(IReadOnlyList<string> tokens)
    {
        private int _at;

        /// <summary>The deepest it reaches: <c>-maxdepth</c>'s.</summary>
        public int MaxDepth { get; private set; } = int.MaxValue;

        /// <summary>The shallowest it matches at: <c>-mindepth</c>'s.</summary>
        public int MinDepth { get; private set; }

        /// <summary>Whether it walks contents first: <c>-depth</c>, or <c>-delete</c>, which turns it on.</summary>
        public bool DepthFirst { get; private set; }

        /// <summary>The tests find's words ask whose answer this does not tell, each with the value it takes.</summary>
        private static readonly string[] UnknownValued =
        [
            "-size", "-mtime", "-mmin", "-atime", "-amin", "-ctime", "-cmin", "-used", "-newer", "-anewer", "-cnewer", "-perm",
            "-user", "-group", "-uid", "-gid", "-links", "-inum", "-samefile", "-lname", "-ilname", "-fstype", "-context",
            "-xtype",
        ];

        /// <summary>Whether a <c>-regextype</c> was read: a <c>-regex</c> after it is of a kind this does not read.</summary>
        private bool _regexType;

        /// <summary>The tests whose answer this does not tell that take no value.</summary>
        private static readonly string[] UnknownBare = ["-executable", "-readable", "-writable", "-nouser", "-nogroup"];

        /// <summary>The options that change no answer and take no value: each holds.</summary>
        private static readonly string[] BareOptions =
            ["-mount", "-xdev", "-follow", "-noleaf", "-ignore_readdir_race", "-noignore_readdir_race", "-daystart", "-warn", "-nowarn"];

        public WalkNode Read()
        {
            if (tokens.Count == 0)
            {
                return new WalkNode.Act(WalkAction.None, Holds: true);
            }

            var expression = List();

            return _at < tokens.Count
                ? throw new NotSupportedException($"find's expression holds '{tokens[_at]}' where none of its operators stands, which this does not read")
                : expression;
        }

        private WalkNode List()
        {
            var left = Either();

            while (_at < tokens.Count && tokens[_at] == ",")
            {
                _at++;
                left = new WalkNode.Both(left, Either());
            }

            return left;
        }

        private WalkNode Either()
        {
            var left = All();

            while (_at < tokens.Count && tokens[_at] is "-o" or "-or")
            {
                _at++;
                left = new WalkNode.Or(left, All());
            }

            return left;
        }

        private WalkNode All()
        {
            var left = Negated();

            while (_at < tokens.Count && tokens[_at] is not ("-o" or "-or" or "," or ")"))
            {
                if (tokens[_at] is "-a" or "-and")
                {
                    _at++;
                }

                left = new WalkNode.And(left, Negated());
            }

            return left;
        }

        private WalkNode Negated()
        {
            if (_at < tokens.Count && tokens[_at] is "!" or "-not")
            {
                _at++;
                return new WalkNode.Not(Negated());
            }

            return Primary();
        }

        private WalkNode Primary()
        {
            if (_at >= tokens.Count)
            {
                throw new NotSupportedException("find's expression ends where a test or an action is wanted, which this does not read");
            }

            var token = tokens[_at++];

            if (token == "(")
            {
                var inner = List();

                if (_at >= tokens.Count || tokens[_at] != ")")
                {
                    throw new NotSupportedException("a parenthesis of find's expression does not close, which this does not read");
                }

                _at++;
                return inner;
            }

            switch (token)
            {
                case "-name" or "-iname":
                    return Named(token, WalkName.Name);
                case "-path" or "-wholename" or "-ipath" or "-iwholename":
                    return Named(token, WalkName.Printed);
                case "-type":
                    return new WalkNode.Test(new WalkTest.Kind(Value(token)));
                case "-empty":
                    return new WalkNode.Test(new WalkTest.Empty());
                case "-true" or "-false":
                    return new WalkNode.Test(new WalkTest.Constant(token == "-true"));
                case "-maxdepth" or "-mindepth":
                    var depth = int.TryParse(Value(token), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var levels)
                        ? levels
                        : throw new NotSupportedException($"find's '{token}' is given no number of levels, which this does not read");

                    if (token == "-maxdepth")
                    {
                        MaxDepth = depth;
                    }
                    else
                    {
                        MinDepth = depth;
                    }

                    return Holds();
                case "-depth" or "-d":
                    DepthFirst = true;
                    return Holds();
                case "-regex" or "-iregex":
                    var regex = Value(token);

                    // Emacs' expressions, find's own, read as .NET reads them where both read them alike: whole paths matched.
                    return new WalkNode.Test(!_regexType && ReadAlike(regex)
                        ? new WalkTest.Named("^(?:" + regex + ")$", IgnoreCase: token == "-iregex", Regular: true, WalkName.Printed)
                        : new WalkTest.Unknown());
                case "-regextype" or "-files0-from":
                    _regexType |= token == "-regextype";
                    _ = Value(token);
                    return Holds();
                case "-print" or "-print0" or "-ls" or "-quit":
                    return new WalkNode.Act(WalkAction.None, Holds: true);
                case "-printf" or "-fprint" or "-fprint0" or "-fls":
                    _ = Value(token);
                    return new WalkNode.Act(WalkAction.None, Holds: true);
                case "-fprintf":
                    _ = Value(token);
                    _ = Value(token);
                    return new WalkNode.Act(WalkAction.None, Holds: true);
                case "-prune":
                    return new WalkNode.Act(WalkAction.Prune, Holds: true);
                case "-delete":
                    DepthFirst = true;
                    return new WalkNode.Act(WalkAction.RemovesFilesAndEmpty, Holds: true);
                case "-exec" or "-execdir" or "-ok" or "-okdir":
                    _at--;

                    var start = _at;
                    var executed = FindExecuted(tokens, ref _at);

                    if (_at >= tokens.Count)
                    {
                        throw new NotSupportedException($"find's '{tokens[start]}' runs a command that does not end, which this does not read");
                    }

                    _at++;
                    return new WalkNode.Act(RemovalBy(executed, executed.Any(word => word.Contains("{}", StringComparison.Ordinal))), Holds: false);
            }

            if (UnknownValued.Contains(token, StringComparer.Ordinal) || NewerThan().IsMatch(token))
            {
                _ = Value(token);
                return new WalkNode.Test(new WalkTest.Unknown());
            }

            if (UnknownBare.Contains(token, StringComparer.Ordinal))
            {
                return new WalkNode.Test(new WalkTest.Unknown());
            }

            return BareOptions.Contains(token, StringComparer.Ordinal)
                ? Holds()
                : throw new NotSupportedException($"'{token}' is a test or an action of find's this does not read");
        }

        private WalkNode.Test Named(string token, WalkName of)
            => new(new WalkTest.Named(Value(token), IgnoreCase: token.StartsWith("-i", StringComparison.Ordinal), Regular: false, of));

        private static WalkNode.Test Holds() => new(new WalkTest.Constant(true));

        /// <summary>
        /// Whether Emacs and .NET read <paramref name="regex"/> alike: no group, alternative or count of either's - an
        /// unescaped <c>|</c>, parenthesis or brace, or one escaped - no character class by name, and no escape but of a
        /// character each reads as itself.
        /// </summary>
        private static bool ReadAlike(string regex)
        {
            if (regex.Contains("[:", StringComparison.Ordinal))
            {
                return false;
            }

            for (var at = 0; at < regex.Length; at++)
            {
                if (regex[at] is '|' or '(' or ')' or '{' or '}')
                {
                    return false;
                }

                if (regex[at] == '\\' && (++at >= regex.Length || !".*+?[]^$\\/-".Contains(regex[at], StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }

        private string Value(string primary)
            => _at < tokens.Count ? tokens[_at++] : throw new NotSupportedException($"find's '{primary}' is given no value, which this does not read");
    }

    /// <summary>find's <c>-newerXY</c>, which compares a time of the entry's with a time of a file's or a date.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^-newer[aBcmt][aBcmt]$")]
    private static partial System.Text.RegularExpressions.Regex NewerThan();

    /// <summary>fd's short options that take a value, in a cluster's rest or the next word.</summary>
    private const string FdShortValued = "tedESocjxX";

    /// <summary>fd's long options that take a value, each with what it is to the walk.</summary>
    private static readonly string[] FdLongValued =
    [
        "type", "extension", "max-depth", "maxdepth", "min-depth", "exact-depth", "exclude", "size", "changed-within",
        "changed-before", "change-newer-than", "change-older-than", "newer", "older", "owner", "ignore-file", "color", "threads",
        "max-results", "base-directory", "path-separator", "batch-size", "max-buffer-time", "search-path", "format", "hyperlink",
    ];

    /// <summary>fd's long options that take no value.</summary>
    private static readonly string[] FdLongFlags =
    [
        "hidden", "no-hidden", "no-ignore", "ignore", "no-ignore-vcs", "ignore-vcs", "no-require-git", "no-ignore-parent",
        "unrestricted", "case-sensitive", "ignore-case", "glob", "regex", "fixed-strings", "and", "absolute-path",
        "relative-path", "list-details", "follow", "no-follow", "full-path", "print0", "prune", "one-file-system", "show-errors",
        "quiet", "has-results", "strip-cwd-prefix", "no-global-ignore-file",
    ];

    /// <summary>
    /// An <c>fd</c>'s walk, read as fd reads its words: its pattern - a regular expression searched for in each entry's
    /// name, its whole path with <c>-p</c>, a glob with <c>-g</c>, a text with <c>-F</c>, its case smart - its paths, its
    /// filters of kind and extension, its depths, and the entries it walks past: a hidden one - its name begins with a
    /// dot - without <c>-H</c>, <c>-u</c>, and each an <c>-E</c> excludes, none of whose contents it walks. It hands
    /// each entry at depth 1 or more to the command <c>-x</c> or <c>-X</c> runs; null where that removes nothing.
    /// </summary>
    /// <exception cref="NotSupportedException">The fd removes what it matches by words this does not read.</exception>
    private static FileWalk? FdWalk(IReadOnlyList<string> command)
    {
        string? pattern = null;
        var paths = new List<string>();
        var (hidden, glob, fixedText, fullPath) = (false, false, false, false);
        bool? ignoreCase = null;
        var kinds = new List<WalkNode>();
        var extensions = new List<WalkNode>();
        var excluded = new List<string>();
        var unknown = false;
        var (maxDepth, minDepth) = (int.MaxValue, 1);
        var removal = WalkAction.None;
        var options = true;

        void Given(string option, string? value)
        {
            switch (option)
            {
                case "H" or "hidden" or "u" or "unrestricted":
                    hidden = true;
                    break;
                case "no-hidden":
                    hidden = false;
                    break;
                case "g" or "glob":
                    glob = true;
                    break;
                case "regex":
                    glob = false;
                    break;
                case "F" or "fixed-strings":
                    fixedText = true;
                    break;
                case "p" or "full-path":
                    fullPath = true;
                    break;
                case "i" or "ignore-case":
                    ignoreCase = true;
                    break;
                case "s" or "case-sensitive":
                    ignoreCase = false;
                    break;
                case "t" or "type":
                    kinds.Add(value switch
                    {
                        "f" or "file" => new WalkNode.Test(new WalkTest.Kind("f")),
                        "d" or "dir" or "directory" => new WalkNode.Test(new WalkTest.Kind("d")),
                        "l" or "symlink" => new WalkNode.Test(new WalkTest.Kind("l")),
                        "e" or "empty" => new WalkNode.Test(new WalkTest.Empty()),
                        _ => new WalkNode.Test(new WalkTest.Unknown()),
                    });
                    break;
                case "e" or "extension":
                    extensions.Add(new WalkNode.Test(new WalkTest.Named("*." + value?.TrimStart('.'), IgnoreCase: true, Regular: false, WalkName.Name)));
                    break;
                case "E" or "exclude":
                    excluded.Add(value ?? string.Empty);
                    break;
                case "d" or "max-depth" or "maxdepth" or "min-depth" or "exact-depth":
                    var levels = int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var depth)
                        ? depth
                        : throw new NotSupportedException($"fd's '{option}' is given no number of levels, which this does not read");
                    maxDepth = option is "min-depth" ? maxDepth : levels;
                    minDepth = option is "min-depth" or "exact-depth" ? Math.Max(1, levels) : minDepth;
                    break;
                case "search-path":
                    paths.Add(value ?? string.Empty);
                    break;
                case "S" or "size" or "changed-within" or "changed-before" or "change-newer-than" or "change-older-than" or "newer" or "older" or "o" or "owner":
                    unknown = true;
                    break;
            }
        }

        // The command -x or -X runs from its first word, glued or the next: to a ';', a remover of it removing each entry.
        void Executed(string? first, ref int at)
        {
            var executed = new List<string>(first is { Length: > 0 } ? [first] : []);

            for (at++; at < command.Count && command[at] != ";"; at++)
            {
                executed.Add(command[at]);
            }

            if (executed.Contains("{//}", StringComparer.Ordinal))
            {
                throw new NotSupportedException("fd's '{//}' hands the command the directory that holds each entry, which this does not read");
            }

            removal = RemovalBy(executed, handed: true) is var by and not WalkAction.None ? by : removal;
        }

        for (var at = 1; at < command.Count; at++)
        {
            var word = command[at];

            if (options && word == "--")
            {
                options = false;
                continue;
            }

            if (!options || word.Length < 2 || word[0] != '-')
            {
                if (pattern is null)
                {
                    pattern = word;
                }
                else
                {
                    paths.Add(word);
                }

                continue;
            }

            if (word is "-h" or "--help" or "-V" or "--version")
            {
                return null;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = word.IndexOf('=', StringComparison.Ordinal);
                var spelled = equals < 0 ? word[2..] : word[2..equals];

                // --exec and --exec-batch run the words after them, to a ';', each entry handed to them.
                if (spelled is "exec" or "exec-batch")
                {
                    Executed(equals >= 0 ? word[(equals + 1)..] : null, ref at);
                }
                else if (FdLongValued.Contains(spelled, StringComparer.Ordinal))
                {
                    Given(spelled, equals >= 0 ? word[(equals + 1)..] : at + 1 < command.Count ? command[++at] : null);
                }
                else if (FdLongFlags.Contains(spelled, StringComparer.Ordinal))
                {
                    Given(spelled, null);
                }
                else
                {
                    throw new NotSupportedException($"'{word}' is an option of fd's this does not read, which may take the word after it as its value");
                }

                continue;
            }

            for (var letter = 1; letter < word.Length; letter++)
            {
                var option = word[letter].ToString();

                // -x and -X run the words after them - the cluster's rest first - to a ';', each entry handed to them.
                if (word[letter] is 'x' or 'X')
                {
                    Executed(letter + 1 < word.Length ? word[(letter + 1)..] : null, ref at);
                    break;
                }

                if (FdShortValued.Contains(word[letter], StringComparison.Ordinal))
                {
                    Given(option, letter + 1 < word.Length ? word[(letter + 1)..] : at + 1 < command.Count ? command[++at] : null);
                    break;
                }

                if (!"HIugpFisLal01q".Contains(word[letter], StringComparison.Ordinal))
                {
                    throw new NotSupportedException($"'{word}' gives an option of fd's this does not read, which may take the word after it as its value");
                }

                Given(option, null);
            }
        }

        if (removal is WalkAction.None)
        {
            return null;
        }

        // What it asks of an entry it does not walk past: its pattern, every kind and extension filter, and what this does not tell.
        var asked = new List<WalkNode>();

        if (pattern is { Length: > 0 })
        {
            var regular = !glob;
            var text = fixedText && regular ? System.Text.RegularExpressions.Regex.Escape(pattern) : pattern;

            asked.Add(new WalkNode.Test(new WalkTest.Named(text, ignoreCase ?? !pattern.Any(char.IsUpper), regular, fullPath ? WalkName.Absolute : WalkName.Name)));
        }

        if (kinds.Count > 0)
        {
            asked.Add(kinds.Aggregate((left, right) => new WalkNode.Or(left, right)));
        }

        if (extensions.Count > 0)
        {
            asked.Add(extensions.Aggregate((left, right) => new WalkNode.Or(left, right)));
        }

        if (unknown)
        {
            asked.Add(new WalkNode.Test(new WalkTest.Unknown()));
        }

        WalkNode expression = asked.Append(new WalkNode.Act(removal, Holds: false)).Aggregate((left, right) => new WalkNode.And(left, right));

        // What it walks past, nothing it holds walked: each entry an -E excludes - by its name, or by its path below the
        // start where the glob holds a separator - and, without -H, a hidden one.
        foreach (var exclude in excluded.Where(exclude => exclude.Trim('/').Length > 0))
        {
            var trimmed = exclude.Trim('/');
            var passed = new WalkNode.Test(new WalkTest.Named(trimmed, IgnoreCase: false, Regular: false, trimmed.Contains('/', StringComparison.Ordinal) ? WalkName.Relative : WalkName.Name));

            expression = new WalkNode.Or(new WalkNode.And(passed, new WalkNode.Act(WalkAction.Prune, Holds: true)), expression);
        }

        if (!hidden)
        {
            var dotted = new WalkNode.Test(new WalkTest.Named(".*", IgnoreCase: false, Regular: false, WalkName.Name));

            expression = new WalkNode.Or(new WalkNode.And(dotted, new WalkNode.Act(WalkAction.Prune, Holds: true)), expression);
        }

        return new FileWalk(paths.Count > 0 ? paths : ["."], expression, maxDepth, minDepth, DepthFirst: false);
    }
}
