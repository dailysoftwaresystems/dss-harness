using System.Globalization;
using System.Text;
using System.Text.Json;
using DssHarness.Core.Execution;
using DssHarness.Core.Json;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// A template of a request - an address, or a text of a <c>POST</c>'s body - whose names in braces are filled for each
/// query: <c>{query}</c>, the query; <c>{limit}</c>, the most results asked; and each name a lookup found, as
/// <c>discover</c>'s. Read by the configuration's one brace grammar (<see cref="FlowTemplate.Pieces(string)"/>): <c>{{</c>
/// and <c>}}</c> stand for a brace, another expander's <c>${...}</c> is left as written, and a name is one the
/// configuration's placeholders take (<see cref="LegPathNames.IsName"/>).
/// </summary>
public sealed class WebRequestTemplate
{
    /// <summary>The name filled with the query.</summary>
    public const string Query = "query";

    /// <summary>The name filled with the most results asked.</summary>
    public const string Limit = "limit";

    private readonly IReadOnlyList<(string Text, bool Name)> _pieces;

    private WebRequestTemplate(string text, IReadOnlyList<(string Text, bool Name)> pieces)
    {
        Text = text;
        _pieces = pieces;
    }

    /// <summary>The template as written.</summary>
    public string Text { get; }

    /// <summary>The names it fills, in its order, each once.</summary>
    public IReadOnlyList<string> Names => [.. _pieces.Where(piece => piece.Name).Select(piece => piece.Text).Distinct(StringComparer.Ordinal)];

    /// <summary>Reads <paramref name="text"/>.</summary>
    /// <exception cref="FormatException">It is not written whole by the grammar, or a name in braces is none.</exception>
    public static WebRequestTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (WorkflowExpressions.TemplateSyntaxProblem(text) is { } broken)
        {
            throw new FormatException(broken);
        }

        var pieces = new List<(string, bool)>();

        // Every placeholder is closed: the grammar's check said so.
        foreach (var piece in FlowTemplate.Pieces(text))
        {
            if (piece.Kind == FlowTemplate.PieceKind.Text)
            {
                pieces.Add((piece.Text, false));
                continue;
            }

            if (!LegPathNames.IsName(piece.Text))
            {
                throw new FormatException($"'{{{piece.Text}}}' names no value: a name {LegPathNames.NameRule}");
            }

            pieces.Add((piece.Text, true));
        }

        return new WebRequestTemplate(text, pieces);
    }

    /// <summary>
    /// Why <paramref name="text"/> is no template of a request filled from <paramref name="names"/> - beside
    /// <see cref="Query"/> and <see cref="Limit"/> - or <see langword="null"/> where it is one.
    /// </summary>
    /// <param name="text">The template.</param>
    /// <param name="names">The names lookups find for it.</param>
    /// <param name="needsQuery">Whether it must name <c>{query}</c>.</param>
    public static string? Problem(string text, IReadOnlyCollection<string> names, bool needsQuery)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);

        WebRequestTemplate template;

        try
        {
            template = Parse(text);
        }
        catch (FormatException ex)
        {
            return $"is no template: {ex.Message}";
        }

        if (template.Names.FirstOrDefault(name => name is not Query and not Limit && !names.Contains(name)) is { } unknown)
        {
            return $"names '{{{unknown}}}', which nothing fills: {{{Query}}}, {{{Limit}}}{string.Concat(names.Select(name => $", {{{name}}}"))}";
        }

        return needsQuery && !template.Names.Contains(Query) ? $"never names {{{Query}}}, so no query would reach the source" : null;
    }

    /// <summary>
    /// Why <paramref name="text"/> is no template of an address filled from <paramref name="names"/> - beside
    /// <see cref="Query"/> and <see cref="Limit"/> - that reads, once filled, as an <c>http</c> or <c>https</c> address
    /// (each name filled as a found address is); <see langword="null"/> where it is one. Said of a source's key, as
    /// <c>'url' ...</c>.
    /// </summary>
    /// <param name="key">The source's key that holds it.</param>
    /// <param name="text">The template.</param>
    /// <param name="names">The names lookups find for it.</param>
    /// <param name="needsQuery">Whether it must name <c>{query}</c>.</param>
    public static string? AddressProblem(string key, string text, IReadOnlyCollection<string> names, bool needsQuery)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (Problem(text, names, needsQuery) is { } problem)
        {
            return $"'{key}' {problem}";
        }

        var filled = Parse(text).Address("q", 1, names.ToDictionary(name => name, _ => "https://found.invalid/x", StringComparer.Ordinal));

        return WebAddresses.IsWeb(filled) ? null : $"'{key}' is an http or https address once filled, and '{text}' is not";
    }

    /// <summary>The template as an address: each name filled - the query escaped for an address, a found value as found.</summary>
    /// <param name="query">The query.</param>
    /// <param name="limit">The most results asked.</param>
    /// <param name="found">The values lookups found.</param>
    public string Address(string query, int limit, IReadOnlyDictionary<string, string> found)
        => Fill(query, limit, found, Uri.EscapeDataString);

    /// <summary>The template as a text: each name filled as it is.</summary>
    /// <param name="query">The query.</param>
    /// <param name="limit">The most results asked.</param>
    /// <param name="found">The values lookups found.</param>
    public string Plain(string query, int limit, IReadOnlyDictionary<string, string> found)
        => Fill(query, limit, found, value => value);

    /// <summary>Whether the template is <c>{limit}</c> alone, which a body fills as a number.</summary>
    public bool IsLimitAlone => _pieces is [(Limit, true)];

    private string Fill(string query, int limit, IReadOnlyDictionary<string, string> found, Func<string, string> escape)
    {
        var filled = new StringBuilder();

        foreach (var (text, name) in _pieces)
        {
            filled.Append(!name ? text
                : text == Query ? escape(query)
                : text == Limit ? limit.ToString(CultureInfo.InvariantCulture)
                : found.TryGetValue(text, out var value) ? value
                : throw new InvalidOperationException($"'{{{text}}}' is filled by no lookup of this source."));
        }

        return filled.ToString();
    }
}

