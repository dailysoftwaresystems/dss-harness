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
/// non-recursive <c>$ref</c>, <c>description</c>, <c>title</c> and <c>default</c>, <c>minItems</c>, the three
/// <c>format</c>s that are checked (date-time, date, uuid) and <c>pattern</c>, read within one second. Anything
/// else is refused where the definition is checked, naming the place in the schema that says it.
/// </para>
/// <para>
/// The same class answers whether a document answers a schema, and its problems name the document's own path -
/// <c>$.output.findings[2].severity: "urgent" is not one of ...</c> - for a person, and for a model asked to
/// repair its answer. A schema that says what the subset does not is never walked: the two checks are both pure,
/// over the nodes as they were parsed.
/// </para>
/// </remarks>
public static class JsonSchemaSubset
{
    /// <summary>The types a schema may name.</summary>
    private static readonly IReadOnlyList<string> Types = ["object", "array", "string", "integer", "number", "boolean", "null"];

    /// <summary>The formats that are checked; every other is refused where the schema is.</summary>
    private static readonly IReadOnlyList<string> Formats = ["date-time", "date", "uuid"];

    /// <summary>A pattern is read within one second, so a hostile one cannot hold a run.</summary>
    private static readonly TimeSpan PatternLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Why <paramref name="schema"/> is not a schema this engine reads, or nothing where it is: a key the subset
    /// does not take, a value of the wrong shape, an object without its <c>additionalProperties: false</c>, a
    /// <c>$ref</c> that points nowhere or into itself. Each problem names the place in the schema that says it,
    /// as a JSON path from the schema's root.
    /// </summary>
    /// <param name="schema">A schema, as a file parsed it.</param>
    public static IReadOnlyList<string> SubsetProblems(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return Walk(schema, "$", schema, [], []);
    }

