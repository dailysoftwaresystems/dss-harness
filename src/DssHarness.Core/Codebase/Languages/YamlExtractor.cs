using System.Globalization;
using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A YAML data file - <c>.yml</c> and <c>.yaml</c>, but a Dart package's <c>pubspec.yaml</c>, which its own extractor
/// reads - read as the data it holds, its anchors and aliases followed and its merge keys read as the keys of the
/// mapping that merges them: each scalar key anywhere down to <see cref="FileExtract.MostKeys"/> of them a
/// <c>member</c> SubKind <c>key</c> named by its path, its value as its summary. A GitHub Actions workflow -
/// a YAML file under <c>.github/workflows/</c> - is read further as its jobs and its steps, each a <c>section</c>, and
/// a step's or job's <c>uses:</c> that names a local action by a path of the repository is an import of its
/// <c>action.yml</c>. Where the file stops reading as YAML, the line it stops at is marked dynamic.
/// </summary>
public sealed class YamlExtractor : ICodeGraphExtractor
{
    /// <summary>How deep a file's keys are read: no data file nests past it.</summary>
    private const int MostDepth = 8;

    /// <summary>How deep a file may nest at all, past which nothing of it is read: a guard against a file made to nest without end.</summary>
    private const int MostNesting = 100;

    /// <summary>The id-like attributes a workflow's keys are named by.</summary>
    private const string Jobs = "jobs";

