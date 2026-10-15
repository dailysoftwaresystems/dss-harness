using System.Globalization;
using System.Text.Json;

namespace RepoHarness.Core.Testing;

/// <summary>
/// What ctest's own description of a leg's tests says (<c>--show-only=json-v1</c>, as <see cref="CtestEntries.ShowOnly"/>
/// asks it): which tests it registers for a binary, and what of their entries a sweep that starts the binary itself, never
/// through ctest, does not apply.
/// </summary>
/// <remarks>
/// Pure: it reads a description already made and matches in memory, so the matching rule is tested without starting
/// ctest, and the reader that starts it (<see cref="CtestDescription"/>) owns no rule of its own.
/// </remarks>
public static class CtestEntries
{
    /// <summary>The option that asks ctest for its own description of the tests it registers.</summary>
    public const string ShowOnly = "--show-only=json-v1";

    /// <summary>
    /// The properties of a test's ctest entry that name what a sweep that starts the binary itself does not apply, in the
    /// order the closed list is stated in: everything a test's entry can set up around it that a bare run of its program
    /// has none of.
    /// </summary>
    public static readonly IReadOnlyList<string> NotAppliedNames =
    [
        "ENVIRONMENT",
        "ENVIRONMENT_MODIFICATION",
        "WORKING_DIRECTORY",
        "FIXTURES_REQUIRED",
        "FIXTURES_SETUP",
        "FIXTURES_CLEANUP",
        "DEPENDS",
        "REQUIRED_FILES",
        "RESOURCE_LOCK",
        "RUN_SERIAL",
        "TIMEOUT",
        "WILL_FAIL",
        "PASS_REGULAR_EXPRESSION",
        "FAIL_REGULAR_EXPRESSION",
        "SKIP_REGULAR_EXPRESSION",
        "SKIP_RETURN_CODE",
        "DISABLED",
    ];

