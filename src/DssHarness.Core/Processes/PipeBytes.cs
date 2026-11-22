using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// The pipe under the stream a child's output is read from, taken before anything is read from it: what tells a
/// stream whose reading a process the child left running can hold open from one that is read to its end. The pipe's
/// own state is asked of no system on Windows: the read that pends on it is the answer - anything more would have
/// ended it - and the handle a synchronous read pends on answers nothing else, a peek on it waiting for that read to
/// end first. On Linux and macOS the pipe answers while a read pends on it, and is asked: a read there can wait
/// merely to be run, its task no answer at all while what the child wrote already sits in the pipe, so the pipe
/// itself says whether that is so.
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
    /// Whether <paramref name="pipe"/> holds anything to read, asked of the system only where a read pending on it
    /// leaves it askable: Linux and macOS, where a pipe answers while a read waits and a read can wait merely to be
    /// run, its task no answer while what the child wrote sits in the pipe. Never asked on Windows, whose handles
    /// would answer nothing until the pending read ended: there a read is the system's own to hold, submitted as it
    /// is made, so its task alone tells whether anything waits.
    /// </summary>
    /// <param name="pipe">The pipe a child's output is read through, or <see langword="null"/> where there is none.</param>
    /// <returns>Whether the pipe holds what the child wrote and no read has taken; <see langword="false"/> on Windows, and where it cannot be asked.</returns>
    public static bool HoldsData(SafeHandle? pipe)
    {
        if (pipe is null || OperatingSystem.IsWindows())
        {
            return false;
        }

        var asked = new PollFd[1];
        asked[0].Fd = pipe.DangerousGetHandle().ToInt32();
        asked[0].Events = PollIn;

        try
        {
            return PollOne(asked, 1, 0) == 1 && (asked[0].Revents & PollIn) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // A pipe the system would not be asked: the read that pends on it stays the only answer.
            return false;
        }
    }

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
