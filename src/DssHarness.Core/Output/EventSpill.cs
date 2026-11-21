using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Json;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Output;

/// <summary>
/// The events a front end's sink has not yet been given once more wait than an operation holds in memory, set down in the
/// operation's event file in the order they were published and taken back in that order: so a sink that lags - a window
/// held still for days - is given every event in the end, and memory holds no more for it than a sink that keeps up.
/// </summary>
/// <remarks>
/// <para>
/// Each event is one record: four bytes of its length, then its JSON - its own type and members, at any depth
/// (<see cref="JsonDepth"/>) - read back as that type. A member typed only as an object, an outcome's facts, is read back
/// as the JSON it was written as, which a front end writes as it was. An event this build would not read back as it wrote
/// it - one whose type a reader cannot make - is checked as it is set down and held in memory, a record of its number
/// standing in its place, so none is lost to its shape. Records are written a page at a time (<see cref="PageBytes"/>), so
/// the disk is asked to keep a page, never each event; a disk that will not take a page leaves its events, and every one
/// after them, waiting in memory in their order, after every record the file holds.
/// </para>
/// <para>
/// Not safe for two callers at once: its output adds and takes under its own lock. Once every event set down has been
/// taken the file is emptied, so it holds only what waits; disposed, it is removed.
/// </para>
/// </remarks>
/// <param name="fileSystem">Writes and reads the file.</param>
/// <param name="path">The operation's event file, its directory already there; made the first time a page is written.</param>
public sealed class EventSpill(IFileSystem fileSystem, string path) : IDisposable
{
    /// <summary>
    /// How many bytes of records wait in memory before they are written to the file together: a page of writing, not a
    /// bound - what does not fit is the next page.
    /// </summary>
    public const int PageBytes = 64 * 1024;

    /// <summary>What a record that stands for an event held in memory names it by.</summary>
    private const string HeldMember = "held";

    /// <summary>What a record names its event's type by.</summary>
    private const string TypeMember = "type";

    /// <summary>What a record carries its event in.</summary>
    private const string EventMember = "event";

    /// <summary>How an event is written and read: its members as declared, at any depth.</summary>
    private static readonly JsonSerializerOptions Options = JsonDepth.Serializer(JsonSerializerOptions.Default);

    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>The events a record of their number stands for, by that number.</summary>
    private readonly Dictionary<long, RunEvent> _held = [];

    /// <summary>The records not yet written, with their events, in order: the page being filled.</summary>
    private readonly List<(RunEvent Event, byte[] Record)> _page = [];

    /// <summary>The events the disk would not take, in order, after every record the file and the page hold.</summary>
    private readonly Queue<RunEvent> _unwritten = new();

    private IAppendFile? _file;

    /// <summary>How many bytes the page holds.</summary>
    private int _pageBytes;

    /// <summary>Where the next record to take begins in the file.</summary>
    private long _next;

    /// <summary>How many records the file holds since it was last emptied: the number of the first on the page.</summary>
    private long _written;

    /// <summary>How many of them have been taken.</summary>
    private long _taken;

    /// <summary>The operation's event file.</summary>
    public string Path { get; } = path;

    /// <summary>How many events wait to be taken.</summary>
    public long Waiting => _written - _taken + _page.Count + _unwritten.Count;