    /// <summary>Reads <paramref name="json"/> as ctest's description of the tests it registers, or why it is no such description.</summary>
    /// <param name="json">What <c>ctest --show-only=json-v1</c> wrote.</param>
    public static CtestRead Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "ctestInfo")
            {
                return CtestRead.Refused("it is no ctest test description: its kind is not ctestInfo");
            }

            if (root.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Object
                && version.TryGetProperty("major", out var major)
                && major.ValueKind == JsonValueKind.Number
                && major.TryGetInt32(out var majorVersion)
                && majorVersion != 1)
            {
                return CtestRead.Refused($"its version is {majorVersion}, and this reads major version 1");
            }

            if (!root.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array)
            {
                return CtestRead.Refused("it names no tests");
            }

            var readTests = new List<CtestTest>();

            foreach (var test in tests.EnumerateArray())
            {
                if (test.ValueKind != JsonValueKind.Object
                    || !test.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String)
                {
                    return CtestRead.Refused("one of its tests is named by no string");
                }

                IReadOnlyList<string>? command = null;

                if (test.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.Array)
                {
                    var words = new List<string>();

                    foreach (var word in commandElement.EnumerateArray())
                    {
                        if (word.ValueKind != JsonValueKind.String)
                        {
                            return CtestRead.Refused($"the command of its test '{name.GetString()}' holds what is no string");
                        }

                        words.Add(word.GetString()!);
                    }

                    command = words;
                }

                var properties = new List<CtestProperty>();

                if (test.TryGetProperty("properties", out var listed) && listed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var property in listed.EnumerateArray())
                    {
                        if (property.ValueKind != JsonValueKind.Object
                            || !property.TryGetProperty("name", out var named)
                            || named.ValueKind != JsonValueKind.String
                            || !property.TryGetProperty("value", out var value))
                        {
                            return CtestRead.Refused($"a property of its test '{name.GetString()}' names itself by no string");
                        }

                        if (ValueOf(value) is not { } read)
                        {
                            return CtestRead.Refused($"a property of its test '{name.GetString()}' holds a value this reads no shape of");
                        }

                        properties.Add(new CtestProperty(named.GetString()!, read));
                    }
                }

                readTests.Add(new CtestTest(name.GetString()!, command, properties));
            }

            return CtestRead.Cited(readTests);
        }
        catch (JsonException unread)
        {
            return CtestRead.Refused($"it is no JSON: {unread.Message}");
        }
    }

    /// <summary>
    /// What of <paramref name="test"/>'s ctest entry a sweep that starts the binary <paramref name="program"/> itself does
    /// not apply, as a line shows each: an environment by its variables, never their values; a required fixture by its
    /// name and the tests whose <c>FIXTURES_SETUP</c> set it up; the working directory only where it is another than
    /// <paramref name="start"/>, where the sweep starts the binary; and the command's own arguments, a launcher's words
    /// before the program among them.
    /// </summary>
    /// <param name="tests">Every test the description registers, whose setups a required fixture is found among.</param>
    /// <param name="test">The test whose entry is read.</param>
    /// <param name="program">The binary the sweep starts, whole, in the worker.</param>
    /// <param name="start">Where the sweep starts the binary.</param>
    /// <param name="names">How the machine that starts the binary compares names, as its file system does.</param>
    public static IReadOnlyList<NotApplied> NotAppliedOf(IReadOnlyList<CtestTest> tests, CtestTest test, string program, string start, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(test);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(start);
        ArgumentNullException.ThrowIfNull(names);

        var notApplied = new List<NotApplied>();

        foreach (var property in test.Properties)
        {
            if (!NotAppliedNames.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (property.Name is "ENVIRONMENT" or "ENVIRONMENT_MODIFICATION")
            {
                notApplied.Add(new NotApplied(property.Name, [.. property.Value.Shown.Select(VariableOf)]));
            }
            else if (property.Name == "WORKING_DIRECTORY")
            {
                if (property.Value.Shown is [var directory] && !names.Equals(Whole(directory, start), Whole(start, start)))
                {
                    notApplied.Add(new NotApplied(property.Name, property.Value.Shown));
                }
            }
            else if (property.Name == "FIXTURES_REQUIRED")
            {
                var setUpBy = tests
                    .Where(other => !ReferenceEquals(other, test)
                        && other.Property("FIXTURES_SETUP") is { } setup
                        && setup.Value.Shown.Any(fixture => property.Value.Shown.Contains(fixture, StringComparer.Ordinal)))
                    .Select(other => other.Name)
                    .ToList();

                notApplied.Add(new NotApplied(property.Name, property.Value.Shown, setUpBy.Count > 0 ? setUpBy : null));
            }
            else
            {
                notApplied.Add(new NotApplied(property.Name, property.Value.Shown));
            }
        }

        var arguments = WordsAround(test.Command, program, start, names);

        if (arguments.Count > 0)
        {
            notApplied.Add(new NotApplied("arguments", arguments));
        }

        return notApplied;
    }

    /// <summary>
    /// The tests of <paramref name="tests"/> that start the binary <paramref name="program"/>, and how each of
    /// <paramref name="redCases"/> belongs to one: the one test that starts the binary; the test each case names, by its
    /// name or by an argument; no way to tell; or no test at all. What of each matched test's entry a sweep does not apply
    /// is said with it.
    /// </summary>
    /// <param name="tests">Every test the description registers.</param>
    /// <param name="program">The binary the sweep starts, whole, in the worker.</param>
    /// <param name="redCases">The cases the binary's unmutated run reddened.</param>
    /// <param name="start">Where the sweep starts the binary.</param>
    /// <param name="names">How the machine that starts the binary compares names, as its file system does.</param>
    public static CtestMatch For(IReadOnlyList<CtestTest> tests, string program, IReadOnlyList<string> redCases, string start, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(redCases);
        ArgumentException.ThrowIfNullOrWhiteSpace(start);
        ArgumentNullException.ThrowIfNull(names);

        var candidates = tests.Where(test => Starts(test, program, start, names)).ToList();

        if (candidates.Count == 0)
        {
            return CtestMatch.Of(
                CtestMatched.None,
                [],
                $"ctest, as this leg's tests start it, registers no test that starts {program}");
        }

        if (candidates.Count == 1)
        {
            var only = candidates[0];
            var notApplied = NotAppliedOf(tests, only, program, start, names);

            return CtestMatch.Of(
                CtestMatched.OneTest,
                [new CtestTestLine(only.Name, redCases, notApplied)],
                $"ctest starts {program} as one test, '{only.Name}', "
                    + (notApplied.Count == 0 ? "with nothing a sweep does not apply" : $"whose {Listed(notApplied)} a sweep does not apply"));
        }

        var matched = new Dictionary<CtestTest, List<string>>();

        foreach (var redCase in redCases)
        {
            var by = candidates
                .Where(test => string.Equals(test.Name, redCase, StringComparison.Ordinal)
                    || test.Name.EndsWith(redCase, StringComparison.Ordinal)
                    || WordsAround(test.Command, program, start, names).Any(argument =>
                        string.Equals(argument, redCase, StringComparison.Ordinal)
                        || argument.EndsWith("=" + redCase, StringComparison.Ordinal)))
                .Distinct()
                .ToList();

            if (by.Count != 1)
            {
                return Uncertain(candidates, program, redCases, start, names);
            }

            if (!matched.TryGetValue(by[0], out var cases))
            {
                matched[by[0]] = cases = [];
            }

            cases.Add(redCase);
        }

        var lines = matched
            .Select(pair => new CtestTestLine(pair.Key.Name, pair.Value, NotAppliedOf(tests, pair.Key, program, start, names)))
            .ToList();

        return CtestMatch.Of(
            CtestMatched.ByName,
            lines,
            string.Join("; ", lines.Select(line => $"ctest registers {Quoted(line.Cases)} as test '{line.Test}', "
                + (line.NotApplied.Count == 0 ? "with nothing a sweep does not apply" : $"whose {Listed(line.NotApplied)} a sweep does not apply"))));
    }

    /// <summary>
    /// The match where no single test could be named for the red cases: the tests that start the binary, counted by what of
    /// their entries a sweep does not apply.
    /// </summary>
    private static CtestMatch Uncertain(IReadOnlyList<CtestTest> tests, string program, IReadOnlyList<string> redCases, string start, StringComparer names)
    {
        var counted = NotAppliedNames
            .Select(name => (Name: name, Count: tests.Count(test => NotAppliedOf(tests, test, program, start, names).Any(left => left.Name == name))))
            .Where(count => count.Count > 0)
            .ToList();

        var said = $"which of the {tests.Count.ToString(CultureInfo.InvariantCulture)} tests ctest registers for {program} runs {Quoted(redCases)} could not be told by its name";

        if (counted.Count > 0)
        {
            var items = string.Join(
                " and ",
                counted.Select((count, at) => at == 0
                    ? $"{count.Count.ToString(CultureInfo.InvariantCulture)} declare {count.Name}"
                    : $"{count.Count.ToString(CultureInfo.InvariantCulture)} {count.Name}"));

            said += $"; among them {items}, {(counted.Count == 1 ? "which a sweep does not apply" : counted.Count == 2 ? "neither of which a sweep applies" : "none of which a sweep applies")}";
        }

        return CtestMatch.Of(
            CtestMatched.Uncertain,
            [.. tests.Select(test => new CtestTestLine(test.Name, [], NotAppliedOf(tests, test, program, start, names)))],
            said);
    }

    /// <summary>Whether <paramref name="test"/>'s command starts the binary <paramref name="program"/>, as one of its words, compared as full paths.</summary>
    private static bool Starts(CtestTest test, string program, string start, StringComparer names)
        => test.Command is { } command
            && command.Any(word => names.Equals(Whole(word, start), Whole(program, start)));

    /// <summary>
    /// The words of <paramref name="command"/> around the program <paramref name="program"/>: those after it, and where
    /// the first word is not the program - a launcher, as <c>python wrapper.py binary args</c> - those before it too.
    /// </summary>
    private static IReadOnlyList<string> WordsAround(IReadOnlyList<string>? command, string program, string start, StringComparer names)
    {
        if (command is not { } words)
        {
            return [];
        }

        var at = -1;

        for (var index = 0; index < words.Count; index++)
        {
            if (names.Equals(Whole(words[index], start), Whole(program, start)))
            {
                at = index;
                break;
            }
        }

        if (at < 0)
        {
            return [];
        }

        return at == 0 ? [.. words.Skip(1)] : [.. words.Take(at), .. words.Skip(at + 1)];
    }

    /// <summary>
    /// <paramref name="path"/> as a full path, made whole and canonical against <paramref name="start"/> however either is
    /// spelled: ctest's description writes forward slashes whatever the system, and a relative path is the test command's
    /// own to resolve from where it starts.
    /// </summary>
    private static string Whole(string path, string start)
        => Path.GetFullPath(path, start);

    /// <summary>The variable a value of an environment names, which is all a line shows of it: never its operation or value.</summary>
    private static string VariableOf(string value)
    {
        var at = value.IndexOfAny(['=', ':']);

        return at < 0 ? value : value[..at];
    }

    /// <summary>How a line lists what a sweep does not apply: each as its name and, in brackets, what it holds.</summary>
    private static string Listed(IReadOnlyList<NotApplied> notApplied)
        => string.Join(
            " and ",
            notApplied.Select(left =>
                $"{left.Name} ({string.Join(", ", [.. left.Value, .. left.SetUpBy?.Select(setUp => $"set up by test '{setUp}'") ?? []])})"));

    /// <summary>How a line quotes one or many names, each in its own quotation marks.</summary>
    private static string Quoted(IReadOnlyList<string> names)
        => string.Join(", ", names.Select(name => $"'{name}'"));

    /// <summary>A property's value as ctest writes it: one string, one number, one boolean or a list of strings.</summary>
    private static CtestValue? ValueOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => CtestValue.Of(value.GetString()!),
        JsonValueKind.Number => CtestValue.Of(value.GetDouble()),
        JsonValueKind.True => CtestValue.Of(true),
        JsonValueKind.False => CtestValue.Of(false),
        JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            => CtestValue.Of([.. value.EnumerateArray().Select(item => item.GetString()!)]),
        _ => null,
    };
}

