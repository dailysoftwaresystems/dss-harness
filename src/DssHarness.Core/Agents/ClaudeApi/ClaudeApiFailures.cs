using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;

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
/// conversation a defect of this tool's, never sent again; any other 400 a request no try mends; and an overload, a
/// server's error, a request timed out or a conflict, a network that failed and a stream cut, the service failing for
/// now.
/// </remarks>
public static partial class ClaudeApiFailures
{
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

    /// <summary>The most of a body that is no error the API wrote said in a failure: the start of what came.</summary>
    public const int MostSaid = 300;

    /// <summary>What a request the API refused is, as the loop acts on it.</summary>
    /// <param name="refused">The refusal: its status, its body, the wait it named.</param>
    /// <param name="first">Whether the request was its conversation's first.</param>
    /// <param name="model">The model the request asked for.</param>
    public static ChatFailure Of(ApiEvent.Refused refused, bool first, string model)
    {
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(model);

        var (type, message, request, code) = Read(refused.Body);
        var status = refused.Status.ToString(CultureInfo.InvariantCulture);
        var said = type is null
            ? $"the API answered {status}{(refused.Body.Length > 0 ? $": {Start(refused.Body)}" : " with nothing said")}"
            : $"the API answered {status} {type}: {message}{(request is null ? string.Empty : $" (request {request})")}";

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
        var said = $"the API's stream failed: {type ?? "an error"}: {message ?? Start(data)}{(request is null ? string.Empty : $" (request {request})")}";

        return Classed(null, type, code, message ?? string.Empty, said, null, first, model);
    }

    /// <summary>A request that never reached the API: the network failed, which is the service failing for now.</summary>
    /// <param name="why">What the network said.</param>
    public static ChatFailure Unreached(string why) => new(ChatFailureKind.Transient, $"the API could not be reached: {why}");

    /// <summary>An answer whose stream was cut part way: the service failing for now.</summary>
    /// <param name="why">What the network said.</param>
    public static ChatFailure Cut(string why) => new(ChatFailureKind.Transient, $"the answer's stream was cut: {why}");

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
            (400, _) or (null, "invalid_request_error") when message.Contains(BoundElsewhere, StringComparison.OrdinalIgnoreCase) => new(
                ChatFailureKind.Fatal,
                $"{said} - this tool changed the conversation's history under a thinking block, which is a defect of its own: the request is not sent again, nor its thinking dropped"),
            (400, _) or (null, "invalid_request_error") when first => new(
                ChatFailureKind.Unserved,
                $"{said}; if the request itself is not at fault, the organization this key belongs to is not given '{model}' - a model that needs 30-day data retention is refused this way where the organization keeps less - and another model can be put first under its tier in tierConfig, or named on the step"),
            (408 or 409 or >= 500, _) or (null, "overloaded_error" or "api_error" or "timeout_error") => new(ChatFailureKind.Transient, said, retryAfter),
            _ => new(ChatFailureKind.Fatal, said),
        };

    /// <summary>An error's type, message and request, and the code its details name, as the API writes them; nulls where the body is no error of its.</summary>
    private static (string? Type, string? Message, string? Request, string? Code) Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, null);
            }

            return (
                Text(error, "type"),
                Text(error, "message"),
                Text(root, "request_id"),
                error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object ? Text(details, "error_code") : null);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    /// <summary>When the API says access resumes - "You will regain access on 2026-09-01 at 00:00 UTC" - where it says it; null where it does not.</summary>
    private static DateTimeOffset? Resumes(string message)
        => RegainsAccess().Match(message) is { Success: true } said
            && DateTimeOffset.TryParseExact($"{said.Groups["date"].Value} {said.Groups["time"].Value}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;

    [GeneratedRegex(@"regain access on (?<date>\d{4}-\d{2}-\d{2}) at (?<time>\d{2}:\d{2}) UTC", RegexOptions.CultureInvariant)]
    private static partial Regex RegainsAccess();

    private static string? Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    /// <summary>The start of a body that is no error the API wrote, on one line.</summary>
    private static string Start(string body)
    {
        var line = string.Join(' ', body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return line.Length <= MostSaid ? line : $"{line[..MostSaid]}...";
    }
}
