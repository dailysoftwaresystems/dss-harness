using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Schemas;

/// <summary>
/// The one validator of a workflow's schemas and of the answers held to them: a small subset of JSON Schema,
/// written here rather than taken from a library so that every refusal is a short JSON path a model can act on.
/// </summary>
/// <remarks>
/// <para>
/// What a schema may say: <c>type</c> (object, array, string, integer, number, boolean, null, or one of those
/// with null as <c>["t","null"]</c>), <c>properties</c> and <c>required</c>, <c>additionalProperties: false</c> -
/// which every object holds - <c>items</c>, a scalar <c>enum</c> and <c>const</c>, <c>anyOf</c>, a local
/// non-recursive <c>$ref</c> to one of the <c>$defs</c> the root holds, <c>description</c>, <c>title</c> and
/// <c>default</c>, <c>minItems</c> and <c>maxItems</c>, the three <c>format</c>s that are checked (RFC 3339's
/// date-time and full-date, and a uuid written in its hyphenated form) and an ECMA-262 <c>pattern</c>, read within
/// one second. Anything else is refused where the definition is checked, naming the place in the schema that says
/// it. A <c>$ref</c> stands alone, beside nothing but words that say nothing of a document - and, at the root, the
/// definitions it names - since the drafts disagree on whether what is said beside one is read at all.
/// </para>
/// <para>
/// The same class answers whether a document answers a schema, and its problems name the document's own path -
/// <c>$.output.findings[2].severity: "urgent" is not one of ...</c>, a name no identifier spells written
/// <c>$["a.b"]</c> - for a person, and for a model asked to repair its answer. Every key a schema says holds at
/// once: <c>anyOf</c> is answered by any one of its shapes, and what is said beside it is checked all the same. A
/// schema that says what the subset does not is never walked: the document check refuses it as its caller's
/// defect. Both checks are pure, over the nodes as they were parsed, and each reads a definition once however
/// many references reach it.
/// </para>
/// </remarks>
public static class JsonSchemaSubset
{
    /// <summary>The types a schema may name.</summary>
    private static readonly IReadOnlyList<string> Types = ["object", "array", "string", "integer", "number", "boolean", "null"];

    /// <summary>The formats that are checked; every other is refused where the schema is.</summary>
    private static readonly IReadOnlyList<string> Formats = ["date-time", "date", "uuid"];

    /// <summary>The keys that bound an array's length.</summary>
    private static readonly IReadOnlyList<string> Bounds = ["minItems", "maxItems"];

    /// <summary>
    /// What a <c>$ref</c> stands beside: words that say nothing of a document, and the definitions, which only the
    /// root holds.
    /// </summary>
    private static readonly IReadOnlyList<string> BesideAReference = ["description", "title", "default", "$defs"];

    /// <summary>How a <c>$ref</c> names a definition: the name follows, and the root's <c>$defs</c> holds it.</summary>
    private const string Definitions = "#/$defs/";

    /// <summary>A pattern is read within one second, so a hostile one cannot hold a run.</summary>
    private static readonly TimeSpan PatternLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How a value or a name is written into a problem: as JSON, escaping only what JSON itself must, so a model
    /// reads <c>"café"</c> as it wrote it. Nothing here is ever emitted into a web page.
    /// </summary>
    private static readonly JsonSerializerOptions Written = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Why <paramref name="schema"/> is not a schema this engine reads, or nothing where it is: a key the subset
    /// does not take, a value of the wrong shape, an object without its <c>additionalProperties: false</c>, a
    /// <c>$ref</c> that points nowhere or into itself. Each problem names the place in the schema that says it,
    /// as a JSON path from the schema's root, and is said once: a definition's own problems are named where it is
    /// defined, however many references reach it.
    /// </summary>
    /// <param name="schema">A schema, as a file parsed it.</param>
    public static IReadOnlyList<string> SubsetProblems(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var problems = new List<string>();
        var references = new List<Reference>();

        Walk(schema, "$", schema, null, problems, references);
        problems.AddRange(Recursions(references));

        return problems;
    }