/// <summary>One test as ctest's own description names it: its name, its command and its properties.</summary>
/// <param name="Name">The test's name, as ctest registers it.</param>
/// <param name="Command">The command its <c>add_test</c> runs, program first; <see langword="null"/> where it declares none.</param>
/// <param name="Properties">Its properties, in the order the description lists them.</param>
public sealed record CtestTest(string Name, IReadOnlyList<string>? Command, IReadOnlyList<CtestProperty> Properties)
{
    /// <summary>The test's property named <paramref name="name"/>, or <see langword="null"/> where it declares none.</summary>
    public CtestProperty? Property(string name)
        => Properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));
}

/// <summary>One property of a ctest test's entry: its name and its value.</summary>
/// <param name="Name">The property's name, as ctest writes it.</param>
/// <param name="Value">Its value.</param>
public sealed record CtestProperty(string Name, CtestValue Value);

/// <summary>
/// A property's value, as ctest writes it: one string, one number, one boolean or a list of strings - shown as the
/// strings each is, a number by its digits.
/// </summary>
public sealed record CtestValue
{
    private CtestValue(IReadOnlyList<string> shown)
    {
        Shown = shown;
    }

    /// <summary>A value of one string.</summary>
    /// <param name="text">The string.</param>
    public static CtestValue Of(string text) => new([text]);

