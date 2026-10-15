using System.Text;
using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Configuration;

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
    /// line; <see langword="null"/> when every one is text, or the file is not JSON at all, which the
    /// serializer then reports as it reports any file it cannot read.
    /// </summary>
    /// <remarks>
    /// Half a character is an escape of a lone surrogate, such as <c>\ud800</c> with no <c>\udc00</c>
    /// to <c>\udfff</c> after it. JSON allows the escape and no text can hold what it names, so the
    /// parser takes the file and every reader of the string then fails in words of its own.
    /// </remarks>
    private static string? HalfCharacter(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.String))
                {
                    continue;
                }

                try
                {
                    _ = reader.GetString();
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    var line = bytes.AsSpan(0, (int)reader.TokenStartIndex).Count((byte)'\n') + 1;

                    return $"line {line}: '{Encoding.UTF8.GetString(reader.ValueSpan)}' spells half a character, "
                        + "an escape of a lone surrogate that no text can hold. Write the whole character, or both of its escapes.";
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
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
