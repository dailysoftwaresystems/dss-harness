using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Output;

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

    /// <summary>
    /// The document a command whose document carries the code it exits with and the line it ends on only where it stops -
    /// list-orchestrator, list-worktree, legs, install-missing-tools, check-ci-legs, check-anchor-citations,
    /// check-anchor-balance and fix-line-endings - answers with where it stops before it has anything of its own to say:
    /// that code and that line, then the document's own members, as <paramref name="empty"/> holds them holding nothing.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    /// <param name="empty">The command's own document, holding nothing.</param>
    public static string Stopped(int exitCode, string summary, JsonObject empty)
    {
        ArgumentNullException.ThrowIfNull(empty);

        var document = new JsonObject { ["exitCode"] = exitCode, ["summary"] = summary };

        foreach (var (member, value) in empty)
        {
            document[member] = value?.DeepClone();
        }

        return document.ToJsonString(Options);
    }
}