    /// <summary>
    /// Why <paramref name="document"/> does not answer <paramref name="schema"/>, or nothing where it does: a
    /// value of the wrong type, a required property missing, a property the schema says nothing of, a scalar no
    /// <c>enum</c> holds, a format or a pattern unmet. Each problem names the document's own path, so a model
    /// asked to repair its answer is told exactly where it broke.
    /// </summary>
    /// <param name="schema">The schema to answer, free of subset problems.</param>
    /// <param name="document">The answer to check; JSON null is a null document.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="schema"/> has subset problems: no document is held to a schema this engine does not read,
    /// and a caller that asks has skipped <see cref="SubsetProblems"/> where the schema was loaded.
    /// </exception>
    public static IReadOnlyList<string> Problems(JsonNode schema, JsonNode? document)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var subset = SubsetProblems(schema);

        if (subset.Count > 0)
        {
            throw new ArgumentException(
                $"The schema is not one this engine reads, so no document is held to it: {string.Join("; ", subset)}.",
                nameof(schema));
        }

        var root = (JsonObject)schema;

        return Check(root, document, "$", new DocumentCheck(root));
    }

    /// <summary>A <c>$ref</c> as the walk found it: the definition it sits in, if any, where it is, and the definition it names.</summary>
    /// <param name="Owner">The name of the root's definition the reference is written in; <see langword="null"/> outside them.</param>
    /// <param name="At">The reference's own path in the schema.</param>
    /// <param name="Target">The name of the definition it reaches.</param>
    private readonly record struct Reference(string? Owner, string At, string Target);

    /// <summary>
    /// Walks a schema, refusing what the subset does not say. Every node is walked once, where it is written: a
    /// <c>$ref</c> is noted, never followed, so its definition is walked where the root defines it.
    /// </summary>
    private static void Walk(
        JsonNode? node,
        string at,
        JsonNode root,
        string? owner,
        List<string> problems,
        List<Reference> references)
    {
        if (node is not JsonObject schema)
        {
            problems.Add($"{at}: a schema is a JSON object");
            return;
        }

        var referred = schema.ContainsKey("$ref");

        foreach (var (key, value) in schema)
        {
            if (referred && key != "$ref" && !BesideAReference.Contains(key, StringComparer.Ordinal))
            {
                problems.Add($"{at}{Member(key)}: is said beside $ref, which stands alone; say it in the definition");
                continue;
            }

            switch (key)
            {
                case "description" or "title" or "default":
                    break;

                case "type":
                    TypeProblems(value, at, problems);
                    break;

                case "properties":
                    if (value is JsonObject properties)
                    {
                        foreach (var (name, property) in properties)
                        {
                            Walk(property, $"{at}.properties{Member(name)}", root, owner, problems, references);
                        }
                    }
                    else
                    {
                        problems.Add($"{at}.properties: is not a mapping of properties to schemas");
                    }

                    break;

                case "required":
                    if (value is not JsonArray required || required.Any(name => name is null || name.GetValueKind() != JsonValueKind.String))
                    {
                        problems.Add($"{at}.required: is not a list of property names");
                    }
                    else if (schema["properties"] is JsonObject named)
                    {
                        foreach (var name in required)
                        {
                            if (!named.ContainsKey(name!.GetValue<string>()))
                            {
                                problems.Add($"{at}.required: '{name.GetValue<string>()}' is named by no property");
                            }
                        }
                    }
                    else
                    {
                        problems.Add($"{at}.required: names properties the schema does not say; name them under properties");
                    }

                    break;

                case "additionalProperties":
                    if (value is not JsonValue isFalse || isFalse.GetValueKind() != JsonValueKind.False)
                    {
                        problems.Add($"{at}.additionalProperties: the subset takes false, always, so every property is one the schema names");
                    }

                    break;

                case "items":
                    Walk(value, $"{at}.items", root, owner, problems, references);
                    break;

                case "enum":
                    if (value is not JsonArray options || options.Count == 0 || options.Any(option => option is not null and not JsonValue))
                    {
                        problems.Add($"{at}.enum: is not a non-empty list of scalars");
                    }

                    break;

                case "const":
                    if (value is not null and not JsonValue)
                    {
                        problems.Add($"{at}.const: is not a scalar");
                    }

                    break;

                case "anyOf":
                    if (value is JsonArray shapes && shapes.Count > 0)
                    {
                        foreach (var (shape, index) in shapes.Select((shape, index) => (shape, index)))
                        {
                            Walk(shape, $"{at}.anyOf[{index}]", root, owner, problems, references);
                        }
                    }
                    else
                    {
                        problems.Add($"{at}.anyOf: is not a non-empty list of schemas");
                    }

                    break;

                case "$ref":
                    if (value is not JsonValue reference || reference.GetValueKind() != JsonValueKind.String)
                    {
                        problems.Add($"{at}.$ref: is not the name of a definition");
                    }
                    else if (Definition(root, reference.GetValue<string>()) is { } target)
                    {
                        references.Add(new Reference(owner, $"{at}.$ref", target));
                    }
                    else
                    {
                        problems.Add($"{at}.$ref: '{reference.GetValue<string>()}' names no definition of this schema");
                    }

                    break;

                case "$defs":
                    if (!ReferenceEquals(schema, root))
                    {
                        problems.Add($"{at}.$defs: definitions are held at the schema's root, where '{Definitions}name' finds them");
                    }
                    else if (value is JsonObject definitions)
                    {
                        foreach (var (name, definition) in definitions)
                        {
                            Walk(definition, $"{at}.$defs{Member(name)}", root, name, problems, references);
                        }
                    }
                    else
                    {
                        problems.Add($"{at}.$defs: is not a mapping of definitions to schemas");
                    }

                    break;

                case "minItems":
                    if (value is not JsonValue number || number.GetValueKind() != JsonValueKind.Number
                        || !number.TryGetValue<int>(out var minItems) || minItems < 0)
                    {
                        problems.Add($"{at}.minItems: is not a whole number from 0");
                    }

                    break;

                case "maxItems":
                    if (value is not JsonValue most || most.GetValueKind() != JsonValueKind.Number
                        || !most.TryGetValue<int>(out var maxItems) || maxItems < 0)
                    {
                        problems.Add($"{at}.maxItems: is not a whole number from 0");
                    }

                    break;

                case "format":
                    if (value is not JsonValue format || format.GetValueKind() != JsonValueKind.String
                        || !Formats.Contains(format.GetValue<string>(), StringComparer.Ordinal))
                    {
                        problems.Add($"{at}.format: is not one of {string.Join(", ", Formats)}");
                    }

                    break;

                case "pattern":
                    if (value is not JsonValue pattern || pattern.GetValueKind() != JsonValueKind.String)
                    {
                        problems.Add($"{at}.pattern: is not text");
                    }
                    else if (ReadPattern(pattern.GetValue<string>(), out var unread) is null)
                    {
                        problems.Add($"{at}.pattern: {unread}");
                    }

                    break;

                default:
                    problems.Add($"{at}{Member(key)}: is said by no schema this engine reads");
                    break;
            }
        }

        // What stands beside a reference was refused key by key above, and is not refused again for what it says.
        if (referred)
        {
            return;
        }

        // Every object says what it holds, so a property the schema does not name is refused, and never slips
        // past by a schema that says nothing.
        if (SaysType(schema, "object") && !schema.ContainsKey("additionalProperties"))
        {
            problems.Add($"{at}: an object holds additionalProperties: false, saying every property is one it names");
        }

        if (schema.ContainsKey("items") && !SaysType(schema, "array"))
        {
            problems.Add($"{at}.items: only an array's items are said");
        }

        foreach (var bound in Bounds)
        {
            if (schema.ContainsKey(bound) && !SaysType(schema, "array"))
            {
                problems.Add($"{at}.{bound}: only an array's length is bounded");
            }
        }

        if (schema.ContainsKey("format") && !SaysType(schema, "string"))
        {
            problems.Add($"{at}.format: only a string is formatted");
        }

        if (schema.ContainsKey("pattern") && !SaysType(schema, "string"))
        {
            problems.Add($"{at}.pattern: only a string is matched");
        }
    }

    /// <summary>
    /// Where a definition reaches itself: each reference, written in a definition, to one that leads back to it -
    /// directly or through others - named once at the reference's own path. A reference outside the definitions
    /// recurses through none of them, since nothing refers to the root.
    /// </summary>
    private static IEnumerable<string> Recursions(IReadOnlyList<Reference> references)
    {
        var inside = references.Where(reference => reference.Owner is not null).ToList();
        var reaches = inside.ToLookup(reference => reference.Owner!, reference => reference.Target, StringComparer.Ordinal);

        foreach (var reference in inside)
        {
            if (Leads(reaches, reference.Target, reference.Owner!))
            {
                yield return $"{reference.At}: '{Definitions}{reference.Target}' reaches itself, and a definition never recurses";
            }
        }
    }

    /// <summary>Whether following references from definition <paramref name="from"/> comes to definition <paramref name="to"/>, itself included.</summary>
    private static bool Leads(ILookup<string, string> reaches, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var waiting = new Stack<string>([from]);

        while (waiting.TryPop(out var name))
        {
            if (string.Equals(name, to, StringComparison.Ordinal))
            {
                return true;
            }

            if (seen.Add(name))
            {
                foreach (var next in reaches[name])
                {
                    waiting.Push(next);
                }
            }
        }

        return false;
    }

    /// <summary>Adds the problems of a <c>type</c> key's value, where it is not one the subset takes.</summary>
    private static void TypeProblems(JsonNode? value, string at, List<string> problems)
    {
        if (value is JsonValue one && one.GetValueKind() == JsonValueKind.String)
        {
            if (!Types.Contains(one.GetValue<string>(), StringComparer.Ordinal))
            {
                problems.Add($"{at}.type: '{one.GetValue<string>()}' is not one of {string.Join(", ", Types)}, or one of them with null");
            }

            return;
        }

        if (value is JsonArray pair
            && pair.Count == 2
            && pair[0] is JsonValue first && first.GetValueKind() == JsonValueKind.String
            && Types.Contains(first.GetValue<string>(), StringComparer.Ordinal) && first.GetValue<string>() != "null"
            && pair[1] is JsonValue second && second.GetValueKind() == JsonValueKind.String && second.GetValue<string>() == "null")
        {
            return;
        }

        problems.Add($"{at}.type: is not one of {string.Join(", ", Types)}, or one of them with null");
    }

    /// <summary>Whether the schema's <c>type</c> says <paramref name="type"/>, alone or with null beside it.</summary>
    private static bool SaysType(JsonObject schema, string type)
    {
        if (schema["type"] is JsonValue one && one.GetValueKind() == JsonValueKind.String)
        {
            return one.GetValue<string>() == type;
        }

        if (schema["type"] is JsonArray pair
            && pair.Count == 2
            && pair[0] is JsonValue first && first.GetValueKind() == JsonValueKind.String)
        {
            return first.GetValue<string>() == type;
        }

        return false;
    }

    /// <summary>The schema's type as the check reads it: the type named, two joined by '+', or <see langword="null"/> where none is.</summary>
    private static string? TypeOf(JsonObject schema)
    {
        if (schema["type"] is JsonValue one && one.GetValueKind() == JsonValueKind.String)
        {
            return one.GetValue<string>();
        }

        if (schema["type"] is JsonArray pair && pair.Count == 2 && pair[0] is JsonValue first && first.GetValueKind() == JsonValueKind.String)
        {
            return $"{first.GetValue<string>()}+null";
        }

        return null;
    }

    /// <summary>
    /// One document's check: the schema's root, and what each definition was found to say of each place in the
    /// document, so a definition many references reach - through the shapes of an <c>anyOf</c>, say - is read
    /// once at each place, never once per way of reaching it.
    /// </summary>
    /// <param name="Root">The schema's root, which holds the definitions.</param>
    private sealed record DocumentCheck(JsonObject Root)
    {
        /// <summary>The problems a definition, by name, found at a place in the document, by path.</summary>
        public Dictionary<(string Definition, string At), IReadOnlyList<string>> Read { get; } = new();
    }

    /// <summary>
    /// Checks a document against a schema the subset check passed, naming the document's own path at every break.
    /// A path names one place in the document, since a name no identifier spells is written in brackets.
    /// </summary>
    private static IReadOnlyList<string> Check(JsonObject said, JsonNode? document, string at, DocumentCheck check)
    {
        // A reference stands alone, so what it names is the whole of what is said here.
        if (said["$ref"] is JsonValue reference)
        {
            var name = reference.GetValue<string>()[Definitions.Length..];

            if (!check.Read.TryGetValue((name, at), out var read))
            {
                read = Check((JsonObject)check.Root["$defs"]![name]!, document, at, check);
                check.Read[(name, at)] = read;
            }

            return read;
        }

        var problems = new List<string>();

        // Every key holds at once: anyOf asks one of its shapes to answer, and what is said beside it is checked
        // all the same.
        if (said["anyOf"] is JsonArray shapes && !shapes.Any(shape => Check((JsonObject)shape!, document, at, check).Count == 0))
        {
            problems.Add($"{at}: answers none of the {shapes.Count} shape{(shapes.Count == 1 ? string.Empty : "s")} its schema offers");
        }

        if (said.ContainsKey("const") && !JsonValue.DeepEquals(said["const"], document))
        {
            problems.Add($"{at}: {Said(document)} is not {Said(said["const"])}");
        }

        if (said["enum"] is JsonArray options && !options.Any(option => JsonValue.DeepEquals(option, document)))
        {
            problems.Add($"{at}: {Said(document)} is not one of {string.Join(", ", options.Select(option => Said(option)))}");
        }

        if (TypeOf(said) is { } type && !Answers(type, document))
        {
            problems.Add($"{at}: {Said(document)} is not {Kind(type)}");
            return problems;
        }

        if (document is JsonObject map)
        {
            foreach (var name in said["required"] as JsonArray ?? [])
            {
                if (!map.ContainsKey(name!.GetValue<string>()))
                {
                    problems.Add($"{at}: '{name.GetValue<string>()}' is required and missing");
                }
            }

            var properties = said["properties"] as JsonObject;

            // Only a schema that closes its properties refuses one it does not name: an anyOf's shapes speak for
            // the properties of a schema that says nothing of them itself.
            var closed = said["additionalProperties"] is JsonValue additional && additional.GetValueKind() == JsonValueKind.False;

            foreach (var (name, value) in map)
            {
                if (properties?[name] is JsonObject property)
                {
                    problems.AddRange(Check(property, value, $"{at}{Member(name)}", check));
                }
                else if (closed)
                {
                    problems.Add($"{at}{Member(name)}: is said by no property of the schema");
                }
            }

            return problems;
        }

        if (document is JsonArray list)
        {
            if (said["minItems"] is JsonValue minimum && minimum.TryGetValue<int>(out var least) && list.Count < least)
            {
                problems.Add($"{at}: holds {list.Count} item{(list.Count == 1 ? string.Empty : "s")} and at least {least} {(least == 1 ? "is" : "are")} asked");
            }

            if (said["maxItems"] is JsonValue most && most.TryGetValue<int>(out var cap) && list.Count > cap)
            {
                problems.Add($"{at}: holds {list.Count} item{(list.Count == 1 ? string.Empty : "s")} and at most {cap} {(cap == 1 ? "is" : "are")} taken");
            }

            if (said["items"] is JsonObject items)
            {
                foreach (var (value, index) in list.Select((value, index) => (value, index)))
                {
                    problems.AddRange(Check(items, value, $"{at}[{index}]", check));
                }
            }

            return problems;
        }

        if (document is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.String)
        {
            return problems;
        }

        var text = scalar.GetValue<string>();

        if (said["format"] is JsonValue format && !FormatHolds(format.GetValue<string>(), text))
        {
            problems.Add($"{at}: {Said(document)} is not a {format.GetValue<string>()}");
        }

        if (said["pattern"] is JsonValue pattern)
        {
            try
            {
                if (!ReadPattern(pattern.GetValue<string>(), out _)!.IsMatch(text))
                {
                    problems.Add($"{at}: {Said(document)} does not match its pattern");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                problems.Add($"{at}: its pattern could not be read within a second");
            }
        }

        return problems;
    }

    /// <summary>Whether a document answers the schema's type, null answering only null and a type with null.</summary>
    private static bool Answers(string type, JsonNode? document)
    {
        foreach (var one in type.Split('+'))
        {
            if (one == "null")
            {
                if (document is null)
                {
                    return true;
                }

                continue;
            }

            if (document is null)
            {
                continue;
            }

            var answered = one switch
            {
                "object" => document is JsonObject,
                "array" => document is JsonArray,
                "string" => document is JsonValue value && value.GetValueKind() == JsonValueKind.String,
                "boolean" => document is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
                "integer" => document is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out long _),
                "number" => document is JsonValue value && value.GetValueKind() == JsonValueKind.Number,
                _ => false,
            };

            if (answered)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A type as a refusal says it: <c>an integer</c>, <c>a string or null</c>, <c>null</c>.</summary>
    private static string Kind(string type)
    {
        var first = type.Split('+')[0];
        var article = first == "null" ? string.Empty : (first[0] is 'a' or 'e' or 'i' or 'o' or 'u') ? "an " : "a ";

        return article + type.Replace("+", " or ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a format's text holds: an RFC 3339 date-time or full-date, or a uuid in its hyphenated form - the
    /// three the subset check lets a schema name.
    /// </summary>
    private static bool FormatHolds(string format, string text)
        => format switch
        {
            "date-time" => IsDateTime(text),
            "date" => IsFullDate(text),
            "uuid" => IsUuid(text),
            _ => throw new UnreachableException($"'{format}' is a format the subset check refuses, and no document is held to it."),
        };

    /// <summary>
    /// Whether text is an RFC 3339 date-time: a full-date, <c>T</c>, the time to the second with any fraction of
    /// it, and <c>Z</c> or the offset from UTC - <c>2026-10-18T10:00:00.123Z</c>. Each part is held to its range,
    /// and a leap second is taken only where it falls, in the last minute of a UTC day.
    /// </summary>
    private static bool IsDateTime(ReadOnlySpan<char> text)
    {
        if (text.Length < "yyyy-mm-ddThh:mm:ssZ".Length
            || !IsFullDate(text[..10])
            || text[10] is not ('T' or 't')
            || text[13] != ':'
            || text[16] != ':'
            || !Digits(text[11..13], out var hour)
            || !Digits(text[14..16], out var minute)
            || !Digits(text[17..19], out var second)
            || hour > 23
            || minute > 59
            || second > 60)
        {
            return false;
        }

        var rest = text[19..];

        if (rest[0] == '.')
        {
            var fraction = 1;

            while (fraction < rest.Length && char.IsAsciiDigit(rest[fraction]))
            {
                fraction++;
            }

            if (fraction == 1)
            {
                return false;
            }

            rest = rest[fraction..];
        }

        int offset;

        if (rest is ['Z' or 'z'])
        {
            offset = 0;
        }
        else if (rest.Length == "+hh:mm".Length
            && rest[0] is ('+' or '-')
            && rest[3] == ':'
            && Digits(rest[1..3], out var offsetHour)
            && Digits(rest[4..], out var offsetMinute)
            && offsetHour <= 23
            && offsetMinute <= 59)
        {
            offset = (rest[0] == '-' ? -1 : 1) * ((offsetHour * 60) + offsetMinute);
        }
        else
        {
            return false;
        }

        const int Day = 24 * 60;

        return second < 60 || ((((hour * 60) + minute - offset) % Day) + Day) % Day == Day - 1;
    }

    /// <summary>Whether text is an RFC 3339 full-date, <c>yyyy-mm-dd</c>, on a day its month holds.</summary>
    private static bool IsFullDate(ReadOnlySpan<char> text)
    {
        if (text.Length != "yyyy-mm-dd".Length
            || text[4] != '-'
            || text[7] != '-'
            || !Digits(text[..4], out var year)
            || !Digits(text[5..7], out var month)
            || !Digits(text[8..], out var day)
            || month is < 1 or > 12
            || day < 1)
        {
            return false;
        }

        var leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
        var days = month switch
        {
            2 => leap ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };

        return day <= days;
    }

    /// <summary>Whether text is a uuid in its hyphenated form alone: 8-4-4-4-12 hexadecimal digits, in either case.</summary>
    private static bool IsUuid(ReadOnlySpan<char> text)
    {
        if (text.Length != 36)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var hyphen = index is 8 or 13 or 18 or 23;

            if (hyphen ? text[index] != '-' : !char.IsAsciiHexDigit(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The number written in ASCII digits alone - never another script's - or <see langword="false"/> where it is not.</summary>
    private static bool Digits(ReadOnlySpan<char> text, out int value)
    {
        value = 0;

        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }

            value = (value * 10) + (character - '0');
        }

        return text.Length > 0;
    }

    /// <summary>
    /// A schema's pattern as .NET runs it the way ECMA-262 reads it, or why it cannot be read - the one reading
    /// both checks share, so a pattern the subset check passes is one the document check runs. Read in .NET's
    /// ECMAScript mode, where <c>\d</c> and <c>\w</c> are ASCII; with its end anchor <c>$</c> made the end of the
    /// text alone, where .NET would also take the place before a final line feed; with <c>.</c> taking no line
    /// terminator, where .NET takes all but the line feed; with <c>[]</c> the empty class; and refusing the groups
    /// ECMA-262 does not write - inline options, comments, atomic and conditional groups - since they change what
    /// <c>$</c> and <c>.</c> mean.
    /// </summary>
    /// <param name="pattern">The pattern as the schema writes it.</param>
    /// <param name="problem">Why it cannot be read; <see langword="null"/> where it can.</param>
    private static Regex? ReadPattern(string pattern, out string? problem)
    {
        var read = new StringBuilder(pattern.Length);

        for (var index = 0; index < pattern.Length; index++)
        {
            switch (pattern[index])
            {
                case '\\':
                    // An escape is copied whole, so the character it escapes is never read as an anchor or a dot;
                    // a control escape takes the character after its 'c' as well.
                    var escape = index + 1 < pattern.Length && pattern[index + 1] == 'c' ? 3 : 2;

                    read.Append(pattern, index, Math.Min(escape, pattern.Length - index));
                    index += escape - 1;
                    break;

                case '[':
                    index = CopyClass(pattern, index, read);
                    break;

                case '(' when index + 1 < pattern.Length && pattern[index + 1] == '?':
                    if (GroupNotEcma(pattern.AsSpan(index + 2)) is { } group)
                    {
                        problem = $"'(?{group}' is not ECMA-262, the dialect a pattern is written in";
                        return null;
                    }

                    read.Append('(');
                    break;

                case '$':
                    read.Append(@"\z");
                    break;

                case '.':
                    read.Append(@"[^\n\r\p{Zl}\p{Zp}]");
                    break;

                default:
                    read.Append(pattern[index]);
                    break;
            }
        }

        try
        {
            problem = null;
            return new Regex(read.ToString(), RegexOptions.ECMAScript, PatternLimit);
        }
        catch (ArgumentException)
        {
            problem = "is not a pattern this engine can read";
            return null;
        }
    }

    /// <summary>
    /// Copies a character class whole and answers the index of its closing bracket. A class closes at its first
    /// unescaped <c>]</c>, as ECMA-262 reads it: <c>[^]</c> takes any one character, as .NET's ECMAScript mode
    /// reads it too, and <c>[]</c> is the empty class no character answers - written as a group that matches
    /// nothing, since .NET would read that bracket as one of the class's characters. Within a class <c>$</c> and
    /// <c>.</c> are characters, so nothing in it is rewritten.
    /// </summary>
    private static int CopyClass(string pattern, int index, StringBuilder read)
    {
        if (index + 1 < pattern.Length && pattern[index + 1] == ']')
        {
            read.Append("(?!)");
            return index + 1;
        }

        read.Append('[');
        index++;

        if (index < pattern.Length && pattern[index] == '^')
        {
            read.Append('^');
            index++;
        }

        for (; index < pattern.Length; index++)
        {
            read.Append(pattern[index]);

            if (pattern[index] == '\\' && index + 1 < pattern.Length)
            {
                read.Append(pattern[++index]);
            }
            else if (pattern[index] == ']')
            {
                return index;
            }
        }

        // Unclosed: the pattern is refused where it is compiled.
        return index;
    }

    /// <summary>
    /// What follows <c>(?</c> where ECMA-262 does not write it, or <see langword="null"/> where it does: a
    /// non-capturing group, a lookahead or lookbehind, or a group named as an identifier.
    /// </summary>
    private static string? GroupNotEcma(ReadOnlySpan<char> rest)
    {
        if (rest is [':' or '=' or '!', ..] or ['<', '=' or '!', ..])
        {
            return null;
        }

        // .NET names a group in word characters, so the names both read are identifiers without '$'.
        if (rest is ['<', ..] && rest.IndexOf('>') is var close and > 1 && IsIdentifier(rest[1..close], dollar: false))
        {
            return null;
        }

        return rest.IsEmpty ? string.Empty : rest[..1].ToString();
    }

    /// <summary>
    /// A member of a path: <c>.name</c> where the name is an identifier, and <c>["a.b"]</c>, written as JSON,
    /// where it is anything else - so a path names one place, and a name holding a dot is never read as two.
    /// </summary>
    private static string Member(string name)
        => IsIdentifier(name, dollar: true) ? $".{name}" : $"[{JsonValue.Create(name).ToJsonString(Written)}]";

    /// <summary>Whether a name is an identifier in ASCII: a letter or '_' - or '$', where it is taken - then those or digits.</summary>
    private static bool IsIdentifier(ReadOnlySpan<char> name, bool dollar)
    {
        if (name.IsEmpty || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_' && !(dollar && character == '$'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The name of the root's definition a <c>$ref</c> reaches - <c>#/$defs/name</c> - or <see langword="null"/>
    /// where it reaches none.
    /// </summary>
    private static string? Definition(JsonNode root, string reference)
    {
        if (!reference.StartsWith(Definitions, StringComparison.Ordinal))
        {
            return null;
        }

        var name = reference[Definitions.Length..];

        return (root as JsonObject)?["$defs"] is JsonObject definitions && definitions.ContainsKey(name)
            ? name
            : null;
    }

    /// <summary>A document's value as a refusal says it: a scalar in JSON, a structure by its kind.</summary>
    private static string Said(JsonNode? document)
        => document switch
        {
            null => "null",
            JsonObject => "an object",
            JsonArray => "an array",
            _ => document.ToJsonString(Written),
        };
}
