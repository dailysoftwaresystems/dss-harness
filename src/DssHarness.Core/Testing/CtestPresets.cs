using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Json;
using DssHarness.Core.Walks;

namespace DssHarness.Core.Testing;

/// <summary>
/// Which of its filters a ctest test preset sets, read from the preset files ctest reads it from.
/// </summary>
/// <remarks>
/// <para>
/// Only whether each filter is set to something, never what it matches: a value may hold macros only
/// ctest expands, and whether a preset sets a filter at all is what decides whether an option given on
/// ctest's command line is read beside it. The one value read is a flag's: whether the preset takes a
/// union. Read as ctest reads presets: <c>CMakePresets.json</c> and <c>CMakeUserPresets.json</c> in the
/// directory ctest starts in, each with the files its <c>include</c> list names, relative to it; and
/// every preset a preset inherits, however far along. A filter is set where any of them sets it to
/// something: measured with ctest 4.3.2, it takes the first value along the inherits that says
/// something, and an empty one says nothing - a preset that sets a filter to "" still has the one it
/// inherits. Whether a preset takes the union of what it chooses is decided by the first to say, itself
/// before the ones it inherits, in order.
/// </para>
/// <para>
/// Each file and each preset is read once (<see cref="Memo{TKey, TAnswer}"/>): a file included by several is read
/// the first time, and a preset several inherit - a diamond, however many levels of them - is answered once and its
/// answer reused, on a stack of the memo's own, so neither the chain of includes nor that of inherits reaches past a
/// thread's stack however long it is. Only a preset that inherits itself, through any chain, is refused.
/// </para>
/// </remarks>
internal static class CtestPresets
{
    /// <summary>The preset files ctest reads, in the directory it starts in.</summary>
    private static readonly string[] Files = ["CMakePresets.json", "CMakeUserPresets.json"];

    /// <summary>
    /// What a preset sets when it takes the union of the tests its filters choose rather than those
    /// every one chooses; named among the filters it sets.
    /// </summary>
    public const string Union = "include.useUnion";

