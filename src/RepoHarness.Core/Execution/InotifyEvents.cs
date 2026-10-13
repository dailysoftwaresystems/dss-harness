using System.Buffers.Binary;
using System.Text;

namespace RepoHarness.Core.Execution;

/// <summary>The bits of an inotify mask this tool asks for or reads, as <c>linux/inotify.h</c> numbers them.</summary>
internal static class InotifyBits
{
    /// <summary><c>IN_MODIFY</c>: an entry's content was written.</summary>
    public const uint Modify = 0x0000_0002;

    /// <summary><c>IN_ATTRIB</c>: an entry's attributes changed, or the watched directory's own.</summary>
    public const uint Attrib = 0x0000_0004;

    /// <summary><c>IN_MOVED_FROM</c>: an entry left under its name.</summary>
    public const uint MovedFrom = 0x0000_0040;

    /// <summary><c>IN_MOVED_TO</c>: an entry arrived under its name.</summary>
    public const uint MovedTo = 0x0000_0080;

    /// <summary><c>IN_CREATE</c>: an entry was made.</summary>
    public const uint Create = 0x0000_0100;

    /// <summary><c>IN_DELETE</c>: an entry was removed.</summary>
    public const uint Delete = 0x0000_0200;

    /// <summary><c>IN_DELETE_SELF</c>: the watched directory itself was removed.</summary>
    public const uint DeleteSelf = 0x0000_0400;

    /// <summary><c>IN_MOVE_SELF</c>: the watched directory itself was moved.</summary>
    public const uint MoveSelf = 0x0000_0800;

    /// <summary><c>IN_Q_OVERFLOW</c>: the instance's queue overflowed, and events were lost.</summary>
    public const uint QueueOverflow = 0x0000_4000;

    /// <summary><c>IN_IGNORED</c>: the watch is gone - removed, or its directory gone.</summary>
    public const uint Ignored = 0x0000_8000;

    /// <summary><c>IN_ONLYDIR</c>: watch the path only where it is a directory.</summary>
    public const uint OnlyDirectory = 0x0100_0000;

    /// <summary><c>IN_EXCL_UNLINK</c>: nothing of an entry once it is unlinked.</summary>
    public const uint ExcludeUnlinked = 0x0400_0000;

    /// <summary><c>IN_CLOEXEC</c>, which is <c>O_CLOEXEC</c>: no program started holds the instance.</summary>
    public const int CloseOnExec = 0x0008_0000;

    /// <summary>
    /// What every watch asks for, one mask for all, so a directory added twice changes nothing: what .NET's own
    /// watcher asks for the tool's filter, and the watched directory's own removal and move.
    /// </summary>
    public const uint Watched = Create | Delete | Modify | Attrib | MovedFrom | MovedTo | OnlyDirectory | ExcludeUnlinked
        | DeleteSelf | MoveSelf;
}

/// <summary>One event an inotify instance gave.</summary>
/// <param name="Descriptor">The watch it is for; -1 for one of the instance's own, an overflow.</param>
/// <param name="Mask">What happened, in <see cref="InotifyBits"/>.</param>
/// <param name="Name">The entry's name in the watched directory; empty where the event is of the directory itself.</param>
/// <param name="NameUnreadable">Whether the record carried a name that is no UTF-8, which <paramref name="Name"/> then is not.</param>
internal readonly record struct InotifyEvent(int Descriptor, uint Mask, string Name, bool NameUnreadable = false)
{
    /// <summary>Whether the event says <paramref name="bits"/>, any of them.</summary>
    public bool Says(uint bits) => (Mask & bits) != 0;
}

/// <summary>What one read of an inotify instance holds.</summary>
/// <param name="Events">Each whole record, in the order read.</param>
/// <param name="CutShort">Whether a record ran past what was read: what it and those after it said is lost.</param>
internal sealed record InotifyRecords(IReadOnlyList<InotifyEvent> Events, bool CutShort);

/// <summary>
/// Reads what an inotify instance gives - <c>struct inotify_event</c> records, one after another - into events, and
/// does nothing else, so every shape a record takes is read the same way on every system a test runs on.
/// </summary>
/// <remarks>
/// A record is four numbers in this machine's order - the watch, the mask, the cookie that pairs a rename's two
/// halves, the length of the name - and then the name, padded with NULs to that length. The cookie is not read: each
/// half of a rename names its own entry, which is all a watch counts.
/// </remarks>
internal static class InotifyEvents
{
    /// <summary>How long a record is before its name.</summary>
    public const int HeaderBytes = 16;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The records <paramref name="read"/> holds.</summary>
    /// <param name="read">What one read of the instance returned, and nothing after it.</param>
    public static InotifyRecords Parse(ReadOnlySpan<byte> read)
    {
        var events = new List<InotifyEvent>();
        var at = 0;

        while (at < read.Length)
        {
            if (read.Length - at < HeaderBytes)
            {
                return new InotifyRecords(events, CutShort: true);
            }

            var header = read.Slice(at, HeaderBytes);
            var descriptor = Number(header);
            var mask = (uint)Number(header[4..]);
            var length = (uint)Number(header[12..]);

            if (length > (uint)(read.Length - at - HeaderBytes))
            {
                return new InotifyRecords(events, CutShort: true);
            }

            var name = read.Slice(at + HeaderBytes, (int)length);
            var end = name.IndexOf((byte)0);

            if (end >= 0)
            {
                name = name[..end];
            }

            events.Add(Decoded(descriptor, mask, name));
            at += HeaderBytes + (int)length;
        }

        return new InotifyRecords(events, CutShort: false);
    }

    /// <summary>One of a record's numbers, in this machine's order, as the kernel writes it.</summary>
    private static int Number(ReadOnlySpan<byte> bytes)
        => BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes);

    private static InotifyEvent Decoded(int descriptor, uint mask, ReadOnlySpan<byte> name)
    {
        try
        {
            return new InotifyEvent(descriptor, mask, Strict.GetString(name));
        }
        catch (DecoderFallbackException)
        {
            return new InotifyEvent(descriptor, mask, string.Empty, NameUnreadable: true);
        }
    }
}
