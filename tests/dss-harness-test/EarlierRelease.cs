using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;

namespace DssHarness.Tests;

/// <summary>
/// A run's frozen definition as an earlier release wrote it, for the tests that hold this build to taking such a run up:
/// the forms that release's serializer gave what this build writes its own way, and the digest of its own form that
/// release recorded in <c>flow.json</c> as the run began.
/// </summary>
internal static class EarlierRelease
{
    /// <summary>The members an advisor's record held before its <c>kinds</c>, in the order each release wrote them.</summary>
    private static readonly string[] BeforeKinds = ["agent", "prompt", "tier", "model", "effort", "client"];

    /// <summary>The run's <c>definition.json</c> as this build froze it, read as a tree to write again as an earlier release would have.</summary>
    /// <param name="run">The run.</param>
    public static JsonObject Frozen(OrchestratorLayout run)
        => JsonNode.Parse(File.ReadAllText(run.DefinitionFile))!.AsObject();

    /// <summary>
    /// <paramref name="frozen"/> with every advisor - the run's own and each one a use carries - holding the <c>kinds</c>
    /// an earlier release wrote of it, after the members that release wrote before it: <paramref name="kinds"/>, none for
    /// an advisor whose file named no kinds, as every release that read an advisor wrote it.
    /// </summary>
    /// <param name="frozen">The definition as this build froze it.</param>
    /// <param name="kinds">The kinds the advisor's file named.</param>
    public static JsonObject WithKinds(JsonObject frozen, params string[] kinds)
    {
        var earlier = frozen.DeepClone().AsObject();
        var advisors = 0;

        foreach (var advisor in Objects(earlier).SelectMany(owner => owner.Where(member => member.Key == "advisor" && member.Value is JsonObject).Select(member => member.Value!.AsObject())).ToList())
        {
            var after = BeforeKinds.Select(advisor.IndexOf).Max();
            advisor.Insert(after + 1, "kinds", new JsonArray([.. kinds.Select(kind => (JsonNode?)JsonValue.Create(kind))]));
            advisors++;
        }

        // A form that changed nothing would let a test pass over the definition it meant to change.
        Assert.NotEqual(0, advisors);
        return earlier;
    }

    /// <summary>
    /// <paramref name="frozen"/> with every null left out, as each release wrote a definition before this build wrote
    /// such members null or not: an advisor's tier, model, effort and client, a recovery's maxVisits, an input's and a
    /// param's description and default among them.
    /// </summary>
    /// <param name="frozen">The definition as this build froze it.</param>
    public static JsonObject WithoutNulls(JsonObject frozen)
    {
        var earlier = frozen.DeepClone().AsObject();
        var left = 0;

        foreach (var owner in Objects(earlier).ToList())
        {
            foreach (var name in owner.Where(member => member.Value is null).Select(member => member.Key).ToList())
            {
                _ = owner.Remove(name);
                left++;
            }
        }

        // A form that changed nothing would let a test pass over the definition it meant to change.
        Assert.NotEqual(0, left);
        return earlier;
    }

    /// <summary>
    /// Writes <paramref name="earlier"/> as the run's <c>definition.json</c>, and in its <c>flow.json</c> the digest the
    /// earlier release recorded of it - the SHA-256 of the definition as that release wrote it, its path left empty.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="earlier">The definition as the earlier release wrote it.</param>
    /// <returns>The digest written.</returns>
    public static string Freeze(OrchestratorLayout run, JsonObject earlier)
    {
        var digested = earlier.DeepClone().AsObject();
        digested["path"] = string.Empty;

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(digested.ToJsonString(JsonStateFile.Options))));
        var flow = JsonNode.Parse(File.ReadAllText(run.FlowFile))!.AsObject();

        flow["definitionDigest"] = digest;
        File.WriteAllText(run.DefinitionFile, earlier.ToJsonString(JsonStateFile.Options) + "\n");
        File.WriteAllText(run.FlowFile, flow.ToJsonString(JsonStateFile.Options) + "\n");

        return digest;
    }

    /// <summary>Every object in <paramref name="node"/>, itself first, at any depth.</summary>
    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject owner:
                yield return owner;

                foreach (var inner in owner.SelectMany(member => Objects(member.Value)))
                {
                    yield return inner;
                }

                break;

            case JsonArray items:
                foreach (var inner in items.SelectMany(Objects))
                {
                    yield return inner;
                }

                break;
        }
    }
}