    /// <summary>
    /// Why <paramref name="document"/> does not answer <paramref name="schema"/>, or nothing where it does: a
    /// value of the wrong type, a required property missing, a property the schema says nothing of, a scalar no
    /// <c>enum</c> holds, a format or a pattern unmet. Each problem names the document's own path, so a model
    /// asked to repair its answer is told exactly where it broke.
    /// </summary>
    /// <param name="schema">The schema to answer, already free of subset problems.</param>
    /// <param name="document">The answer to check; JSON null is a null document.</param>
    public static IReadOnlyList<string> Problems(JsonNode schema, JsonNode? document)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return Check(schema, document, "$", schema, []);
    }

    /// <summary>Walks a schema, refusing what the subset does not say.</summary>
    private static IReadOnlyList<string> Walk(
        JsonNode? node,
        string at,
        JsonNode root,
        List<string> problems,
        IReadOnlyList<JsonNode> refs)
    {
        if (node is not JsonObject schema)
        {
            problems.Add($"{at}: a schema is a JSON object");
            return problems;
        }

        foreach (var (key, value) in schema)
        {
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
                            Walk(property, $"{at}.properties.{name}", root, problems, refs);
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
                    Walk(value, $"{at}.items", root, problems, refs);
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
                            Walk(shape, $"{at}.anyOf[{index}]", root, problems, refs);
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
                        break;
                    }

                    var target = Resolve(root, reference.GetValue<string>());

                    if (target is null)
                    {
                        problems.Add($"{at}.$ref: '{reference.GetValue<string>()}' names no definition of this schema");
                    }
                    else if (refs.Contains(target, ReferenceEqualityComparer.Instance))
                    {
                        problems.Add($"{at}.$ref: '{reference.GetValue<string>()}' reaches itself, and a definition never recurses");
                    }
                    else
                    {
                        Walk(target, $"{at}.$ref", root, problems, [.. refs, target]);
                    }

                    break;

                case "$defs":
                    if (value is JsonObject definitions)
                    {
                        foreach (var (name, definition) in definitions)
                        {
                            Walk(definition, $"{at}.$defs.{name}", root, problems, []);
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
                    else
                    {
                        try
                        {
                            _ = new Regex(pattern.GetValue<string>(), RegexOptions.None, PatternLimit);
                        }
                        catch (ArgumentException)
                        {
                            problems.Add($"{at}.pattern: is not a pattern this engine can read");
                        }
                    }

                    break;

                default:
                    problems.Add($"{at}.{key}: is said by no schema this engine reads");
                    break;
            }
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

        if ((schema.ContainsKey("minItems") || schema.ContainsKey("maxItems")) && !SaysType(schema, "array"))
        {
            problems.Add($"{at}: only an array's length is bounded");
        }

        if (schema.ContainsKey("format") && !SaysType(schema, "string"))
        {
            problems.Add($"{at}.format: only a string is formatted");
        }

        if (schema.ContainsKey("pattern") && !SaysType(schema, "string"))
        {
            problems.Add($"{at}.pattern: only a string is matched");
        }

        return problems;
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

    /// <summary>Checks a document against a schema, naming the document's own path at every break.</summary>
    private static IReadOnlyList<string> Check(
        JsonNode? schema,
        JsonNode? document,
        string at,
        JsonNode root,
        IReadOnlyList<JsonNode> refs)
    {
        var problems = new List<string>();

        if (schema is not JsonObject said)
        {
            return problems;
        }

        // A reference is followed first, and never twice in one place: a schema that reaches itself is refused
        // where the definition is checked, and never walked here.
        if (said["$ref"] is JsonValue reference && reference.GetValueKind() == JsonValueKind.String)
        {
            if (Resolve(root, reference.GetValue<string>()) is { } target
                && !refs.Contains(target, ReferenceEqualityComparer.Instance))
            {
                problems.AddRange(Check(target, document, at, root, [.. refs, target]));
            }

            return problems;
        }

        if (said["anyOf"] is JsonArray shapes)
        {
            foreach (var shape in shapes)
            {
                if (Check(shape, document, at, root, refs).Count == 0)
                {
                    return problems;
                }
            }

            // No shape answered; the rest of this schema is said beside anyOf and checked beside its refusal.
            problems.Add($"{at}: answers none of the {shapes.Count} shapes its schema offers");
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
            problems.Add($"{at}: {Said(document)} is not a {type.Replace("+", " or ")}");
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

            var properties = said["properties"] as JsonObject ?? [];

            foreach (var (name, value) in map)
            {
                if (properties.ContainsKey(name))
                {
                    problems.AddRange(Check(properties[name], value, $"{at}.{name}", root, refs));
                }
                else
                {
                    problems.Add($"{at}.{name}: is said by no property of the schema");
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

            if (said["items"] is { } items)
            {
                foreach (var (value, index) in list.Select((value, index) => (value, index)))
                {
                    problems.AddRange(Check(items, value, $"{at}[{index}]", root, refs));
                }
            }
        }

        if (document is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String && said["format"] is JsonValue format
            && !FormatHolds(format.GetValue<string>(), scalar.GetValue<string>()))
        {
            problems.Add($"{at}: {Said(document)} is not a {format.GetValue<string>()}");
        }

        if (document is JsonValue matched && matched.GetValueKind() == JsonValueKind.String && said["pattern"] is JsonValue pattern)
        {
            try
            {
                if (!new Regex(pattern.GetValue<string>(), RegexOptions.None, PatternLimit).IsMatch(matched.GetValue<string>()))
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

    /// <summary>Whether a format's text holds: an ISO 8601 date with time, its date alone, or a UUID.</summary>
    private static bool FormatHolds(string format, string text)
        => format switch
        {
            "date-time" => DateTime.TryParseExact(
                text,
                ["o", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:sszzz"],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _),
            "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _),
            "uuid" => Guid.TryParseExact(text, "D", out _) || Guid.TryParseExact(text, "B", out _),
            _ => true,
        };

    /// <summary>Resolves a local <c>$ref</c> - <c>#/$defs/name</c> - within the root schema; <see langword="null"/> where it points nowhere.</summary>
    private static JsonNode? Resolve(JsonNode root, string reference)
    {
        if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal))
        {
            return null;
        }

        var name = reference["#/$defs/".Length..];

        return (root as JsonObject)?["$defs"] is JsonObject defs && defs.ContainsKey(name)
            ? defs[name]
            : null;
    }

    /// <summary>A document's value as a refusal says it: a scalar in JSON, a structure by its kind.</summary>
    private static string Said(JsonNode? document)
        => document switch
        {
            null => "null",
            JsonObject => "an object",
            JsonArray => "an array",
            _ => document.ToJsonString(),
        };
}
