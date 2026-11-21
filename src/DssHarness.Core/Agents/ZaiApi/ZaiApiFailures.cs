using System.Text;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;

namespace DssHarness.Core.Agents.ZaiApi;

/// <summary>
/// What each failure of Z.AI is, as the loop acts on it (part 03, "The loop for the two APIs"): the one table of its
/// codes - its body's <c>error.code</c>, a string - and of its statuses where it names none, said in Z.AI's own words.
/// </summary>
/// <remarks>
/// A usage limit names when it resets in its own sentence; a plan without the model (1311) is a model the provider will
/// not serve the account (the user, 2026-10-26); a code the table does not hold is read by its status, and fails the
/// step where that says nothing more.
/// </remarks>
public static class ZaiApiFailures
{
    /// <summary>Z.AI's codes, each row what its codes are - as the loop acts on them, and as the help says them.</summary>
    public static readonly IReadOnlyList<ZaiCodes> Codes =
    [
        new(["1000", "1001", "1003", "1005"], ChatFailureKind.KeyRefused, "a key it does not take"),
        new(["1113"], ChatFailureKind.AccountLimit, "an empty balance", BalanceSpent: true),
        new(["1308", "1310", "1316", "1317", "1318", "1319", "1320", "1321"], ChatFailureKind.AccountLimit, "a usage limit that names when it resets"),
        new(["1302"], ChatFailureKind.RateLimited, "a rate limit"),
        new(["1305", "1234", "1200", "1230"], ChatFailureKind.Transient, "an overload, a network error or a call failed on its side"),
        new(["1261"], ChatFailureKind.ContextFull, "a prompt too long"),
        new(["1301"], ChatFailureKind.Declined, "content it will not answer"),
        new(["1211", "1220", "1311"], ChatFailureKind.Unserved, "a model unknown, an API the key may not call or a plan without the model"),
        new(["1309", "1313", "1314", "1315"], ChatFailureKind.Fatal, "a plan that does not cover the call or has ended"),
    ];

    /// <summary>
    /// Z.AI's statuses where its body names no code <see cref="Codes"/> holds, each row what the loop takes them for; past
    /// them a request timed out, a conflict and a server's error fail for now (<see cref="ApiAnswers.FailsForNow"/>), and
    /// any other status fails the step.
    /// </summary>
    public static readonly IReadOnlyList<ZaiStatuses> Statuses =
    [
        new([401], ChatFailureKind.KeyRefused),
        new([429], ChatFailureKind.RateLimited),
        new([413], ChatFailureKind.ContextFull),
        new([403, 404], ChatFailureKind.Unserved),
    ];

    /// <summary>What a request Z.AI refused is, as the loop acts on it.</summary>
    /// <param name="refused">The refusal: its status, its body, the wait it named.</param>
    public static ChatFailure Of(ApiEvent.Refused refused)
    {
        ArgumentNullException.ThrowIfNull(refused);

        var (code, message) = Read(refused.Body);
        var said = code is null && message is null
            ? ApiAnswers.Answered(refused.Status, refused.Body)
            : ApiAnswers.Refusal(refused.Status, code ?? "an error", message);

        return Classed(refused.Status, code, said, refused.RetryAfter);
    }

    /// <summary>What an error Z.AI sent in the middle of an answer's stream is, as the loop acts on it.</summary>
    /// <param name="data">The chunk that holds the error.</param>
    public static ChatFailure OfStream(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var (code, message) = Read(data);

        return Classed(null, code, ApiAnswers.StreamFailure(code, message, data), null);
    }

    /// <summary>
    /// The failure of an error's code (<see cref="Codes"/>), else of its status (<see cref="Statuses"/>) - for a code the
    /// table does not hold too - said as <paramref name="said"/>, a wait it names kept where the failure is waited out.
    /// </summary>
    private static ChatFailure Classed(int? status, string? code, string said, TimeSpan? retryAfter)
    {
        var (kind, balanceSpent) = Codes.FirstOrDefault(row => row.Codes.Contains(code, StringComparer.Ordinal)) is { } coded
            ? (coded.Kind, coded.BalanceSpent)
            : Statuses.FirstOrDefault(row => status is { } at && row.Statuses.Contains(at)) is { } named
                ? (named.Kind, false)
                : (status is { } failing && ApiAnswers.FailsForNow(failing) ? ChatFailureKind.Transient : ChatFailureKind.Fatal, false);

        return new(kind, said, kind is ChatFailureKind.RateLimited or ChatFailureKind.Transient ? retryAfter : null, BalanceSpent: balanceSpent);
    }

    /// <summary>What an error of Z.AI's says of itself, by each member's path: its code and its message.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<string>> ErrorMembers = [["error", "code"], ["error", "message"]];

    /// <summary>A refused body's error code and message, read as it streams from where it is kept, never whole; nulls where the body is no error of Z.AI's.</summary>
    private static (string? Code, string? Message) Read(ApiRefusedBody body)
    {
        using var stream = body.Open();
        return Read(stream);
    }

    /// <summary>An error's code and message, as Z.AI writes them in a chunk of its stream; nulls where the chunk is no error of its.</summary>
    private static (string? Code, string? Message) Read(string data)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data), writable: false);
        return Read(stream);
    }

    /// <summary>An error's code - a string, or a number as it is written - and message, read from <paramref name="json"/> as it streams.</summary>
    private static (string? Code, string? Message) Read(Stream json)
    {
        var members = ApiAnswers.Members(json, ErrorMembers);

        return (members.GetValueOrDefault("error.code"), members.GetValueOrDefault("error.message"));
    }
}

/// <summary>One row of Z.AI's codes: the codes, what the loop takes them for, and what they are, as the help says it.</summary>
/// <param name="Codes">The codes, as Z.AI writes them.</param>
/// <param name="Kind">What the loop takes them for.</param>
/// <param name="What">What they are, as the help says it.</param>
/// <param name="BalanceSpent">Whether they say the key's balance is spent.</param>
public sealed record ZaiCodes(IReadOnlyList<string> Codes, ChatFailureKind Kind, string What, bool BalanceSpent = false);

/// <summary>One row of Z.AI's statuses where its body names no code the table holds: the statuses, and what the loop takes them for.</summary>
/// <param name="Statuses">The statuses.</param>
/// <param name="Kind">What the loop takes them for.</param>
public sealed record ZaiStatuses(IReadOnlyList<int> Statuses, ChatFailureKind Kind);
