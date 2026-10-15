using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Configuration;

/// <summary>
/// One named set-up: the command line that brings up what a repository's work needs on this machine - services, a
/// database, a container - and the one that brings it down again.
/// </summary>
/// <remarks>
/// Both are required: a set-up that can come up and not down leaves what it started running after every run that
/// used it, which no later run would ever stop. Its lines may name its inputs, <c>{name}</c> in any argument, each
/// filled in with the value the command line, or whatever names the set-up, gives it, or with its default.
/// </remarks>
public sealed class SetUpConfig
{
    /// <summary>The command line <c>up</c> runs.</summary>
    [Description("the command line 'up <name>' runs - a program and its arguments, with no shell - or a map of platform "
        + "(windows, linux, macos) or all to one")]
    public required SetUpLine Up { get; init; }

    /// <summary>The command line <c>down</c> runs.</summary>
    [Description("the command line 'down <name>' runs, in the shape up takes")]
    public required SetUpLine Down { get; init; }

    /// <summary>
    /// The values its lines name, <c>{name}</c> in an argument: each given by <c>--input name=value</c> on <c>up</c> and
    /// <c>down</c>, or by the <c>with</c> of what names the set-up, over its default.
    /// </summary>
    [Description("values its lines name as {name}, each with an optional default: given by --input name=value on up and "
        + "down, or by the with of a runner's or test invocation's setUp")]
    public Dictionary<string, SetUpInput> Inputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which legs share it once it is up: <see cref="SharedByTree"/>, those working in one tree of a machine, each tree
    /// bringing up its own; or <see cref="SharedByMachine"/>, every leg of the repository on the machine, whatever tree it
    /// works in. Another repository's set-up of the name is another set-up, never shared.
    /// </summary>
    /// <remarks>
    /// By tree unless it says otherwise: a set-up serving one tree's own build - a server built from it - shared with a
    /// leg in another tree would run that leg's work against the other tree's code, which nothing would say. One whose
    /// lines start what a machine holds once - a container's name, a port - is shared by the machine, or the first tree to
    /// end brings it down under every other.
    /// </remarks>
    [Description("which legs share it once it is up: 'tree' (the default), the legs working in one tree of a machine, each "
        + "tree bringing up its own; or 'machine', every leg of the repository on the machine whatever tree it works in - for what a machine "
        + "holds once, as a container's name or a port")]
    public string Shared { get; init; } = SharedByTree;

    /// <summary>Whether every leg of the repository on the machine shares it, whatever tree it works in (<see cref="Shared"/>).</summary>
    [JsonIgnore]
    public bool MachineWide => string.Equals(Shared, SharedByMachine, StringComparison.Ordinal);

    /// <summary>The <see cref="Shared"/> of a set-up the legs in one tree of a machine share.</summary>
    public const string SharedByTree = "tree";

    /// <summary>The <see cref="Shared"/> of a set-up every leg of the repository on a machine shares, whatever tree it works in.</summary>
    public const string SharedByMachine = "machine";

    /// <summary>The line <paramref name="direction"/> runs.</summary>
    /// <param name="direction">Up or down.</param>
    public SetUpLine Line(SetUpDirection direction) => direction == SetUpDirection.Up ? Up : Down;

    /// <summary>The key the up line is written under, and the command that runs it.</summary>
    public const string UpKey = "up";

    /// <summary>The key the down line is written under, and the command that runs it.</summary>
    public const string DownKey = "down";

    /// <summary>
    /// The key the line <paramref name="direction"/> runs is written under, and the command that runs it: one word for
    /// both, so the file and the command line never name a direction apart.
    /// </summary>
    /// <param name="direction">Up or down.</param>
    public static string KeyOf(SetUpDirection direction) => direction == SetUpDirection.Up ? UpKey : DownKey;
}

/// <summary>Which of a set-up's two command lines runs.</summary>
public enum SetUpDirection
{
    /// <summary>The line that brings it up.</summary>
    Up,

    /// <summary>The line that brings it down.</summary>
    Down,
}

