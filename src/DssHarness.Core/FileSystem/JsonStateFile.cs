using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// How every file of state the harness keeps is written and read: the run lock, a log directory's owner, the record of
/// the copies hosts keep, the record of the heavy legs admitted onto a machine, and what orchestrators and their agents
/// keep. One set of options, so no two of them can come to read their files by different rules.
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

    /// <summary>
    /// Reads a state file, parsed straight from the disk a little at a time, never held as one text. A list of reference
    /// items is the exception: the converter that refuses its null items is one the serializer cannot pause inside, so the
    /// list's bytes are held whole while it is read - a record that grows with what it describes is kept as lines instead
    /// (<see cref="ReadLines"/>).
    /// </summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    /// <param name="options">Its options; <see cref="Options"/> where none are given, which also read a file of one line.</param>
    /// <returns>The record, or null where the file holds null.</returns>
    /// <exception cref="JsonException">It does not read as the record.</exception>
    public static T? Read<T>(IFileSystem fileSystem, string path, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        using var stream = fileSystem.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, options ?? Options);
    }

    /// <summary>
    /// Writes a state file whole and atomically, ending with a newline, serialized straight to the disk a little at a
    /// time: never made into one text first. A list of reference items is written whole before any of it reaches the disk,
    /// for the reason <see cref="Read"/> gives; a record that grows with what it describes is kept as lines instead
    /// (<see cref="WriteLines"/>).
    /// </summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="fileSystem">The file system it is written through.</param>
    /// <param name="path">The file.</param>
    /// <param name="record">The record.</param>
    /// <param name="options">Its options; <see cref="Options"/> where none are given.</param>
    public static void Write<T>(IFileSystem fileSystem, string path, T record, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        fileSystem.WriteAtomic(path, stream =>
        {
            JsonSerializer.Serialize(stream, record, options ?? Options);
            stream.WriteByte((byte)'\n');
        });
    }

    /// <summary>Reads a state file of lines (<see cref="JsonLineReader"/>), one line at a time, as <paramref name="read"/> takes them.</summary>
    /// <typeparam name="T">The record.</typeparam>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    /// <param name="read">Reads the record from the file's lines, each by its number.</param>
    /// <returns>The record.</returns>
    /// <exception cref="JsonException">A line does not read as the record says it is.</exception>
    public static T ReadLines<T>(IFileSystem fileSystem, string path, Func<JsonLineReader, T> read)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(read);

        using var lines = FileLines.Read(fileSystem, path).GetEnumerator();
        return read(new JsonLineReader(lines));
    }

    /// <summary>
    /// The records of a log a writer appends to a line at a time (<see cref="Agents.AppendLog"/>), each whole line one
    /// record read by <see cref="LineOptions"/>, read a line at a time: a last line no line feed ended is a writer's torn
    /// line - half an append, or a whole record whose line feed never reached the disk - set aside and given back, never
    /// read as a record and never repaired; none where there is no file.
    /// </summary>
    /// <typeparam name="T">What each line holds.</typeparam>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The log.</param>
    /// <param name="problem">Why a record read does not stand where it does, given the records before it; null where it does.</param>
    /// <param name="refused">The refusal of a whole line that is no record, by its number from 1 and why.</param>
    /// <returns>Each record in order, and the torn line set aside where there was one.</returns>
    public static (IReadOnlyList<T> Records, string? Torn) ReadLog<T>(IFileSystem fileSystem, string path, Func<T, IReadOnlyList<T>, string?> problem, Func<int, string, Exception> refused)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(refused);

        if (fileSystem.KindOf(path) != PathKind.File)
        {
            return ([], null);
        }

        var records = new List<T>();

        // A line at a time: a log grows with what writes it.
        foreach (var (text, ended) in FileLines.Read(fileSystem, path))
        {
            if (!ended)
            {
                return (records, text);
            }

            T? read;

            // A record of many kinds whose line names none, or not first, is no more one than a line that is no JSON.
            try
            {
                read = JsonSerializer.Deserialize<T>(text.TrimEnd('\r'), LineOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw refused(records.Count + 1, ex.Message.TrimEnd('.'));
            }

            if ((read is null ? "it holds null" : problem(read, records)) is { } why)
            {
                throw refused(records.Count + 1, why);
            }

            records.Add(read!);
        }

        return (records, null);
    }

    /// <summary>Writes a state file as lines (<see cref="JsonLineWriter"/>), whole and atomically, as <paramref name="write"/> gives them.</summary>
    /// <param name="fileSystem">The file system it is written through.</param>
    /// <param name="path">The file.</param>
    /// <param name="write">Writes the record's lines, each value on one of its own.</param>
    public static void WriteLines(IFileSystem fileSystem, string path, Action<JsonLineWriter> write)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(write);

        fileSystem.WriteAtomic(path, stream => write(new JsonLineWriter(stream)));
    }
}