/// <summary>
/// A template of one field of a result, read from one item of a source's JSON answer: text with JSON paths in braces -
/// <c>{@.title}</c>, <c>https://stackoverflow.com/q/{@.question_id}</c>, <c>{@.domain}{@.path}</c> - each path relative to
/// the item (<c>@</c>) or from the answer's root (<c>$</c>). A path's values that are text, numbers or truths are its
/// text, several joined by commas; one that gives none leaves the whole template unanswered. Read by the configuration's
/// one brace grammar (<see cref="FlowTemplate.Pieces(string, bool)"/>), a path closed by the first <c>}</c> outside its
/// quoted names: <c>{{</c> and <c>}}</c> stand for a brace, another expander's <c>${...}</c> is left as written.
/// </summary>
public sealed class WebItemTemplate
{
    private readonly IReadOnlyList<(string? Text, JsonPath? Path)> _pieces;

    private WebItemTemplate(string text, IReadOnlyList<(string? Text, JsonPath? Path)> pieces)
    {
        Text = text;
        _pieces = pieces;
    }

    /// <summary>The template as written.</summary>
    public string Text { get; }

    /// <summary>Reads <paramref name="text"/>.</summary>
    /// <exception cref="FormatException">It is not written whole by the grammar, a path is none, or none is named.</exception>
    public static WebItemTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (WorkflowExpressions.TemplateSyntaxProblem(text, quoted: true) is { } broken)
        {
            throw new FormatException(broken);
        }

        var pieces = new List<(string?, JsonPath?)>();

        // Every placeholder is closed: the grammar's check said so.
        foreach (var piece in FlowTemplate.Pieces(text, quoted: true))
        {
            switch (piece.Kind)
            {
                case FlowTemplate.PieceKind.Text:
                    pieces.Add((piece.Text, null));
                    break;

                default:
                    try
                    {
                        pieces.Add((null, piece.Text.StartsWith('@') ? JsonPath.ParseRelative(piece.Text) : JsonPath.Parse(piece.Text)));
                    }
                    catch (JsonPathException ex)
                    {
                        throw new FormatException($"'{{{piece.Text}}}' is no JSONPath query of the item ('@') or the answer ('$'): {ex.Message}", ex);
                    }

                    break;
            }
        }

        return pieces.Any(piece => piece.Item2 is not null)
            ? new WebItemTemplate(text, pieces)
            : throw new FormatException("it reads no value of the item, and a template reads at least one, as {@.title}");
    }

    /// <summary>Why <paramref name="text"/> is no template of a field, or <see langword="null"/> where it is one.</summary>
    /// <param name="text">The template.</param>
    public static string? Problem(string text)
    {
        try
        {
            Parse(text);
            return null;
        }
        catch (FormatException ex)
        {
            return $"is no template: {ex.Message}";
        }
    }

    /// <summary>The field's text for <paramref name="item"/> of <paramref name="root"/>, or <see langword="null"/> where a path of it gives no text.</summary>
    /// <param name="item">The item, which <c>@</c> names.</param>
    /// <param name="root">The whole answer, which <c>$</c> names.</param>
    public string? Fill(JsonElement item, JsonElement root)
    {
        var filled = new StringBuilder();

        foreach (var (text, path) in _pieces)
        {
            if (path is null)
            {
                filled.Append(text);
                continue;
            }

            var values = path.Select(item, root).Select(TextOf).OfType<string>().ToList();

            if (values.Count == 0)
            {
                return null;
            }

            filled.Append(string.Join(", ", values));

            // No more is filled than a field is read for, and two past it - one, where a pair would be parted - so its reader
            // cuts it and marks the cut: a value of the answer's root, as long as it likes, is not written whole for every item.
            if (filled.Length > WebPageText.MostFieldRead)
            {
                return Output.TextCut.Head(filled.ToString(), WebPageText.MostFieldRead + 2);
            }
        }

        return filled.ToString();
    }

    /// <summary>A value's text: a text as it is, a number as written, a truth as <c>true</c> or <c>false</c>; nothing of any other.</summary>
    internal static string? TextOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