    /// <summary>
    /// The filters test preset <paramref name="name"/> sets to something - <c>include.name</c>,
    /// <c>exclude.label</c> and the like, and <see cref="Union"/> where it takes a union - as ctest would read
    /// it starting in <paramref name="directory"/>; or why that could not be read, to end a sentence.
    /// </summary>
    /// <param name="fileSystem">What the preset files are read through.</param>
    /// <param name="directory">The directory ctest starts in.</param>
    /// <param name="name">The test preset.</param>
    public static (IReadOnlySet<string>? Filters, string? Unread) FiltersOf(IFileSystem fileSystem, string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var presets = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        try
        {
            var load = Loading(fileSystem, presets);

            foreach (var file in Files.Select(file => Path.GetFullPath(Path.Combine(directory, file))).Where(fileSystem.FileExists))
            {
                _ = load.Of(file);
            }

            if (!presets.ContainsKey(name))
            {
                return (null, $"no test preset '{name}' is in the preset files in '{directory}'");
            }

            // Every filter a preset or any it inherits sets to something, each preset answered once.
            var sets = new Memo<string, IReadOnlySet<string>>(
                preset => Parents(Preset(presets, preset), preset),
                (preset, inherited) =>
                {
                    var own = Preset(presets, preset);
                    var set = new HashSet<string>(
                        Ctest.PresetFilters.Where(filter => Own(own, filter) is { } value && (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 0)),
                        StringComparer.Ordinal);

                    foreach (var parents in inherited)
                    {
                        set.UnionWith(parents);
                    }

                    return set;
                },
                InheritsItself<IReadOnlySet<string>>,
                StringComparer.Ordinal);

            // The value the first to say gives the union: the preset itself, then those it inherits in order.
            var union = new Memo<string, JsonElement?>(
                preset => Parents(Preset(presets, preset), preset),
                (preset, inherited) => Own(Preset(presets, preset), Union) ?? inherited.FirstOrDefault(value => value is not null),
                InheritsItself<JsonElement?>,
                StringComparer.Ordinal);

            var filters = new HashSet<string>(sets.Of(name), StringComparer.Ordinal);

            if (union.Of(name) is { ValueKind: JsonValueKind.True })
            {
                filters.Add(Union);
            }

            return (filters, null);
        }
        // An include naming a path the runtime rejects - a NUL in it - is as unreadable as one that is not there.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (null, $"the preset files in '{directory}' could not be read: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>
    /// What reads a preset file, and before it each file it includes, into <paramref name="presets"/>: each file once, the
    /// files it includes before its own presets, so a file's own preset stands over one of the same name it includes - and
    /// one included again, or by a file it includes, read no second time.
    /// </summary>
    private static Memo<string, bool> Loading(IFileSystem fileSystem, Dictionary<string, JsonElement> presets)
    {
        var roots = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        return new Memo<string, bool>(
            file => Includes(file, Root(file)),
            (file, _) =>
            {
                if (Root(file).TryGetProperty("testPresets", out var tests))
                {
                    foreach (var preset in tests.EnumerateArray())
                    {
                        presets[preset.GetProperty("name").GetString() ?? throw new InvalidOperationException($"'{file}' holds a test preset with no name")] = preset;
                    }
                }

                return true;
            },
            _ => true,
            StringComparer.Ordinal);

        // The file's document, read the first time it is asked and kept: its includes are asked first, its presets after.
        JsonElement Root(string file)
        {
            if (!roots.TryGetValue(file, out var root))
            {
                using var document = JsonDepth.ParseDocument(fileSystem.ReadAllText(file));

                root = document.RootElement.Clone();
                roots[file] = root;
            }

            return root;
        }
    }

    /// <summary>The files <paramref name="root"/>, the document of <paramref name="file"/>, includes, each as a whole path, in order.</summary>
    private static IEnumerable<string> Includes(string file, JsonElement root)
    {
        if (!root.TryGetProperty("include", out var include))
        {
            return [];
        }

        var included = new List<string>();

        foreach (var entry in include.EnumerateArray())
        {
            var path = entry.GetString() ?? throw new InvalidOperationException($"'{file}' includes a file it does not name");

            // Expanded by CMake alone, from values this build cannot see.
            if (path.Contains('$', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"'{file}' includes '{path}', whose macros only CMake expands");
            }

            included.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path)));
        }

        return included;
    }

    /// <summary>A preset met again while it is still being answered: it inherits itself, which ctest refuses too.</summary>
    private static T InheritsItself<T>(string name) => throw new InvalidOperationException($"test preset '{name}' inherits itself");

    /// <summary>Preset <paramref name="name"/>, refused where no preset file declares it.</summary>
    private static JsonElement Preset(Dictionary<string, JsonElement> presets, string name)
        => presets.TryGetValue(name, out var preset)
            ? preset
            : throw new InvalidOperationException($"test preset '{name}' is inherited and not declared");

    /// <summary>What <paramref name="preset"/> itself gives <paramref name="setting"/>, under <c>filter</c>, or <see langword="null"/>.</summary>
    private static JsonElement? Own(JsonElement preset, string setting)
    {
        var dot = setting.IndexOf('.', StringComparison.Ordinal);

        return preset.TryGetProperty("filter", out var filters)
            && filters.TryGetProperty(setting[..dot], out var ofKind)
            && ofKind.TryGetProperty(setting[(dot + 1)..], out var value)
                ? value
                : null;
    }

    /// <summary>The presets <paramref name="preset"/> inherits, in order.</summary>
    private static IEnumerable<string> Parents(JsonElement preset, string name)
        => !preset.TryGetProperty("inherits", out var inherits)
            ? []
            : inherits.ValueKind == JsonValueKind.String
                ? [inherits.GetString()!]
                : [.. inherits.EnumerateArray().Select(parent => parent.GetString() ?? throw new InvalidOperationException($"test preset '{name}' inherits a preset it does not name"))];
}
