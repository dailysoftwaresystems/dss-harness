using System.Text.RegularExpressions;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// Reads a mutation arms registry: a repository's own line-oriented data file, one row to a line, its fields separated
/// by <c>|</c>, each row's kind its first field.
/// </summary>
/// <remarks>
/// <para>
/// The rows it reads, after the kind:
/// <list type="bullet">
/// <item><c>A | arm | site | before | after | red kind | target | runner | cases | diag | why</c> - an arm;</item>
/// <item><c>C | arm | case | why</c> - a case its mutation must redden;</item>
/// <item><c>G | arm | case | why</c> - a neighbour that must run and stay green;</item>
/// <item><c>B | arm | control before | control after | why</c> - a BUILD-RED arm's paired positive control;</item>
/// <item><c>M | arm | site | before | after | why</c> - another site, mutated together with the arm's own;</item>
/// <item><c>S | arm | legs | why</c> - the legs the arm runs on, in the <c>--legs</c> syntax.</item>
/// </list>
/// before, after, diag and a control's texts name files holding the text, relative to the repository root, so a
/// mutation can span lines and hold any byte a field could not.
/// </para>
/// <para>
/// Strict, and every problem is collected and returned together, each with its line: a registry is a declaration of
/// what the tests prove, and a row read past is an arm nobody drives. Every field is trimmed; a line whose first
/// character that is not blank is <c>#</c> is a comment, so a why may hold one; nothing is escaped; and the last field
/// takes the rest of the line, so a why may hold a <c>|</c>. The rows a mutation harness of a repository's own once
/// needed and this one derives - R, X, I, F and T - are refused, each naming what took its place, rather than read
/// past: a row that looks as though it still decides something, and does not, is the same defect as a misspelt key.
/// </para>
/// </remarks>
public static partial class MutationRegistryParser
{
    /// <summary>What a BUILD-RED arm's diag says: its expectation is its paired control, never a diagnostic's text.</summary>
    public const string PairedControlToken = "PAIRED-CONTROL";

    /// <summary>What a BUILD-RED arm's runner says: nothing runs.</summary>
    public const string NoRunner = "-";

    /// <summary>How the registry spells <see cref="RedKind.TestRed"/>.</summary>
    public const string TestRed = "TEST-RED";

    /// <summary>How the registry spells <see cref="RedKind.BuildRed"/>.</summary>
    public const string BuildRed = "BUILD-RED";

