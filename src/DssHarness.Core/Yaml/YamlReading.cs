using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Core.Yaml;

/// <summary>
/// The one reader of YAML this tool has, over YamlDotNet's representation model: it loads a file's
/// root mapping, and reads the keys, scalars and booleans of a file of mappings, reporting every
/// problem together with the line and column it was found at.
/// </summary>
/// <remarks>
/// <para>
/// Read through the representation model rather than a serialiser on purpose. A serialiser would
/// reach a typed object faster and lose the two things a file of rules needs most: the position of
/// the key that was wrong, and the fact that a key was not recognised at all. A silently ignored
/// key is a rule nobody applied, and the configuration reader elsewhere in this repository refuses
/// one for the same reason.
/// </para>
/// <para>
/// Every reader of a YAML file in this tool - the action reader and the workflow reader - reads
/// through these helpers, so two files written the same way are read the same way, save for the one
/// thing a reader may ask of its own files before they are read: bounds (<see cref="YamlBounds"/>).
/// The workflow reader asks them, so a workflow file holding an alias or nested past its bound is
/// refused; the action reader asks none, so an action file is read whatever it holds of either.
/// </para>
/// </remarks>
public static class YamlReading
{
    /// <summary>
    /// Loads <paramref name="text"/> as one document whose top level is a mapping of keys, or says
    /// why it is not: every problem reported together.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="oneDocument">
    /// How a refusal says the file holds one document, as the kind of file being read spells it -
    /// for instance <c>an action file holds one, so that nothing has to decide which of them a
    /// runner ran</c>.
    /// </param>
    /// <param name="problems">Where problems are collected.</param>
    /// <param name="bounds">
    /// What the text is held to before it is read into nodes, or <see langword="null"/> where the
    /// reader holds it to nothing: see <see cref="YamlBounds"/>.
    /// </param>
    public static YamlMappingNode? Root(string text, string oneDocument, List<string> problems, YamlBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(problems);

        if (bounds is not null && !Bounded(text, bounds, problems))
        {
            return null;
        }

        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException exception)
        {
            problems.Add($"line {exception.Start.Line}, column {exception.Start.Column}: "
                + $"the file is not valid YAML ({exception.Message}).");
            return null;
        }

        if (stream.Documents.Count == 0)
        {
            problems.Add("the file is empty.");
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            problems.Add($"the file holds {stream.Documents.Count} YAML documents; {oneDocument}.");
            return null;
        }

        if (stream.Documents[0].RootNode is YamlMappingNode mapping)
        {
            return mapping;
        }