    /// <summary>A value of one number.</summary>
    /// <param name="number">The number.</param>
    public static CtestValue Of(double number) => new([number.ToString("0.###", CultureInfo.InvariantCulture)]);

    /// <summary>A value of one boolean.</summary>
    /// <param name="flag">The boolean.</param>
    public static CtestValue Of(bool flag) => new([flag ? "true" : "false"]);

    /// <summary>A value of a list of strings.</summary>
    /// <param name="list">The strings.</param>
    public static CtestValue Of(IReadOnlyList<string> list) => new(list);

    /// <summary>How a line shows the value: each string it is, a number as its digits.</summary>
    public IReadOnlyList<string> Shown { get; }
}

/// <summary>What ctest's own description holds - each test it registers - or why it is no such description.</summary>
public sealed record CtestRead
{
    private CtestRead(IReadOnlyList<CtestTest>? tests, string? why)
    {
        Tests = tests;
        Why = why;
    }

    /// <summary>Each test the description registers; <see langword="null"/> where it is no description.</summary>
    public IReadOnlyList<CtestTest>? Tests { get; }

    /// <summary>Why it is no description ctest answers a reader with; <see langword="null"/> where it is one.</summary>
    public string? Why { get; }

    /// <summary>A description that registered <paramref name="tests"/>.</summary>
    internal static CtestRead Cited(IReadOnlyList<CtestTest> tests) => new(tests, null);

