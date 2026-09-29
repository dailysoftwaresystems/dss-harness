using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoHarness.Core.Output;

/// <summary>
/// How every command's <c>--json</c> document is written: a ledger, the legs, the tools, the anchors, the worktrees and
/// the orchestrators. One set of options, so no two documents can come to be written by different rules.
/// </summary>
/// <remarks>
/// Indented, camelCase, without the members that are null, and with paths, names, statuses and cells written as they
/// are: they hold backslashes, emoji and any text, and escaping them would make the document unreadable to a person for
/// no benefit to a parser.
/// </remarks>
public static class ReportJson
{
    /// <summary>The options a document is written with.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
