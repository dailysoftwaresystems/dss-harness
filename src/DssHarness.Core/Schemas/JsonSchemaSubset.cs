using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Json;
using DssHarness.Core.Text;
using DssHarness.Core.Walks;

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
/// in its hyphenated form) and an ECMA-262 <c>pattern</c> - written as .NET's, matched in time linear in the text it
/// reads (<see cref="LinearRegex"/>), and refused where .NET would read it otherwise or that matcher lacks what it
/// holds. Anything else is refused where the definition is checked, naming the place
/// in the schema that says it. Each key is said of its own type alone - <c>properties</c>, <c>required</c> and
/// <c>additionalProperties</c> of an object, <c>items</c> and the bounds of an array, <c>format</c> and
/// <c>pattern</c> of a string - so a schema that names properties says an object, and closes it. A <c>$ref</c>
/// stands alone, beside nothing but words that say nothing of a document - and, at the root, the
/// definitions it names - since the drafts disagree on whether what is said beside one is read at all.
/// </para>
/// <para>
/// A <c>$ref</c> may also name a definition of a schema file beside the one holding it -
/// <c>plan.schema.json#/$defs/workItem</c>, one file name and one definition of it. Such a definition is shared:
/// it is taken into the schema's own <c>$defs</c>, and the reference is made the local one, wherever the schema is
/// read with the files beside it (<see cref="Shared"/>) - so the work items of a plan, a self-audit and a
/// diagnosis are one definition, held once, beside the schemas that share it. A shared reference the files beside
/// the schema do not answer is left naming the file it names - one a taken definition holds to its own file's
/// definitions is written so too, never left local to name a definition of the schema's own - and the subset check
/// refuses it as it refuses a local one that names nothing. Every reader of a schema's text reads it by the one
/// reading (<see cref="Whole"/>).
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
/// <para>
/// A schema and a document nest as deep as they are written, and are read so: every walk here goes down on a stack of its
/// own (<see cref="Frame{T}"/>, <see cref="JsonWalk"/>), never the thread's, and a place's path is spelt only where a
/// problem names it (<see cref="JsonPlace"/>), so neither a deep schema nor a deep answer is refused, or ends the
/// process, for its depth.
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

        Walk(schema, JsonPlace.Root, schema, null, problems, references).Run();
        problems.AddRange(Recursions(references));

        return problems;
    }

    /// <summary>
    /// Why <paramref name="text"/>, a schema as its file holds it, is not a schema this engine reads, or nothing where it
    /// is, as the one reading of a schema's text (<see cref="Whole"/>) says it: text that is not JSON, JSON that is no
    /// object, or what <see cref="SubsetProblems"/> says of the object, its shared definitions taken in where the files
    /// beside it are read. The one check of a schema's text, made where a workflow is checked - so a run never starts
    /// over a schema no answer could be held to - and again before a step's client is taken, for the file can change
    /// between.
    /// </summary>
    /// <param name="text">The schema's text.</param>
    /// <param name="neighbours">
    /// The parsed root of the schema file a shared reference names, or <see langword="null"/> where none is read beside
    /// the schema; null where the files beside it are not read at all, and a shared reference stands refused.
    /// </param>
    public static IReadOnlyList<string> TextProblems(string text, Func<string, JsonObject?>? neighbours = null)
        => Whole(text, neighbours).Problems;

    /// <summary>
    /// A schema's text read as the engine reads it - the one reading of a schema's text, which the check of a
    /// workflow's files, the typing of a path by a step's schema and a step's own reading of its schema at a run each
    /// ask, so none of them parses or judges a schema's text another way: parsed; the definitions its references share
    /// with the schema files beside it taken in where those files are read (<see cref="Shared"/>); and held to the
    /// subset (<see cref="SubsetProblems"/>). Text that is not JSON, and JSON that is no object, is said at the root as
    /// the subset says a place that holds no schema, and read no further.
    /// </summary>
    /// <param name="text">The schema's text, as its file holds it.</param>
    /// <param name="neighbours">
    /// The parsed root of the schema file a shared reference names, or <see langword="null"/> where none is read beside
    /// the schema; null where the files beside it are not read at all, and a shared reference stands refused.
    /// </param>
    public static WholeSchema Whole(string text, Func<string, JsonObject?>? neighbours = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        JsonNode? parsed;

        try
        {
            parsed = JsonDepth.ParseNode(text);
        }
        catch (JsonException ex)
        {
            return new WholeSchema(null, TookIn: false, [$"$: is not JSON ({ex.Message})"]);
        }

        // JSON's null parses to no node at all, and is no schema as any other value that is not an object is not.
        if (parsed is not JsonObject schema)
        {
            return new WholeSchema(null, TookIn: false, [NoObject("$")]);
        }

        var whole = neighbours is null ? schema : Shared(schema, neighbours);

        return new WholeSchema(whole, TookIn: !JsonWalk.Same(schema, whole), SubsetProblems(whole));
    }

    /// <summary>A schema's text as the engine reads it (<see cref="Whole"/>).</summary>
    /// <param name="Schema">
    /// The schema, its shared definitions taken in where any were - whether or not it is one the subset reads; null where
    /// the text is not JSON or no object, and there is no schema to read.
    /// </param>
    /// <param name="TookIn">
    /// Whether its references took a definition in from a schema file beside it, so the schema read is no longer the
    /// file's text as it was written.
    /// </param>
    /// <param name="Problems">Why it is not a schema the engine reads, each naming its place from the schema's root; none where it is one.</param>
    public sealed record WholeSchema(JsonObject? Schema, bool TookIn, IReadOnlyList<string> Problems);

    /// <summary>What is said of a place in a schema that holds something other than a JSON object.</summary>
    /// <param name="at">The place, as a JSON path from the schema's root.</param>
    private static string NoObject(string at) => $"{at}: a schema is a JSON object";

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
        var check = new DocumentCheck(root, at);

        return InWords(Check(root, document, JsonPlace.Root, check).Run(), check).Run();
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

        return Respell(root, JsonWalk.Copy(document), new DocumentCheck(root, "$")).Run();
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
    /// The schema with the definitions its references share with the schema files beside it taken in: each
    /// <c>$ref</c> that names a definition of another schema file - <c>plan.schema.json#/$defs/workItem</c>, the
    /// file beside this schema's own - and each definition those reach, in the files they come from, is copied into
    /// the schema's own <c>$defs</c> and the reference is made the local one, so schemas that share a definition
    /// hold it once, between them. A reference that names nothing the files beside the schema answer - a file not
    /// there, a definition it does not hold, or a name this schema holds already, said otherwise, or took from another
    /// file - is left naming the file it names, for <see cref="SubsetProblems"/> to refuse: a reference a taken
    /// definition holds to a definition of its own file, written there as a local one (<c>#/$defs/closureClass</c>), is
    /// written naming that file (<c>plan.schema.json#/$defs/closureClass</c>), so it is never left to bind to a
    /// definition of this schema that only shares its name. A schema whose own <c>$defs</c> is no mapping takes nothing
    /// in: its definitions and its shared references stand as they are written, for the subset check to refuse both.
    /// The schema given is never changed: what comes back is a copy.
    /// </summary>
    /// <param name="schema">A schema's root, as its file holds it.</param>
    /// <param name="neighbours">
    /// The parsed root of the schema file a reference names, or <see langword="null"/> where none is read beside
    /// the schema - missing, unreadable, or no JSON object. Where the files sit is the caller's to know; this, the
    /// one rule of sharing a definition, stays here.
    /// </param>
    public static JsonObject Shared(JsonObject schema, Func<string, JsonObject?> neighbours)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(neighbours);

        var whole = (JsonObject)JsonWalk.Copy(schema)!;
        var sharing = References(whole).Where(reference => reference.File is not null).ToList();

        // Definitions that are no mapping are never replaced by one the file did not write: they are left for the
        // subset check to refuse, and with them every shared reference, which nothing could be taken in for.
        if (sharing.Count == 0 || (whole.ContainsKey("$defs") && whole["$defs"] is not JsonObject))
        {
            return whole;
        }

        var definitions = whole["$defs"] as JsonObject ?? new JsonObject();
        whole["$defs"] = definitions;

        // Each name taken, with the file it was taken from: a name the schema already holds is answered only where
        // what it holds is the definition the file names - the very one, taken from that file, or its equal.
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (holder, file, name) in sharing)
        {
            if (Take(file!, name).Run() is { } local)
            {
                holder["$ref"] = local;
            }
        }

        return whole;

        // Where a reference lands once taken - <c>#/$defs/name</c> - or null where nothing answers it beside the schema.
        Frame<string?> Take(string file, string name) => new(self => Taking(self, file, name));

        IEnumerable<Frame> Taking(Frame<string?> self, string file, string name)
        {
            var offered = DefinitionIn(neighbours(file), name);

            if (definitions.ContainsKey(name)
                && !(taken.TryGetValue(name, out var from) && string.Equals(from, file, StringComparison.Ordinal))
                && !JsonWalk.Same(definitions[name], offered))
            {
                self.Give(null);
                yield break;
            }

            if (!definitions.ContainsKey(name))
            {
                if (offered is not JsonObject definition)
                {
                    self.Give(null);
                    yield break;
                }

                var copy = (JsonObject)JsonWalk.Copy(definition)!;

                definitions[name] = copy;
                taken[name] = file;

                // What the taken definition itself reaches - a definition of its own file, or of one beside that -
                // is taken with it, for a definition left pointing at a file the schema does not hold is no whole. One
                // that cannot be taken is written naming the file it reaches into, never left local: written as its own
                // file writes it, it would name whatever this schema holds by that name.
                foreach (var (holder, innerFile, innerName) in References(copy))
                {
                    var reached = innerFile ?? file;
                    var inner = Take(reached, innerName);
                    yield return inner;

                    holder["$ref"] = inner.Result ?? $"{reached}{Definitions}{innerName}";
                }
            }

            self.Give(Definitions + name);
        }
    }

    /// <summary>
    /// The definition <paramref name="name"/> of the schema <paramref name="root"/>, read beside the schema that shares
    /// it; null where the root is not read, its <c>$defs</c> is no mapping, or it holds no such definition - each of
    /// which leaves a reference to it untaken, for the subset check to refuse.
    /// </summary>
    private static JsonNode? DefinitionIn(JsonObject? root, string name)
        => (root?["$defs"] as JsonObject)?[name];

    /// <summary>
    /// Every reference the schema holds, in the order it writes them, with where each stands: the schema that says it, the
    /// file a shared one names (<see cref="Shared"/>), and the definition it names - of the schema itself where the
    /// reference is local, which the schema's own <c>$defs</c> already answers or does not, and never a take's to mend.
    /// </summary>
    private static List<(JsonObject Holder, string? File, string Name)> References(JsonNode? node)
    {
        var found = new List<(JsonObject Holder, string? File, string Name)>();

        foreach (var (_, each) in JsonWalk.Descendants(node))
        {
            if (each is not JsonObject schema || schema["$ref"] is not JsonValue reference || !reference.TryGetValue<string>(out var written))
            {
                continue;
            }

            if (SharedName(written, out var file, out var name))
            {
                found.Add((schema, file, name));
            }
            else if (written.StartsWith(Definitions, StringComparison.Ordinal)
                && written.AsSpan(Definitions.Length).IndexOfAny('/', '#') < 0
                && written.Length > Definitions.Length)
            {
                found.Add((schema, null, written[Definitions.Length..]));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether <paramref name="written"/> names a definition of a schema file beside the one holding it -
    /// <c>plan.schema.json#/$defs/workItem</c>, one file name and one definition of it - and which.
    /// </summary>
    /// <param name="written">The reference, as the schema writes it.</param>
    /// <param name="file">The file it names.</param>
    /// <param name="name">The definition of that file it names.</param>
    private static bool SharedName(string written, out string file, out string name)
    {
        file = string.Empty;
        name = string.Empty;

        var beside = written.IndexOf('#');

        if (beside < 1)
        {
            return false;
        }

        file = written[..beside];

        if (file.IndexOfAny(['/', '\\']) >= 0 || file is "." or "..")
        {
            return false;
        }

        var target = written[beside..];

        if (!target.StartsWith(Definitions, StringComparison.Ordinal))
        {
            return false;
        }

        name = target[Definitions.Length..];

        return name.Length > 0 && name.IndexOfAny(['/', '#']) < 0;
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
    /// <param name="At">The reference's own place in the schema, spelt as its path where a problem names it.</param>
    /// <param name="Target">The name of the definition it reaches.</param>
    private readonly record struct Reference(string? Owner, JsonPlace At, string Target);

    /// <summary>
    /// Walks a schema, refusing what the subset does not say. Every node is walked once, where it is written: a
    /// <c>$ref</c> is noted, never followed, so its definition is walked where the root defines it. Each schema it holds is
    /// walked as a frame of its own (<see cref="Frame{T}"/>), so a schema nests as deep as it is written.
    /// </summary>
    private static Frame<bool> Walk(
        JsonNode? node,
        JsonPlace at,
        JsonNode root,
        string? owner,
        List<string> problems,
        List<Reference> references)
        => new(self => Walking(self, node, at, root, owner, problems, references));

    /// <summary>The body of <see cref="Walk"/>: the problems of one schema, each schema it holds walked in turn where it is written.</summary>
    private static IEnumerable<Frame> Walking(
        Frame<bool> self,
        JsonNode? node,
        JsonPlace at,
        JsonNode root,
        string? owner,
        List<string> problems,
        List<Reference> references)
    {
        if (node is not JsonObject schema)
        {
            problems.Add(NoObject(at.Path()));
            self.Give(true);
            yield break;
        }

        var referred = schema.ContainsKey("$ref");

        foreach (var (key, value) in schema)
        {
            if (referred && key != "$ref" && !BesideAReference.Contains(key, StringComparer.Ordinal))
            {
                problems.Add($"{at.Member(key)}: is said beside $ref, which stands alone; say it in the definition");
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
                        var under = at.Member("properties");

                        foreach (var (name, property) in properties)
                        {
                            yield return Walk(property, under.Member(name), root, owner, problems, references);
                        }
                    }
                    else
                    {
                        problems.Add($"{at.Member("properties")}: is not a mapping of properties to schemas");
                    }

                    break;

                case "required":
                    if (value is not JsonArray required || required.Any(name => name is null || name.GetValueKind() != JsonValueKind.String))
                    {
                        problems.Add($"{at.Member("required")}: is not a list of property names");
                    }
                    else if (schema["properties"] is JsonObject named)
                    {
                        foreach (var name in required)
                        {
                            if (!named.ContainsKey(name!.GetValue<string>()))
                            {
                                problems.Add($"{at.Member("required")}: '{name.GetValue<string>()}' is named by no property");
                            }
                        }
                    }
                    else
                    {
                        problems.Add($"{at.Member("required")}: names properties the schema does not say; name them under properties");
                    }

                    break;

                case "additionalProperties":
                    if (value is not JsonValue isFalse || isFalse.GetValueKind() != JsonValueKind.False)
                    {
                        problems.Add($"{at.Member("additionalProperties")}: the subset takes false, always, so every property is one the schema names");
                    }

                    break;

                case "items":
                    yield return Walk(value, at.Member("items"), root, owner, problems, references);
                    break;

                case "enum":
                    if (value is not JsonArray options || options.Count == 0 || options.Any(option => option is not null and not JsonValue))
                    {
                        problems.Add($"{at.Member("enum")}: is not a non-empty list of scalars");
                    }

                    break;

                case "const":
                    if (value is not null and not JsonValue)
                    {
                        problems.Add($"{at.Member("const")}: is not a scalar");
                    }

                    break;

                case "anyOf":
                    if (value is JsonArray shapes && shapes.Count > 0)
                    {
                        var under = at.Member("anyOf");

                        for (var index = 0; index < shapes.Count; index++)
                        {
                            yield return Walk(shapes[index], under.Item(index), root, owner, problems, references);
                        }
                    }
                    else
                    {
                        problems.Add($"{at.Member("anyOf")}: is not a non-empty list of schemas");
                    }

                    break;

                case "$ref":
                    if (value is not JsonValue reference || reference.GetValueKind() != JsonValueKind.String)
                    {
                        problems.Add($"{at.Member("$ref")}: is not the name of a definition");
                    }
                    else if (Definition(root, reference.GetValue<string>()) is { } target)
                    {
                        references.Add(new Reference(owner, at.Member("$ref"), target));
                    }
                    else if (SharedName(reference.GetValue<string>(), out var file, out var name))
                    {
                        problems.Add(
                            $"{at.Member("$ref")}: '{reference.GetValue<string>()}' names a definition of the schema file '{file}' beside this one, and it was not taken into this schema's own definitions"
                                + $" - the file is not beside it, or holds no definition '{name}', or this schema holds the name already, said otherwise");
                    }
                    else
                    {
                        problems.Add($"{at.Member("$ref")}: '{reference.GetValue<string>()}' names no definition of this schema");
                    }

                    break;

                case "$defs":
                    if (!ReferenceEquals(schema, root))
                    {
                        problems.Add($"{at.Member("$defs")}: definitions are held at the schema's root, where '{Definitions}name' finds them");
                    }
                    else if (value is JsonObject definitions)
                    {
                        var under = at.Member("$defs");

                        foreach (var (name, definition) in definitions)
                        {
                            yield return Walk(definition, under.Member(name), root, name, problems, references);
                        }
                    }
                    else
                    {
                        problems.Add($"{at.Member("$defs")}: is not a mapping of definitions to schemas");
                    }

                    break;

                case "minItems" or "maxItems":
                    if (Count(value) is null)
                    {
                        problems.Add($"{at.Member(key)}: is not a whole number from 0");
                    }

                    break;

                case "format":
                    if (value is not JsonValue format || format.GetValueKind() != JsonValueKind.String
                        || !Formats.Contains(format.GetValue<string>(), StringComparer.Ordinal))
                    {
                        problems.Add($"{at.Member("format")}: is not one of {string.Join(", ", Formats)}");
                    }

                    break;

                case "pattern":
                    if (value is not JsonValue pattern || pattern.GetValueKind() != JsonValueKind.String)
                    {
                        problems.Add($"{at.Member("pattern")}: is not text");
                    }
                    else if (ReadPattern(pattern.GetValue<string>(), out var unread) is null)
                    {
                        problems.Add($"{at.Member("pattern")}: {unread}");
                    }

                    break;

                default:
                    problems.Add($"{at.Member(key)}: is said by no schema this engine reads");
                    break;
            }
        }

        self.Give(true);

        // What stands beside a reference was refused key by key above, and is not refused again for what it says.
        if (referred)
        {
            yield break;
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
                problems.Add($"{at.Member(key)}: only an object's properties are said");
            }
        }

        if (schema.ContainsKey("items") && !SaysType(schema, "array"))
        {
            problems.Add($"{at.Member("items")}: only an array's items are said");
        }

        foreach (var bound in Bounds)
        {
            if (schema.ContainsKey(bound) && !SaysType(schema, "array"))
            {
                problems.Add($"{at.Member(bound)}: only an array's length is bounded");
            }
        }

        if (schema.ContainsKey("format") && !SaysType(schema, "string"))
        {
            problems.Add($"{at.Member("format")}: only a string is formatted");
        }

        if (schema.ContainsKey("pattern") && !SaysType(schema, "string"))
        {
            problems.Add($"{at.Member("pattern")}: only a string is matched");
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
    private static void TypeProblems(JsonNode? value, JsonPlace at, List<string> problems)
    {
        if (value is JsonValue one && one.GetValueKind() == JsonValueKind.String)
        {
            if (!Types.Contains(one.GetValue<string>(), StringComparer.Ordinal))
            {
                problems.Add($"{at.Member("type")}: '{one.GetValue<string>()}' is not one of {string.Join(", ", Types)}, or one of them with null");
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

        problems.Add($"{at.Member("type")}: is not one of {string.Join(", ", Types)}, or one of them with null");
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
    /// <remarks>
    /// Each place of the document is one object however many shapes reach it (<see cref="Member"/>, <see cref="Item"/>), so
    /// what a definition said of it is found by the place itself; and a place's path is spelt only where a problem names
    /// it, from where the document stands in a larger one, so a deep document's paths are never all spelt.
    /// </remarks>
    /// <param name="root">The schema's root, which holds the definitions.</param>
    /// <param name="at">The document's own path, from the root of what holds it.</param>
    private sealed class DocumentCheck(JsonObject root, string at)
    {
        private readonly Dictionary<(JsonPlace Holder, string Name), JsonPlace> _members = [];
        private readonly Dictionary<(JsonPlace Holder, int Index), JsonPlace> _items = [];
        private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

        /// <summary>The schema's root, which holds the definitions.</summary>
        public JsonObject Root { get; } = root;

        /// <summary>The problems a definition, by name, found at a place in the document.</summary>
        public Dictionary<(string Definition, JsonPlace At), IReadOnlyList<Objection>> Read { get; } = [];

        /// <summary>The place of the member <paramref name="name"/> of the object at <paramref name="holder"/>: one object, however often it is asked.</summary>
        public JsonPlace Member(JsonPlace holder, string name)
        {
            if (!_members.TryGetValue((holder, name), out var place))
            {
                place = holder.Member(name);
                _members.Add((holder, name), place);
            }

            return place;
        }

        /// <summary>The place of the item at <paramref name="index"/> of the array at <paramref name="holder"/>: one object, however often it is asked.</summary>
        public JsonPlace Item(JsonPlace holder, int index)
        {
            if (!_items.TryGetValue((holder, index), out var place))
            {
                place = holder.Item(index);
                _items.Add((holder, index), place);
            }

            return place;
        }

        /// <summary>The path of <paramref name="place"/>, from the root of what holds the document.</summary>
        public string Spelt(JsonPlace place) => place.Depth == 0 ? at : at + place.Path()[1..];

        /// <summary>A pattern the subset check passed, read once for every value of the document it is asked of.</summary>
        public Regex Pattern(string pattern)
        {
            if (!_patterns.TryGetValue(pattern, out var regex))
            {
                regex = ReadPattern(pattern, out _)!;
                _patterns.Add(pattern, regex);
            }

            return regex;
        }
    }

    /// <summary>
    /// Spells each text of <paramref name="document"/> an <c>enum</c> or a <c>const</c> holds in another case as the
    /// schema spells it, in place, where exactly one value matches it so: what <see cref="Respelled"/> gives, walked as
    /// the document check walks, each schema a frame of its own.
    /// </summary>
    private static Frame<JsonNode?> Respell(JsonObject said, JsonNode? document, DocumentCheck check)
        => new(self => Respelling(self, said, document, check));

    /// <summary>The body of <see cref="Respell"/>.</summary>
    private static IEnumerable<Frame> Respelling(Frame<JsonNode?> self, JsonObject said, JsonNode? document, DocumentCheck check)
    {
        if (said["$ref"] is JsonValue reference)
        {
            var defined = Respell((JsonObject)check.Root["$defs"]![reference.GetValue<string>()[Definitions.Length..]]!, document, check);
            yield return defined;

            self.Give(defined.Result);
            yield break;
        }

        if (said["anyOf"] is JsonArray shapes)
        {
            foreach (var shape in shapes.Cast<JsonObject>())
            {
                var spelled = Respell(shape, JsonWalk.Copy(document), check);
                yield return spelled;

                // Each shape is tried on its own copy, so what one found at a place is never read for another.
                var tried = Check(shape, spelled.Result, JsonPlace.Root, new DocumentCheck(check.Root, "$"));
                yield return tried;

                if (tried.Result.Count == 0)
                {
                    document = spelled.Result;
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

            self.Give(spellings.Contains(text, StringComparer.Ordinal) || alike.Count != 1 ? document : JsonValue.Create(alike[0]));
            yield break;
        }

        if (document is JsonObject map && said["properties"] is JsonObject properties)
        {
            foreach (var name in map.Select(member => member.Key).ToList())
            {
                if (properties[name] is JsonObject property)
                {
                    var member = map[name];
                    var spelled = Respell(property, member, check);
                    yield return spelled;

                    if (!ReferenceEquals(spelled.Result, member))
                    {
                        map[name] = spelled.Result;
                    }
                }
            }
        }

        if (document is JsonArray list && said["items"] is JsonObject items)
        {
            for (var index = 0; index < list.Count; index++)
            {
                var item = list[index];
                var spelled = Respell(items, item, check);
                yield return spelled;

                if (!ReferenceEquals(spelled.Result, item))
                {
                    list[index] = spelled.Result;
                }
            }
        }

        self.Give(document);
    }

    /// <summary>
    /// Checks a document against a schema the subset check passed, naming the document's own path at every break.
    /// A path names one place in the document, since a name no identifier spells is written in brackets. Each schema
    /// a member or an item is held to is checked as a frame of its own, so a document nests as deep as it is written.
    /// </summary>
    private static Frame<IReadOnlyList<Objection>> Check(JsonObject said, JsonNode? document, JsonPlace at, DocumentCheck check)
        => new(self => Checking(self, said, document, at, check));

    /// <summary>The body of <see cref="Check"/>.</summary>
    private static IEnumerable<Frame> Checking(Frame<IReadOnlyList<Objection>> self, JsonObject said, JsonNode? document, JsonPlace at, DocumentCheck check)
    {
        // A reference stands alone, so what it names is the whole of what is said here - kept as what that definition
        // found at this place, so a place two shapes read by one definition is said once (InWords).
        if (said["$ref"] is JsonValue reference)
        {
            var name = reference.GetValue<string>()[Definitions.Length..];

            if (!check.Read.TryGetValue((name, at), out var read))
            {
                var defined = Check((JsonObject)check.Root["$defs"]![name]!, document, at, check);
                yield return defined;

                read = defined.Result.Count == 0 ? [] : [new Defined(name, at, defined.Result)];
                check.Read[(name, at)] = read;
            }

            self.Give(read);
            yield break;
        }

        var problems = new List<Objection>();

        // Every key holds at once: anyOf asks one of its shapes to answer, and what is said beside it is checked
        // all the same. Where none answers, what each objected to is kept with it, so the one answering can tell
        // which shape it meant and what that shape still asks.
        if (said["anyOf"] is JsonArray shapes)
        {
            List<IReadOnlyList<Objection>>? objections = [];

            foreach (var shape in shapes)
            {
                var tried = Check((JsonObject)shape!, document, at, check);
                yield return tried;

                if (tried.Result.Count == 0)
                {
                    objections = null;
                    break;
                }

                objections.Add(tried.Result);
            }

            if (objections is not null)
            {
                problems.Add(new Unanswered(at, objections));
            }
        }

        if (said.ContainsKey("const") && !JsonWalk.Same(said["const"], document))
        {
            problems.Add(new Worded($"{check.Spelt(at)}: {Said(document)} is not {Said(said["const"])}"));
        }

        if (said["enum"] is JsonArray options && !options.Any(option => JsonWalk.Same(option, document)))
        {
            problems.Add(new Worded($"{check.Spelt(at)}: {Said(document)} is not one of {string.Join(", ", options.Select(option => Said(option)))}"));
        }

        if (TypeOf(said) is { } type && !Answers(type, document))
        {
            problems.Add(new Worded($"{check.Spelt(at)}: {Said(document)} is not {Kind(type)}"));
            self.Give(problems);
            yield break;
        }

        if (document is JsonObject map)
        {
            foreach (var name in said["required"] as JsonArray ?? [])
            {
                if (!map.ContainsKey(name!.GetValue<string>()))
                {
                    problems.Add(new Worded($"{check.Spelt(at)}: '{name.GetValue<string>()}' is required and missing"));
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
                    var member = Check(property, value, check.Member(at, name), check);
                    yield return member;

                    problems.AddRange(member.Result);
                }
                else if (closed)
                {
                    problems.Add(new Worded($"{check.Spelt(check.Member(at, name))}: is said by no property of the schema"));
                }
            }

            self.Give(problems);
            yield break;
        }

        if (document is JsonArray list)
        {
            if (Count(said["minItems"]) is { } least && list.Count < least)
            {
                problems.Add(new Worded($"{check.Spelt(at)}: holds {list.Count} item{(list.Count == 1 ? string.Empty : "s")} and at least {least} {(least == 1 ? "is" : "are")} asked"));
            }

            if (Count(said["maxItems"]) is { } cap && list.Count > cap)
            {
                problems.Add(new Worded($"{check.Spelt(at)}: holds {list.Count} item{(list.Count == 1 ? string.Empty : "s")} and at most {cap} {(cap == 1 ? "is" : "are")} taken"));
            }

            if (said["items"] is JsonObject items)
            {
                for (var index = 0; index < list.Count; index++)
                {
                    var item = Check(items, list[index], check.Item(at, index), check);
                    yield return item;

                    problems.AddRange(item.Result);
                }
            }

            self.Give(problems);
            yield break;
        }

        if (document is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
        {
            var text = scalar.GetValue<string>();

            if (said["format"] is JsonValue format && !FormatHolds(format.GetValue<string>(), text))
            {
                problems.Add(new Worded($"{check.Spelt(at)}: {Said(document)} is not a {format.GetValue<string>()}"));
            }

            // Matched in time linear in the text, so a pattern never holds the check, and none is cut short.
            if (said["pattern"] is JsonValue pattern && !check.Pattern(pattern.GetValue<string>()).IsMatch(text))
            {
                problems.Add(new Worded($"{check.Spelt(at)}: {Said(document)} does not match its pattern"));
            }
        }

        self.Give(problems);
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

    /// <summary>One thing the document check found, kept until the check is over and then worded (<see cref="InWords"/>).</summary>
    private abstract record Objection;

    /// <summary>A problem, in its words.</summary>
    /// <param name="Text">The problem, its path first.</param>
    private sealed record Worded(string Text) : Objection;

    /// <summary>
    /// What the definition <paramref name="Definition"/> found at the place <paramref name="At"/>: one object, kept once for
    /// the place however many shapes read it by that definition (<see cref="DocumentCheck.Read"/>).
    /// </summary>
    /// <param name="Definition">The definition's name.</param>
    /// <param name="At">The place it read.</param>
    /// <param name="Found">What it found there.</param>
    private sealed record Defined(string Definition, JsonPlace At, IReadOnlyList<Objection> Found) : Objection;

    /// <summary>A place no shape of an <c>anyOf</c> answers, with what each shape found there, in the schema's order.</summary>
    /// <param name="At">The place.</param>
    /// <param name="Shapes">What each shape found.</param>
    private sealed record Unanswered(JsonPlace At, IReadOnlyList<IReadOnlyList<Objection>> Shapes) : Objection;

    /// <summary>
    /// What the check found, in words, in the order it found them: each problem as it was worded; a place no shape of an
    /// <c>anyOf</c> answers as one problem - the count of its shapes, then what each objected to, each shape's problems in
    /// parentheses, so the one answering can tell which shape it meant and what that shape still asks, however deep the
    /// shapes nest. What a definition found at a place is worded where it is first met, and every later shape that read the
    /// place by that definition says so in a line of its own rather than again - so the words grow with what the check
    /// read, never with the number of ways a schema's definitions reach one place. Worded on a stack of its own, as deep as
    /// the shapes nest.
    /// </summary>
    /// <param name="found">What the check found.</param>
    /// <param name="check">The check, which spells each place.</param>
    private static Frame<IReadOnlyList<string>> InWords(IReadOnlyList<Objection> found, DocumentCheck check)
        => Wording(found, check, new HashSet<(string, JsonPlace)>());

    /// <summary>The words of <paramref name="found"/>, each definition at a place worded once over the whole check (<paramref name="worded"/>).</summary>
    private static Frame<IReadOnlyList<string>> Wording(IReadOnlyList<Objection> found, DocumentCheck check, HashSet<(string, JsonPlace)> worded)
        => new(self => WordingOf(self, found, check, worded));

    /// <summary>The body of <see cref="Wording"/>.</summary>
    private static IEnumerable<Frame> WordingOf(Frame<IReadOnlyList<string>> self, IReadOnlyList<Objection> found, DocumentCheck check, HashSet<(string, JsonPlace)> worded)
    {
        var words = new List<string>();

        foreach (var objection in found)
        {
            switch (objection)
            {
                case Worded text:
                    words.Add(text.Text);
                    break;

                case Defined defined when !worded.Add((defined.Definition, defined.At)):
                    words.Add($"{check.Spelt(defined.At)}: does not answer '{Definitions}{defined.Definition}', as said above");
                    break;

                case Defined defined:
                    var inner = Wording(defined.Found, check, worded);
                    yield return inner;

                    words.AddRange(inner.Result);
                    break;

                case Unanswered unanswered:
                    var shapes = new List<string>();

                    foreach (var (shape, index) in unanswered.Shapes.Select((shape, index) => (shape, index)))
                    {
                        var shapeWords = Wording(shape, check, worded);
                        yield return shapeWords;

                        shapes.Add($"shape {(index + 1).ToString(CultureInfo.InvariantCulture)} ({string.Join("; ", shapeWords.Result)})");
                    }

                    words.Add($"{check.Spelt(unanswered.At)}: answers none of the {unanswered.Shapes.Count.ToString(CultureInfo.InvariantCulture)} shape{(unanswered.Shapes.Count == 1 ? string.Empty : "s")} its schema offers - {string.Join(", ", shapes)}");
                    break;
            }
        }

        self.Give(words);
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
    /// A schema's pattern as .NET runs it the way ECMA-262 reads it, or why it cannot be read - the one reading both checks
    /// share, so a pattern the subset check passes is one the document check runs. ECMA-262's pattern is written in .NET's
    /// grammar and matched by .NET's linear-time engine (<see cref="LinearRegex"/>), in time linear in the text however the
    /// pattern nests, with no timeout: <c>\d</c> and <c>\w</c>, and their complements, are ASCII's, in a class or out;
    /// <c>\s</c> and <c>\S</c> ECMA-262's white space and its complement; the end anchor <c>$</c> the end of the text alone,
    /// where .NET would also take the place before a final line feed; <c>.</c> takes no line terminator, where .NET takes all
    /// but the line feed; a class closes at its first <c>]</c>, <c>[]</c> the empty class and <c>[^]</c> any character, and a
    /// <c>[</c> within it is a character, where .NET reads class subtraction; an identity escape - <c>\q</c> - is its
    /// character, as ECMA-262 reads one; and a number escaped past the groups the pattern holds is the character its octal
    /// digits code, as ECMA-262's Annex B reads it, where .NET would read a group.
    /// </summary>
    /// <remarks>
    /// What .NET reads one way and ECMA-262 another, where no such writing gives ECMA-262's meaning, is refused: a pattern
    /// that means one thing here and another to whoever else reads the schema is one nobody can rely on. So are the groups
    /// ECMA-262 does not write - inline options, comments, atomic and conditional groups - since they change what <c>$</c>
    /// and <c>.</c> mean; .NET's own escapes - the anchors <c>\A</c>, <c>\Z</c>, <c>\z</c> and <c>\G</c> outside a class,
    /// and in a class or out the categories <c>\p{..}</c> and <c>\P{..}</c>, the bell <c>\a</c> and the escape character
    /// <c>\e</c> - which ECMA-262 reads as the letter escaped; a word boundary, <c>\b</c> or <c>\B</c> outside a class, which
    /// ECMA-262 draws between ASCII's word characters and the linear engine between Unicode's; .NET's spellings of a
    /// backreference beside ECMA-262's <c>\k&lt;name&gt;</c> - <c>\k'name'</c>, <c>\&lt;name&gt;</c>, <c>\'name'</c> and a
    /// group's number in angle brackets - which ECMA-262 reads as characters or refuses; and a group's name given twice,
    /// which .NET reads as one group and a provider refuses outright, so a schema the subset passes would be no schema to
    /// whoever is sent it. The escapes are refused rather than read as ECMA-262 reads them: nobody writes <c>\A</c> to mean
    /// the letter, and ECMA-262 read with its <c>u</c> flag, as a provider may read a schema's patterns, refuses each of
    /// them - or, for <c>\p{..}</c>, reads the category .NET does.
    /// <para>
    /// What the linear engine lacks - a backreference, a lookahead or a lookbehind - is refused as that engine says it,
    /// naming the pattern as the schema writes it: a pattern matched by backtracking could hold a check for as long as it
    /// liked, and one cut short by a timeout would answer nothing.
    /// </para>
    /// </remarks>
    /// <param name="pattern">The pattern as the schema writes it.</param>
    /// <param name="problem">Why it cannot be read; <see langword="null"/> where it can.</param>
    private static Regex? ReadPattern(string pattern, out string? problem)
    {
        var read = new StringBuilder(pattern.Length);

        // Every capturing group the pattern holds, wherever it stands: a number escaped is a backreference only where the
        // pattern holds that many groups, and a reference can come before its group.
        var groups = GroupsIn(pattern);

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

                    var escaped = index + 1 < pattern.Length ? pattern[index + 1] : '\0';

                    if (escaped is >= '1' and <= '9')
                    {
                        index = CopyNumbered(pattern, index, groups, read);
                        break;
                    }

                    if (Shorthand(escaped, inClass: false) is { } written)
                    {
                        read.Append(written);
                        index++;
                        break;
                    }

                    // Any other escape is copied whole, so the character it escapes is never read as an anchor or a dot; a
                    // control escape takes the character after its 'c' as well.
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

        if (LinearRegex.TryCompile(read.ToString(), RegexOptions.None, pattern, out var regex, out var refused))
        {
            problem = null;
            return regex;
        }

        // The linear engine's own sentence, which names the pattern as the schema writes it, said after the place's path.
        problem = char.ToLowerInvariant(refused[0]) + refused[1..].TrimEnd('.');
        return null;
    }

    /// <summary>
    /// How many capturing groups <paramref name="pattern"/> holds, as ECMA-262 counts them: every <c>(</c> that is neither
    /// escaped, in a class, nor followed by <c>?</c>, and every named group, <c>(?&lt;name&gt;</c>.
    /// </summary>
    private static int GroupsIn(string pattern)
    {
        var groups = 0;

        for (var index = 0; index < pattern.Length; index++)
        {
            switch (pattern[index])
            {
                case '\\':
                    index++;
                    break;

                case '[':
                    index = ClassEnd(pattern, index);
                    break;

                case '(' when index + 1 < pattern.Length && pattern[index + 1] == '?':
                    if (pattern.AsSpan(index + 2) is ['<', not ('=' or '!'), ..])
                    {
                        groups++;
                    }

                    break;

                case '(':
                    groups++;
                    break;
            }
        }

        return groups;
    }

    /// <summary>
    /// Where the class that opens at <paramref name="index"/> closes, as ECMA-262 closes one: at its first unescaped
    /// <c>]</c>, the one right after <c>[</c> or <c>[^</c> among them; the pattern's end where it never closes.
    /// </summary>
    private static int ClassEnd(string pattern, int index)
    {
        index++;

        if (index < pattern.Length && pattern[index] == '^')
        {
            index++;
        }

        for (; index < pattern.Length && pattern[index] != ']'; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
            }
        }

        return Math.Min(index, pattern.Length);
    }

    /// <summary>
    /// Copies a number escaped outside a class, from its backslash at <paramref name="index"/>, as ECMA-262 reads it, and
    /// answers the index of its last digit: a backreference where its number - every digit written - is a group's the
    /// pattern holds, copied for the linear engine to refuse; else, as Annex B reads it, a <c>8</c> or <c>9</c> its own
    /// character, or the character up to three octal digits code - <c>\12</c> beside one group is a line feed - and any
    /// digit after them a character of its own.
    /// </summary>
    private static int CopyNumbered(string pattern, int index, int groups, StringBuilder read)
    {
        var end = index + 1;

        while (end < pattern.Length && char.IsAsciiDigit(pattern[end]))
        {
            end++;
        }

        var digits = pattern.AsSpan(index + 1, end - index - 1);

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= groups)
        {
            read.Append('\\').Append(digits);
            return end - 1;
        }

        var octal = 0;
        var code = 0;

        if (digits[0] is not ('8' or '9'))
        {
            // As many octal digits as stay within 0377: three where the first is 0 to 3, else two.
            var most = digits[0] <= '3' ? 3 : 2;

            while (octal < digits.Length && octal < most && digits[octal] is >= '0' and <= '7')
            {
                code = (code * 8) + (digits[octal] - '0');
                octal++;
            }

            read.Append(CultureInfo.InvariantCulture, $@"\u{code:X4}");
        }

        foreach (var digit in digits[octal..])
        {
            read.Append(digit);
        }

        return end - 1;
    }

    /// <summary>
    /// The class escapes ECMA-262 reads as ASCII's - <c>\d</c>, <c>\D</c>, <c>\w</c>, <c>\W</c> - and its white space, as
    /// .NET is to read them: outside a class a class of its own, and within one its members, or <see langword="null"/>
    /// where <paramref name="escaped"/> is none of them, or, within a class, <c>\S</c>, which no list of members spells
    /// (<see cref="CopyClass"/>). An identity escape of a letter or a digit .NET does not read - <c>\q</c> - is its
    /// character, by its code.
    /// </summary>
    private static string? Shorthand(char escaped, bool inClass)
    {
        var members = escaped switch
        {
            'd' => "0-9",
            'D' => @"\u0000-/:-￿",
            'w' => "0-9A-Za-z_",
            'W' => @"\u0000-/:-@[-^`{-￿",
            's' => Space,
            _ => null,
        };

        if (members is not null)
        {
            return inClass ? members : $"[{members}]";
        }

        if (escaped == 'S')
        {
            return inClass ? null : $"[^{Space}]";
        }

        // ECMA-262 reads an escape it does not define as the character escaped. .NET refuses a word character it does not
        // read as an escape of its own, and reads any other - punctuation, a space - as the character, as ECMA-262 does.
        var known = inClass ? EscapesInAClass : EscapesOutsideAClass;

        return (char.IsLetterOrDigit(escaped) || escaped == '_') && !known.Contains(escaped, StringComparison.Ordinal)
            ? $@"\u{(int)escaped:X4}"
            : null;
    }

    /// <summary>
    /// The word characters a backslash leads outside a class that .NET reads as escapes of its own, each either read alike
    /// by ECMA-262, written otherwise (<see cref="Shorthand"/>, <see cref="CopyNumbered"/>), or refused before
    /// (<see cref="EscapeNotEcma"/>): every other is to ECMA-262 the character itself.
    /// </summary>
    private const string EscapesOutsideAClass = "0123456789aAbBcdDefGknpPrsStuvwWxzZ";

    /// <summary>The word characters a backslash leads within a class that .NET reads as escapes of its own, as <see cref="EscapesOutsideAClass"/> are.</summary>
    private const string EscapesInAClass = "01234567abcdDefnpPrsStuvwWx";

    /// <summary>
    /// Copies a character class as ECMA-262 reads it and answers the index of its closing bracket. A class closes
    /// at its first unescaped <c>]</c>: <c>[^]</c> takes any one character, and <c>[]</c> is the empty class no character
    /// answers - each written as a class of every code unit or of none, since .NET would read that bracket as one of the
    /// class's characters. A <c>[</c> within is a character, and is escaped so .NET never reads <c>-[...]</c> as the
    /// subtraction ECMA-262 does not have. <c>\d</c>, <c>\w</c> and their complements within are ASCII's, written as the
    /// code units they hold; <c>\s</c> within is ECMA-262's white space; and a class holding <c>\S</c> - whose ECMA-262
    /// meaning no list of .NET class members spells - is written as the choice between its other members and every
    /// character but white space, or, negated, as the white space its other members do not hold. A <c>-</c> beside a class
    /// escape is a character, as ECMA-262's Annex B reads it, and is escaped so .NET never reads a range of it. Within a
    /// class <c>$</c> and <c>.</c> are characters, so nothing else in it is rewritten; an escape .NET reads otherwise there is
    /// refused, as <see cref="ReadPattern"/> says.
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
            read.Append(@"[^\u0000-￿]");
            return index + 1;
        }

        index++;

        var negated = index < pattern.Length && pattern[index] == '^';
        var members = new StringBuilder();
        var notSpace = false;

        // Whether the member copied last is a class escape, which a '-' after it never makes a range of; and whether it is a
        // '-' as written, which a class escape after it never makes a range of either.
        var escapedLast = false;
        var hyphenLast = false;

        for (index += negated ? 1 : 0; index < pattern.Length && pattern[index] != ']'; index++)
        {
            if (pattern[index] == '\\' && index + 1 < pattern.Length)
            {
                if (EscapeNotEcma(pattern.AsSpan(index + 1), inClass: true) is { } dotnet)
                {
                    problem = ReadApart(dotnet);
                    return index;
                }

                var escaped = pattern[++index];
                var classEscape = escaped is 'd' or 'D' or 'w' or 'W' or 's' or 'S';

                // A '-' just before a class escape is a character, never a range's: ECMA-262 makes no range of one.
                if (classEscape && hyphenLast)
                {
                    members.Length--;
                    members.Append(@"\-");
                }

                if (escaped == 'S')
                {
                    notSpace = true;
                }
                else
                {
                    members.Append(Shorthand(escaped, inClass: true) ?? $@"\{escaped}");
                }

                (escapedLast, hyphenLast) = (classEscape, false);
                continue;
            }

            var hyphen = pattern[index] == '-';

            members.Append(pattern[index] switch
            {
                '[' => @"\[",
                '-' when escapedLast => @"\-",
                var character => character.ToString(),
            });
            (escapedLast, hyphenLast) = (false, hyphen && !escapedLast);
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
            (true, false, 0) => @"[\u0000-￿]",
            (true, false, _) => $"[^{members}]",
            (false, true, 0) => $"[^{Space}]",
            (false, true, _) => $"(?:[{members}]|[^{Space}])",
            (true, true, 0) => $"[{Space}]",
            (true, true, _) => $"[{Space}-[{members}]]",
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
        if (rest is ['<', ..] && rest.IndexOf('>') is var close and > 1 && IsIdentifier(rest[1..close]))
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
    /// <param name="inClass">Whether the escape is within a class, where .NET reads the anchors' letters, a backspace and
    /// the backreferences' characters as ECMA-262 does.</param>
    private static string? EscapeNotEcma(ReadOnlySpan<char> rest, bool inClass)
    {
        switch (rest)
        {
            case ['p' or 'P' or 'a' or 'e', ..]:
            case ['A' or 'Z' or 'z' or 'G' or 'b' or 'B', ..] when !inClass:
                return $@"\{rest[0]}";

            case ['k', '\'' or '<', ..] or ['<' or '\'', ..] when !inClass:
                var opens = rest[0] == 'k' ? 1 : 0;
                var close = rest[opens] == '<' ? '>' : '\'';
                var name = rest[(opens + 1)..];
                var length = name.IndexOf(close);

                // Read as .NET reads a reference: a name or a number up to its closing mark. ECMA-262's own is
                // \k<name>, by a name alone; anything else .NET either reads as ECMA-262 does or cannot read.
                if (length < 1
                    || (!IsIdentifier(name[..length]) && !IsDigits(name[..length]))
                    || (opens == 1 && close == '>' && !char.IsAsciiDigit(name[0])))
                {
                    return null;
                }

                return $@"\{rest[..(opens + length + 2)]}";

            default:
                return null;
        }
    }

    /// <summary>Why a pattern holding <paramref name="written"/> is refused: the two dialects read it apart.</summary>
    /// <param name="written">What the pattern writes, as it writes it.</param>
    private static string ReadApart(string written)
        => $"'{written}' means one thing to .NET and another to ECMA-262, the dialect a pattern is written in";

    /// <summary>Whether text is ASCII digits alone, at least one.</summary>
    private static bool IsDigits(ReadOnlySpan<char> text)
        => !text.IsEmpty && !text.ContainsAnyExceptInRange('0', '9');

    /// <summary>Whether a group's name is one both dialects read: a letter or '_' in ASCII, then those or digits.</summary>
    private static bool IsIdentifier(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
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
