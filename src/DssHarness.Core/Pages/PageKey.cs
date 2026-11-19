using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Pages;

/// <summary>
/// The one spelling of the key an answer's pages are kept under (<see cref="KeptAnswers{TKept}"/>): an ask hashed to one
/// name, whatever its spelling, and never its page - so every page of one ask reads the one result kept for it.
/// </summary>
public static class PageKey
{
    /// <summary>What the key of a call's result begins with: no ask's key, a digest alone, begins so.</summary>
    public const string CallPrefix = "call-";

    /// <summary>The ask a call is kept for, hashed to one name: the operation and the ask as the tool resolved it, its members in their names' order.</summary>
    /// <param name="operation">The operation's name, as <c>op</c> says it.</param>
    /// <param name="asked">
    /// The ask as the tool resolved it - each node by its id, each file by the path the view holds it at, each default
    /// filled in - so two spellings of one ask are one key; never the page.
    /// </param>
    public static string Of(string operation, JsonObject asked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(asked);

        if (asked.ContainsKey("page"))
        {
            throw new ArgumentException("an ask is kept whatever page of it is asked", nameof(asked));
        }

        var members = asked.Where(member => member.Value is not null).OrderBy(member => member.Key, StringComparer.Ordinal);
        var said = operation + "\n" + string.Join("\n", members.Select(member => $"{member.Key}={member.Value!.ToJsonString()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(said))).ToLowerInvariant();
    }

    /// <summary>
    /// The key a call's whole result is kept under: <see cref="CallPrefix"/> and the full digest of the call's id, so two
    /// ids however long are two keys, and none is an ask's (<see cref="Of"/>).
    /// </summary>
    /// <param name="id">The call's id, as its client named it.</param>
    public static string OfCall(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return CallPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
    }
}
