using System.Xml;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// MSBuild's projects and solutions: a <c>.csproj</c> or a <c>.slnx</c> is one <c>project</c> node over the whole file,
/// named by its file name, its header its root element's line and a project's summary the first sentence of its
/// <c>&lt;Description&gt;</c>, with a <c>projectReference</c> to each project it names - a project's
/// <c>&lt;ProjectReference Include&gt;</c>, a solution's <c>&lt;Project Path&gt;</c> - by that project's file name. The
/// file is read as XML as far as it parses, the line it stops at marked dynamic; nothing MSBuild evaluates is evaluated.
/// </summary>
public sealed class MsbuildExtractor : ICodeGraphExtractor
{
    /// <inheritdoc/>
    public string Language => "msbuild";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".csproj", ".slnx");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "MSBuild projects (.csproj) and solutions (.slnx): a project node for each, named by its file name, a project's summary from its Description, and a projectReference to each project it names that the repository holds",
        [
            "what MSBuild evaluates: a reference under a condition is taken whatever the condition, a property in a path is left as written, and imported .props and .targets files are not read",
            "a project named by a wildcard, and two projects of one file name, which a reference names both of",
            "a solution in the older .sln form",
            "what follows the point a file stops reading as XML, a line its extract marks dynamic",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines;
        var name = CodeGraphIds.FileName(input.Path);
        var solution = name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
        var id = $"{Language}:{input.Path}";
        var references = new List<ExtractReference>();
        string? signature = null;
        string? summary = null;
        ExtractDynamic? stopped = null;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreProcessingInstructions = true };

        try
        {
            using var reader = XmlReader.Create(new StringReader(input.Text), settings);
            var at = (IXmlLineInfo)reader;
            var describing = false;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Text && describing)
                {
                    summary ??= ExtractText.Flat(reader.Value) is { Length: > 0 } described ? ExtractText.Summary(described) : null;
                }

                describing = false;

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                // The root element's line, as the project's header.
                signature ??= ExtractText.Signature(ExtractText.Flat(lines[FileExtract.LineWithin(at.LineNumber, lines.Count) - 1]));
                describing = !solution && reader.LocalName == "Description" && !reader.IsEmptyElement;

                var named = (solution, reader.LocalName) switch
                {
                    (false, "ProjectReference") => reader.GetAttribute("Include"),
                    (true, "Project") => reader.GetAttribute("Path"),
                    _ => null,
                };

                foreach (var project in (named ?? string.Empty).Split(';').Select(ProjectName).OfType<string>())
                {
                    references.Add(new ExtractReference(id, CodeEdgeKinds.ProjectReference, project, FileExtract.LineWithin(at.LineNumber, lines.Count)));
                }
            }
        }
        catch (XmlException ex)
        {
            // What follows the first fault is not read: the file is graphed as far as it parses, and its extract says where
            // it stopped, so a reference missing after it is never taken for one the project does not make.
            stopped = new ExtractDynamic(FileExtract.LineWithin(ex.LineNumber, lines.Count), "the file stops reading as XML here, and nothing after it is read");
        }

        var node = new ExtractNode(id, CodeNodeKinds.Project, name, input.Path, 1, lines.Count, signature ?? name)
        {
            Summary = summary,
        };

        return new FileExtract([node]) { References = references, Dynamic = stopped is null ? [] : [stopped] };
    }

    /// <summary>The file name of the project <paramref name="path"/> names, as its node is named; <see langword="null"/> where it names none, or names it by a wildcard.</summary>
    private static string? ProjectName(string path)
    {
        var name = CodeGraphIds.FileName(path.Trim().Replace('\\', '/'));

        return name.Length == 0 || name.Contains('*', StringComparison.Ordinal) || name.Contains('?', StringComparison.Ordinal) ? null : name;
    }
}
