using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Secrets;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// One source of the harness's web search, by its name: its keys as the configuration writes them - every source's own
/// (<see cref="OwnKeys"/>) and its kind's (<see cref="IWebSearchKind.Keys"/>) - read by name in any case, as the
/// configuration's keys are.
/// </summary>
public sealed class WebSearchSource
{
    /// <summary>A source's pace where it names none: the most requests it is sent in a minute.</summary>
    public const int DefaultPerMinute = 30;

    /// <summary>The most requests a source may be sent in a minute: ten a second.</summary>
    public const int MostPerMinute = 600;

    /// <summary>The most requests a source may be sent in a day, where it names a bound.</summary>
    public const int MostPerDay = 1_000_000;

    /// <summary>A source of <paramref name="name"/>, its keys <paramref name="settings"/>.</summary>
    /// <param name="name">Its name, as the configuration declares it.</param>
    /// <param name="settings">Its keys: a JSON object, kept as it is.</param>
    /// <exception cref="ArgumentException"><paramref name="settings"/> is no object.</exception>
    public WebSearchSource(string name, JsonElement settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"A source's keys are an object, and '{name}' is given {settings.ValueKind}.", nameof(settings));
        }

        Name = name;
        Settings = settings.Clone();
    }

    /// <summary>
    /// The keys every source takes, whatever its kind: what it is, how the agent is told of it, whether it is used, and
    /// how often it may be asked.
    /// </summary>
    public static IReadOnlyList<KeyDescription> OwnKeys { get; } =
    [
        new("kind", "the kind of source it is, one of the kinds this tool holds") { Required = true },
        new("tags", "names a step's search may choose it by, beside its own"),
        new("description", "what the agent is told the source is for"),
        new("terms", "what its owner's terms say of programs asking it, which help says beside it"),
        new("enabled", "false turns the source off"),
        new("perMinute", $"the most requests it is sent in a minute on this machine, from 1 to {MostPerMinute}; {DefaultPerMinute} where none is said"),
        new("perDay", $"the most requests it is sent in a day on this machine, from 1 to {MostPerDay:N0}; no bound where none is said"),
        new("credential", WebSearchCredential.Meaning),
    ];

    /// <summary>Its name, as the configuration declares it.</summary>
    public string Name { get; }

    /// <summary>Its keys, as written.</summary>
    public JsonElement Settings { get; }

    /// <summary>The kind it names; <see langword="null"/> where it names none, or names it by no text.</summary>
    public string? Kind => Text("kind");

    /// <summary>The most requests it is sent in a minute: its <c>perMinute</c>, held to <see cref="OwnProblems"/>, else <see cref="DefaultPerMinute"/>.</summary>
    public int PerMinute => Integer("perMinute") ?? DefaultPerMinute;

    /// <summary>The most requests it is sent in a day: its <c>perDay</c>, held to <see cref="OwnProblems"/>; <see langword="null"/> where it names no bound.</summary>
    public int? PerDay => Integer("perDay");

    /// <summary>Whether it is used: true unless its <c>enabled</c> says false.</summary>
    public bool Enabled => Value("enabled") is not { ValueKind: JsonValueKind.False };

    /// <summary>The tags a step's search may choose it by, beside its name.</summary>
    public IReadOnlyList<string> Tags => Texts("tags") ?? [];

    /// <summary>What the agent is told it is for; <see langword="null"/> where it says nothing.</summary>
    public string? Description => Text("description");

    /// <summary>What its owner's terms say of programs asking it; <see langword="null"/> where it says nothing.</summary>
    public string? Terms => Text("terms");

    /// <summary>Its key's place and origin, where it names one that holds (<see cref="WebSearchCredential.Problems"/>); <see langword="null"/> otherwise.</summary>
    public WebSearchCredential? Credential
        => Value("credential") is { } credential && WebSearchCredential.Problems(credential).Count == 0 ? WebSearchCredential.Of(credential) : null;

    /// <summary>
    /// Why the values of the keys every source takes cannot be used, each problem one line naming its key: a key given
    /// twice, in two spellings or one; <c>tags</c> a list of names; <c>description</c> and <c>terms</c> texts that say
    /// something; <c>enabled</c> true or false; <c>perMinute</c> and <c>perDay</c> whole numbers within their bounds;
    /// <c>credential</c> one of its two shapes. Empty where they can be; a key not given is no problem here.
    /// </summary>
    public IReadOnlyList<string> OwnProblems()
    {
        var problems = new List<string>();

        if (JsonObjectKeys.TwiceProblem(Settings, string.Empty) is { } twice)
        {
            problems.Add(twice);
        }

        if (Has("tags") && (Value("tags") is not { ValueKind: JsonValueKind.Array } || Texts("tags") is not { } tags || tags.Any(tag => !IsName(tag))))
        {
            problems.Add($"'tags' is a list of names a step's search may choose the source by, each {NameRule}");
        }

        foreach (var (key, what) in OwnKeys.Where(own => own.Name is "description" or "terms").Select(own => (own.Name, own.Meaning)))
        {
            if (TextProblem(key) is { } text)
            {
                problems.Add(text);
            }
            else if (Text(key) is { } said && string.IsNullOrWhiteSpace(said))
            {
                problems.Add($"'{key}' says nothing; it is {what}");
            }
        }

        if (FlagProblem("enabled") is { } enabled)
        {
            problems.Add(enabled);
        }

        problems.AddRange(new[] { IntegerProblem("perMinute", 1, MostPerMinute), IntegerProblem("perDay", 1, MostPerDay) }.OfType<string>());

        if (Value("credential") is { } credential)
        {
            problems.AddRange(WebSearchCredential.Problems(credential));
        }

        return problems;
    }

    /// <summary>Every key it is given, as written.</summary>
    public IEnumerable<string> Keys => Settings.EnumerateObject().Select(member => member.Name);

    /// <summary>The value of <paramref name="key"/>, matched in any case; <see langword="null"/> where it is not given.</summary>
    /// <param name="key">The key.</param>
    public JsonElement? Value(string key) => JsonObjectKeys.Value(Settings, key);

    /// <summary>Whether <paramref name="key"/> is given.</summary>
    /// <param name="key">The key.</param>
    public bool Has(string key) => Value(key) is not null;

    /// <summary>The text <paramref name="key"/> holds; <see langword="null"/> where it holds none.</summary>
    /// <param name="key">The key.</param>
    public string? Text(string key) => JsonObjectKeys.Text(Settings, key);

    /// <summary>
    /// The texts <paramref name="key"/> holds: one text, or a list of them; <see langword="null"/> where it holds neither.
    /// </summary>
    /// <param name="key">The key.</param>
    public IReadOnlyList<string>? Texts(string key) => Value(key) switch
    {
        { ValueKind: JsonValueKind.String } one => [one.GetString()!],
        { ValueKind: JsonValueKind.Array } many when many.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            => [.. many.EnumerateArray().Select(item => item.GetString()!)],
        _ => null,
    };

    /// <summary>Why <paramref name="key"/>, if given, is neither true nor false; <see langword="null"/> where it is one, or is not given.</summary>
    /// <param name="key">The key.</param>
    public string? FlagProblem(string key)
        => Value(key) is { ValueKind: not (JsonValueKind.True or JsonValueKind.False) } value ? $"'{key}' is true or false, and is {value.GetRawText()}" : null;

    /// <summary>The whole number <paramref name="key"/> holds; <see langword="null"/> where it holds none an <see cref="int"/> takes.</summary>
    /// <param name="key">The key.</param>
    public int? Integer(string key) => Value(key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    /// <summary>Why <paramref name="key"/>, if given, is not a whole number from <paramref name="least"/> to <paramref name="most"/>; <see langword="null"/> where it is one, or is not given.</summary>
    /// <param name="key">The key.</param>
    /// <param name="least">The least it may be.</param>
    /// <param name="most">The most it may be.</param>
    public string? IntegerProblem(string key, int least, int most)
        => Value(key) is not { } value ? null
            : value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < least || number > most
                ? $"'{key}' is a whole number from {least.ToString("N0", CultureInfo.InvariantCulture)} to {most.ToString("N0", CultureInfo.InvariantCulture)}, and is {value.GetRawText()}"
                : null;

    /// <summary>Why <paramref name="key"/>, if given, is no text; <see langword="null"/> where it is one, or is not given.</summary>
    /// <param name="key">The key.</param>
    public string? TextProblem(string key)
        => Value(key) is { ValueKind: not JsonValueKind.String } value ? $"'{key}' is a text, and is {value.GetRawText()}" : null;

    /// <summary>
    /// Why <paramref name="key"/>, if given, is no absolute <c>http</c> or <c>https</c> address; <see langword="null"/>
    /// where it is one, or is not given.
    /// </summary>
    /// <param name="key">The key.</param>
    public string? AddressProblem(string key)
        => TextProblem(key) ?? (Text(key) is { } address && !WebAddresses.IsWeb(address)
            ? $"'{key}' is an http or https address, written whole, and is '{address}'"
            : null);

    /// <summary>
    /// What a name of the web search is made of - a source's, which names its index's directory, a tag's and a kind's - as
    /// a refusal says it: the one rule every name that names a directory here is held to (<see cref="WorktreeName"/>), so
    /// two names that differ only in case are never two directories that are one.
    /// </summary>
    public const string NameRule = "lower-case letters and digits joined by single hyphens";

    /// <summary>Whether <paramref name="name"/> is a name of the web search (<see cref="NameRule"/>): a source's, a tag's, a kind's.</summary>
    /// <param name="name">The name.</param>
    public static bool IsName([NotNullWhen(true)] string? name) => WorktreeName.ValidateFormat(name).TryGetName(out _, out _);

    /// <summary>The source's name.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// A source's key, as its <c>credential</c> says where it comes from and where it goes: a variable of the agents' <c>.env</c>
/// slot - <c>{ "from": "env", "name": ..., "query": ... }</c> or <c>"header": ...</c> - or the token of the machine's
/// <c>gh</c> login for its <c>host</c> - <c>{ "from": "gh" }</c>, github.com's, sent as <c>Authorization: Bearer</c> unless it
/// says otherwise, to that host or its API alone; <c>format</c>
/// writes what is sent, <c>{credential}</c> standing for the key. Its keys are read in any case, as the configuration's
/// are. One owner of the shape: the configuration's check reads its problems here, and the search the key's place.
/// </summary>
/// <param name="From"><c>env</c> or <c>gh</c>.</param>
/// <param name="Name">The variable of the slot, for <c>env</c>; <see langword="null"/> for <c>gh</c>.</param>
/// <param name="Query">The query parameter it is sent as; <see langword="null"/> where it is a header.</param>
/// <param name="Header">The header it is sent as; <see langword="null"/> where it is a query parameter.</param>
/// <param name="Format">What is sent, <c>{credential}</c> standing for the key.</param>
/// <param name="Host">The host whose <c>gh</c> login it is, for <c>gh</c>; <see langword="null"/> for <c>env</c>.</param>
public sealed record WebSearchCredential(string From, string? Name, string? Query, string? Header, string Format, string? Host = null)
{
    /// <summary>The host a <c>gh</c> login is for where its credential says none.</summary>
    public const string GitHubHost = "github.com";

    /// <summary>What the key is, as help says it.</summary>
    public const string Meaning = "a key to send it, used where it is there and never required: { \"from\": \"env\", \"name\": a variable of the .env slot, "
        + "and \"query\": a parameter or \"header\": a header } or { \"from\": \"gh\", \"host\": its host, github.com where left out }, the machine's gh "
        + "login for that host, sent as Authorization: Bearer to it or its api. host alone; "
        + "\"format\" writes what is sent, {credential} standing for the key";

    /// <summary>The name in <see cref="Format"/> that stands for the key.</summary>
    public const string Placeholder = "credential";

    /// <summary>The credential <paramref name="value"/> says, held to <see cref="Problems"/>.</summary>
    /// <param name="value">The <c>credential</c> key's value.</param>
    public static WebSearchCredential Of(JsonElement value)
    {
        var from = JsonObjectKeys.Text(value, "from")?.ToLowerInvariant() ?? throw new ArgumentException("A credential names where it comes from.", nameof(value));
        var query = JsonObjectKeys.Text(value, "query");
        var header = query is null ? JsonObjectKeys.Text(value, "header") ?? (from == "gh" ? "Authorization" : null) : null;
        var format = JsonObjectKeys.Text(value, "format") ?? (from == "gh" && string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase) ? $"Bearer {{{Placeholder}}}" : $"{{{Placeholder}}}");

        var host = from == "gh" ? JsonObjectKeys.Text(value, "host")?.ToLowerInvariant() ?? GitHubHost : null;

        return new WebSearchCredential(from, JsonObjectKeys.Text(value, "name"), query, header, format, host);
    }

    /// <summary>
    /// What is sent for <paramref name="key"/>: <see cref="Format"/> read by the configuration's one brace grammar
    /// (<see cref="FlowTemplate.Pieces(string)"/>), as <see cref="Problems"/> checked it - <c>{credential}</c> the key, a
    /// doubled brace one brace.
    /// </summary>
    /// <param name="key">The key.</param>
    public string Sent(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Concat(FlowTemplate.Pieces(Format).Select(piece => piece.Kind == FlowTemplate.PieceKind.Placeholder ? key : piece.Text));
    }

    /// <summary>
    /// Every form of <paramref name="key"/> a request carries or an answer echoes, which a step's mask holds so no record,
    /// event or answer keeps one: the key; as a query parameter, the key escaped - as an answer echoing the address it was
    /// asked at holds it; and the key in a JSON text, as an encoder escapes it - .NET's a '+', PHP's a '/' - for an error
    /// that quotes it is told as it was answered.
    /// </summary>
    /// <param name="key">The key.</param>
    public IReadOnlyList<string> Forms(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string[] forms = Query is null ? [key] : [key, Uri.EscapeDataString(key)];

        return [.. forms.Concat([JsonEncodedText.Encode(key).Value, key.Replace("/", "\\/", StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The key as the fetch sends it to its source: <paramref name="key"/>, written by <see cref="Format"/>, as the header or query parameter this says.</summary>
    /// <param name="key">The key.</param>
    public WebFetchKey Fetched(string key)
        => (Query is { } parameter ? new WebFetchKey(parameter, Sent(key), Query: true) : new WebFetchKey(Header!, Sent(key))) with { Hosts = Host is { } host ? [host, "api." + host] : null };

    /// <summary>Why <paramref name="value"/> is no credential, each problem one line naming its key; empty where it is one.</summary>
    /// <param name="value">The <c>credential</c> key's value.</param>
    public static IReadOnlyList<string> Problems(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return [$"'credential' is an object: {Meaning}"];
        }

        var problems = new List<string>();

        if (JsonObjectKeys.TwiceProblem(value, "credential.") is { } twice)
        {
            problems.Add(twice);
        }

        var from = JsonObjectKeys.Text(value, "from")?.ToLowerInvariant();
        string[] takes = from == "gh" ? ["from", "host", "query", "header", "format"] : ["from", "name", "query", "header", "format"];

        if (from is not ("env" or "gh"))
        {
            problems.Add("'credential.from' is env, a variable of the .env slot, or gh, the machine's gh login");
        }

        foreach (var member in value.EnumerateObject())
        {
            if (!takes.Contains(member.Name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"'credential' takes {string.Join(", ", takes)}{(from is null ? string.Empty : $" from {from}")}, and not '{member.Name}'");
            }
            else if (member.Value.ValueKind != JsonValueKind.String)
            {
                problems.Add($"'credential.{member.Name}' is a text, and is {member.Value.GetRawText()}");
            }
        }

        if (from == "env")
        {
            if (JsonObjectKeys.Text(value, "name") is not { } name)
            {
                problems.Add("'credential.name' is missing: the variable of the .env slot that holds the key");
            }
            else if (!AgentKeyStore.IsVariableName(name))
            {
                problems.Add($"'credential.name' is a variable's name, {AgentKeyStore.VariableNameShape}, and is '{name}'");
            }
        }

        if (from == "gh" && JsonObjectKeys.Text(value, "host") is { } host && Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            problems.Add($"'credential.host' is the host the machine's gh login is for, as {GitHubHost}, and is '{host}'");
        }

        var query = JsonObjectKeys.Text(value, "query");
        var header = JsonObjectKeys.Text(value, "header");

        if (query is not null && header is not null)
        {
            problems.Add("'credential' is sent as a query parameter or as a header, and names both");
        }
        else if (from == "env" && query is null && header is null)
        {
            problems.Add("'credential' says where the key is sent: 'query', a parameter, or 'header', a header");
        }

        if (query is not null && (query.Length == 0 || Uri.EscapeDataString(query) != query))
        {
            problems.Add($"'credential.query' is a query parameter's name - letters, digits, '-', '_', '.' and '~' - and is '{query}'");
        }

        if (header is not null && !WebSearchRequest.Http.IsHeaderName(header))
        {
            problems.Add($"'credential.header' is a header's name, and is '{header}'");
        }
        else if (header is not null && WebSearchRequest.Http.OwnHeaders.FirstOrDefault(own => string.Equals(own, header, StringComparison.OrdinalIgnoreCase)) is { } own)
        {
            problems.Add($"'credential.header' is {own}, which the request itself says");
        }
        else if (header is not null && !WebSearchRequest.Http.IsRequestHeader(header))
        {
            problems.Add($"'credential.header' is '{header}', which a body says of itself and no request carries");
        }

        if (JsonObjectKeys.Text(value, "format") is { } format)
        {
            var pieces = FlowTemplate.Pieces(format).ToList();

            if (pieces.Any(piece => piece.Kind == FlowTemplate.PieceKind.Unclosed || (piece.Kind == FlowTemplate.PieceKind.Placeholder && piece.Text != Placeholder))
                || !pieces.Any(piece => piece.Kind == FlowTemplate.PieceKind.Placeholder))
            {
                problems.Add($"'credential.format' writes what is sent, naming {{{Placeholder}}} for the key and no other name, and is '{format}'");
            }
        }

        return problems;
    }
}
