using System.Globalization;
using System.Text.Json;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// A state file written as lines, each value on one of its own with <see cref="JsonStateFile.LineOptions"/> - for a record
/// that grows with what it describes, such as a codebase graph's link result. A list of a record is read and written
/// whole by the converter that refuses its null items, which the serializer cannot pause inside, so a record of one list
/// of a hundred thousand items is held whole as bytes while it is read or written; as lines, no more than one item is.
/// </summary>
/// <param name="stream">The file being written.</param>
public sealed class JsonLineWriter(Stream stream)
{
    /// <summary>Writes <paramref name="line"/> on a line of its own.</summary>
    /// <typeparam name="T">What the line holds.</typeparam>
    /// <param name="line">The value.</param>
    public void Write<T>(T line)
    {
        JsonSerializer.Serialize(stream, line, JsonStateFile.LineOptions);
        stream.WriteByte((byte)'\n');
    }
}

/// <summary>A state file of lines, read one line at a time (<see cref="JsonLineWriter"/>, <see cref="FileLines"/>), each line by its number.</summary>
/// <param name="lines">The file's lines, as they are read.</param>
public sealed class JsonLineReader(IEnumerator<FileLine> lines)
{
    private int _number;

    /// <summary>The next line, read as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">What the line holds.</typeparam>
    /// <param name="what">What the line is, as a refusal names it.</param>
    /// <exception cref="JsonException">The file ends before it, or it holds null, or it does not read as one.</exception>
    public T Next<T>(string what)
    {
        if (!lines.MoveNext())
        {
            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"it ends after line {_number}, where {what} was to be"));
        }

        _number++;

        try
        {
            return JsonSerializer.Deserialize<T>(lines.Current.Text, JsonStateFile.LineOptions) ?? throw new JsonException("it holds null");
        }
        catch (JsonException ex)
        {
            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"line {_number}, {what}, does not read: {ex.Message.TrimEnd('.')}"), ex);
        }
    }

    /// <summary>Refuses a file that goes on past the lines read.</summary>
    /// <exception cref="JsonException">It holds another line.</exception>
    public void End()
    {
        if (lines.MoveNext())
        {
            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"it goes on past line {_number}, where it was to end"));
        }
    }
}
