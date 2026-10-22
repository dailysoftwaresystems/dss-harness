using System.Text.Json.Nodes;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>How a step's runner reads a key that takes a list of names: written out, or as the engine read it where the step started.</summary>
internal static class FlowStepKeys
{
    /// <summary>
    /// The names a key that takes a list of them holds the step to - an anchor check's <c>rows</c> or <c>paths</c>, a fold's
    /// <c>within</c>, <c>allow.new</c> and <c>allow.merge</c>, a refresh's <c>paths</c>, <c>except</c>, <c>stale</c> and
    /// <c>merge</c>, a rebase's <c>merge</c>: those written out, those its expression yielded where the step started - none
    /// where it read nothing - or null where the step names none.
    /// </summary>
    /// <param name="list">The key.</param>
    /// <param name="run">The step's run, with what the engine read where it started.</param>
    /// <param name="key">The key's name, as the engine reads it under.</param>
    /// <exception cref="HarnessException">
    /// The engine handed on no list of names for an expression it reads where the step starts: its own fault (70), never
    /// read as naming nothing, which would judge nothing and pass.
    /// </exception>
    public static IReadOnlyList<string>? NamesOf(FlowList? list, FlowStepRun run, string key)
    {
        if (list is null)
        {
            return null;
        }

        if (list.Expression is null)
        {
            return list.Written;
        }

        return run.Read is not null
            && run.Read.TryGetValue(key, out var value)
            && value.ToNode() is JsonArray names
            && names.All(name => name is JsonValue text && text.GetValueKind() == System.Text.Json.JsonValueKind.String)
                ? [.. names.Select(name => name!.GetValue<string>())]
                : throw new HarnessException(HarnessExit.InternalError, $"'{run.Instance}' was handed no names its {key} read where it started");
    }
}
