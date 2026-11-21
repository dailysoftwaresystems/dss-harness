using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Json;

/// <summary>
/// A place in a JSON document as a walk reaches it: the document's root, a member of an object by its name, or an item of
/// an array by its index, each within the place that holds it. Its path is spelt only when it is asked for
/// (<see cref="Path"/>), by a loop up its holders, and never kept: a path held on every place would make the paths of a
/// walk down a document n deep add up to characters quadratic in n.
/// </summary>
/// <remarks>
/// <para>
/// A path is spelt as the repository spells a document's own path wherever it names one (the problems
/// <see cref="Schemas.JsonSchemaSubset"/> finds): <c>$</c> for the root; a member whose name is an identifier in ASCII - a
/// letter, <c>_</c> or <c>$</c>, then those or digits - as <c>.name</c>; any other name in brackets, written as JSON
/// writes a string, escaping only what JSON itself must, so <c>$["a.b"]</c> is one member, never two, and
/// <c>$["café"]</c> reads as it was written; and an item as <c>[index]</c>, from 0. A path names a place to whoever reads
/// it - a person, or a model asked to repair its answer - and is not written to be read back as a query: RFC 9535 spells
/// no <c>$</c> in a bare name, and <c>$.$ref</c> is how the repository names that member.
/// </para>
/// <para>
/// A class rather than a record: a record's own equality, hash and text would each go up <see cref="Parent"/> one call
/// per level, and a place 100,000 deep would overflow the stack there. Two places are the same place only as one object.
/// </para>
/// </remarks>
public sealed class JsonPlace
{
    /// <summary>
    /// How a name is written in brackets: as JSON, escaping only what JSON itself must. Nothing here is emitted into a web
    /// page, where the default encoder's escapes would matter.
    /// </summary>
    private static readonly JsonSerializerOptions Written = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The place within <paramref name="parent"/> that <paramref name="name"/> or <paramref name="index"/> names; the root where both are null.</summary>
    private JsonPlace(JsonPlace? parent, string? name, int? index)
    {
        Parent = parent;
        Name = name;
        Index = index;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    /// <summary>A document's root, <c>$</c>: held by nothing, at depth 0.</summary>
    public static JsonPlace Root { get; } = new(null, null, null);

    /// <summary>The place that holds this one; <see langword="null"/> at the root.</summary>
    public JsonPlace? Parent { get; }

    /// <summary>The member's name, where the place is a member of an object; <see langword="null"/> otherwise.</summary>
    public string? Name { get; }

    /// <summary>The item's index, from 0, where the place is an item of an array; <see langword="null"/> otherwise.</summary>
    public int? Index { get; }

    /// <summary>How many places hold this one: 0 at the root, 1 for a member or an item of the root, and so on down.</summary>
    public int Depth { get; }

    /// <summary>The place of the member named <paramref name="name"/> of the object at this place.</summary>
    /// <param name="name">The member's name, as the object holds it.</param>
    public JsonPlace Member(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return new JsonPlace(this, name, null);
    }

    /// <summary>The place of the item at <paramref name="index"/> of the array at this place.</summary>
    /// <param name="index">The item's index, from 0.</param>
    public JsonPlace Item(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return new JsonPlace(this, null, index);
    }

    /// <summary>
    /// The place's path from the document's root, spelt as the remarks say: the places that hold it gathered by a loop up
    /// its holders, then written from the root down into one string - in time and memory linear in the path, at any depth.
    /// </summary>
    public string Path()
    {
        var below = new JsonPlace[Depth];
        var at = this;

        for (var level = Depth - 1; level >= 0; level--)
        {
            below[level] = at;
            at = at.Parent!;
        }

        var path = new StringBuilder("$");

        foreach (var place in below)
        {
            path.Append(place.Name is { } name ? Segment(name) : Segment(place.Index!.Value));
        }

        return path.ToString();
    }

    /// <summary>The place's path (<see cref="Path"/>), so a place written into a message is said as its path.</summary>
    public override string ToString() => Path();

    /// <summary>How a path writes the member named <paramref name="name"/>: <c>.name</c> where the name is an identifier, and <c>["name"]</c> otherwise.</summary>
    /// <param name="name">The member's name.</param>
    public static string Segment(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return IsIdentifier(name) ? $".{name}" : $"[{JsonValue.Create(name).ToJsonString(Written)}]";
    }

    /// <summary>How a path writes the item at <paramref name="index"/>: <c>[index]</c>.</summary>
    /// <param name="index">The item's index, from 0.</param>
    public static string Segment(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return $"[{index.ToString(CultureInfo.InvariantCulture)}]";
    }

    /// <summary>Whether a name is an identifier in ASCII: a letter, <c>_</c> or <c>$</c>, then those or digits.</summary>
    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '$'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Every place of a JSON document, each with what it holds, from its root down: the root itself first, then each member
/// or item in the order the document holds them, each before the places under it - document order, as a reader of the
/// text meets their starts. A walk goes down on a stack of its own, never the call stack, holding one enumeration for each
/// level above the place it stands at, so a document of any depth is walked in time linear in what it holds.
/// </summary>
/// <remarks>
/// A walk is lazy: a place is reached as it is asked for, and a walk left early goes no further. What is walked must not
/// change under it, as for any enumeration, and an element's document stays open while it goes on. A
/// <see cref="JsonNode"/> read from text makes its children as the walk first reaches them, at the cost
/// <see cref="JsonDepth"/> says .NET's making has; a <see cref="JsonValue"/> is a place with nothing under it, whatever it
/// holds.
/// </remarks>
public static class JsonWalk
{
    /// <summary>Every place of the document at <paramref name="root"/>, with the element there, in document order.</summary>
    /// <param name="root">The document's root element.</param>
    /// <returns>The root's place and element first; a member an object names twice is a place twice, as the document writes it.</returns>
    public static IEnumerable<(JsonPlace Place, JsonElement Element)> Descendants(JsonElement root)
    {
        yield return (JsonPlace.Root, root);

        var open = new Stack<ElementsUnder>();

        if (ElementsUnder.Of(JsonPlace.Root, root) is { } first)
        {
            open.Push(first);
        }

        while (open.TryPeek(out var under))
        {
            if (!under.Next(out var place, out var element))
            {
                open.Pop();
                continue;
            }

            yield return (place, element);

            if (ElementsUnder.Of(place, element) is { } inner)
            {
                open.Push(inner);
            }
        }
    }

    /// <summary>Every place of the document at <paramref name="root"/>, with the node there, in document order.</summary>
    /// <param name="root">The document's root node; <see langword="null"/> for JSON's null.</param>
    /// <returns>The root's place and node first; a member whose value is JSON's null is a place with a <see langword="null"/> node.</returns>
    public static IEnumerable<(JsonPlace Place, JsonNode? Node)> Descendants(JsonNode? root)
    {
        yield return (JsonPlace.Root, root);

        var open = new Stack<NodesUnder>();

        try
        {
            if (NodesUnder.Of(JsonPlace.Root, root) is { } first)
            {
                open.Push(first);
            }

            while (open.TryPeek(out var under))
            {
                if (!under.Next(out var place, out var node))
                {
                    open.Pop().Dispose();
                    continue;
                }

                yield return (place, node);

                if (NodesUnder.Of(place, node) is { } inner)
                {
                    open.Push(inner);
                }
            }
        }
        finally
        {
            // A walk left early lets go of the enumerations it still holds open.
            while (open.TryPop(out var under))
            {
                under.Dispose();
            }
        }
    }

    /// <summary>
    /// A copy of <paramref name="node"/> and everything under it, made on a stack of its own - what
    /// <see cref="JsonNode.DeepClone"/> makes, which goes one call per level and overflows the stack on a deep document.
    /// Each node is given to the one that holds it once everything under it is made, while that holder has no parent of its
    /// own yet, so the making is linear in what the document holds at any depth.
    /// </summary>
    /// <param name="node">The node to copy; <see langword="null"/> for JSON's null, which is copied as itself.</param>
    /// <returns>The copy, held by nothing; members in the order the node holds them.</returns>
    public static JsonNode? Copy(JsonNode? node)
    {
        if (node is not (JsonObject or JsonArray))
        {
            return node?.DeepClone();
        }

        var open = new Stack<Copying>();
        open.Push(Copying.Of(node, null, -1)!);

        while (true)
        {
            var copying = open.Peek();

            if (copying.Next(out var name, out var index, out var child))
            {
                if (Copying.Of(child, name, index) is { } inner)
                {
                    open.Push(inner);
                }
                else
                {
                    copying.Add(name, child?.DeepClone());
                }

                continue;
            }

            open.Pop();

            if (!open.TryPeek(out var holder))
            {
                return copying.Made;
            }

            holder.Add(copying.Name, copying.Made);
        }
    }

    /// <summary>
    /// Whether <paramref name="left"/> and <paramref name="right"/> are the same JSON, as <see cref="JsonNode.DeepEquals"/>
    /// says - an object's members by name, in any order, an array's items in order, and two values as .NET compares them -
    /// compared on a stack of its own, so documents of any depth are compared.
    /// </summary>
    /// <param name="left">One node; <see langword="null"/> for JSON's null.</param>
    /// <param name="right">The other.</param>
    public static bool Same(JsonNode? left, JsonNode? right)
    {
        var pairs = new Stack<(JsonNode? Left, JsonNode? Right)>();
        pairs.Push((left, right));

        while (pairs.TryPop(out var pair))
        {
            switch (pair)
            {
                case (JsonObject one, JsonObject other):
                    if (one.Count != other.Count)
                    {
                        return false;
                    }

                    foreach (var (name, value) in one)
                    {
                        if (!other.TryGetPropertyValue(name, out var otherValue))
                        {
                            return false;
                        }

                        pairs.Push((value, otherValue));
                    }

                    break;

                case (JsonArray one, JsonArray other):
                    if (one.Count != other.Count)
                    {
                        return false;
                    }

                    for (var index = 0; index < one.Count; index++)
                    {
                        pairs.Push((one[index], other[index]));
                    }

                    break;

                case (JsonObject or JsonArray, _) or (_, JsonObject or JsonArray):
                    return false;

                // Two values, or JSON's null: .NET's own comparison, which goes down nothing.
                default:
                    if (!JsonNode.DeepEquals(pair.Left, pair.Right))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// An object or an array being copied (<see cref="Copy"/>): what it is copied from, gone through one member or item at a
    /// time, the copy made so far, and where the copy goes in the one that holds it.
    /// </summary>
    private sealed class Copying
    {
        private readonly IEnumerator<KeyValuePair<string, JsonNode?>>? _members;
        private readonly IEnumerator<JsonNode?>? _items;
        private int _index;

        private Copying(JsonNode made, string? name, int index, IEnumerator<KeyValuePair<string, JsonNode?>>? members, IEnumerator<JsonNode?>? items)
        {
            Made = made;
            Name = name;
            Index = index;
            _members = members;
            _items = items;
        }

        /// <summary>The copy, made so far.</summary>
        public JsonNode Made { get; }

        /// <summary>The member's name the copy goes under in the object that holds it; <see langword="null"/> for an item, or the root.</summary>
        public string? Name { get; }

        /// <summary>The item's index the copy goes at in the array that holds it; -1 for a member, or the root.</summary>
        public int Index { get; }

        /// <summary>
        /// The copying of <paramref name="node"/>, where it is an object or an array; <see langword="null"/> otherwise. The copy
        /// is given options of its own, as <see cref="JsonDepth.ParseNode"/> gives a node: one made with none would ask its
        /// holder for them, a call a level, and asking the node copied would ask up its own holders.
        /// </summary>
        public static Copying? Of(JsonNode? node, string? name, int index) => node switch
        {
            JsonObject members => new Copying(new JsonObject(CopiedOptions), name, index, members.GetEnumerator(), null),
            JsonArray items => new Copying(new JsonArray(CopiedOptions), name, index, null, items.GetEnumerator()),
            _ => null,
        };

        /// <summary>The options every object and array a copy makes is given: .NET's own.</summary>
        private static readonly JsonNodeOptions CopiedOptions = new();

        /// <summary>The next member or item to copy; <see langword="false"/> where none is left.</summary>
        public bool Next(out string? name, out int index, out JsonNode? child)
        {
            if (_members is not null && _members.MoveNext())
            {
                (name, index, child) = (_members.Current.Key, -1, _members.Current.Value);
                return true;
            }

            if (_items is not null && _items.MoveNext())
            {
                (name, index, child) = (null, _index++, _items.Current);
                return true;
            }

            (name, index, child) = (null, -1, null);
            return false;
        }

        /// <summary>Puts <paramref name="copy"/> in the copy made so far: under <paramref name="name"/> in an object, last in an array.</summary>
        public void Add(string? name, JsonNode? copy)
        {
            if (Made is JsonObject members)
            {
                members.Add(name!, copy);
            }
            else
            {
                ((JsonArray)Made).Add(copy);
            }
        }
    }

    /// <summary>The elements directly under one object or array, gone through one at a time, with the place each is at.</summary>
    private sealed class ElementsUnder
    {
        private readonly JsonPlace _place;
        private readonly bool _isObject;

        // Enumerators of .NET's are structs: held in fields that are not read-only, each move moves the one held here,
        // never a copy of it.
        private JsonElement.ObjectEnumerator _members;
        private JsonElement.ArrayEnumerator _items;
        private int _index;

        /// <summary>The members of the object at <paramref name="place"/>.</summary>
        private ElementsUnder(JsonPlace place, JsonElement.ObjectEnumerator members)
        {
            _place = place;
            _isObject = true;
            _members = members;
        }

        /// <summary>The items of the array at <paramref name="place"/>.</summary>
        private ElementsUnder(JsonPlace place, JsonElement.ArrayEnumerator items)
        {
            _place = place;
            _items = items;
        }

        /// <summary>What is under <paramref name="element"/>, at <paramref name="place"/>; <see langword="null"/> where it is no object or array.</summary>
        public static ElementsUnder? Of(JsonPlace place, JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => new ElementsUnder(place, element.EnumerateObject()),
            JsonValueKind.Array => new ElementsUnder(place, element.EnumerateArray()),
            _ => null,
        };

        /// <summary>The next element under it, with its place; <see langword="false"/> where none is left.</summary>
        public bool Next(out JsonPlace place, out JsonElement element)
        {
            if (_isObject ? _members.MoveNext() : _items.MoveNext())
            {
                (place, element) = _isObject
                    ? (_place.Member(_members.Current.Name), _members.Current.Value)
                    : (_place.Item(_index++), _items.Current);

                return true;
            }

            (place, element) = (_place, default);
            return false;
        }
    }

    /// <summary>The nodes directly under one object or array, gone through one at a time, with the place each is at.</summary>
    private sealed class NodesUnder : IDisposable
    {
        private readonly JsonPlace _place;
        private readonly IEnumerator<KeyValuePair<string, JsonNode?>>? _members;
        private readonly IEnumerator<JsonNode?>? _items;
        private int _index;

        /// <summary>The members of the object, or the items of the array, at <paramref name="place"/>: one of the two given.</summary>
        private NodesUnder(JsonPlace place, IEnumerator<KeyValuePair<string, JsonNode?>>? members, IEnumerator<JsonNode?>? items)
        {
            _place = place;
            _members = members;
            _items = items;
        }

        /// <summary>What is under <paramref name="node"/>, at <paramref name="place"/>; <see langword="null"/> where it is no object or array.</summary>
        public static NodesUnder? Of(JsonPlace place, JsonNode? node) => node switch
        {
            JsonObject members => new NodesUnder(place, members.GetEnumerator(), null),
            JsonArray items => new NodesUnder(place, null, items.GetEnumerator()),
            _ => null,
        };

        /// <summary>The next node under it, with its place; <see langword="false"/> where none is left.</summary>
        public bool Next(out JsonPlace place, out JsonNode? node)
        {
            if (_members is not null && _members.MoveNext())
            {
                (place, node) = (_place.Member(_members.Current.Key), _members.Current.Value);
                return true;
            }

            if (_items is not null && _items.MoveNext())
            {
                (place, node) = (_place.Item(_index++), _items.Current);
                return true;
            }

            (place, node) = (_place, null);
            return false;
        }

        /// <summary>Lets go of the enumeration it holds.</summary>
        public void Dispose()
        {
            _members?.Dispose();
            _items?.Dispose();
        }
    }
}