/// <summary>
/// A set-up's command line, written as one line for every platform or as a map of platform, or <c>all</c>, to the
/// line that runs there.
/// </summary>
/// <remarks>
/// A line is a program and its arguments, split as an action file's <c>run</c> lines are
/// (<see cref="Processes.CommandLineText"/>): no shell parses it. A platform whose line differs - a <c>.cmd</c> on
/// Windows, a <c>.sh</c> elsewhere - says so in the map. One line, never a list: what takes several steps is a
/// script, which the line runs.
/// </remarks>
[JsonConverter(typeof(SetUpLineConverter))]
public sealed class SetUpLine : PerPlatformText
{
    private SetUpLine(IEnumerable<KeyValuePair<string, string>> lines, string? plain)
        : base(lines, plain)
    {
    }

    /// <summary>One line on every platform.</summary>
    /// <param name="line">The command line.</param>
    /// <exception cref="ArgumentException">The line is blank; it would run nothing.</exception>
    public static SetUpLine Everywhere(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        return new SetUpLine([new(PlatformScope.Every, line)], line);
    }

    /// <summary>One line per platform.</summary>
    /// <param name="lines">The lines, keyed by platform name or <see cref="PlatformScope.Every"/>.</param>
    public static SetUpLine Keyed(IEnumerable<KeyValuePair<string, string>> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return new SetUpLine(lines, plain: null);
    }

    /// <summary>A bare line, which runs on every platform.</summary>
    /// <param name="line">The command line.</param>
    public static implicit operator SetUpLine(string line) => Everywhere(line);
}

/// <summary>
/// Reads a <see cref="SetUpLine"/> written either as a bare string or as a map of platform to line, and writes it
/// back in the shape it was read.
/// </summary>
public sealed class SetUpLineConverter : PerPlatformTextConverter<SetUpLine>
{
    /// <inheritdoc />
    protected override PerPlatformWords Words { get; } = new(
        "A setUp command",
        "command line",
        "program",
        "a set-up that runs nothing brings nothing up and nothing down.");

    /// <inheritdoc />
    protected override SetUpLine Everywhere(string text) => SetUpLine.Everywhere(text);

    /// <inheritdoc />
    protected override SetUpLine Keyed(Dictionary<string, string> values) => SetUpLine.Keyed(values);
}

/// <summary>One value a set-up's lines name, as <c>{name}</c>.</summary>
public sealed class SetUpInput
{
    /// <summary>What the lines are given where nothing gives a value; left out, whatever runs the set-up must give one.</summary>
    [Description("what the lines are given where nothing gives a value; left out, whatever runs the set-up must give one")]
    public string? Default { get; init; }

    /// <summary>What the value is for, said where one is missing.</summary>
    [Description("what the value is for, said where one is missing")]
    public string? Description { get; init; }
}

/// <summary>
/// One set-up a runner or a test invocation names: the set-up, and the values it gives the set-up's inputs. Written
/// as the set-up's name alone where it gives none, or as <c>{ "name": ..., "with": { ... } }</c>.
/// </summary>
[JsonConverter(typeof(SetUpUseConverter))]
public sealed class SetUpUse : IEquatable<SetUpUse>
{
    /// <summary>The set-up, as <c>setUp</c> names it.</summary>
    public required string Name { get; init; }

    /// <summary>Values for its inputs, by name, over each input's default.</summary>
    public Dictionary<string, string> With { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A set-up named alone, giving its inputs nothing.</summary>
    /// <param name="name">The set-up.</param>
    public static implicit operator SetUpUse(string name) => new() { Name = name };

    /// <summary>
    /// Whether <paramref name="other"/> names the same set-up, ignoring case, with the same values: inputs by name
    /// ignoring case, values exactly.
    /// </summary>
    /// <param name="other">The use to compare with.</param>
    public bool Equals(SetUpUse? other)
        => other is not null
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
            && SetUpValues.Same(With, other.With);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SetUpUse);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <summary>The use as a refusal names it: the set-up, then the values it gives, if any.</summary>
    public override string ToString() => Name + SetUpValues.Said(With);
}

/// <summary>What one set-up's inputs are given: compared and said one way wherever a set-up's values are.</summary>
public static class SetUpValues
{
    /// <summary>Whether two sets of values give the same inputs the same values: names ignoring case, values exactly.</summary>
    /// <param name="one">One set.</param>
    /// <param name="other">The other.</param>
    public static bool Same(IReadOnlyDictionary<string, string> one, IReadOnlyDictionary<string, string> other)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(other);

