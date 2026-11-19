using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// Whether the pipe a child's stream is read from holds anything unread just now: on Windows by how many bytes it holds
/// (<c>PeekNamedPipe</c>), on Linux and macOS by whether reading it would wait (<c>poll</c>) - what tells the end of what a
/// child wrote from what a process it left running writes after, once the child has ended.
/// </summary>
internal static class PipeBytes
{
    /// <summary><c>POLLIN</c>: there is something to read.</summary>
    private const short PollIn = 0x1;

    /// <summary><c>POLLHUP</c>: every writer has gone, so a read ends the stream at once.</summary>
    private const short PollHangUp = 0x10;

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
    /// Whether <paramref name="pipe"/> holds nothing to read and has a writer still: <see langword="true"/> where a read
    /// would wait for a writer, <see langword="false"/> where it holds bytes or its writers have all gone, and
    /// <see langword="null"/> where it cannot be asked.
    /// </summary>
    /// <param name="pipe">The pipe, as <see cref="Handle"/> took it.</param>
    public static bool? Quiet(SafeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        try
        {
            if (pipe.IsInvalid || pipe.IsClosed)
            {
                return null;
            }

            if (OperatingSystem.IsWindows())
            {
                return pipe is SafeFileHandle file && PeekNamedPipe(file, IntPtr.Zero, 0, IntPtr.Zero, out var waiting, IntPtr.Zero) ? waiting == 0 : null;
            }

            var asked = new PollFd { Fd = (int)pipe.DangerousGetHandle(), Events = PollIn };

            return poll(ref asked, 1, 0) switch
            {
                0 => true,
                1 => (asked.Revents & (PollIn | PollHangUp)) == 0,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is ObjectDisposedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>One descriptor <c>poll</c> asks of, as the C library lays it out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int poll(ref PollFd fds, nuint count, int timeout);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, int size, IntPtr read, out int waiting, IntPtr left);
}