        problems.Add(At(stream.Documents[0].RootNode, "the file's top level is not a mapping of keys."));
        return null;
    }

    /// <summary>A problem's place in the file, so it points at what it is about.</summary>
    public static string At(YamlNode node, string message)
        => $"line {node.Start.Line}, column {node.Start.Column}: {message}";

    /// <summary>The plain name <paramref name="node"/> is, or a problem where it is not one.</summary>
    public static string? KeyOf(YamlNode node, List<string> problems)
    {
        if (node is YamlScalarNode { Value: { } value })
        {
            return value;
        }

        problems.Add(At(node, "a key is not a plain name."));
        return null;
    }

    /// <summary>The scalar <paramref name="node"/> is, or <see langword="null"/> where it is not one.</summary>
    public static string? ScalarOf(YamlNode node)
        => node is YamlScalarNode { Value: { } value } ? value : null;

    /// <summary>
    /// The scalar <paramref name="node"/> is, or a problem where it is not one, naming
    /// <paramref name="what"/> it was meant to be.
    /// </summary>
    public static string? RequireScalar(YamlNode node, string what, List<string> problems)
    {
        if (node is YamlScalarNode { Value: { } value })
        {
            return value;
        }

        problems.Add(At(node, $"'{what}' is not a single value."));
        return null;
    }

    /// <summary>
    /// The true or false <paramref name="node"/> is, or a problem where it says something else,
    /// naming <paramref name="what"/> it was meant to be.
    /// </summary>
    /// <remarks>
    /// Read as <see cref="bool.TryParse(string, out bool)"/> reads it: the word true or false in any
    /// case - <c>True</c> and <c>FALSE</c> among them - with any space around it. Anything else, the
    /// other words YAML can mean a boolean by - <c>yes</c>, <c>on</c> - included, is refused rather
    /// than read as false. Keys like these turn guards on, so a misspelling read as "off" is a file
    /// that looks guarded and is not - which is the failure the guards exist to make impossible.
    /// </remarks>
    public static bool? RequireBoolean(YamlNode node, string what, List<string> problems)
    {
        var text = RequireScalar(node, what, problems);

        if (text is null)
        {
            return null;
        }

        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        problems.Add(At(node, $"'{what}' is '{text}', which is not true or false."));
        return null;
    }

    /// <summary>
    /// The key <paramref name="node"/> names, when <paramref name="known"/> lists it. Any other is
    /// a problem added here, before a reader looks at it.
    /// </summary>
    public static string? KnownKey(YamlNode node, IReadOnlyList<string> known, string what, List<string> problems)
    {
        var key = KeyOf(node, problems);

        if (key is null || known.Contains(key, StringComparer.Ordinal))
        {
            return key;
        }

        problems.Add(At(node, $"'{key}' is not a {what}. Known: '{string.Join("', '", known)}'."));
        return null;
    }

    /// <summary>
    /// Whether <paramref name="text"/> keeps to <paramref name="bounds"/>, read as events before
    /// anything is read into nodes: no alias, and mappings and lists nested no deeper than the
    /// bounds allow. The first place it does not is added to <paramref name="problems"/>.
    /// </summary>
    /// <remarks>
    /// Read as events because nodes are what the bounds guard: an alias makes a few hundred bytes
    /// stand for millions of nodes, and nesting past any file's depth is recursed through to be
    /// read at all. Text that is not YAML passes here, to be refused where it is read into nodes, in
    /// the parser's own words.
    /// </remarks>
    private static bool Bounded(string text, YamlBounds bounds, List<string> problems)
    {
        var parser = new Parser(new StringReader(text));
        var depth = 0;

        try
        {
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case AnchorAlias alias:
                        problems.Add($"line {alias.Start.Line}, column {alias.Start.Column}: the alias '*{alias.Value}' repeats a part of the file by reference; {bounds.NoAlias}.");
                        return false;

                    case MappingStart or SequenceStart:
                        if (++depth > bounds.MaxDepth)
                        {
                            problems.Add($"line {parser.Current.Start.Line}, column {parser.Current.Start.Column}: the file nests more than {bounds.MaxDepth} levels deep; {bounds.TooDeep}.");
                            return false;
                        }

                        break;

                    case MappingEnd or SequenceEnd:
                        depth--;
                        break;
                }
            }
        }
        catch (YamlException)
        {
            return true;
        }

        return true;
    }
}

/// <summary>
/// What a reader holds its files' YAML to before they are read into nodes (<see cref="YamlReading.Root"/>): no alias,
/// and no deeper nesting than <paramref name="MaxDepth"/>. Asked by a reader of files whose every part is meant to be
/// written out, as a workflow's definition is; the action reader asks none.
/// </summary>
/// <param name="MaxDepth">How deep mappings and lists may nest inside one another.</param>
/// <param name="NoAlias">
/// Why the reader's files write no alias, completing the refusal of one after a semicolon - for instance <c>a
/// definition writes each of its steps out, so that what it says is what it reads</c>.
/// </param>
/// <param name="TooDeep">
/// Why none of the reader's files nests deeper, completing the refusal of one after a semicolon - for instance <c>no
/// definition does</c>.
/// </param>
public sealed record YamlBounds(int MaxDepth, string NoAlias, string TooDeep);