        return one.Count == other.Count
            && one.All(pair => other.FirstOrDefault(given => string.Equals(given.Key, pair.Key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } match
                && string.Equals(match.Value, pair.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// The value each of <paramref name="setUp"/>'s inputs takes: what <paramref name="given"/> gives it, else its
    /// default - keyed by the input as declared - and every reason that cannot be, each a clause after
    /// <paramref name="subject"/>: a name the set-up declares no input by, an input given two values under names that
    /// differ only in case, a value given empty, an input given nothing that has no default.
    /// </summary>
    /// <param name="name">The set-up, as declared.</param>
    /// <param name="setUp">The set-up.</param>
    /// <param name="given">The values given, by input, matched ignoring case.</param>
    /// <param name="subject">What gives them, as a refusal names it: <c>--input</c>, or what names the set-up.</param>
    /// <param name="problems">Every reason the values cannot be had; empty where they can.</param>
    /// <remarks>
    /// The one reading of a set-up's values, for the file as it is checked, for <c>up</c> and <c>down</c>, and for a leg
    /// that sets up: an input left out where it has no default would reach the line as its own text in braces, and a
    /// value for no input changes nothing while saying it did.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Resolve(
        string name,
        SetUpConfig setUp,
        IReadOnlyDictionary<string, string> given,
        string subject,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(setUp);
        ArgumentNullException.ThrowIfNull(given);

        var found = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var declared = setUp.Inputs.Count == 0 ? "it declares none" : $"it declares {string.Join(", ", setUp.Inputs.Keys)}";

        foreach (var twice in given.Keys.GroupBy(input => input, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            found.Add($"{subject} gives input '{twice.Key}' of set-up '{name}' {twice.Count()} values, as {string.Join(" and ", twice.Select(input => $"'{input}'"))}: an input is named ignoring case");
        }

        foreach (var (input, value) in given)
        {
            if (!setUp.Inputs.ContainsKey(input))
            {
                found.Add($"{subject} gives '{input}' a value, and set-up '{name}' declares no such input: {declared}");
            }
            else if (value.Length == 0)
            {
                found.Add($"{subject} gives input '{input}' of set-up '{name}' no value");
            }
        }

        foreach (var (input, declaration) in setUp.Inputs)
        {
            if (given.FirstOrDefault(pair => string.Equals(pair.Key, input, StringComparison.OrdinalIgnoreCase)) is { Key: not null, Value.Length: > 0 } pair)
            {
                values[input] = pair.Value;
            }
            else if (declaration.Default is { } fallback)
            {
                values[input] = fallback;
            }
            else
            {
                found.Add($"{subject} gives input '{input}' of set-up '{name}' no value, and it has no default"
                    + (declaration.Description is { Length: > 0 } what ? $" ({what})" : string.Empty));
            }
        }

        problems = found;

        return values;
    }

    /// <summary>The values as a line says them after the set-up's name: <c> (profile=ci, port=5432)</c>, or nothing.</summary>
    /// <param name="values">The values, by input.</param>
    public static string Said(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values.Count == 0 ? string.Empty : $" ({string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"))})";
    }
}

/// <summary>
/// Reads a <see cref="SetUpUse"/> written as a set-up's name or as an object naming it with values, and writes it back
/// in the shape that says it: the name alone where it gives no value.
/// </summary>
public sealed class SetUpUseConverter : JsonConverter<SetUpUse>
{
    /// <inheritdoc />
    public override SetUpUse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new SetUpUse { Name = reader.GetString() ?? string.Empty };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"A setUp entry is a set-up's name, or {{ \"name\": ..., \"with\": {{ ... }} }}, found {reader.TokenType}.");
        }

        var written = JsonSerializer.Deserialize<Written>(ref reader, options)
            ?? throw new JsonException("A setUp entry is a set-up's name, or { \"name\": ..., \"with\": { ... } }, found null.");

        return new SetUpUse { Name = written.Name, With = new(written.With ?? [], StringComparer.OrdinalIgnoreCase) };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SetUpUse value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.With.Count == 0)
        {
            writer.WriteStringValue(value.Name);
            return;
        }

        JsonSerializer.Serialize(writer, new Written { Name = value.Name, With = value.With }, options);
    }

    /// <summary>The object form, read with the file's own rules: an unknown key, or one written twice, is refused.</summary>
    private sealed class Written
    {
        public required string Name { get; init; }

        public Dictionary<string, string>? With { get; init; }
    }
}
