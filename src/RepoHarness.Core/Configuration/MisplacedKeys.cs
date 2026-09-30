using System.Text.Json;
using RepoHarness.Core.Runners;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// Keys <c>config.json</c> refuses where they are written, each said with where what it means is read
/// instead: a key this tool once read and no longer does, one it took and never read, and a key only a
/// step of an action file takes, written on a runner's phase.
/// </summary>
/// <remarks>
/// Each is refused like any unknown key, because a key that loads is a key that is read. The refusal
/// says where the key belongs: told only that it is unknown, a reader deletes the line and loses what
/// it was there for - a compiler cache's store, or a check of what a step makes.
/// <para>
/// Looked for in the file itself, before the serializer reads it. The serializer does refuse the
/// key, but inside a map of named entries - the hosts under <c>wsl</c> and <c>ssh</c>, the runners
/// under <c>predefinedRunners</c> - it no longer knows which entry it was reading, and could not say
/// whose key it was.
/// </para>
/// </remarks>
internal static class MisplacedKeys
{
    private const string CompilerCacheDirectory = "compilerCacheDirectory";

    /// <summary>
    /// Each misplaced key <paramref name="json"/> holds, said with where it belongs: empty when it
    /// holds none, or is not JSON at all, which the serializer then reports as it reports any file it
    /// cannot read.
    /// </summary>
    /// <param name="json">The configuration file's text.</param>
    public static IReadOnlyList<string> In(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;

        try
        {
            // Read as the serializer reads it, so a file it accepts is one this can look through.
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            return [.. Retired(document.RootElement), .. StepKeysOnPhases(document.RootElement)];
        }
    }

    private static IEnumerable<string> Retired(JsonElement root)
        => HostSections(root)
            .Where(section => Property(section.Settings, CompilerCacheDirectory) is not null)
            .Select(section => $"{section.Name} {CompilerCacheDirectory} is no longer read. A compiler "
                + "cache's store is that cache's own variable: declare it under the host's env - "
                + "\"env\": { \"CCACHE_DIR\": \"...\" } for ccache - where every process a leg "
                + "starts on that host sees it, and a build keys the cache against the leg's own tree.")
            .Concat(TestSections(root)
                .Where(section => Property(section.Test, TestConfigs) is not null)
                .Select(section => $"{section.Name} test {TestConfigs} is not read, and never was: nothing built or "
                    + "tested the configs it named. Each leg builds and tests the one build config its 'config' names; "
                    + "declare a leg for each config to test, and delete the key."));

    /// <summary>
    /// A test section's list of build configs to test: written by <c>init</c> and checked against
    /// <c>buildConfigs</c>, and read by nothing, so a file naming <c>release</c> there tested debug alone.
    /// </summary>
    private const string TestConfigs = "configs";

    /// <summary>Every test section the file declares - a project's and a leg's - named as a refusal names it.</summary>
    private static IEnumerable<(string Name, JsonElement Test)> TestSections(JsonElement root)
    {
        if (Property(root, "projects") is { ValueKind: JsonValueKind.Array } projects)
        {
            var position = 0;

            foreach (var project in projects.EnumerateArray())
            {
                position++;

                if (Property(project, "test") is { ValueKind: JsonValueKind.Object } test)
                {
                    var name = NameOrPlace(project, position);

                    yield return ($"project {name}", test);
                }
            }
        }

        if (Property(root, "legs") is { ValueKind: JsonValueKind.Object } legs)
        {
            foreach (var leg in legs.EnumerateObject())
            {
                if (Property(leg.Value, "test") is { ValueKind: JsonValueKind.Object } test)
                {
                    yield return ($"leg '{leg.Name}'", test);
                }
            }
        }
    }

    /// <summary>
    /// Every phase that declares a key only a step of an action file takes, said with the keys a phase
    /// does take.
    /// </summary>
    /// <remarks>
    /// Which keys those are is read from the two lists that decide it - what a step takes and what a
    /// phase does - so a key a step gains later is refused on a phase in the same words.
    /// </remarks>
    private static IEnumerable<string> StepKeysOnPhases(JsonElement root)
    {
        if (Property(root, "predefinedRunners") is not { ValueKind: JsonValueKind.Object } runners)
        {
            yield break;
        }

        var phaseKeys = KeyDescription.Names(ConfigKeys.Of<RunnerPhase>());
        var stepOnly = KeyDescription.Names(ActionFileKeys.Step).Except(phaseKeys, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var runner in runners.EnumerateObject())
        {
            if (Property(runner.Value, "phases") is not { ValueKind: JsonValueKind.Array } phases)
            {
                continue;
            }

            var position = 0;

            foreach (var phase in phases.EnumerateArray())
            {
                position++;

                var declared = stepOnly.Where(key => Property(phase, key) is not null).ToList();

                if (declared.Count == 0)
                {
                    continue;
                }

                var name = NameOrPlace(phase, position);
                var them = declared.Count == 1 ? "it" : "them";

                yield return $"predefined runner '{runner.Name}' phase {name} declares {Quoted(declared)}, which only a step "
                    + $"of an action file takes; nothing reads {them} on a phase. Declare the work as a step of an action "
                    + $"file to use {them}, or remove {them}. A phase takes {Quoted(phaseKeys)}.";
            }
        }
    }

    private static string Quoted(IEnumerable<string> keys) => $"'{string.Join("', '", keys)}'";

    /// <summary>Every host section the file declares, named as a refusal names it.</summary>
    private static IEnumerable<(string Name, JsonElement Settings)> HostSections(JsonElement root)
    {
        if (Property(root, "hosts") is not { ValueKind: JsonValueKind.Object } hosts)
        {
            yield break;
        }

        if (Property(hosts, "local") is { ValueKind: JsonValueKind.Object } local)
        {
            yield return ("hosts.local", local);
        }

        foreach (var kind in new[] { "wsl", "ssh" })
        {
            if (Property(hosts, kind) is not { ValueKind: JsonValueKind.Object } declared)
            {
                continue;
            }

            foreach (var host in declared.EnumerateObject().Where(host => host.Value.ValueKind == JsonValueKind.Object))
            {
                yield return ($"hosts.{kind} '{host.Name}'", host.Value);
            }
        }
    }

    /// <summary>The property <paramref name="name"/> names, found ignoring case as the serializer finds it.</summary>
    private static JsonElement? Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject()
                .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value)
                .FirstOrDefault()
            : null;

    /// <summary>An entry of a list as a message names it: by its name where it has one, else by its place, from 1.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="position">Its place in the list, from 1.</param>
    private static string NameOrPlace(JsonElement entry, int position)
        => Property(entry, "name") is { ValueKind: JsonValueKind.String } named ? $"'{named.GetString()}'" : $"#{position}";
}
