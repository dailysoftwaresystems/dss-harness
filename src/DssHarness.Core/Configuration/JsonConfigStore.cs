using System.Text;
using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;

namespace DssHarness.Core.Configuration;

/// <inheritdoc cref="IConfigStore"/>
public sealed class JsonConfigStore(IFileSystem fileSystem) : IConfigStore
{
    private static readonly JsonSerializerOptions Options = JsonConfigOptions.Default;

    private readonly IFileSystem _fileSystem = fileSystem;

    public HarnessConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_fileSystem.FileExists(path))
        {
            throw new ConfigException($"No configuration at '{path}'. Run '{ToolPackage.Command} init' first.");
        }

        var json = _fileSystem.ReadAllText(path);

        // Before anything reads a string out of it. A key or a value spelling half a character stopped
        // the command as a defect in this tool where a check read the key, and was reported as a value
        // that could not be converted where the serializer read it.
        if (HalfCharacter(json) is { } spelt)
        {
            throw new ConfigException($"'{path}' could not be read: {spelt}");
        }

        // Before the serializer, which would call a key this tool retired, or a step's key on a
        // phase, merely unknown: this says where each belongs.
        if (MisplacedKeys.In(json) is [_, ..] misplaced)
        {
            throw new ConfigException($"'{path}' could not be read: {string.Join(" ", misplaced)}");
        }

        HarnessConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<HarnessConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            // A missing required member and an unknown key are reported by the same
            // exception type as a stray brace. Saying "is not valid JSON" for those
            // sends the reader looking for a syntax error that is not there.
            throw new ConfigException($"'{path}' could not be read: {Describe(ex)}", ex);
        }

        if (config is null)
        {
            throw new ConfigException($"'{path}' is empty.");
        }

        // The agent sections' own refusals, the agent policy's and the web search's (V56, and V55 of its defaults), made
        // where the file is read and said together: a config that loads is one the tool can use, a restriction that loads
        // is a restriction the tool reads, and a source that loads is one its kind can ask.
        var (_, problems) = DssHarness.Core.Agents.AgentProviders.Resolve(config);
        var search = DssHarness.Core.Web.Search.WebSearchSources.Of(config, DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild);

        if ((IReadOnlyList<string>)[
            .. problems,
            .. DssHarness.Core.Agents.Policy.AgentPolicy.RuleProblems(config),
            .. search.Problems,
            .. config.AgentContext?.Continuation?.Problems() ?? [],
            .. config.AgentContext?.Compaction?.Problems() ?? [],
        ] is [_, ..] refused)
        {
            throw new ConfigException($"'{path}' could not be read: {string.Join(" ", refused)}");
        }

        HarnessConfigValidator.ThrowIfInvalid(config, path);
        return config;
    }

    public void Save(string path, HarnessConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(config);

        _fileSystem.WriteAllTextAtomic(path, Serialize(config));
    }

    public string Serialize(HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, Options) + JsonConfigOptions.NewLine;
    }

    /// <summary>
    /// The first key or value in <paramref name="json"/> that spells half a character, said with its
    /// line (<see cref="Json.JsonHalfCharacter"/>); <see langword="null"/> when every one is text, or the
    /// file is not JSON at all, which the serializer then reports as it reports any file it cannot read.
    /// </summary>
    private static string? HalfCharacter(string json)
    {
        return Json.JsonHalfCharacter.Find(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is var (line, spelt)
            ? $"line {line}: '{spelt}' spells half a character, an escape of a lone surrogate that no text can hold. Write the whole character, or both of its escapes."
            : null;
    }

    /// <summary>
    /// The parser's message, with the line it concerns. The serializer's own messages carry
    /// their location already; a message raised by one of the configuration's converters,
    /// such as a list holding null, does not, and is of little use without it.
    /// </summary>
    private static string Describe(JsonException exception)
    {
        if (exception.LineNumber is not { } line
            || exception.Message.Contains("LineNumber", StringComparison.Ordinal))
        {
            return exception.Message;
        }

        return $"{exception.Message} (line {line + 1})";
    }
}