    /// <summary>The fields each row this reads takes after its kind, as a problem names them.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["A"] = ["arm", "site", "before", "after", "red kind", "target", "runner", "cases", "diag", "why"],
        ["C"] = ["arm", "case", "why"],
        ["G"] = ["arm", "case", "why"],
        ["B"] = ["arm", "control before", "control after", "why"],
        ["M"] = ["arm", "site", "before", "after", "why"],
        ["S"] = ["arm", "legs", "why"],
    };

    /// <summary>Each row this no longer reads, and what took its place.</summary>
    private static readonly IReadOnlyDictionary<string, string> Replaced = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["R"] = "an R row is not read: each leg is swept in copies of its own tree, built as its own project and variant are",
        ["X"] = "an X row is not read: a worker copy carries what sync carries, so .gitignore and sync's exclusions decide what it leaves out",
        ["I"] = "an I row is not read: a worker configures as its leg's variant does, with the same flags and compilers by construction",
        ["F"] = "an F row is not read: the dependency sources the leg's own build fetched are read from its CMake cache and given to every worker",
        ["T"] = "a T row is not read: what must rebuild is read from ninja's records of the leg's build, as every object that depends on a site",
    };

    /// <summary>
    /// Reads <paramref name="lines"/>, checking every row and every arm's shape, and that every file in
    /// <paramref name="texts"/> is cited by some row.
    /// </summary>
    /// <param name="lines">The registry's lines, in order: line numbers count from 1.</param>
    /// <param name="texts">The text directory as listed, or <see langword="null"/> where none is configured.</param>
    public static MutationRegistryReading Parse(IReadOnlyList<string> lines, TextDirectoryListing? texts)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var reading = new Reading();

        for (var index = 0; index < lines.Count; index++)
        {
            reading.Read(index + 1, lines[index]);
        }

        reading.CheckShapes();
        reading.CheckCover(texts);

        return new MutationRegistryReading(new MutationRegistry([.. reading.Arms.Select(draft => draft.Build())]), reading.Problems);
    }

    /// <summary>
    /// What is wrong with <paramref name="path"/> as a path a row names, or <see langword="null"/> where nothing is: a
    /// file relative to the repository root, spelled one way on every platform.
    /// </summary>
    /// <param name="path">The path as the row gives it.</param>
    public static string? PathProblem(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!PathCharacters().IsMatch(path))
        {
            return "holds a character outside [A-Za-z0-9_./-]";
        }

        if (path.StartsWith('/'))
        {
            return "is absolute: a row names a path relative to the repository root";
        }

        if (path.EndsWith('/'))
        {
            return "ends in a slash: a row names a file, never a directory";
        }

        var segments = path.Split('/');

        if (segments.Contains(".."))
        {
            return "climbs out with '..': a row never names anything outside the repository";
        }

        return segments.Any(segment => segment is "" or ".")
            ? "holds an empty or '.' segment: a row spells each file one way, as the text directory's listing does"
            : null;
    }

    /// <summary>An arm's id: the name its records are kept under, so nothing a file name could not hold.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdCharacters();

    /// <summary>A path a row names.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9_./-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathCharacters();

    /// <summary>A build target's name, as CMake allows one.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9_.+-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TargetCharacters();

    /// <summary>A case count.</summary>
    [GeneratedRegex(@"\A[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    /// <summary>"an" or "a", as a row's kind is read aloud.</summary>
    private static string Article(string kind) => kind is "A" or "F" or "I" or "M" or "R" or "S" or "X" ? "an" : "a";

    /// <summary>The registry as it is read, row by row.</summary>
    private sealed class Reading
    {
        private readonly Dictionary<string, ArmDraft> _byId = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Ids of A rows refused, whose other rows are passed over rather than refused again for naming no arm.</summary>
        private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every text a row cites, as it spells it.</summary>
        private readonly HashSet<string> _cited = new(StringComparer.Ordinal);

        public List<ArmDraft> Arms { get; } = [];

        public List<string> Problems { get; } = [];

        public void Read(int line, string raw)
        {
            var text = raw.Trim();

            if (text.Length == 0 || text.StartsWith('#'))
            {
                return;
            }

            var kind = text.Split('|', 2)[0].Trim();

            if (Replaced.TryGetValue(kind, out var replaced))
            {
                Problems.Add($"line {line}: {replaced}");
                return;
            }

            if (!Fields.TryGetValue(kind, out var names))
            {
                Problems.Add($"line {line}: '{kind}' is no row this registry reads, which are A, C, G, B, M and S");
                return;
            }

            var fields = text.Split('|', names.Length + 1).Select(field => field.Trim()).ToArray();

            if (fields.Length != names.Length + 1)
            {
                Problems.Add(
                    $"line {line}: {Article(kind)} {kind} row has {fields.Length} field(s), expected {names.Length + 1}: "
                    + $"{kind} | {string.Join(" | ", names)}");
                return;
            }

            var empty = names.Where((_, at) => fields[at + 1].Length == 0).ToList();

            if (empty.Count > 0)
            {
                Problems.Add($"line {line}: {Article(kind)} {kind} row's {string.Join(", ", empty)} {(empty.Count == 1 ? "is" : "are")} empty");

                if (kind == "A")
                {
                    _refused.Add(fields[1]);
                }

                return;
            }

            switch (kind)
            {
                case "A":
                    ReadArm(line, fields);
                    break;

                case "C" or "G":
                    ReadCase(line, kind, fields);
                    break;

                case "B":
                    ReadControl(line, fields);
                    break;

                case "M":
                    ReadCoupled(line, fields);
                    break;

                default:
                    ReadScope(line, fields);
                    break;
            }
        }

        public void CheckShapes()
        {
            foreach (var arm in Arms)
            {
                if (arm.Kind == RedKind.TestRed && arm.Reds.Count == 0)
                {
                    Problems.Add($"line {arm.Line}: arm '{arm.Id}' is {TestRed} and declares no C row: an arm names the cases its mutation must redden");
                }

                if (arm.Kind == RedKind.BuildRed && arm.Control is null)
                {
                    Problems.Add(
                        $"line {arm.Line}: arm '{arm.Id}' is {BuildRed} and has no B row: a mutation that does not compile "
                        + "proves something only beside a paired control, at the same site, that does");
                }
            }

            if (Arms.Count == 0 && _refused.Count == 0)
            {
                Problems.Add("the registry declares no A row: an empty one would report every arm behaving as declared while holding none");
            }
        }

        public void CheckCover(TextDirectoryListing? texts)
        {
            if (texts is null)
            {
                return;
            }

            if (texts.Files is null)
            {
                Problems.Add($"the text directory '{texts.Directory}' is not a directory, so whether every text in it is cited cannot be read");
                return;
            }

            if (texts.Files.Count == 0)
            {
                Problems.Add($"the text directory '{texts.Directory}' holds no file");
                return;
            }

            foreach (var name in texts.Files.Order(StringComparer.Ordinal))
            {
                var path = texts.Directory + "/" + name;

                if (!_cited.Contains(path))
                {
                    Problems.Add($"'{path}' is in the text directory and no row cites it: a mutation text nobody drives");
                }
            }
        }

        private void ReadArm(int line, string[] fields)
        {
            var id = fields[1];

            Cite(fields[3], fields[4]);

            if (fields[9] != PairedControlToken)
            {
                Cite(fields[9]);
            }

            if (!IdCharacters().IsMatch(id))
            {
                Problems.Add($"line {line}: arm id '{id}' holds a character outside [A-Za-z0-9_-]");
                _refused.Add(id);
                return;
            }

            if (_byId.TryGetValue(id, out var first))
            {
                // Ignoring case: an arm's records are kept under its id, and two ids one file system reads as one
                // would write each other's.
                Problems.Add($"line {line}: arm '{id}' is declared twice, first at line {first.Line} as '{first.Id}'");
                return;
            }

            RequirePath(line, "site", fields[2]);
            RequirePath(line, "before", fields[3]);
            RequirePath(line, "after", fields[4]);

            RedKind? kind = fields[5] switch
            {
                TestRed => RedKind.TestRed,
                BuildRed => RedKind.BuildRed,
                _ => null,
            };

            if (kind is null)
            {
                Problems.Add(
                    $"line {line}: red kind '{fields[5]}' is neither {TestRed} nor {BuildRed}: it is declared, never "
                    + "inferred from what happened, which would make a build-red arm and a broken harness one thing");
            }

            if (!TargetCharacters().IsMatch(fields[6]))
            {
                Problems.Add($"line {line}: target '{fields[6]}' holds a character a CMake target cannot, outside [A-Za-z0-9_.+-]");
            }

            var runner = fields[7];

            if (runner != NoRunner && !TargetCharacters().IsMatch(runner))
            {
                Problems.Add($"line {line}: runner '{runner}' holds a character a CMake target cannot, outside [A-Za-z0-9_.+-]");
            }

            int? cases = Digits().IsMatch(fields[8]) && int.TryParse(fields[8], out var count) ? count : null;

            if (cases is null)
            {
                Problems.Add($"line {line}: case count '{fields[8]}' is not a whole number");
            }

            var diagnostic = fields[9];

            if (diagnostic != PairedControlToken)
            {
                RequirePath(line, "diag", diagnostic);
            }

            switch (kind)
            {
                case RedKind.TestRed:
                    if (diagnostic == PairedControlToken)
                    {
                        Problems.Add($"line {line}: arm '{id}' is {TestRed} and its diag is {PairedControlToken}: it names the file holding the text its run must say");
                    }

                    if (cases == 0)
                    {
                        Problems.Add($"line {line}: arm '{id}' is {TestRed} and declares 0 cases: a binary that runs no case cannot redden");
                    }

                    if (runner == NoRunner)
                    {
                        Problems.Add($"line {line}: arm '{id}' is {TestRed} and names no runner: the binary whose cases must fail is its runner");
                    }

                    break;

                case RedKind.BuildRed:
                    if (diagnostic != PairedControlToken)
                    {
                        Problems.Add(
                            $"line {line}: arm '{id}' is {BuildRed} and its diag is not {PairedControlToken}: compilers word "
                            + "one rejection their own ways, so what a build-red arm expects is its paired control");
                    }

                    if (cases is > 0)
                    {
                        Problems.Add($"line {line}: arm '{id}' is {BuildRed} and declares {cases} case(s): nothing runs, so it declares 0");
                    }

                    if (runner != NoRunner)
                    {
                        Problems.Add($"line {line}: arm '{id}' is {BuildRed} and names runner '{runner}': nothing runs, so its runner is '{NoRunner}'");
                    }

                    break;
            }

            var arm = new ArmDraft(id, line, new MutationSite(fields[2], fields[3], fields[4], line), kind, fields[6], runner, cases ?? 0, diagnostic, fields[10]);

            _byId.Add(id, arm);
            Arms.Add(arm);
        }

        private void ReadCase(int line, string kind, string[] fields)
        {
            if (Arm(line, kind, fields[1]) is not { } arm)
            {
                return;
            }

            var @case = fields[2];

            if (@case.Any(char.IsControl))
            {
                Problems.Add($"line {line}: case '{@case}' holds a control character, which no report names a case with");
                return;
            }

            var red = kind == "C";

            if (arm.Kind == RedKind.BuildRed)
            {
                Problems.Add(
                    $"line {line}: arm '{arm.Id}' is {BuildRed} and carries {Article(kind)} {kind} row: nothing runs, so no case "
                    + (red ? "can redden" : "can stay green"));
                return;
            }

            var same = red ? arm.Reds : arm.Greens;
            var other = red ? arm.Greens : arm.Reds;

            if (same.Contains(@case, StringComparer.Ordinal))
            {
                Problems.Add($"line {line}: arm '{arm.Id}' declares case '{@case}' {(red ? "red" : "green")} twice");
                return;
            }

            if (other.Contains(@case, StringComparer.Ordinal))
            {
                Problems.Add($"line {line}: arm '{arm.Id}' declares case '{@case}' both red and green");
                return;
            }

            same.Add(@case);
        }

        private void ReadControl(int line, string[] fields)
        {
            Cite(fields[2], fields[3]);

            if (Arm(line, "B", fields[1]) is not { } arm)
            {
                return;
            }

            RequirePath(line, "control before", fields[2]);
            RequirePath(line, "control after", fields[3]);

            if (arm.Kind == RedKind.TestRed)
            {
                Problems.Add($"line {line}: arm '{arm.Id}' is {TestRed} and carries a B row: a paired positive control belongs to a {BuildRed} arm");
                return;
            }

            if (arm.Control is { } first)
            {
                Problems.Add($"line {line}: arm '{arm.Id}' has a second B row, the first at line {first.Line}: a paired positive control is one substitution");
                return;
            }

            arm.Control = new PairedControl(fields[2], fields[3], line);
        }

        private void ReadCoupled(int line, string[] fields)
        {
            Cite(fields[3], fields[4]);

            if (Arm(line, "M", fields[1]) is not { } arm)
            {
                return;
            }

            RequirePath(line, "site", fields[2]);
            RequirePath(line, "before", fields[3]);
            RequirePath(line, "after", fields[4]);

            if (arm.Kind == RedKind.BuildRed)
            {
                Problems.Add(
                    $"line {line}: arm '{arm.Id}' is {BuildRed} and carries an M row: its paired control substitutes at its own "
                    + "site alone, so a coupled edit the control does not undo would make the control's build prove nothing");
                return;
            }

            if (arm.Own.Site == fields[2] || arm.Coupled.Any(site => site.Site == fields[2]))
            {
                Problems.Add(
                    $"line {line}: arm '{arm.Id}' already mutates '{fields[2]}': two edits to one file would be taken and put "
                    + "back over each other, so a coupled site is another file");
                return;
            }

            arm.Coupled.Add(new MutationSite(fields[2], fields[3], fields[4], line));
        }

        private void ReadScope(int line, string[] fields)
        {
            if (Arm(line, "S", fields[1]) is not { } arm)
            {
                return;
            }

            if (arm.Scope is { } first)
            {
                Problems.Add($"line {line}: arm '{arm.Id}' has a second S row, the first at line {first.Line}: one row names every leg it runs on");
                return;
            }

            var legs = fields[2].Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (legs.Length == 0)
            {
                Problems.Add($"line {line}: the S row of arm '{arm.Id}' names no leg or leg set; leave the row out for every selected leg");
                return;
            }

            arm.Scope = new ArmScope(legs, line);
        }

        /// <summary>The arm a row names, or <see langword="null"/> - said, unless its A row was refused already.</summary>
        private ArmDraft? Arm(int line, string kind, string id)
        {
            if (_byId.TryGetValue(id, out var arm))
            {
                return arm;
            }

            if (!_refused.Contains(id))
            {
                Problems.Add(
                    $"line {line}: {Article(kind)} {kind} row names arm '{id}', which no A row above it declares: a row for "
                    + "an arm nobody declared is refused, never passed over");
            }

            return null;
        }

        private void RequirePath(int line, string field, string path)
        {
            if (PathProblem(path) is { } problem)
            {
                Problems.Add($"line {line}: {field} '{path}' {problem}");
            }
        }

        private void Cite(params string[] paths)
        {
            foreach (var path in paths)
            {
                _cited.Add(path);
            }
        }
    }

    /// <summary>An arm while its rows are read: what its A row said, and what its other rows have added since.</summary>
    private sealed class ArmDraft(string id, int line, MutationSite own, RedKind? kind, string target, string runner, int cases, string diagnostic, string why)
    {
        public string Id { get; } = id;

        public int Line { get; } = line;

        public MutationSite Own { get; } = own;

        /// <summary>Its kind, or <see langword="null"/> where its A row's could not be read, and nothing that depends on it is checked.</summary>
        public RedKind? Kind { get; } = kind;

        public List<string> Reds { get; } = [];

        public List<string> Greens { get; } = [];

        public List<MutationSite> Coupled { get; } = [];

        public PairedControl? Control { get; set; }

        public ArmScope? Scope { get; set; }

        public MutationArm Build() => new()
        {
            Id = Id,
            Line = Line,
            Own = Own,
            Kind = Kind ?? RedKind.TestRed,
            Target = target,
            Runner = runner,
            Cases = cases,
            Diagnostic = diagnostic,
            Why = why,
            Reds = [.. Reds],
            Greens = [.. Greens],
            Control = Control,
            Coupled = [.. Coupled],
            Scope = Scope,
        };
    }
}
