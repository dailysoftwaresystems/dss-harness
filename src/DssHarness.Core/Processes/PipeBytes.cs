using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// The pipe under the stream a child's output is read from, taken before anything is read from it, and what it holds once
/// no read waits on it: what tells a stream whose every character has been read, while a process the child left running
/// holds it open, from one that is still to be read. Asked only between reads, a look having ended the read that waited
/// (<see cref="ProcessRunner.StreamCapture"/>): on Windows the handle a synchronous read waits on answers nothing else until
/// that read ends, a peek waiting for it; and on every system a read's own task says nothing of the pipe - on Windows it
/// waits to be run by a pool thread, the read not yet made, while what the child wrote sits in the pipe, and on Linux and
/// macOS it can do the same - so the pipe itself is asked, never the task.
/// </summary>
internal static class PipeBytes
{
    /// <summary>
    /// The pipe under <paramref name="stream"/>, as the process gives a child's output - a file on Windows, a pipe on
    /// Linux and macOS - taken before anything is read from it; <see langword="null"/> where it is neither.
    /// </summary>
    /// <param name="stream">The stream a child's output is read from.</param>
    public static SafeHandle? Handle(Stream stream)
        => stream switch
        {
            FileStream file when OperatingSystem.IsWindows() => file.SafeFileHandle,
            PipeStream pipe when !OperatingSystem.IsWindows() => pipe.SafePipeHandle,
            _ => null,
        };

    /// <summary>
    /// The stream <paramref name="stream"/>'s pipe is read through, holding no byte of its own between reads: on Windows one
    /// over the same handle that buffers nothing - the process's own keeps what one read of the pipe took past what was asked
    /// of it, bytes the pipe, asked, does not hold - and elsewhere the stream itself, a pipe's stream buffering nothing.
    /// </summary>
    /// <param name="stream">The stream a child's output is read from, nothing read from it yet.</param>
    public static Stream Unbuffered(Stream stream)
        => stream is FileStream file && OperatingSystem.IsWindows()
            ? new FileStream(file.SafeFileHandle, FileAccess.Read, bufferSize: 0, file.IsAsync)
            : stream;

    /// <summary>
    /// Whether <paramref name="pipe"/> holds nothing while something still holds it open for writing, asked while no read
    /// waits on it: on Windows by a peek, which answers at once where no read is pending and says a pipe every writer has let
    /// go of is broken; on Linux and macOS by poll(2), which says what there is to read and a pipe every writer has let go
    /// of hung up. A pipe that holds bytes, one every writer has let go of, and one the system would not be asked about are
    /// none of these: their reading goes on, to its end where there is nothing more.
    /// </summary>
    /// <param name="pipe">The pipe a child's output is read through.</param>
    public static bool HeldEmpty(SafeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return PeekNamedPipe(pipe, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero) && available == 0;
            }

            var asked = new PollFd[1];
            asked[0].Fd = pipe.DangerousGetHandle().ToInt32();
            asked[0].Events = PollIn;

            // Ready on nothing: no byte to read, and no hang-up, which every writer letting go of the pipe says.
            return PollOne(asked, 1, 0) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // A pipe the system would not be asked about: read to its end.
            return false;
        }
    }

    /// <summary>
    /// PeekNamedPipe: what a pipe holds, taking none of it - answered at once where no read is pending on the handle;
    /// ERROR_BROKEN_PIPE where every writer has let go of it and nothing is left.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafeHandle pipe, IntPtr buffer, int size, IntPtr read, out int available, IntPtr left);

    /// <summary>poll(2): whether a descriptor is ready, asked of one descriptor, waiting no time at all.</summary>
    [DllImport("libc", EntryPoint = "poll", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int PollOne([In, Out] PollFd[] descriptors, int count, int timeout);

    /// <summary>POLLIN: there is what to read.</summary>
    private const short PollIn = 0x1;

    /// <summary>One descriptor asked of by poll(2).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }
}
