using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoHarness.Core.FileSystem;

/// <summary>
/// How every file of state the harness keeps is written and read: the run lock, a log directory's owner, the record of
/// the copies hosts keep, and what orchestrators and their agents keep. One set of options, so no two of them can come
/// to read their files by different rules.
/// </summary>
/// <remarks>
/// A shape this build does not recognise is a hard failure rather than silent data loss, as it is wherever this tool
/// reads JSON that decides something: a member no type declares is refused, never dropped; a member written twice is
/// refused rather than read as its last value; and null is refused where the type declares a value, a list's items
/// included, as is a member the type needs that the file leaves out, rather than either being read in as nothing and
/// failing later in whatever trusts it - a member that may be left out says so by having a default. Written indented, camelCase,
/// without the members that are null, and with paths and names written as they are rather than escaped - a person reads
/// these files as often as the tool does. An enumeration is written by its name.
/// </remarks>
public static class JsonStateFile
{
    /// <summary>The options a state file is written and read with.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(), new Configuration.NonNullListConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        NewLine = "\n",
    };

    /// <summary>The same, one object to a line: a log's entries, each appended whole.</summary>
    public static JsonSerializerOptions LineOptions { get; } = new(Options) { WriteIndented = false };
}