    /// <summary>
    /// A spill in this machine's temporary directory for <paramref name="operation"/>, removed with it: where an operation
    /// whose front end names no event file of its own sets down what its sink lags behind.
    /// </summary>
    /// <param name="operation">The operation, which names the file.</param>
    public static EventSpill Temporary(OperationId operation)
        => new(
            new PhysicalFileSystem(FilePermissionsFactory.Create()),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dssharness-events-{operation}.events"));

    /// <summary>Sets <paramref name="event"/> down after every event that waits. Never fails: what the disk will not take waits in memory.</summary>
    /// <param name="event">The event, stamped.</param>
    public void Add(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (_unwritten.Count > 0)
        {
            _unwritten.Enqueue(@event);
            return;
        }

        var record = Record(@event);

        if (record is null)
        {
            // Read back, it would be another event than this one: held as itself, its record standing in its place.
            var number = _written + _page.Count;
            _held[number] = @event;
            record = Framed(JsonSerializer.SerializeToUtf8Bytes(new JsonObject { [HeldMember] = number }, Options));
        }

        _page.Add((@event, record));
        _pageBytes += record.Length;

        if (_pageBytes >= PageBytes)
        {
            WritePage();
        }
    }

    /// <summary>
    /// The event that has waited longest, taken; and the file emptied once nothing waits. Where the file could not be read
    /// back, an <see cref="EventsUnread"/> saying how many of its records were lost, and why, stands for them - every event
    /// held in memory in their place still given after it - so a loss is said where it fell, never passed over.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing waits.</exception>
    public RunEvent Take()
    {
        if (Waiting == 0)
        {
            throw new InvalidOperationException("No event waits in the event file.");
        }

        if (_taken == _written && _page.Count > 0)
        {
            // The next to take is on the page: written first, so every record is taken from one place.
            WritePage();
        }

        var taken = _taken < _written ? Next() : _unwritten.Dequeue();

        if (Waiting == 0)
        {
            Emptied();
        }

        return taken;
    }

    /// <summary>Closes the file and removes it, with the claim its writer held beside it.</summary>
    public void Dispose()
    {
        _page.Clear();
        _pageBytes = 0;

        if (_file is null)
        {
            return;
        }

        _file.Dispose();
        _file = null;

        foreach (var made in new[] { Path, Path + ".lock" })
        {
            try
            {
                _fileSystem.DeleteFile(made);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary directory, holding nothing anyone reads.
            }
        }
    }

    /// <summary>
    /// Writes the page to the file; where the disk will not take it, its events wait in memory in their order, and every
    /// one after them with them.
    /// </summary>
    private void WritePage()
    {
        var bytes = new byte[_pageBytes];
        var at = 0;

        foreach (var (_, record) in _page)
        {
            record.CopyTo(bytes, at);
            at += record.Length;
        }

        try
        {
            _file ??= _fileSystem.OpenForAppend(Path);
            _file.AppendFlushed(bytes);
            _written += _page.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (var index = 0; index < _page.Count; index++)
            {
                _held.Remove(_written + index);
                _unwritten.Enqueue(_page[index].Event);
            }
        }

        _page.Clear();
        _pageBytes = 0;
    }

    /// <summary>The next record's event; or, where the file could not be read, what was lost of it.</summary>
    private RunEvent Next()
    {
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            _file!.ReadExactlyAt(_next, length);

            var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
            _file.ReadExactlyAt(_next + sizeof(int), bytes);

            var number = _taken;
            (_next, _taken) = (_next + sizeof(int) + bytes.Length, _taken + 1);

            using var document = JsonDocument.Parse(bytes, JsonDepth.Document);

            return document.RootElement.TryGetProperty(HeldMember, out _) && _held.Remove(number, out var held) ? held : Made(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // What is left of the file cannot be read on: every record left in it is taken as lost, but for the events held
            // in memory in their place, which wait after the notice in their order, ahead of what was never written.
            var held = _held.Where(entry => entry.Key >= _taken && entry.Key < _written).OrderBy(entry => entry.Key).Select(entry => entry.Value).ToList();
            var lost = _written - _taken - held.Count;
            var after = _unwritten.ToList();

            foreach (var key in _held.Keys.Where(key => key < _written).ToList())
            {
                _held.Remove(key);
            }

            _taken = _written;
            _unwritten.Clear();

            foreach (var waiting in held.Concat(after))
            {
                _unwritten.Enqueue(waiting);
            }

            return new EventsUnread(lost, $"the event file '{Path}' could not be read back: {ex.Message}");
        }
    }

    /// <summary>The file emptied, once nothing waits, so it holds only what waits.</summary>
    private void Emptied()
    {
        _held.Clear();

        try
        {
            _file?.TruncateTo(0);
            (_next, _written, _taken) = (0, 0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it was: the next records go after what it holds, and are read from where they begin.
            (_next, _written, _taken) = (_file!.Length, 0, 0);
        }
    }

    /// <summary>
    /// <paramref name="event"/>'s record, or <see langword="null"/> where it would not be read back as an event of its type -
    /// its type cannot be named, written or made again.
    /// </summary>
    private static byte[]? Record(RunEvent @event)
    {
        var type = @event.GetType();

        if (type.AssemblyQualifiedName is not { } named)
        {
            return null;
        }

        try
        {
            var written = JsonSerializer.SerializeToUtf8Bytes(
                new JsonObject
                {
                    [TypeMember] = named,
                    [EventMember] = JsonSerializer.SerializeToNode(@event, type, Options),
                },
                Options);

            // Read back as it will be: an event its reader cannot make is held instead, never lost when it is taken.
            using (var document = JsonDocument.Parse(written, JsonDepth.Document))
            {
                _ = Made(document.RootElement);
            }

            return Framed(written);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The event a record names the type and members of, made again.</summary>
    /// <exception cref="JsonException">The record names no event this build makes.</exception>
    private static RunEvent Made(JsonElement record)
    {
        var type = Type.GetType(record.GetProperty(TypeMember).GetString() ?? string.Empty, throwOnError: false);

        return type is not null && typeof(RunEvent).IsAssignableFrom(type) && record.GetProperty(EventMember).Deserialize(type, Options) is RunEvent made
            ? made
            : throw new JsonException("the record names no event this build makes");
    }

    /// <summary><paramref name="written"/> after four bytes of its length.</summary>
    private static byte[] Framed(byte[] written)
    {
        var framed = new byte[sizeof(int) + written.Length];

        BinaryPrimitives.WriteInt32LittleEndian(framed, written.Length);
        written.CopyTo(framed, sizeof(int));

        return framed;
    }
}
