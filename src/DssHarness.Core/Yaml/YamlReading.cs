using YamlDotNet.Core;
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
/// Every reader of a YAML file in this tool reads through these helpers, so two files written the
/// same way cannot be read two ways.
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
    public static YamlMappingNode? Root(string text, string oneDocument, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(problems);

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
    /// Anything that is not exactly true or false is refused, rather than read as false. Keys like
    /// these turn guards on, so a misspelling read as "off" is a file that looks guarded and is
    /// not - which is the failure the guards exist to make impossible.
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
}