    /// <inheritdoc/>
    public string Language => "yaml";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".yml", ".yaml");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "YAML data files (.yml, .yaml, anchors and aliases followed and merge keys read): each scalar key anywhere in the file as a key node named by its path with its value as its summary, a GitHub Actions workflow's jobs and steps each as a section, and a uses: naming a local action as an import of its action.yml",
        [
            "pubspec.yaml, which the pubspec extractor reads",
            $"a key nested past 8 levels, a file nested past {MostNesting.ToString(System.Globalization.CultureInfo.InvariantCulture)} levels, and a file of more than {FileExtract.MostKeys.ToString(System.Globalization.CultureInfo.InvariantCulture)} keys: the first {FileExtract.MostKeys.ToString(System.Globalization.CultureInfo.InvariantCulture)} are kept, the rest counted",
            "a key's value past the cut of a summary, and a secret a key holds: neither is masked here",
            "a step's or a job's uses: that names an action of a marketplace or of a repository by its owner: no import of it",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var stop = YamlReading.TooDeepBy(input.Text, MostNesting);
        YamlNode? root = null;

        if (stop is null)
        {
            root = YamlReading.LoadDocument(input.Text, "a data file holds one", bounds: null, out var loaded);
            stop = loaded;
        }

        var nodes = new List<ExtractNode>();
        var imports = new List<ExtractImport>();
        var ids = new ExtractIds();
        var keys = 0;
        var beyond = 0;

        if (root is not null)
        {
            if (root is YamlMappingNode workflow && IsWorkflow(input.Path))
            {
                Workflow(input.Path, lines.Count, workflow, ids, nodes, imports, ref keys, ref beyond);
            }
            else
            {
                Walk(input.Path, lines.Count, root, [], null, ids, nodes, imports, ref keys, ref beyond);
            }
        }

        return new FileExtract([.. nodes])
        {
            Imports = imports,
            KeysBeyond = beyond,
            Dynamic = stop is null ? [] : [new ExtractDynamic(FileExtract.LineWithin(stop.Line, lines.Count), $"the file stops reading here: {stop.Why}")],
        };
    }

    /// <summary>Whether <paramref name="path"/> is a GitHub Actions workflow: a YAML file under <c>.github/workflows/</c>.</summary>
    private static bool IsWorkflow(string path)
        => path.StartsWith(".github/workflows/", StringComparison.Ordinal) && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));

    /// <summary>
    /// A workflow: each job a <c>section</c> over its lines and each of its steps a section inside it, a <c>uses:</c> that
    /// names a local action an import of its <c>action.yml</c>, and its keys - and every other key of the file - as any
    /// data file's are.
    /// </summary>
    private void Workflow(
        string file,
        int lineCount,
        YamlMappingNode root,
        ExtractIds ids,
        List<ExtractNode> nodes,
        List<ExtractImport> imports,
        ref int keys,
        ref int beyond)
    {
        Walk(file, lineCount, root, [], null, ids, nodes, imports, ref keys, ref beyond);

        if (!root.Children.TryGetValue(new YamlScalarNode(Jobs), out var held) || held is not YamlMappingNode jobs)
        {
            return;
        }

        foreach (var (key, value) in jobs.Children)
        {
            if (YamlReading.ScalarOf(key) is not { Length: > 0 } job || value is not YamlMappingNode mapping)
            {
                continue;
            }

            var jobLine = Line(key, lineCount);
            var jobSection = ids.Next($"{Language}:{file}#{Jobs}.{job}");
            nodes.Add(new ExtractNode(jobSection, CodeNodeKinds.Section, job, $"{file}#{Jobs}.{job}", jobLine, SpanOf(value, lineCount), $"{Jobs}.{job}"));

            foreach (var (inner, step) in mapping.Children)
            {
                if (YamlReading.ScalarOf(inner) is not "steps" || step is not YamlSequenceNode listed)
                {
                    continue;
                }

                for (var at = 0; at < listed.Children.Count; at++)
                {
                    if (listed.Children[at] is not YamlMappingNode one)
                    {
                        continue;
                    }

                    var start = Line(one, lineCount);
                    var end = at + 1 < listed.Children.Count ? Math.Max(start, Line(listed.Children[at + 1], lineCount) - 1) : Math.Max(start, SpanOf(value, lineCount));
                    var named = NameOf(one) is { Length: > 0 } name ? name : $"step {(at + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    var id = ids.Next($"{jobSection}.steps[{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}]");

                    nodes.Add(new ExtractNode(id, CodeNodeKinds.Section, named, $"{file}#{Jobs}.{job}.steps[{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}]", start, end, $"{Jobs}.{job}.steps[{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}]")
                    {
                        Parent = jobSection,
                    });

                    UsedAction(file, lineCount, one, imports);
                }
            }
        }
    }

    /// <summary>The <c>name</c> a step gives itself, where it gives one.</summary>
    private static string? NameOf(YamlMappingNode step)
        => step.Children.TryGetValue(new YamlScalarNode("name"), out var named) ? YamlReading.ScalarOf(named) : null;

    /// <summary>
    /// A <c>uses:</c> of <paramref name="mapping"/> that names a local action: an import of its <c>action.yml</c>, with
    /// <c>action.yaml</c> standing in for it where that is the one the tree holds. A local action's path is the
    /// repository's own root, so the import names it from the workflow file's directory.
    /// </summary>
    private static void UsedAction(string file, int lineCount, YamlMappingNode mapping, List<ExtractImport> imports)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("uses"), out var used) || YamlReading.ScalarOf(used) is not { Length: > 1 } action || action[0] != '.')
        {
            return;
        }

        var line = Line(used, lineCount);
        var directory = action.EndsWith("@", StringComparison.Ordinal) ? action : action.Split('@')[0];

        foreach (var pair in new[] { ("action.yml", false), ("action.yaml", true) })
        {
            if (Rooted(file, CodeGraphIds.Clean($"{directory}/{pair.Item1}")) is { } target)
            {
                imports.Add(new ExtractImport(CodeImportForms.Relative, target, line) { Fallback = pair.Item2 });
            }
        }
    }

    /// <summary>
    /// The path <paramref name="written"/> names - a path of the repository's root - as the file at
    /// <paramref name="file"/> writes one relative to its own directory: the linker resolves it against the file's
    /// directory, which takes the climb out.
    /// </summary>
    private static string Rooted(string file, string written)
    {
        var directory = file.Contains('/', StringComparison.Ordinal) ? file[..file.LastIndexOf('/')] : string.Empty;
        var up = directory.Length == 0 ? 0 : directory.Split('/').Length;
        return string.Concat(Enumerable.Repeat("../", up)) + written;
    }

    /// <summary>
    /// Walks <paramref name="node"/> under the key path <paramref name="path"/>: a scalar key a key node named by its
    /// path, a mapping's keys walked in turn - a merge key's merged mapping walked as the merging one's own - and a
    /// sequence item's path holding its place.
    /// </summary>
    private void Walk(
        string file,
        int lineCount,
        YamlNode node,
        IReadOnlyList<string> path,
        string? section,
        ExtractIds ids,
        List<ExtractNode> nodes,
        List<ExtractImport> imports,
        ref int keys,
        ref int beyond)
    {
        if (path.Count >= MostDepth)
        {
            return;
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    if (YamlReading.ScalarOf(key) is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    if (name == "<<")
                    {
                        // A merge key's mapping is the merging one's own: walked at its path, and no key of its own.
                        Walk(file, lineCount, value, path, section, ids, nodes, imports, ref keys, ref beyond);
                        continue;
                    }

                    Member(file, lineCount, name, value, [.. path, name], section, ids, nodes, imports, ref keys, ref beyond);
                }

                break;

            case YamlSequenceNode sequence:
                for (var at = 0; at < sequence.Children.Count; at++)
                {
                    var place = $"[{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
                    Member(file, lineCount, place, sequence.Children[at], [.. path, place], section, ids, nodes, imports, ref keys, ref beyond);
                }

                break;
        }
    }

    /// <summary>One key or item of a walk: a scalar a key, a container walked on.</summary>
    private void Member(
        string file,
        int lineCount,
        string name,
        YamlNode value,
        IReadOnlyList<string> path,
        string? section,
        ExtractIds ids,
        List<ExtractNode> nodes,
        List<ExtractImport> imports,
        ref int keys,
        ref int beyond)
    {
        if (value is YamlMappingNode or YamlSequenceNode)
        {
            Walk(file, lineCount, value, path, section, ids, nodes, imports, ref keys, ref beyond);
            return;
        }

        if (keys >= FileExtract.MostKeys)
        {
            beyond++;
            return;
        }

        keys++;
        var key = JsonExtractor.KeyPath(path);
        var line = FileExtract.LineWithin((int)Math.Clamp(value.Start.Line, 1, int.MaxValue), lineCount);

        nodes.Add(new ExtractNode(ids.Next($"{Language}:{file}#{key}"), CodeNodeKinds.Member, name, key, line, line, key)
        {
            SubKind = "key",
            Parent = section,
            Summary = YamlReading.ScalarOf(value) is { Length: > 0 } scalar && ExtractText.Flat(scalar) is { Length: > 0 } flat ? ExtractText.Summary(flat) : null,
        });
    }

    /// <summary>The line <paramref name="node"/> begins on, within the file's <paramref name="lineCount"/> lines.</summary>
    private static int Line(YamlNode node, int lineCount) => FileExtract.LineWithin((int)Math.Clamp(node.Start.Line, 1, int.MaxValue), lineCount);

    /// <summary>
    /// The last line <paramref name="node"/> or anything inside it is written on, within the file's
    /// <paramref name="lineCount"/> lines: a collection's own end mark is not where its content ends, so its last line is
    /// read from everything it holds.
    /// </summary>
    private static int SpanOf(YamlNode node, int lineCount)
    {
        var last = (int)Math.Clamp(node.Start.Line, 1, int.MaxValue);

        foreach (var child in node.AllNodes)
        {
            last = Math.Max(last, (int)Math.Clamp(child.End.Line, 1, int.MaxValue));
            last = Math.Max(last, (int)Math.Clamp(child.Start.Line, 1, int.MaxValue));
        }

        return FileExtract.LineWithin(last, lineCount);
    }
}
