using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// The pipe under the stream a child's output is read from, taken before anything is read from it: what tells a
/// stream whose reading a process the child left running can hold open from one that is read to its end. The pipe's
/// own state is asked of no system: the read that pends on it is the answer - anything more would have ended it -
/// and on Windows the handle a synchronous read pends on answers nothing else, a peek on it waiting for that read
/// to end first.
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
}
