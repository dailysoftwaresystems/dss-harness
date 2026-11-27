using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Text;

namespace DssHarness.Core.Agents.ClaudeApi;

/// <summary>
/// What each failure of the Claude API is, as the loop acts on it (part 03, "The loop for the two APIs" and "The engine's
/// fallback"): the one table of its statuses and its errors' types, said in the API's own words with the request it
/// names.
/// </summary>
/// <remarks>
/// A spend limit reached - the organization's monthly cap, a 429 its error's details name, or one the organization or a
/// workspace set, a 400 that says so - is the key's limit at once, lifting when the API says access resumes; a 401 is a
/// key the provider refuses; a 402 the key's limit, its balance spent; a 429 a limit of the request's model on its key,
/// waited out within the client's tries and its longest wait; a 413 or a prompt too long the context full; a
/// 403 or a 404 - and a 400 answering a conversation's first request, which a model the key's organization is not set
/// up for is refused with - a model the provider will not serve the account; a 400 for a thinking block bound to another
/// conversation a defect of this tool's, never sent again; a 400 for a format more than structured outputs compile - its
/// grammar too large, more optional or union-typed properties than they take, a schema too complex, a conversation's first
/// request among them - the schema refused (<see cref="RefusesTheFormat"/>), which the loop answers by asking without it;
/// any other 400 a request no try mends;
/// and an overload, a server's error, a request timed out or a conflict, a network that failed and a stream cut, the
/// service failing for now.
/// </remarks>
public static class ClaudeApiFailures
{
    /// <summary>What an error of the API's says of itself, by each member's path: its type, its message, the request, and the code its details name.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<string>> ErrorMembers = [["error", "type"], ["error", "message"], ["request_id"], ["error", "details", "error_code"]];

    /// <summary>When the API says access resumes, as its sentence of a spend limit says it.</summary>
    private static readonly Regex RegainsAccess = LinearRegex.Compile(@"regain access on (?<date>\d{4}-\d{2}-\d{2}) at (?<time>\d{2}:\d{2}) UTC");

    /// <summary>What the error's details name a request refused at its organization's monthly spend cap: a 429 that no wait mends.</summary>
    public const string SpendCapCode = "enforced_spend_limit_reached";

    /// <summary>How the API begins what it says of a request refused at a spend limit the organization or a workspace set.</summary>
    public const string SetSpendLimit = "You have reached your specified";

    /// <summary>What the API's sentence of a spend limit names beside <see cref="SetSpendLimit"/>.</summary>
    public const string UsageLimits = "API usage limits";

    /// <summary>What the API says of a request it refused because the conversation's history changed under a thinking block.</summary>
    public const string BoundElsewhere = "bound to a different conversation";

    /// <summary>What the API says of a request whose prompt its model's context cannot hold.</summary>
    public const string PromptTooLong = "prompt is too long";

    /// <summary>What the API says of a request whose format's schema is more than its structured outputs compile.</summary>
    public const string GrammarTooLarge = "compiled grammar is too large";

    /// <summary>What the API says of a request whose format holds more optional properties than its structured outputs take.</summary>
    public const string TooManyOptional = "too many optional parameters";

    /// <summary>What the API says of a request whose format holds more properties of a union type - a type array or an <c>anyOf</c> - than its structured outputs take.</summary>
    public const string TooManyUnions = "too many parameters with union types";

    /// <summary>What the API says of a request whose format's schema is too complex for its structured outputs to compile.</summary>
    public const string TooComplex = "too complex for compilation";

    /// <summary>
    /// Every way the API says a request's format is more than its structured outputs compile - its grammar too large, more
    /// optional or union-typed properties than it takes, a schema too complex - each matched in what it says whatever its
    /// case: the one list of them, which is the format refused (<see cref="ChatFailureKind.SchemaRefused"/>) and never a
    /// model the account is not given.
    /// </summary>
    public static IReadOnlyList<string> MoreThanItCompiles { get; } = [GrammarTooLarge, TooManyOptional, TooManyUnions, TooComplex];

    /// <summary>
    /// Whether <paramref name="refused"/> refuses the request's format - its schema more than structured outputs compile
    /// (<see cref="MoreThanItCompiles"/>) - which is the schema refused (<see cref="ChatFailureKind.SchemaRefused"/>), never
    /// a failure of the step's: the loop sends the run's requests no format from then on, the envelope said in words in a
    /// message of the conversation, and every later run of that envelope on that model in the client's life says it so from
    /// its first request.
    /// </summary>
    /// <param name="refused">The refusal.</param>
    public static bool RefusesTheFormat(ApiEvent.Refused refused)
    {
        ArgumentNullException.ThrowIfNull(refused);

        var (type, message, _, _) = Read(refused.Body);

        return FormatTooLarge(refused.Status, type, message ?? string.Empty);
    }

    /// <summary>What a request the API refused is, as the loop acts on it.</summary>
    /// <param name="refused">The refusal: its status, its body, the wait it named.</param>
    /// <param name="first">Whether the request was its conversation's first.</param>
    /// <param name="model">The model the request asked for.</param>
    public static ChatFailure Of(ApiEvent.Refused refused, bool first, string model)
    {
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(model);

        var (type, message, request, code) = Read(refused.Body);
        var said = type is null
            ? ApiAnswers.Answered(refused.Status, refused.Body)
            : ApiAnswers.Refusal(refused.Status, type, message, request);

        return Classed(refused.Status, type, code, message ?? string.Empty, said, refused.RetryAfter, first, model);
    }