    /// <summary>No description: <paramref name="why"/> it is none.</summary>
    internal static CtestRead Refused(string why) => new(null, why);
}

/// <summary>What of one test's ctest entry a sweep that starts the binary itself does not apply, and how a line shows it.</summary>
/// <param name="Name">What is not applied: a property of the test's entry, or the command's own arguments.</param>
/// <param name="Value">How a line shows it: names for an environment, never values; the value for the rest.</param>
/// <param name="SetUpBy">The tests that set up each fixture <see cref="Value"/> names, where <see cref="Name"/> is a required fixture.</param>
public sealed record NotApplied(string Name, IReadOnlyList<string> Value, IReadOnlyList<string>? SetUpBy = null);

/// <summary>How a red control's red cases were matched to the tests ctest registers for its binary.</summary>
public enum CtestMatched
{
    /// <summary>One test starts the binary: the whole entry is its.</summary>
    OneTest,

    /// <summary>Each red case named one test, by its name or by an argument.</summary>
    ByName,

    /// <summary>No way to tell which test runs a red case.</summary>
    Uncertain,

    /// <summary>No test starts the binary at all.</summary>
    None,
}

/// <summary>One test of the answer, with the red cases matched to it and what of its entry a sweep does not apply.</summary>
/// <param name="Test">The test's name, as ctest registers it.</param>
/// <param name="Cases">The red cases matched to it; none where the match told none apart.</param>
/// <param name="NotApplied">What of its entry a sweep does not apply.</param>
public sealed record CtestTestLine(string Test, IReadOnlyList<string> Cases, IReadOnlyList<NotApplied> NotApplied);

/// <summary>
/// What ctest said of a red control's binary: which of the tests it registers start it, which runs each red case, and what
/// of those tests' entries a sweep does not apply.
/// </summary>
/// <remarks>
/// The sentence (<see cref="Said"/>) and the parts agree because both are made here, once, by the match.
/// </remarks>
public sealed record CtestMatch
{
    private CtestMatch(CtestMatched matched, IReadOnlyList<CtestTestLine> tests, string said)
    {
        Matched = matched;
        Tests = tests;
        Said = said;
    }

    /// <summary>How the red cases were matched.</summary>
    public CtestMatched Matched { get; }

    /// <summary>Each test of the answer, with the cases matched to it and what of its entry a sweep does not apply.</summary>
    public IReadOnlyList<CtestTestLine> Tests { get; }

    /// <summary>
    /// The sentence that follows the control's red cases on the leg's line: what ctest registers for them that a sweep
    /// does not apply.
    /// </summary>
    public string Said { get; }

    /// <summary>How the leg's document says <see cref="Matched"/>.</summary>
    public string Wire => Matched switch
    {
        CtestMatched.OneTest => "one-test",
        CtestMatched.ByName => "by-name",
        CtestMatched.Uncertain => "uncertain",
        CtestMatched.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(Matched), Matched, "Every way a match can go is said."),
    };

    /// <summary>The match, said once.</summary>
    internal static CtestMatch Of(CtestMatched matched, IReadOnlyList<CtestTestLine> tests, string said)
        => new(matched, tests, said);
}
