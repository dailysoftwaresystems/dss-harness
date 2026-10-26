using System.Diagnostics;
using System.Globalization;
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
/// with null as <c>["t","null"]</c>; an integer is any number with no fraction, <c>7.0</c> among them),
/// <c>properties</c> and <c>required</c>, <c>additionalProperties: false</c> - which every object holds -
/// <c>items</c>, a scalar <c>enum</c> and <c>const</c>, <c>anyOf</c>, a local non-recursive <c>$ref</c> to one of
/// the <c>$defs</c> the root holds, <c>description</c>, <c>title</c> and <c>default</c>, <c>minItems</c> and
/// <c>maxItems</c>, the three <c>format</c>s that are checked (RFC 3339's date-time and full-date, and a uuid written
/// in its hyphenated form) and an ECMA-262 <c>pattern</c> - refused where .NET, which runs it, would read it
/// otherwise - read within one second. Anything else is refused where the definition is checked, naming the place
/// in the schema that says it. Each key is said of its own type alone - <c>properties</c>, <c>required</c> and
/// <c>additionalProperties</c> of an object, <c>items</c> and the bounds of an array, <c>format</c> and
/// <c>pattern</c> of a string - so a schema that names properties says an object, and closes it. A <c>$ref</c>
/// stands alone, beside nothing but words that say nothing of a document - and, at the root, the
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

    /// <summary>The keys that say an object's properties.</summary>
    private static readonly IReadOnlyList<string> ObjectKeys = ["properties", "required", "additionalProperties"];

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
    /// ECMA-262's white space and line terminators, written as a class's members: tab, line feed, vertical tab,
    /// form feed, carriage return, every space separator, the byte order mark, and the line and paragraph
    /// separators.
    /// </summary>
    private const string Space = @"\t\n\v\f\r\p{Zs}\uFEFF\p{Zl}\p{Zp}";

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
    public static IReadOnlyList<string> Problems(JsonNode schema, JsonNode? document) => Problems(schema, document, "$");

    /// <summary>
    /// Why <paramref name="document"/> does not answer <paramref name="schema"/>, as <see cref="Problems(JsonNode, JsonNode?)"/>
    /// says it, each path named from <paramref name="at"/>: where the document stands in a larger one -
    /// <c>$.output</c> for the output an answer's envelope holds.
    /// </summary>
    /// <param name="schema">The schema to answer, free of subset problems.</param>
    /// <param name="document">The answer to check; JSON null is a null document.</param>
    /// <param name="at">The document's own path, from the root of what holds it.</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> has subset problems.</exception>
    public static IReadOnlyList<string> Problems(JsonNode schema, JsonNode? document, string at)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrEmpty(at);

        var subset = SubsetProblems(schema);

        if (subset.Count > 0)
        {
            throw new ArgumentException(
                $"The schema is not one this engine reads, so no document is held to it: {string.Join("; ", subset)}.",
                nameof(schema));
        }

        var root = (JsonObject)schema;

        return Check(root, document, at, new DocumentCheck(root));
    }

    /// <summary>
    /// <paramref name="document"/> with each text an <c>enum</c> or a <c>const</c> of <paramref name="schema"/> holds in
    /// another case spelled as the schema spells it - where exactly one of its values matches it so - and every other
    /// value as it is: a model held to a schema may answer a value in another case (the Claude API's structured outputs
    /// say so), which reads as the value it names. Under an <c>anyOf</c>, the first shape the spelled document answers
    /// spells it. The document is never changed: what comes back is a copy.
    /// </summary>
    /// <param name="schema">The schema the document is held to, free of subset problems.</param>
    /// <param name="document">The document; JSON null is a null document.</param>
    /// <exception cref="ArgumentException"><paramref name="schema"/> has subset problems.</exception>
    public static JsonNode? Respelled(JsonNode schema, JsonNode? document)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var subset = SubsetProblems(schema);

        if (subset.Count > 0)
        {
            throw new ArgumentException($"The schema is not one this engine reads, so no document is spelled by it: {string.Join("; ", subset)}.", nameof(schema));
        }

        var root = (JsonObject)schema;

        return Respell(root, document?.DeepClone(), new DocumentCheck(root));
    }

    /// <summary>
    /// Every schema <paramref name="schema"/> holds, itself first: each of its properties', its items', each shape of its
    /// <c>anyOf</c> and each of its definitions, and every one each of those holds - the places a copy of the schema sent
    /// to a reader that takes less than the subset is changed at.
    /// </summary>
    /// <param name="schema">A schema free of subset problems.</param>
    public static IEnumerable<JsonObject> SchemasIn(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var left = new Stack<JsonObject>([schema]);

        while (left.TryPop(out var each))
        {
            yield return each;

            IEnumerable<JsonNode?> held =
            [
                .. (each["properties"] as JsonObject)?.Select(property => property.Value) ?? [],
                each["items"],
                .. each["anyOf"] as JsonArray ?? [],
                .. (each["$defs"] as JsonObject)?.Select(definition => definition.Value) ?? [],
            ];

            foreach (var inner in held.OfType<JsonObject>().Reverse())
            {
                left.Push(inner);
            }
        }
    }

    /// <summary>
    /// What <paramref name="schema"/> says of the document at its root, as an expression's path or a placeholder is
    /// typed by: the kinds of value it may be, and what its members and its items are said to be.
    /// </summary>
    /// <param name="schema">A schema free of subset problems.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="schema"/> has subset problems: nothing is typed by a schema this engine does not read.
    /// </exception>
    public static Reading Read(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var subset = SubsetProblems(schema);

        if (subset.Count > 0)
        {
            throw new ArgumentException(
                $"The schema is not one this engine reads, so nothing is typed by it: {string.Join("; ", subset)}.",
                nameof(schema));
        }

        return Reading.Of((JsonObject)schema);
    }

    /// <summary>
    /// What is said of the value at one place of a document: the kinds it may be, what a member of it is said to be,
    /// and what an item of it is - each place read from the schemas that say it, an <c>anyOf</c> read as its shapes,
    /// a <c>$ref</c> as the definition it names. Where nothing says what the value is, it may be anything, and so may
    /// its members and its items: a reading refuses only what no value its schema allows could be.
    /// </summary>
    public sealed class Reading
    {
        /// <summary>The ways the value may be, any one of which it is; none where no value is.</summary>
        private readonly IReadOnlyList<Shape> _shapes;

        /// <summary>A reading of the shapes given, any one of which the value is.</summary>
        private Reading(IReadOnlyList<Shape> shapes) => _shapes = shapes;

        /// <summary>
        /// One way the value may be: the schemas that hold of it at once, each with the root its references are read
        /// in; the kinds it is limited to beside them, where something other than a schema limits it; and the reading
        /// of its items, where that is said other than by a schema. No schema and no limit is a value of any kind.
        /// </summary>
        private sealed record Shape(IReadOnlyList<(JsonObject Root, JsonObject Schema)> Said, IReadOnlySet<string>? Limit, Reading? ItemsSaid);

        /// <summary>A value nothing says anything of: any kind, with members and items of any kind.</summary>
        public static Reading Anything { get; } = new([new Shape([], null, null)]);

        /// <summary>A value of one of <paramref name="kinds"/>, and nothing more said of it: its members and its items may be anything.</summary>
        /// <param name="kinds">The kinds, each one the subset names.</param>
        public static Reading Of(params string[] kinds)
        {
            ArgumentNullException.ThrowIfNull(kinds);

            return new([new Shape([], new HashSet<string>(kinds, StringComparer.Ordinal), null)]);
        }

        /// <summary>The document at the root of <paramref name="root"/>, a schema the subset check passed.</summary>
        internal static Reading Of(JsonObject root) => new([.. Shapes(root, [root])]);

        /// <summary>A list whose items are <paramref name="items"/>.</summary>
        /// <param name="items">What each item of the list is.</param>
        public static Reading ListOf(Reading items)
        {
            ArgumentNullException.ThrowIfNull(items);

            return new([new Shape([], new HashSet<string>(["array"], StringComparer.Ordinal), items)]);
        }

        /// <summary>
        /// The kinds the value may be, in the order the subset names them; <see langword="null"/> where it may be any:
        /// a place no schema gives a type, or one an <c>anyOf</c> shape leaves open.
        /// </summary>
        public IReadOnlyList<string>? Kinds
        {
            get
            {
                var kinds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var shape in _shapes)
                {
                    if (KindsOf(shape) is not { } said)
                    {
                        return null;
                    }

                    kinds.UnionWith(said);
                }

                return [.. Types.Where(kinds.Contains)];
            }
        }

        /// <summary>Whether the value may be of <paramref name="kind"/>: always, where it may be any.</summary>
        /// <param name="kind">A kind the subset names.</param>
        public bool MayBe(string kind) => Kinds is not { } kinds || kinds.Contains(kind, StringComparer.Ordinal);

        /// <summary>The value read as either this or <paramref name="other"/>: any shape of the one or of the other.</summary>
        /// <param name="other">The other reading.</param>
        public Reading Or(Reading other)
        {
            ArgumentNullException.ThrowIfNull(other);

            return new([.. _shapes, .. other._shapes]);
        }

        /// <summary>The value read where it is not <paramref name="kind"/>: what is left of each shape once that kind is taken out.</summary>
        /// <param name="kind">A kind the subset names.</param>
        public Reading Without(string kind)
            => new([.. _shapes.Select(shape => shape with
            {
                Limit = new HashSet<string>(
                    ((IEnumerable<string>?)KindsOf(shape) ?? Types).Where(each => !string.Equals(each, kind, StringComparison.Ordinal)),
                    StringComparer.Ordinal),
            })]);

        /// <summary>
        /// What the member <paramref name="name"/> of the value is said to be, or <see langword="null"/> where no value
        /// the reading allows holds it: every object a schema says closes its properties, so a member none of them
        /// names is none. Where a shape says nothing of the value's members, the member may be anything.
        /// </summary>
        /// <param name="name">The member's name.</param>
        public Reading? Member(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            var found = new List<Shape>();

            foreach (var shape in _shapes)
            {
                if (KindsOf(shape) is { } kinds && !kinds.Contains("object"))
                {
                    continue;
                }

                // Every schema that says an object closes it (additionalProperties: false), so the member is one each
                // of them names; where none says an object, nothing here says what the value holds.
                var objects = shape.Said.Where(said => SaysType(said.Schema, "object")).ToList();

                if (objects.Count == 0)
                {
                    found.AddRange(Anything._shapes);
                    continue;
                }

                var named = objects
                    .Select(said => said.Schema["properties"] is JsonObject properties && properties[name] is JsonObject property ? property : null)
                    .ToList();

                if (named.All(property => property is not null))
                {
                    found.AddRange(Shapes(objects[0].Root, [.. named.Select(property => property!)]));
                }
            }

            return found.Count == 0 ? null : new Reading(found);
        }

        /// <summary>
        /// What an item of the value is said to be, or <see langword="null"/> where no value the reading allows is a
        /// list. Where a list's items are not said, an item may be anything.
        /// </summary>
        public Reading? Items
        {
            get
            {
                var found = new List<Shape>();

                foreach (var shape in _shapes)
                {
                    if (KindsOf(shape) is { } kinds && !kinds.Contains("array"))
                    {
                        continue;
                    }

                    if (shape.ItemsSaid is { } said)
                    {
                        found.AddRange(said._shapes);
                        continue;
                    }

                    // Every schema that says the items holds of each, so an item answers them all at once.
                    var items = shape.Said.Where(each => each.Schema["items"] is JsonObject).ToList();

                    found.AddRange(items.Count == 0
                        ? Anything._shapes
                        : Shapes(items[0].Root, [.. items.Select(each => (JsonObject)each.Schema["items"]!)]));
                }

                return found.Count == 0 ? null : new Reading(found);
            }
        }

        /// <summary>
        /// The shapes the schemas <paramref name="said"/> allow together, each read in <paramref name="root"/>: a
        /// reference read as the definition it names, and each <c>anyOf</c> as one shape for each of its own, beside
        /// what the rest say. A subset schema never recurses, so the reading ends.
        /// </summary>
        private static IEnumerable<Shape> Shapes(JsonObject root, IReadOnlyList<JsonObject> said)
        {
            var resolved = said.Select(schema => Resolve(root, schema)).ToList();
            IEnumerable<IReadOnlyList<JsonObject>> combinations = [[]];

            foreach (var schema in resolved)
            {
                var choices = schema["anyOf"] is JsonArray shapes
                    ? shapes.Select(shape => (IReadOnlyList<JsonObject>)[schema, Resolve(root, (JsonObject)shape!)]).ToList()
                    : [[schema]];

                combinations = combinations.SelectMany(combination => choices.Select(choice => (IReadOnlyList<JsonObject>)[.. combination, .. choice])).ToList();
            }

            return combinations.Select(combination => new Shape([.. combination.Select(schema => (root, schema))], null, null));
        }

        /// <summary>The definition a reference names, or the schema itself where it is none.</summary>
        private static JsonObject Resolve(JsonObject root, JsonObject schema)
            => schema["$ref"] is JsonValue reference
                ? (JsonObject)root["$defs"]![reference.GetValue<string>()[Definitions.Length..]]!
                : schema;

        /// <summary>The kinds one shape allows, or <see langword="null"/> where it allows any.</summary>
        private static IReadOnlySet<string>? KindsOf(Shape shape)
        {
            IReadOnlySet<string>? kinds = shape.Limit;

            foreach (var (_, schema) in shape.Said)
            {
                if (SaidKinds(schema) is { } said)
                {
                    kinds = kinds is null ? said : new HashSet<string>(kinds.Where(said.Contains), StringComparer.Ordinal);
                }
            }

            return kinds;
        }

        /// <summary>
        /// The kinds one schema, on its own, says: its <c>type</c>, an integer being a number too; else those of its
        /// <c>const</c> or its <c>enum</c>; else <see langword="null"/>, any.
        /// </summary>
        private static IReadOnlySet<string>? SaidKinds(JsonObject schema)
        {
            if (TypeOf(schema) is { } type)
            {
                var kinds = new HashSet<string>(type.Split('+'), StringComparer.Ordinal);

                if (kinds.Contains("number"))
                {
                    kinds.Add("integer");
                }

                return kinds;
            }

            if (schema.ContainsKey("const"))
            {
                return new HashSet<string>([KindOfValue(schema["const"])], StringComparer.Ordinal);
            }

            return schema["enum"] is JsonArray options
                ? new HashSet<string>(options.Select(KindOfValue), StringComparer.Ordinal)
                : null;
        }

        /// <summary>The kind a scalar written in a schema is.</summary>
        private static string KindOfValue(JsonNode? value)
            => value?.GetValueKind() switch
            {
                null or JsonValueKind.Null => "null",
                JsonValueKind.String => "string",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Number when value is JsonValue number && number.TryGetValue(out long _) => "integer",
                JsonValueKind.Number => "number",
                JsonValueKind.Array => "array",
                _ => "object",
            };
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

                case "minItems" or "maxItems":
                    if (Count(value) is null)
                    {
                        problems.Add($"{at}.{key}: is not a whole number from 0");
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
        // past by a schema that says nothing: an object's keys are said of an object alone, and an object always
        // closes its properties.
        if (SaysType(schema, "object") && !schema.ContainsKey("additionalProperties"))
        {
            problems.Add($"{at}: an object holds additionalProperties: false, saying every property is one it names");
        }

        foreach (var key in ObjectKeys)
        {
            if (schema.ContainsKey(key) && !SaysType(schema, "object"))
            {
                problems.Add($"{at}.{key}: only an object's properties are said");
            }
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
    /// Spells each text of <paramref name="document"/> an <c>enum</c> or a <c>const</c> holds in another case as the
    /// schema spells it, in place, where exactly one value matches it so: what <see cref="Respelled"/> gives, walked as
    /// the document check walks.
    /// </summary>
    private static JsonNode? Respell(JsonObject said, JsonNode? document, DocumentCheck check)
    {
        if (said["$ref"] is JsonValue reference)
        {
            return Respell((JsonObject)check.Root["$defs"]![reference.GetValue<string>()[Definitions.Length..]]!, document, check);
        }

        if (said["anyOf"] is JsonArray shapes)
        {
            foreach (var shape in shapes.Cast<JsonObject>())
            {
                var spelled = Respell(shape, document?.DeepClone(), check);

                // Each shape is tried on its own copy, so what one found at a place is never read for another.
                if (Check(shape, spelled, "$", new DocumentCheck(check.Root)).Count == 0)
                {
                    document = spelled;
                    break;
                }
            }
        }

        if (document is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
        {
            var text = scalar.GetValue<string>();
            IEnumerable<JsonNode?> values = [.. said["enum"] as JsonArray ?? [], .. said.ContainsKey("const") ? [said["const"]] : Array.Empty<JsonNode?>()];
            var spellings = values
                .Where(value => value is JsonValue each && each.GetValueKind() == JsonValueKind.String)
                .Select(value => value!.GetValue<string>())
                .ToList();
            var alike = spellings.Where(value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase)).ToList();

            return spellings.Contains(text, StringComparer.Ordinal) || alike.Count != 1 ? document : JsonValue.Create(alike[0]);
        }

        if (document is JsonObject map && said["properties"] is JsonObject properties)
        {
            foreach (var name in map.Select(member => member.Key).ToList())
            {
                if (properties[name] is JsonObject property && Respell(property, map[name], check) is var spelled && !ReferenceEquals(spelled, map[name]))
                {
                    map[name] = spelled;
                }
            }
        }

        if (document is JsonArray list && said["items"] is JsonObject items)
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (Respell(items, list[index], check) is var spelled && !ReferenceEquals(spelled, list[index]))
                {
                    list[index] = spelled;
                }
            }
        }

        return document;
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

            // A schema says properties only where it says an object, and closes them there, so it refuses every
            // property it does not name; one that says no type - an anyOf's, say - leaves an object's properties to
            // its shapes.
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
            if (Count(said["minItems"]) is { } least && list.Count < least)
            {
                problems.Add($"{at}: holds {list.Count} item{(list.Count == 1 ? string.Empty : "s")} and at least {least} {(least == 1 ? "is" : "are")} asked");
            }

            if (Count(said["maxItems"]) is { } cap && list.Count > cap)
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
                "integer" => document is JsonValue value && value.GetValueKind() == JsonValueKind.Number && IsWhole(value.ToJsonString()),
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
    /// Whether a JSON number is whole - an integer as JSON Schema means one, any number with no fraction, so <c>7.0</c>
    /// and <c>1.5e1</c> are and <c>7.5</c> is not - read from its digits as written, so no number is rounded into a
    /// whole one or out of one however many digits it has, and none is too large to be one.
    /// </summary>
    /// <param name="number">The number as JSON writes it.</param>
    private static bool IsWhole(string number)
    {
        var exponentAt = number.AsSpan().IndexOfAny('e', 'E');
        var significand = exponentAt < 0 ? number : number[..exponentAt];
        var point = significand.IndexOf('.', StringComparison.Ordinal);
        var fraction = point < 0 ? string.Empty : significand[(point + 1)..];
        var digits = ((point < 0 ? significand : significand[..point]).TrimStart('-') + fraction).TrimStart('0');

        if (digits.Length == 0)
        {
            return true;
        }

        var exponent = 0L;

        if (exponentAt >= 0
            && !long.TryParse(number.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            // An exponent no long holds outweighs every digit written: it carries them all before the point, or leaves
            // them all after it.
            return number[exponentAt + 1] != '-';
        }

        // The value is its digits times ten to the exponent less the fraction's length: whole where the zeros that end
        // the digits make up for every place that power leaves after the point.
        var trailing = digits.Length - digits.TrimEnd('0').Length;

        return exponent >= fraction.Length - trailing;
    }

    /// <summary>
    /// A count as a schema writes one - a whole number from 0, <c>2</c> and <c>2.0</c> alike - or <see langword="null"/>
    /// where the value is none, or more than a list holds: the one reading both checks share, so a bound the subset
    /// check passes is one the document check holds a document to.
    /// </summary>
    /// <param name="value">What the schema says the count is.</param>
    internal static int? Count(JsonNode? value)
        => value is JsonValue number
            && number.GetValueKind() == JsonValueKind.Number
            && number.ToJsonString() is var text
            && IsWhole(text)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var count)
            && count is >= 0m and <= int.MaxValue
                ? (int)count
                : null;

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
    /// terminator, where .NET takes all but the line feed; with <c>\s</c> and <c>\S</c> ECMA-262's white space and
    /// its complement, in a class or out, where .NET takes ASCII's; and with a class closed at its first <c>]</c>,
    /// <c>[]</c> the empty class and a <c>[</c> within it a character, where .NET reads class subtraction.
    /// </summary>
    /// <remarks>
    /// What .NET reads one way and ECMA-262 another, where no such rewriting gives ECMA-262's meaning, is refused: a
    /// pattern that means one thing here and another to whoever else reads the schema is one nobody can rely on. So
    /// are the groups ECMA-262 does not write - inline options, comments, atomic and conditional groups - since they
    /// change what <c>$</c> and <c>.</c> mean; .NET's own escapes - the anchors <c>\A</c>, <c>\Z</c>, <c>\z</c> and
    /// <c>\G</c> outside a class, and in a class or out the categories <c>\p{..}</c> and <c>\P{..}</c>, the bell
    /// <c>\a</c> and the escape character <c>\e</c> - which ECMA-262 reads as the letter escaped; .NET's spellings
    /// of a backreference beside ECMA-262's <c>\k&lt;name&gt;</c> - <c>\k'name'</c>, <c>\&lt;name&gt;</c>,
    /// <c>\'name'</c> and a group's number in angle brackets - which ECMA-262 reads as characters or refuses; and a
    /// backreference by number that the two read apart, .NET counting the named groups after every other and taking
    /// <c>\12</c>, where there are fewer than twelve groups, as <c>\1</c> and a <c>2</c>, where ECMA-262 takes the
    /// character it codes; and a group's name given twice, which .NET reads as one group and a provider refuses
    /// outright, so a schema the subset passes would be no schema to whoever is sent it. The escapes are refused rather than read as ECMA-262 reads them: nobody writes <c>\A</c>
    /// to mean the letter, and ECMA-262 read with its <c>u</c> flag, as a provider may read a schema's patterns,
    /// refuses each of them - or, for <c>\p{..}</c>, reads the category .NET does.
    /// </remarks>
    /// <param name="pattern">The pattern as the schema writes it.</param>
    /// <param name="problem">Why it cannot be read; <see langword="null"/> where it can.</param>
    private static Regex? ReadPattern(string pattern, out string? problem)
    {
        var read = new StringBuilder(pattern.Length);

        // Each capturing group in the order it opens, and whether it is named; and each backreference by number, as
        // written - held to the groups once all of them are known, since a reference can come before its group.
        var named = new List<bool>();
        var numbered = new List<string>();

        // Each group's name, once: .NET reads a name given twice as one group, which a provider refuses outright.
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < pattern.Length; index++)
        {
            switch (pattern[index])
            {
                case '\\':
                    if (EscapeNotEcma(pattern.AsSpan(index + 1), inClass: false) is { } dotnet)
                    {
                        problem = ReadApart(dotnet);
                        return null;
                    }

                    // An escape is copied whole, so the character it escapes is never read as an anchor or a dot;
                    // a control escape takes the character after its 'c' as well, and white space is ECMA-262's,
                    // where .NET's ECMAScript mode takes ASCII's alone.
                    var escaped = index + 1 < pattern.Length ? pattern[index + 1] : '\0';

                    if (escaped is 's' or 'S')
                    {
                        read.Append(escaped == 's' ? "[" : "[^").Append(Space).Append(']');
                        index++;
                        break;
                    }

                    if (escaped is >= '1' and <= '9')
                    {
                        var digits = index + 2;

                        while (digits < pattern.Length && char.IsAsciiDigit(pattern[digits]))
                        {
                            digits++;
                        }

                        numbered.Add(pattern[index..digits]);
                    }

                    var escape = escaped == 'c' ? 3 : 2;

                    read.Append(pattern, index, Math.Min(escape, pattern.Length - index));
                    index += escape - 1;
                    break;

                case '[':
                    index = CopyClass(pattern, index, read, out problem);

                    if (problem is not null)
                    {
                        return null;
                    }

                    break;

                case '(' when index + 1 < pattern.Length && pattern[index + 1] == '?':
                    if (GroupNotEcma(pattern.AsSpan(index + 2)) is { } group)
                    {
                        problem = $"'(?{group}' is not ECMA-262, the dialect a pattern is written in";
                        return null;
                    }

                    // A lookbehind is no group; anything else '(?<' opens that the check above takes is a named one.
                    if (pattern.AsSpan(index + 2) is ['<', not ('=' or '!'), ..])
                    {
                        var name = pattern[(index + 3)..pattern.IndexOf('>', index + 3)];

                        if (!names.Add(name))
                        {
                            problem = $"'(?<{name}>' names group '{name}' a second time, which a provider reading ECMA-262, the dialect a pattern is written in, refuses";
                            return null;
                        }

                        named.Add(true);
                    }

                    read.Append('(');
                    break;

                case '(':
                    named.Add(false);
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

        if (numbered.FirstOrDefault(reference => CountedApart(reference, named)) is { } apart)
        {
            problem = ReadApart(apart);
            return null;
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
    /// Copies a character class as ECMA-262 reads it and answers the index of its closing bracket. A class closes
    /// at its first unescaped <c>]</c>: <c>[^]</c> takes any one character, as .NET's ECMAScript mode reads it
    /// too, and <c>[]</c> is the empty class no character answers - written as a group that matches nothing,
    /// since .NET would read that bracket as one of the class's characters. A <c>[</c> within is a character, and
    /// is escaped so .NET never reads <c>-[...]</c> as the subtraction ECMA-262 does not have. <c>\s</c> within
    /// is ECMA-262's white space, and a class holding <c>\S</c> - whose ECMA-262 meaning no list of .NET class
    /// members spells - is written as the choice between its other members and every character but white space,
    /// or, negated, as white space its other members do not hold. Within a class
    /// <c>$</c> and <c>.</c> are characters, so nothing else in it is rewritten; an escape .NET reads otherwise there
    /// is refused, as <see cref="ReadPattern"/> says.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="index">Where the class opens.</param>
    /// <param name="read">The pattern as .NET is to read it, the class appended.</param>
    /// <param name="problem">Why the class cannot be read; <see langword="null"/> where it can.</param>
    private static int CopyClass(string pattern, int index, StringBuilder read, out string? problem)
    {
        problem = null;

        if (index + 1 < pattern.Length && pattern[index + 1] == ']')
        {
            read.Append("(?!)");
            return index + 1;
        }

        index++;

        var negated = index < pattern.Length && pattern[index] == '^';
        var members = new StringBuilder();
        var notSpace = false;

        for (index += negated ? 1 : 0; index < pattern.Length && pattern[index] != ']'; index++)
        {
            if (pattern[index] == '\\' && index + 1 < pattern.Length)
            {
                if (EscapeNotEcma(pattern.AsSpan(index + 1), inClass: true) is { } dotnet)
                {
                    problem = ReadApart(dotnet);
                    return index;
                }

                switch (pattern[++index])
                {
                    case 's':
                        members.Append(Space);
                        break;

                    case 'S':
                        notSpace = true;
                        break;

                    default:
                        members.Append('\\').Append(pattern[index]);
                        break;
                }
            }
            else
            {
                members.Append(pattern[index] == '[' ? @"\[" : pattern[index].ToString());
            }
        }

        if (index == pattern.Length)
        {
            // Unclosed: the pattern is refused where it is compiled.
            read.Append(negated ? "[^" : "[").Append(members);
            return index;
        }

        read.Append((negated, notSpace, members.Length) switch
        {
            (false, false, _) => $"[{members}]",
            (true, false, _) => $"[^{members}]",
            (false, true, 0) => $"[^{Space}]",
            (false, true, _) => $"(?:[{members}]|[^{Space}])",
            (true, true, 0) => $"[{Space}]",
            (true, true, _) => $"(?:(?![{members}])[{Space}])",
        });

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
    /// The escape at the head of <paramref name="rest"/> - what follows a backslash - as written, where .NET reads it
    /// one way and ECMA-262 another, or <see langword="null"/> where the two read it alike or .NET cannot read it at all.
    /// </summary>
    /// <param name="rest">The pattern from the character after the backslash.</param>
    /// <param name="inClass">Whether the escape is within a class, where .NET reads the anchors' letters and the
    /// backreferences' characters as ECMA-262 does.</param>
    private static string? EscapeNotEcma(ReadOnlySpan<char> rest, bool inClass)
    {
        switch (rest)
        {
            case ['p' or 'P' or 'a' or 'e', ..]:
            case ['A' or 'Z' or 'z' or 'G', ..] when !inClass:
                return $@"\{rest[0]}";

            case ['k', '\'' or '<', ..] or ['<' or '\'', ..] when !inClass:
                var opens = rest[0] == 'k' ? 1 : 0;
                var close = rest[opens] == '<' ? '>' : '\'';
                var name = rest[(opens + 1)..];
                var length = name.IndexOf(close);

                // Read as .NET reads a reference: a name or a number up to its closing mark. ECMA-262's own is
                // \k<name>, by a name alone; anything else .NET either reads as ECMA-262 does or cannot read.
                if (length < 1
                    || (!IsIdentifier(name[..length], dollar: false) && !IsDigits(name[..length]))
                    || (opens == 1 && close == '>' && !char.IsAsciiDigit(name[0])))
                {
                    return null;
                }

                return $@"\{rest[..(opens + length + 2)]}";

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a backreference by number, as written - a backslash and its digits - reaches one group in .NET and
    /// another, or a character, in ECMA-262: .NET counts the groups with no name first and the named ones after them
    /// where ECMA-262 counts every group in the order it opens, and where the number is more than the groups there are,
    /// .NET takes as many of its digits as name a group, where ECMA-262 takes them all as the code of a character.
    /// </summary>
    /// <param name="reference">The backreference as written.</param>
    /// <param name="named">Each capturing group in the order it opens, and whether it is named.</param>
    private static bool CountedApart(string reference, IReadOnlyList<bool> named)
    {
        var digits = reference.AsSpan(1);

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > named.Count)
        {
            return digits.Length > 1 && digits[0] - '0' <= named.Count;
        }

        // Ordered stably, so the groups with no name keep their order ahead of the named ones, which keep theirs.
        var counted = Enumerable.Range(0, named.Count).OrderBy(group => named[group]).ToList();

        return counted[number - 1] != number - 1;
    }

    /// <summary>Why a pattern holding <paramref name="written"/> is refused: the two dialects read it apart.</summary>
    /// <param name="written">What the pattern writes, as it writes it.</param>
    private static string ReadApart(string written)
        => $"'{written}' means one thing to .NET and another to ECMA-262, the dialect a pattern is written in";

    /// <summary>Whether text is ASCII digits alone, at least one.</summary>
    private static bool IsDigits(ReadOnlySpan<char> text)
        => !text.IsEmpty && !text.ContainsAnyExceptInRange('0', '9');

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