    /// <summary>What an error the API sent in the middle of an answer's stream is, as the loop acts on it.</summary>
    /// <param name="data">The <c>error</c> event's data.</param>
    /// <param name="first">Whether the request was its conversation's first.</param>
    /// <param name="model">The model the request asked for.</param>
    public static ChatFailure OfStream(string data, bool first, string model)
    {
        ArgumentNullException.ThrowIfNull(data);

        var (type, message, request, code) = Read(data);
        var said = ApiAnswers.StreamFailure(type, message, data, request);

        return Classed(null, type, code, message ?? string.Empty, said, null, first, model);
    }

    /// <summary>
    /// The failure of a status - none for an error of a stream - and an error's type and the code its details name, said as
    /// <paramref name="said"/>: a spend limit first, whatever its status, for no wait or other model mends it.
    /// </summary>
    private static ChatFailure Classed(int? status, string? type, string? code, string message, string said, TimeSpan? retryAfter, bool first, string model)
        => (status, type) switch
        {
            _ when code == SpendCapCode || (message.StartsWith(SetSpendLimit, StringComparison.Ordinal) && message.Contains(UsageLimits, StringComparison.Ordinal))
                => new(ChatFailureKind.AccountLimit, said, Lifts: Resumes(message)),
            (401, _) or (null, "authentication_error") => new(ChatFailureKind.KeyRefused, said),
            (402, _) or (null, "billing_error") => new(ChatFailureKind.AccountLimit, said, BalanceSpent: true),
            (429, _) or (null, "rate_limit_error") => new(ChatFailureKind.RateLimited, said, retryAfter),
            (413, _) or (null, "request_too_large") => new(ChatFailureKind.ContextFull, said),
            (403 or 404, _) or (null, "permission_error" or "not_found_error") => new(ChatFailureKind.Unserved, said),
            (400, _) or (null, "invalid_request_error") when message.Contains(PromptTooLong, StringComparison.OrdinalIgnoreCase) => new(ChatFailureKind.ContextFull, said),
            _ when FormatTooLarge(status, type, message) => new(ChatFailureKind.SchemaRefused, said),
            (400, _) or (null, "invalid_request_error") when message.Contains(BoundElsewhere, StringComparison.OrdinalIgnoreCase) => new(
                ChatFailureKind.Fatal,
                $"{said} - this tool changed the conversation's history under a thinking block, which is a defect of its own: the request is not sent again, nor its thinking dropped"),
            (400, _) or (null, "invalid_request_error") when first => new(
                ChatFailureKind.Unserved,
                $"{said}; if the request itself is not at fault, the organization this key belongs to is not given '{model}' - a model that needs 30-day data retention is refused this way where the organization keeps less - and another model can be put first under its tier in tierConfig, or named on the step"),
            ({ } failing, _) when ApiAnswers.FailsForNow(failing) => new(ChatFailureKind.Transient, said, retryAfter),
            (null, "overloaded_error" or "api_error" or "timeout_error") => new(ChatFailureKind.Transient, said, retryAfter),
            _ => new(ChatFailureKind.Fatal, said),
        };

    /// <summary>
    /// Whether a refusal of <paramref name="status"/> - none for an error of a stream, then of <paramref name="type"/> -
    /// says <paramref name="message"/> of a format more than structured outputs compile, in any of the ways the API says it
    /// (<see cref="MoreThanItCompiles"/>).
    /// </summary>
    private static bool FormatTooLarge(int? status, string? type, string message)
        => (status == 400 || (status is null && type == "invalid_request_error"))
            && MoreThanItCompiles.Any(said => message.Contains(said, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A refused body's error - its type, message and request, and the code its details name - read as it streams from
    /// where it is kept, never whole; nulls where the body is no error of the API's.
    /// </summary>
    private static (string? Type, string? Message, string? Request, string? Code) Read(ApiRefusedBody body)
    {
        using var stream = body.Open();
        return Read(stream);
    }

    /// <summary>An error's type, message and request, and the code its details name, as the API writes them; nulls where the data is no error of its.</summary>
    private static (string? Type, string? Message, string? Request, string? Code) Read(string data)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data), writable: false);
        return Read(stream);
    }

    /// <summary>What an error of the API's says of itself (<see cref="ErrorMembers"/>), read from <paramref name="json"/> as it streams.</summary>
    private static (string? Type, string? Message, string? Request, string? Code) Read(Stream json)
    {
        var members = ApiAnswers.Members(json, ErrorMembers);

        return (
            members.GetValueOrDefault("error.type"),
            members.GetValueOrDefault("error.message"),
            members.GetValueOrDefault("request_id"),
            members.GetValueOrDefault("error.details.error_code"));
    }

    /// <summary>When the API says access resumes - "You will regain access on 2026-09-01 at 00:00 UTC" - where it says it; null where it does not.</summary>
    private static DateTimeOffset? Resumes(string message)
        => RegainsAccess.Match(message) is { Success: true } said
            && DateTimeOffset.TryParseExact($"{said.Groups["date"].Value} {said.Groups["time"].Value}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;
}
