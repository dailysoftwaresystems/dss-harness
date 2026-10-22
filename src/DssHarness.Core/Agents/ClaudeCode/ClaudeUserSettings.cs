using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// The user's own Claude Code settings a launch reads - <c>settings.json</c> in its Claude Code directory - as the engine
/// reads them before the launch. What of them would give the launch a credential refuses it: a credential is read from
/// the slot alone, and the file's would replace the one the configuration chose. The file and its digest are what the
/// step's record names, so what reached an agent beside its prompt is known; the user's hooks in it never run, since
/// every launch's own settings turn them off.
/// </summary>
public static class ClaudeUserSettings
{
    /// <summary>The user's settings file, in a Claude Code directory.</summary>
    public const string FileName = "settings.json";

    /// <summary>The setting that names a program Claude Code asks for its key.</summary>
    public const string KeyHelper = "apiKeyHelper";

    /// <summary>The byte-order mark an editor may write a UTF-8 file with, which is no part of its JSON.</summary>
    private static ReadOnlySpan<byte> Utf8Mark => [0xEF, 0xBB, 0xBF];

    /// <summary>What every launch says of the user's hooks: none of them runs.</summary>
    public const string HooksOff = "off";

    /// <summary>What the user's settings are to a launch.</summary>
    /// <param name="File">The file the launch reads; null where the directory holds none.</param>
    /// <param name="Digest">Its SHA-256, in lowercase hexadecimal; null where there is no file.</param>
    /// <param name="Refusal">Why the launch is refused, where the file would give it a credential or cannot be read; null where it may start.</param>
    public sealed record Reading(string? File, string? Digest, string? Refusal);

    /// <summary>Reads the user's settings in <paramref name="directory"/> for a launch that signs in by <paramref name="auth"/>.</summary>
    /// <param name="files">The file system.</param>
    /// <param name="directory">The launch's Claude Code directory.</param>
    /// <param name="auth">How the launch signs in.</param>
    public static Reading Read(IFileSystem files, string directory, string auth)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(auth);

        var file = Path.Combine(directory, FileName);

        if (!files.FileExists(file))
        {
            return new Reading(null, null, null);
        }

        byte[] bytes;

        try
        {
            bytes = files.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Reading(file, null, $"'{file}', the user's Claude Code settings this launch reads, could not be read ({ex.Message}); the engine reads it before a launch for a credential it would give");
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        JsonNode? settings;

        try
        {
            settings = JsonNode.Parse(bytes.AsSpan().StartsWith(Utf8Mark) ? bytes.AsSpan(Utf8Mark.Length) : bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return new Reading(file, digest, $"'{file}', the user's Claude Code settings this launch reads, does not read as JSON ({ex.Message}); the engine reads it before a launch for a credential it would give");
        }

        if (settings is not JsonObject root)
        {
            return new Reading(file, digest, $"'{file}', the user's Claude Code settings this launch reads, holds no JSON object; the engine reads it before a launch for a credential it would give");
        }

        // Any case: on Windows a name set in another case is the same variable to the program that reads it.
        var given = (root["env"] as JsonObject)?.Select(entry => entry.Key).FirstOrDefault(name => ClaudeLaunch.IdentityNames.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } name
            ? $"{name} in its env"
            : root.ContainsKey(KeyHelper)
                ? KeyHelper
                : null;

        return given is null
            ? new Reading(file, digest, null)
            : new Reading(
                file,
                digest,
                $"'{file}', the user's Claude Code settings this launch reads, sets {given}, and this client signs in with {AgentAuth.Described(auth)}; a credential is read from the slot alone. Take it out of that file, or give the client a directory of its own (agentClients.{ClaudeCodeClient.ClientName}.{AgentClientSetting.ConfigDirKey})");
    }
}
