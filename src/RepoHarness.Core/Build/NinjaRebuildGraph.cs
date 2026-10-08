using System.Text;
using System.Text.RegularExpressions;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Build;

/// <summary>One object's dependency record, as <c>ninja -t deps</c> printed it.</summary>
/// <param name="Object">The object, as ninja named it.</param>
/// <param name="Count">How many dependencies its header line counted.</param>
/// <param name="Dependencies">The dependencies listed beneath it, each as ninja spelled it.</param>
internal sealed record DepsRecord(string Object, int Count, List<string> Dependencies);

/// <summary>
/// A build directory's objects as ninja rebuilds them: what each one's compile surely includes,
/// and every file a change to which rebuilds it. Every path is held as the build directory
/// resolves it, and compared as the file system holding the build compares names: a header a
/// unit names in another case is, where that file system finds it by either, the one its
/// precompiled header's object recorded.
/// </summary>
internal sealed partial class NinjaRebuildGraph(
    string buildDirectory,
    NinjaManifest manifest,
    IReadOnlyDictionary<string, DepsRecord> records,
    IFileSystem fileSystem,
    StringComparer paths)
{
    /// <summary>
    /// A header line of <c>ninja -t deps</c>: the object, then how many dependencies it recorded.
    /// Whether the record is valid or stale is deliberately not read — a valid record of zero
    /// dependencies is exactly the broken state this looks for.
    /// </summary>
    /// <remarks>
    /// Read up to the first <c>: #deps</c>, so an object whose path holds a space is still counted;
    /// a header starts its line, and the dependencies listed beneath it are indented, so none of them
    /// is ever read as an object.
    /// </remarks>
    [GeneratedRegex(@"^(?<object>\S.*?):\s+#deps\s+(?<count>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DepsHeader { get; }

    /// <summary>A backslash ending a line, which joins it to the next before anything else is read.</summary>
    [GeneratedRegex(@"\\\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex Splice { get; }

    /// <summary>A directive line: its name - <c>include</c>, <c>ifdef</c> - and what follows the name.</summary>
    [GeneratedRegex(@"^\s*#\s*(?<name>\w+)(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Directive { get; }

    /// <summary>What a quoted include names: <c>"util.h"</c>.</summary>
    [GeneratedRegex(@"^\s*""(?<header>[^""\r\n]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedHeader { get; }

    /// <summary>A condition asking whether a name is defined: <c>defined(NAME)</c> or <c>defined NAME</c>.</summary>
    [GeneratedRegex(@"^defined\s*\(\s*(?<name>\w+)\s*\)$|^defined\s+(?<name>\w+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DefinedCondition { get; }

    /// <summary>A condition that is a decimal number: <c>0</c>, <c>1</c>.</summary>
    [GeneratedRegex(@"^(?<digits>\d+)[uUlL]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberCondition { get; }

    /// <summary>The name an <c>#ifdef</c> or an <c>#ifndef</c> asks about.</summary>
    [GeneratedRegex(@"^\s*(?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex AskedName { get; }

    /// <summary>The extensions cl compiles as C++ when no <c>/TP</c> or <c>/TC</c> says otherwise.</summary>
    private static readonly HashSet<string> CPlusPlusExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".cxx", ".cc", ".c++", ".cp", ".ixx", ".cppm",
    };

    /// <summary>
    /// Runs <c>ninja -t deps</c> in <paramref name="buildDirectory"/> as the build ran it, and reads every record it
    /// lists.
    /// </summary>
    /// <param name="processRunner">Runs ninja.</param>
    /// <param name="buildDirectory">The build directory, which ninja runs in.</param>
    /// <param name="appendToPath">The directories the build appended to its PATH, which ninja is looked up on too.</param>
    /// <param name="program">The ninja the build ran, as its configuration recorded it, or <see langword="null"/> to look one up.</param>
    /// <param name="environment">The environment the build's phases ran in.</param>
    /// <param name="cancellationToken">Stops ninja.</param>
    /// <exception cref="HarnessException">
    /// ninja could not be started, did not answer, failed, or answered with nothing. An empty answer is a failure and
    /// never a pass - it is indistinguishable from every object having recorded its headers, and from no object
    /// depending on anything.
    /// </exception>
    internal static async Task<IReadOnlyDictionary<string, DepsRecord>> ReadRecordsAsync(
        IProcessRunner processRunner,
        string buildDirectory,
        IReadOnlyList<string> appendToPath,
        string? program,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processRunner);

        ProcessResult result;

        try
        {
            result = await processRunner
                .RunAsync(
                    NinjaDependencyCheck.Request(buildDirectory, ["-C", buildDirectory, "-t", "deps"], appendToPath, program, environment),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            // The check could not run, which says nothing about the build it was to read: reported
            // as the check that did not run, never as the build failing, and never as a pass.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' could not be started for '{buildDirectory}': {ex.Message}",
                ex);
        }

        if (result.TimedOut)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' did not answer within {NinjaDependencyCheck.Budget.TotalMinutes:0} minutes for '{buildDirectory}'.");
        }

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' failed for '{buildDirectory}' (exit {result.ExitCode}).");
        }

        var records = ReadRecords(result.StandardOutput.Split('\n'));

        if (records.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' listed no objects for '{buildDirectory}'. An empty answer is "
                + "indistinguishable from every object having recorded its headers, so it is never read as one.");
        }

        return records;
    }

    /// <summary>
    /// <paramref name="command"/> split into arguments as cl's C runtime splits its command line: at
    /// spaces and tabs outside quotes, a quote only opening or closing quoting - two inside quotes
    /// being one - and a backslash literal except before a quote, where each pair of them is one
    /// backslash and an odd one left over makes the quote literal.
    /// </summary>
    private static List<string> CommandLineArguments(string command)
    {
        var arguments = new List<string>();
        var argument = new StringBuilder();
        var quoted = false;
        var started = false;

        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];

            if (character == '\\')
            {
                var backslashes = 0;

                while (index < command.Length && command[index] == '\\')
                {
                    backslashes++;
                    index++;
                }

                if (index < command.Length && command[index] == '"')
                {
                    argument.Append('\\', backslashes / 2);

                    if (backslashes % 2 == 1)
                    {
                        argument.Append('"');
                    }
                    else
                    {
                        index--;
                    }
                }
                else
                {
                    argument.Append('\\', backslashes);
                    index--;
                }

                started = true;
            }
            else if (character == '"')
            {
                if (quoted && index + 1 < command.Length && command[index + 1] == '"')
                {
                    argument.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                started = true;
            }
            else if (character is ' ' or '\t' && !quoted)
            {
                if (started)
                {
                    arguments.Add(argument.ToString());
                    argument.Clear();
                    started = false;
                }
            }
            else
            {
                argument.Append(character);
                started = true;
            }
        }

        if (started)
        {
            arguments.Add(argument.ToString());
        }

        return arguments;
    }

    /// <summary>
    /// The language cl compiles <paramref name="source"/> in: C++ under <c>/TP</c> and C under
    /// <c>/TC</c>, the last one given deciding; otherwise C for a <c>.c</c> file and C++ for one with a
    /// C++ extension. Unknown for any other, a resource script's among them.
    /// </summary>
    private static Language LanguageOf(IReadOnlyList<string> arguments, string source)
    {
        if (arguments.LastOrDefault(argument => argument is "/TP" or "-TP" or "/TC" or "-TC") is { } told)
        {
            return told.EndsWith('P') ? Language.CPlusPlus : Language.C;
        }

        var extension = Path.GetExtension(source);

        return string.Equals(extension, ".c", StringComparison.OrdinalIgnoreCase) ? Language.C
            : CPlusPlusExtensions.Contains(extension) ? Language.CPlusPlus
            : Language.Unknown;
    }

    /// <summary>
    /// The headers <paramref name="arguments"/> force-include: <c>/FI</c> or <c>-FI</c>, with its path
    /// joined to it or as the next argument.
    /// </summary>
    private static IEnumerable<string> ForceIncluded(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (argument.Length < 3 || argument[0] is not ('/' or '-') || argument[1] != 'F' || argument[2] != 'I')
            {
                continue;
            }

            if (argument.Length > 3)
            {
                yield return argument[3..];
            }
            else if (index + 1 < arguments.Count)
            {
                yield return arguments[++index];
            }
        }
    }

    /// <summary>
    /// The headers <paramref name="text"/> names by a quoted include that a compile in
    /// <paramref name="language"/> surely compiles: outside every comment, and in no conditional block
    /// that may not be compiled.
    /// </summary>
    private static IEnumerable<string> SurelyCompiledQuotedIncludes(string text, Language language)
    {
        var blocks = new Stack<(Truth Compiled, Truth Taken)>();

        foreach (var line in Uncommented(text).Split('\n'))
        {
            if (Directive.Match(line) is not { Success: true } directive)
            {
                continue;
            }

            var rest = directive.Groups["rest"].Value;

            switch (directive.Groups["name"].Value)
            {
                case "if":
                    Open(Condition(rest, language));
                    break;

                case "ifdef":
                    Open(Defined(AskedName.Match(rest).Groups["name"].Value, language));
                    break;

                case "ifndef":
                    Open(Not(Defined(AskedName.Match(rest).Groups["name"].Value, language)));
                    break;

                case "elif":
                    Otherwise(Condition(rest, language));
                    break;

                case "elifdef":
                    Otherwise(Defined(AskedName.Match(rest).Groups["name"].Value, language));
                    break;

                case "elifndef":
                    Otherwise(Not(Defined(AskedName.Match(rest).Groups["name"].Value, language)));
                    break;

                case "else":
                    Otherwise(Truth.True);
                    break;

                case "endif":
                    blocks.TryPop(out _);
                    break;

                case "include" when blocks.All(block => block.Compiled == Truth.True)
                    && QuotedHeader.Match(rest) is { Success: true } include:
                    yield return include.Groups["header"].Value;
                    break;
            }
        }

        void Open(Truth condition) => blocks.Push((condition, condition));

        // A later branch is compiled where no earlier one was, and its own condition holds.
        void Otherwise(Truth condition)
        {
            if (blocks.TryPop(out var block))
            {
                var compiled = And(Not(block.Taken), condition);

                blocks.Push((compiled, Or(block.Taken, compiled)));
            }
        }
    }

    /// <summary>
    /// What an <c>#if</c> or <c>#elif</c> asks, where the language alone answers it: a decimal number;
    /// whether <c>__cplusplus</c> is defined; <c>__cplusplus</c> itself, which a C++ compile defines
    /// and a C one reads as <c>0</c>; or the negation of one of these. Anything else is unknown.
    /// </summary>
    private static Truth Condition(string expression, Language language)
    {
        var condition = expression.Trim();

        if (condition.StartsWith('!'))
        {
            return Not(Condition(condition[1..], language));
        }

        if (NumberCondition.Match(condition) is { Success: true } number)
        {
            return number.Groups["digits"].Value.Trim('0').Length > 0 ? Truth.True : Truth.False;
        }

        if (DefinedCondition.Match(condition) is { Success: true } defined)
        {
            return Defined(defined.Groups["name"].Value, language);
        }

        return condition == "__cplusplus" ? Defined(condition, language) : Truth.Unknown;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is defined in a compile in <paramref name="language"/>, where the
    /// language alone tells: <c>__cplusplus</c> is, in C++, and is not, in C. Any other name is unknown.
    /// </summary>
    private static Truth Defined(string name, Language language)
        => name != "__cplusplus" ? Truth.Unknown
            : language switch
            {
                Language.CPlusPlus => Truth.True,
                Language.C => Truth.False,
                _ => Truth.Unknown,
            };

    private static Truth Not(Truth truth)
        => truth switch
        {
            Truth.True => Truth.False,
            Truth.False => Truth.True,
            _ => Truth.Unknown,
        };

    private static Truth And(Truth left, Truth right)
        => left == Truth.False || right == Truth.False ? Truth.False
            : left == Truth.True && right == Truth.True ? Truth.True
            : Truth.Unknown;

    private static Truth Or(Truth left, Truth right)
        => left == Truth.True || right == Truth.True ? Truth.True
            : left == Truth.False && right == Truth.False ? Truth.False
            : Truth.Unknown;

    /// <summary>
    /// <paramref name="text"/> as the preprocessor reads its directives: every line a backslash ends
    /// joined to the next first, then every comment a space. A comment opener inside a literal, a raw
    /// string or a number is none - <c>0x1'0000</c> opens no character literal - and a literal left
    /// open is closed by the end of its line; a raw string is a space, lines and all.
    /// </summary>
    private static string Uncommented(string text)
    {
        var spliced = Splice.Replace(text, string.Empty);
        var uncommented = new StringBuilder(spliced.Length);
        var index = 0;

        while (index < spliced.Length)
        {
            var character = spliced[index];
            var next = index + 1 < spliced.Length ? spliced[index + 1] : '\0';

            if (character == '/' && next == '*')
            {
                var end = spliced.IndexOf("*/", index + 2, StringComparison.Ordinal);

                index = end < 0 ? spliced.Length : end + 2;
                uncommented.Append(' ');
            }
            else if (character == '/' && next == '/')
            {
                var end = spliced.IndexOf('\n', index);

                index = end < 0 ? spliced.Length : end;
            }
            else if (character == '"' && RawStringEnd(spliced, index) is { } rawEnd)
            {
                index = rawEnd;
                uncommented.Append(' ');
            }
            else if (character is '"' or '\'')
            {
                var start = index++;

                while (index < spliced.Length && spliced[index] != character && spliced[index] != '\n')
                {
                    index += spliced[index] == '\\' && index + 1 < spliced.Length && spliced[index + 1] != '\n' ? 2 : 1;
                }

                if (index < spliced.Length && spliced[index] == character)
                {
                    index++;
                }

                uncommented.Append(spliced, start, index - start);
            }
            else if (char.IsAsciiDigit(character) && (index == 0 || !IsIdentifierCharacter(spliced[index - 1])))
            {
                var start = index++;

                while (index < spliced.Length
                    && (IsIdentifierCharacter(spliced[index])
                        || spliced[index] == '.'
                        || (spliced[index] == '\'' && index + 1 < spliced.Length && IsIdentifierCharacter(spliced[index + 1]))
                        || (spliced[index] is '+' or '-' && spliced[index - 1] is 'e' or 'E' or 'p' or 'P')))
                {
                    index++;
                }

                uncommented.Append(spliced, start, index - start);
            }
            else
            {
                uncommented.Append(character);
                index++;
            }
        }

        return uncommented.ToString();
    }

    /// <summary>
    /// Where the raw string whose opening quote is at <paramref name="quote"/> ends - <c>R"x(...)x"</c>,
    /// with any of its prefixes - or <see langword="null"/> where that quote opens no raw string.
    /// </summary>
    private static int? RawStringEnd(string text, int quote)
    {
        var prefix = quote;

        while (prefix > 0 && IsIdentifierCharacter(text[prefix - 1]))
        {
            prefix--;
        }

        var open = text.IndexOf('(', quote + 1);

        if (text[prefix..quote] is not ("R" or "u8R" or "uR" or "UR" or "LR") || open < 0 || open - quote - 1 > 16)
        {
            return null;
        }

        var delimiter = text[(quote + 1)..open];

        if (delimiter.Any(character => character is ' ' or ')' or '\\' or '\t' or '\r' or '\n' or '"'))
        {
            return null;
        }

        var close = text.IndexOf(")" + delimiter + "\"", open + 1, StringComparison.Ordinal);

        return close < 0 ? text.Length : close + delimiter.Length + 2;
    }

    private static bool IsIdentifierCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

    /// <summary>
    /// Every object <c>ninja -t deps</c> listed, keyed as ninja canonicalizes its path: the name it
    /// printed, how many dependencies it counted, and the ones it listed beneath, one to an indented line.
    /// </summary>
    internal static Dictionary<string, DepsRecord> ReadRecords(IEnumerable<string> lines)
    {
        var records = new Dictionary<string, DepsRecord>(StringComparer.Ordinal);
        DepsRecord? current = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var match = DepsHeader.Match(line);

            if (match.Success)
            {
                current = new DepsRecord(
                    match.Groups["object"].Value,
                    int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    []);
                records[NinjaManifest.Normalize(current.Object)] = current;
            }
            else if (current is not null && line.Length > 0 && line[0] is ' ' or '\t')
            {
                current.Dependencies.Add(line.TrimStart());
            }
            else
            {
                current = null;
            }
        }

        return records;
    }

    /// <summary>The language cl compiles a unit in, which decides whether <c>__cplusplus</c> is defined.</summary>
    private enum Language
    {
        Unknown,
        C,
        CPlusPlus,
    }

    /// <summary>Whether a condition holds, where the check can tell.</summary>
    private enum Truth
    {
        False,
        True,
        Unknown,
    }

    /// <summary>What each file surely includes in each language, read once however many units read it.</summary>
    private readonly Dictionary<Language, Dictionary<string, List<string>?>> _includes = [];

    /// <summary>What a change to rebuilds what each build line builds, worked out once however many objects reach it.</summary>
    private readonly Dictionary<NinjaEdge, HashSet<string>> _rebuiltFor = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// What a change to rebuilds what each build line builds, following what each build recorded to the build line that
    /// builds it too; worked out once however many objects reach it.
    /// </summary>
    private readonly Dictionary<NinjaEdge, HashSet<string>> _dependedOn = new(ReferenceEqualityComparer.Instance);

    /// <summary>The manifest the build lines are read from.</summary>
    public NinjaManifest Manifest => manifest;

    /// <inheritdoc cref="NinjaManifest.EdgeFor"/>
    public NinjaEdge? EdgeFor(string output) => manifest.EdgeFor(output);

    /// <summary>
    /// Whether a change to <paramref name="path"/> rebuilds what <paramref name="edge"/> builds, as ninja rebuilds it: the
    /// file is an input of its build line or recorded by its build, or a change to it rebuilds a build line building one of
    /// its inputs, or one of the files its build recorded - a precompiled header holding it, a header a command generates
    /// from it - however far back. Compared as the file system holding the build compares names.
    /// </summary>
    /// <param name="edge">The build line.</param>
    /// <param name="path">The file, absolute, or relative to the build directory.</param>
    public bool DependsOn(NinjaEdge edge, string path)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(path);

        return Resolved(path) is { } file && DependedOn(edge).Contains(file);
    }

    /// <summary>
    /// The headers ninja keeps that <paramref name="edge"/>'s compile surely includes: those its
    /// command force-includes, and those <paramref name="source"/> and each of those headers
    /// include, as <see cref="SurelyCompiledQuotedIncludes"/> reads them, resolving beside them.
    /// <see langword="null"/> where one of those files is not there, or cannot be read, so its
    /// object is never excused.
    /// </summary>
    public List<string>? SurelyIncluded(NinjaEdge edge, string source)
    {
        if (Resolved(source) is not { } file)
        {
            return null;
        }

        var arguments = CommandLineArguments(edge.Command);
        var language = LanguageOf(arguments, file);
        var directory = Path.GetDirectoryName(file) ?? string.Empty;
        var forced = ForceIncluded(arguments).Select(header => Beside(directory, header)).OfType<string>().Where(Kept).ToList();
        var included = new List<string>(forced);

        foreach (var reading in forced.Prepend(file))
        {
            if (Includes(reading, language) is not { } headers)
            {
                return null;
            }

            included.AddRange(headers);
        }

        return included;
    }

    /// <summary>
    /// Whether ninja rebuilds what <paramref name="edge"/> builds for each of <paramref name="headers"/>:
    /// each is an input of its build line or recorded by its build, or is rebuilt for by what
    /// builds one of its inputs, however far back.
    /// </summary>
    public bool RebuildsFor(NinjaEdge edge, List<string> headers)
    {
        if (headers.Count == 0)
        {
            return true;
        }

        var own = Own(edge);
        var through = Producers(edge).Select(RebuiltFor).ToList();

        return headers.All(header => own.Contains(header) || through.Exists(rebuiltFor => rebuiltFor.Contains(header)));
    }

    /// <summary>Everything a change to which rebuilds what <paramref name="edge"/> builds, however far back.</summary>
    private HashSet<string> RebuiltFor(NinjaEdge edge) => RebuiltFor(edge, _rebuiltFor, Producers);

    /// <summary>
    /// Everything a change to which rebuilds what <paramref name="edge"/> builds, however far back, through the build lines
    /// that build its inputs and those that build what its build recorded.
    /// </summary>
    private HashSet<string> DependedOn(NinjaEdge edge) => RebuiltFor(edge, _dependedOn, line => Producers(line).Concat(RecordedProducers(line)));

    /// <summary>
    /// Everything a change to which rebuilds what <paramref name="edge"/> builds: what it reads itself, and what rebuilds each
    /// build line <paramref name="producers"/> names for it, however far back, each worked out once in <paramref name="known"/>.
    /// </summary>
    private HashSet<string> RebuiltFor(NinjaEdge edge, Dictionary<NinjaEdge, HashSet<string>> known, Func<NinjaEdge, IEnumerable<NinjaEdge>> producers)
    {
        if (known.TryGetValue(edge, out var found))
        {
            return found;
        }

        // Held before it is complete, so a cycle - which ninja never builds - leads nowhere.
        var rebuiltFor = Own(edge);
        known[edge] = rebuiltFor;

        foreach (var producer in producers(edge))
        {
            rebuiltFor.UnionWith(RebuiltFor(producer, known, producers));
        }

        return rebuiltFor;
    }

    /// <summary>What a change to rebuilds what <paramref name="edge"/> builds by itself: its inputs, and what its build recorded.</summary>
    private HashSet<string> Own(NinjaEdge edge)
    {
        var own = new HashSet<string>(paths);

        foreach (var output in edge.Outputs)
        {
            foreach (var dependency in records.GetValueOrDefault(NinjaManifest.Normalize(output))?.Dependencies ?? [])
            {
                if (Resolved(dependency) is { } path)
                {
                    own.Add(path);
                }
            }
        }

        foreach (var input in edge.Inputs)
        {
            if (Resolved(input) is { } path)
            {
                own.Add(path);
            }
        }

        return own;
    }

    /// <summary>The build lines that build an input of <paramref name="edge"/>.</summary>
    private IEnumerable<NinjaEdge> Producers(NinjaEdge edge) => edge.Inputs.Select(manifest.EdgeFor).OfType<NinjaEdge>();

    /// <summary>
    /// The build lines that build a file <paramref name="edge"/>'s build recorded - a header a command generates, which
    /// the compile found, and ninja rebuilds the object for once it changes - each recorded path read from the build
    /// directory, as the manifest names its outputs.
    /// </summary>
    private IEnumerable<NinjaEdge> RecordedProducers(NinjaEdge edge)
        => edge.Outputs
            .SelectMany(output => records.GetValueOrDefault(NinjaManifest.Normalize(output))?.Dependencies ?? [])
            .Select(dependency => Resolved(dependency) is { } file ? manifest.EdgeFor(Path.GetRelativePath(buildDirectory, file)) : null)
            .OfType<NinjaEdge>();

    /// <summary>
    /// The headers ninja keeps that <paramref name="file"/> surely includes in <paramref name="language"/>,
    /// or <see langword="null"/> where it is not there or cannot be read.
    /// </summary>
    private List<string>? Includes(string file, Language language)
    {
        if (!_includes.TryGetValue(language, out var read))
        {
            _includes[language] = read = new Dictionary<string, List<string>?>(paths);
        }

        if (read.TryGetValue(file, out var known))
        {
            return known;
        }

        List<string>? headers;

        try
        {
            var directory = Path.GetDirectoryName(file) ?? string.Empty;

            headers = fileSystem.FileExists(file)
                ? [.. SurelyCompiledQuotedIncludes(fileSystem.ReadAllText(file), language)
                    .Select(header => Beside(directory, header))
                    .OfType<string>()
                    .Where(Kept)]
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fails closed: a file that cannot be read excuses nothing.
            headers = null;
        }

        read[file] = headers;

        return headers;
    }

    /// <summary>Whether <paramref name="header"/> is there, and ninja keeps it when cl names it.</summary>
    private bool Kept(string header) => fileSystem.FileExists(header) && !NinjaTakesForTheSystems(header);

    /// <summary>
    /// Whether ninja takes <paramref name="header"/> for the system's own under <c>deps = msvc</c>, and
    /// drops it from what the object records: the path ninja holds it by - relative to the build
    /// directory, where both are on one drive - names one of these, as ninja's own
    /// <c>CLParser::IsSystemInclude</c> reads it. A tree kept under <c>Program Files</c> keeps its
    /// own headers, which ninja reaches from its build directory without naming that.
    /// </summary>
    private bool NinjaTakesForTheSystems(string header)
    {
        var held = Path.GetRelativePath(buildDirectory, header);

        return held.Contains("program files", StringComparison.OrdinalIgnoreCase)
            || held.Contains("microsoft visual studio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><paramref name="path"/> as the build directory resolves it - a record's and a build line's are relative to it.</summary>
    private string? Resolved(string path) => Beside(buildDirectory, path);

    /// <summary>
    /// <paramref name="path"/> resolved in <paramref name="directory"/>, or <see langword="null"/>
    /// where it names no path this machine can hold.
    /// </summary>
    private static string? Beside(string directory, string path)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}