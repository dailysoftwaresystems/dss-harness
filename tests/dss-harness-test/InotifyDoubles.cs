using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using DssHarness.Core.Execution;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// inotify's calls as a machine would answer them, with nothing of the machine's own taken: an instance and its watches
/// are numbers kept here, a limit is an error the test says, and what a read returns is what the test gives it, when
/// it gives it. Disposed, a read blocked in it fails as one of a descriptor closed does, so the hub's thread ends.
/// </summary>
internal sealed class ScriptedInotifyCalls : IInotifyCalls, IDisposable
{
    /// <summary><c>EBADF</c>: what a read of a descriptor closed answers.</summary>
    public const int BadDescriptor = 9;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _descriptors = new(StringComparer.Ordinal);
    private readonly BlockingCollection<(byte[]? Bytes, int Error)> _reads = [];
    private readonly CancellationTokenSource _closed = new();
    private readonly List<int> _initFlags = [];
    private readonly List<(string Path, uint Mask)> _added = [];
    private readonly List<int> _removed = [];
    private readonly List<int> _instancesClosed = [];
    private int _nextInstance = 3;
    private int _nextDescriptor = 1;
    private int _reading;

    /// <summary>What making an instance fails with; 0 where it is made.</summary>
    public int InitError { get; set; }

    /// <summary>What adding a watch on a path fails with; 0 where it is added.</summary>
    public Func<string, int> AddError { get; set; } = _ => 0;

    /// <summary>The flags each instance was asked to be made with, in order.</summary>
    public IReadOnlyList<int> InitFlags => Copy(_initFlags);

    /// <summary>Each directory a watch was asked for, and the mask it was asked with, in order: refused ones too.</summary>
    public IReadOnlyList<(string Path, uint Mask)> Added => Copy(_added);

    /// <summary>Each descriptor removed, in order.</summary>
    public IReadOnlyList<int> Removed => Copy(_removed);

    /// <summary>Each instance closed, in order.</summary>
    public IReadOnlyList<int> InstancesClosed => Copy(_instancesClosed);

    /// <summary>How many reads have been begun.</summary>
    public int Reads => Volatile.Read(ref _reading);

    /// <summary>The descriptor the instance holds for <paramref name="path"/>.</summary>
    public int DescriptorOf(string path)
    {
        lock (_gate)
        {
            return _descriptors[path];
        }
    }

    /// <summary>Gives the next read <paramref name="events"/>, as the kernel writes them.</summary>
    public void Give(params InotifyEvent[] events) => GiveBytes(InotifyRecord.Of(events));

    /// <summary>Gives the next read <paramref name="bytes"/>, whatever they are.</summary>
    public void GiveBytes(byte[] bytes) => _reads.Add((bytes, 0));

    /// <summary>Has the next read fail with <paramref name="error"/>.</summary>
    public void FailRead(int error) => _reads.Add((null, error));

    public int Init(int flags, out int error)
    {
        lock (_gate)
        {
            _initFlags.Add(flags);
            error = InitError;

            return error == 0 ? _nextInstance++ : -1;
        }
    }

    public int AddWatch(int instance, string path, uint mask, out int error)
    {
        lock (_gate)
        {
            _added.Add((path, mask));
            error = AddError(path);

            if (error != 0)
            {
                return -1;
            }

            // The kernel's own rule: one descriptor for a directory, however often it is added.
            if (!_descriptors.TryGetValue(path, out var descriptor))
            {
                _descriptors[path] = descriptor = _nextDescriptor++;
            }

            return descriptor;
        }
    }

    public int RemoveWatch(int instance, int watch, out int error)
    {
        lock (_gate)
        {
            _removed.Add(watch);

            foreach (var (path, _) in _descriptors.Where(entry => entry.Value == watch).ToList())
            {
                _descriptors.Remove(path);
            }

            error = 0;
            return 0;
        }
    }

    public int Read(int instance, byte[] buffer, out int error)
    {
        Interlocked.Increment(ref _reading);

        try
        {
            var (bytes, failure) = _reads.Take(_closed.Token);

            if (bytes is null)
            {
                error = failure;
                return -1;
            }

            bytes.CopyTo(buffer, 0);
            error = 0;
            return bytes.Length;
        }
        catch (OperationCanceledException)
        {
            error = BadDescriptor;
            return -1;
        }
    }

    public void Close(int instance)
    {
        lock (_gate)
        {
            _instancesClosed.Add(instance);
        }
    }

    public void Dispose() => _closed.Cancel();

    private List<T> Copy<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }
}

/// <summary>Writes <c>struct inotify_event</c> records as the kernel does: four numbers in this machine's order, then the name padded with NULs.</summary>
internal static class InotifyRecord
{
    /// <summary>The records of <paramref name="events"/>, one after another, each name padded to a multiple of 16.</summary>
    public static byte[] Of(params InotifyEvent[] events)
        => [.. events.SelectMany(heard => One(heard.Descriptor, heard.Mask, Encoding.UTF8.GetBytes(heard.Name), pad: 16))];

    /// <summary>One record whose name is <paramref name="name"/> exactly, padded with NULs to a multiple of <paramref name="pad"/>.</summary>
    public static byte[] One(int descriptor, uint mask, byte[] name, int pad)
    {
        var length = name.Length == 0 ? 0 : ((name.Length / pad) + 1) * pad;
        var record = new byte[InotifyEvents.HeaderBytes + length];

        Write(record.AsSpan(0), descriptor);
        Write(record.AsSpan(4), (int)mask);
        Write(record.AsSpan(8), 0);
        Write(record.AsSpan(12), length);
        name.CopyTo(record, InotifyEvents.HeaderBytes);

        return record;
    }

    private static void Write(Span<byte> into, int number)
    {
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(into, number);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(into, number);
        }
    }
}

/// <summary>A machine's inotify as <c>/proc</c> would show it when a limit is met: the limits, and nobody holding any.</summary>
internal static class InotifyUses
{
    /// <summary>The limits most machines have, and <paramref name="holders"/>.</summary>
    public static InotifyUse Of(params InotifyHolder[] holders) => new(128, 8192, holders);
}
